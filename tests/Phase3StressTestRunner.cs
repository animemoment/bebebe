using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI.Streaming;

namespace Game.Tests;

public partial class Phase3StressTestRunner : Node
{
	public string RunAllTests()
	{
		try
		{
			TestSimulationThreadStartIdempotency();
			TestWorldJobTickerPauseAndSpeed();
			TestConstructionSiteMaterialRefund();
			return ""; // Empty string indicates all assertions passed
		}
		catch (Exception ex)
		{
			return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
		}
	}

	private void TestSimulationThreadStartIdempotency()
	{
		int width = 32, height = 32;
		var ground = new TileType[width, height];
		var trees = new bool[width, height];
		for (int x = 0; x < width; x++)
			for (int y = 0; y < height; y++)
				ground[x, y] = TileType.Grass;

		var thread = new AgentSimulationThread();

		// Start once
		thread.Start(5, ground, trees);

		// Immediately start second time — must stop previous cleanly without exception or hanging
		thread.Start(5, ground, trees);

		// Stop cleanly
		bool stopped = thread.Stop(5000);
		if (!stopped)
			throw new InvalidOperationException("AgentSimulationThread failed to stop within 5000ms after double Start.");
	}

	private void TestWorldJobTickerPauseAndSpeed()
	{
		var timeManager = TimeManager.Instance;
		if (timeManager == null)
		{
			timeManager = new TimeManager();
			timeManager.Name = "TestTimeManager";
			AddChild(timeManager);
		}

		timeManager.SetSpeed(GameSpeed.Paused);
		if (!timeManager.IsPaused)
			throw new InvalidOperationException("TimeManager.IsPaused should be true after setting Paused.");

		timeManager.SetSpeed(GameSpeed.Fast25);
		if (Math.Abs(timeManager.SpeedMultiplier - 25f) > 0.01f)
			throw new InvalidOperationException($"TimeManager.SpeedMultiplier is {timeManager.SpeedMultiplier}, expected 25.");

		timeManager.SetSpeed(GameSpeed.Normal);
		if (Math.Abs(timeManager.SpeedMultiplier - 1f) > 0.01f)
			throw new InvalidOperationException($"TimeManager.SpeedMultiplier is {timeManager.SpeedMultiplier}, expected 1.");
	}

	private void TestConstructionSiteMaterialRefund()
	{
		var cm = ConstructionManager.Instance;
		var gm = GroundItemManager.Instance;
		cm.ClearAll();
		gm.ClearAll();

		var mapData = new MapData(64, 64);
		bool started = cm.StartSite(20, 20, StructureRegistry.WoodWall, mapData);
		if (!started)
			throw new InvalidOperationException("Failed to start construction site at (20, 20)");

		// Check clearance stage: obstacle clearing allowed, delivery not yet
		if (cm.TryGetSite(20, 20, out var site))
		{
			// Simulate clearing completed
			site.Stage = ConstructionStage.Supplying;
		}

		// Deliver 3 logs
		bool delivered = cm.DeliverMaterial(20, 20, ItemId.Log, 3, out int accepted, out _);
		if (!delivered || accepted != 3)
			throw new InvalidOperationException($"DeliverMaterial failed: accepted={accepted}, expected 3");

		// Cancel site
		cm.CancelSite(20, 20);

		// Verify site removed
		if (cm.IsSiteAt(20, 20))
			throw new InvalidOperationException("Site still present after CancelSite");

		// Verify logs refunded to ground items
		if (!gm.HasAvailableLogs)
			throw new InvalidOperationException("Materials were not refunded to GroundItemManager upon CancelSite!");

		// Cleanup
		cm.ClearAll();
		gm.ClearAll();
	}
}
