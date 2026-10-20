using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Simulation;

namespace Game.Tests;

public partial class Phase1StressTestRunner : Node
{
	public string RunAllTests()
	{
		try
		{
			TestZoneConcurrencyAndSnapshot();
			TestHierarchicalPathfinderResetAndAtomicState();
			TestFlatIdleWorkerPoolLifecycle();
			TestFlowFieldManagerClearCache();
			TestAgentSimulationThreadStopTimeout();
			return ""; // Empty string indicates all assertions passed
		}
		catch (Exception ex)
		{
			return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
		}
	}

	private void TestZoneConcurrencyAndSnapshot()
	{
		var tiles = new HashSet<(int X, int Y)> { (10, 10), (10, 11), (11, 10) };
		var zone = new Zone(42, "TestZone", ZoneKind.Farm, tiles);

		if (zone.TotalTiles != 3)
			throw new InvalidOperationException($"TotalTiles mismatch: {zone.TotalTiles}");

		if (!zone.ContainsTile(10, 10))
			throw new InvalidOperationException("ContainsTile(10, 10) failed");

		if (zone.ContainsTile(99, 99))
			throw new InvalidOperationException("ContainsTile(99, 99) should be false");

		var snap = zone.GetTilesSnapshot();
		snap.Add((99, 99));
		if (zone.ContainsTile(99, 99))
			throw new InvalidOperationException("Snapshot mutation affected original zone!");

		// Adversarial concurrency stress test: reader thread iterates zone.Tiles while writer replaces reference
		bool running = true;
		Exception readerEx = null;
		var readTask = Task.Run(() =>
		{
			try
			{
				while (Volatile.Read(ref running))
				{
					var current = zone.Tiles;
					if (current != null)
					{
						foreach (var t in current)
						{
							_ = t.X + t.Y;
						}
					}
				}
			}
			catch (Exception ex)
			{
				readerEx = ex;
			}
		});

		for (int i = 0; i < 5000; i++)
		{
			var newTiles = new HashSet<(int X, int Y)> { (i, i), (i + 1, i) };
			zone.Tiles = newTiles;
		}

		Volatile.Write(ref running, false);
		if (!readTask.Wait(2000))
			throw new TimeoutException("Reader task hung during concurrency test");

		if (readerEx != null)
			throw new AggregateException("Reader encountered concurrent modification exception", readerEx);
	}

	private void TestHierarchicalPathfinderResetAndAtomicState()
	{
		var pf = HierarchicalPathfinder.Instance;
		pf.Reset();

		if (pf.State != null)
			throw new InvalidOperationException("HierarchicalPathfinder.State must be null immediately after Reset()");

		// Verify Invalidate does not throw and maintains consistent state
		pf.Invalidate();
		pf.Reset();

		if (pf.State != null)
			throw new InvalidOperationException("HierarchicalPathfinder.State must be null after second Reset()");
	}

	private void TestFlatIdleWorkerPoolLifecycle()
	{
		var pool = new FlatIdleWorkerPool(64);
		if (pool.TotalIdleCount != 0)
			throw new InvalidOperationException($"Initial worker count must be 0, was {pool.TotalIdleCount}");

		pool.AddIdleWorker(5);
		pool.AddIdleWorker(12);
		pool.AddIdleWorker(23);

		if (pool.TotalIdleCount != 3)
			throw new InvalidOperationException($"Worker count must be 3, was {pool.TotalIdleCount}");

		pool.Clear();
		if (pool.TotalIdleCount != 0)
			throw new InvalidOperationException($"Worker count after Clear() must be 0, was {pool.TotalIdleCount}");
	}

	private void TestFlowFieldManagerClearCache()
	{
		var ff = FlowFieldManager.Instance;
		// Must execute without exception
		ff.ClearCache();
	}

	private void TestAgentSimulationThreadStopTimeout()
	{
		var thread = new AgentSimulationThread();
		if (!thread.Stop(1000))
			throw new InvalidOperationException("AgentSimulationThread.Stop must return true when already stopped");
	}
}
