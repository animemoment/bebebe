using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;
using Game.Simulation.Jobs;
using Game.UI;

namespace Game.Tests;

public partial class Phase5StressTestRunner : Node
{
	public string RunAllTests()
	{
		try
		{
			TestTileClearanceServiceBatchAndFastExit();
			TestJobSpatialPyramidCanExecuteMemoization();
			TestStreamingWorldManagerTransactionalReplace();
			TestWorldGameRulesResidentCellFastPath();
			TestZonePriorityOverrideThreadSafetyAndSnapshot();
			TestMapRendererTileSetCacheReset();
			return ""; // Empty string indicates all assertions passed
		}
		catch (Exception ex)
		{
			return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
		}
	}

	private void TestTileClearanceServiceBatchAndFastExit()
	{
		int w = 32, h = 32;
		var ground = new TileType[w, h];
		var trees = new bool[w, h];
		var stones = new bool[w, h];
		var solidWalls = new bool[w, h];
		var walkable = new List<(int X, int Y)>();
		for (int x = 0; x < w; x++)
		{
			for (int y = 0; y < h; y++)
			{
				ground[x, y] = TileType.Grass;
				walkable.Add((x, y));
			}
		}

		var spatialGrid = new AgentSpatialGrid(w, h);
		var movement = new AgentMovementService();
		var ctx = new SimulationContext(ground, null, trees, solidWalls, walkable, spatialGrid, movement, new Random(42), 64, stones);

		// 1. Fast-path exit on wall without acquiring locks
		ctx.SolidWalls[5, 5] = true;
		var obsWall = TileClearanceService.GetObstacles(5, 5, ctx);
		if ((obsWall & ObstacleMask.HandmadeStructure) == 0)
			throw new InvalidOperationException("GetObstacles must return ObstacleMask.HandmadeStructure for SolidWalls tile.");

		// 2. Fast-path exit on water
		ctx.Ground[6, 6] = TileType.Water;
		var obsWater = TileClearanceService.GetObstacles(6, 6, ctx);
		if ((obsWater & ObstacleMask.ImpassableTerrain) == 0)
			throw new InvalidOperationException("GetObstacles must return ObstacleMask.ImpassableTerrain for Water tile.");

		// 3. Tree obstacle
		ctx.TreeOnGrass[7, 7] = true;
		var obsTree = TileClearanceService.GetObstacles(7, 7, ctx);
		if ((obsTree & ObstacleMask.Tree) == 0)
			throw new InvalidOperationException("GetObstacles must return ObstacleMask.Tree for Tree tile.");

		// 4. Batch query
		var coords = new (int X, int Y)[]
		{
			(5, 5), // Wall
			(6, 6), // Water
			(7, 7), // Tree
			(0, 0)  // Clear Grass
		};
		var buffer = new ObstacleMask[coords.Length];
		TileClearanceService.GetObstaclesBatch(coords, buffer, ctx);

		if ((buffer[0] & ObstacleMask.HandmadeStructure) == 0)
			throw new InvalidOperationException("Batch item 0 should be HandmadeStructure.");
		if ((buffer[1] & ObstacleMask.ImpassableTerrain) == 0)
			throw new InvalidOperationException("Batch item 1 should be ImpassableTerrain.");
		if ((buffer[2] & ObstacleMask.Tree) == 0)
			throw new InvalidOperationException("Batch item 2 should be Tree.");
		if (buffer[3] != ObstacleMask.None)
			throw new InvalidOperationException("Batch item 3 should be None for clear grass.");

		// 5. Adversarial checks from bad_phase5_data.json
		string badDataPath = "res://tests/bad_phase5_data.json";
		if (Godot.FileAccess.FileExists(badDataPath))
		{
			using var f = Godot.FileAccess.Open(badDataPath, Godot.FileAccess.ModeFlags.Read);
			string json = f.GetAsText();
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("clearance_edge_cases", out var edgeCases))
			{
				foreach (var elem in edgeCases.EnumerateArray())
				{
					int ex = elem.GetProperty("x").GetInt32();
					int ey = elem.GetProperty("y").GetInt32();
					// Extreme out of bounds coordinates must return ImpassableTerrain safely
					var res = TileClearanceService.GetObstacles(ex, ey, ctx);
					if ((uint)ex >= (uint)w || (uint)ey >= (uint)h)
					{
						if (res != ObstacleMask.ImpassableTerrain)
							throw new InvalidOperationException($"Out of bounds ({ex}, {ey}) should return ImpassableTerrain.");
					}
				}
			}
		}
	}

	private void TestJobSpatialPyramidCanExecuteMemoization()
	{
		var pyramid = new JobSpatialPyramid();

		// 1. Begin epoch
		pyramid.BeginClaimEpoch();

		var job1 = new JobData { TargetX = 5, TargetY = 5, TypeId = JobTypeId.TreeChopping };
		int id1 = pyramid.RegisterJob(job1);

		// Initial query: cache miss
		if (pyramid.TryGetMemoCanExecute(id1, out _))
			throw new InvalidOperationException("Memo should miss initially.");

		// Memoize true
		pyramid.SetMemoCanExecute(id1, true);
		if (!pyramid.TryGetMemoCanExecute(id1, out bool can1) || !can1)
			throw new InvalidOperationException("Memo should hit and return true.");

		// 2. Next epoch invalidates memoization cleanly
		pyramid.BeginClaimEpoch();
		if (pyramid.TryGetMemoCanExecute(id1, out _))
			throw new InvalidOperationException("Memo should miss after starting a new epoch.");

		// 3. Bounds safety with out-of-range IDs
		if (pyramid.TryGetMemoCanExecute(-1, out _))
			throw new InvalidOperationException("Negative ID must not throw and must return false.");
		if (pyramid.TryGetMemoCanExecute(100000, out _))
			throw new InvalidOperationException("Out-of-range ID must not throw and must return false.");

		pyramid.SetMemoCanExecute(-1, true); // Must not throw
		pyramid.SetMemoCanExecute(100000, true); // Must not throw
	}

	private sealed class MockBlockWorld : IBlockWorld
	{
		public readonly HashSet<(byte Kind, long X, long Y)> Blocks = new();
		public bool ChunkLoaded = true;
		public byte FailKind = 0;

		public bool IsLoaded(ChunkKey key) => ChunkLoaded;
		public bool AddWall(long cellX, long cellY) => AddBlock(WorldDeltaKind.Wall, cellX, cellY);
		public bool RemoveWall(long cellX, long cellY) => RemoveBlock(WorldDeltaKind.Wall, cellX, cellY);
		public bool HasWall(long cellX, long cellY) => HasBlock(WorldDeltaKind.Wall, cellX, cellY);
		public int WallCount => BlockCount(WorldDeltaKind.Wall);

		public bool AddBlock(byte kind, long cellX, long cellY)
		{
			if (!ChunkLoaded || (FailKind != 0 && kind == FailKind)) return false;
			return Blocks.Add((kind, cellX, cellY));
		}

		public bool RemoveBlock(byte kind, long cellX, long cellY)
		{
			if (!ChunkLoaded) return false;
			return Blocks.Remove((kind, cellX, cellY));
		}

		public bool HasBlock(byte kind, long cellX, long cellY) => Blocks.Contains((kind, cellX, cellY));
		public int BlockCount(byte kind)
		{
			int count = 0;
			foreach (var b in Blocks)
				if (b.Kind == kind) count++;
			return count;
		}
	}

	private void TestStreamingWorldManagerTransactionalReplace()
	{
		var mockWorld = new MockBlockWorld();
		var tempDir = System.IO.Path.Combine(OS.GetUserDataDir(), "test_phase5_save_" + Guid.NewGuid().ToString("N"));
		System.IO.Directory.CreateDirectory(tempDir);
		try
		{
			var coordinator = new WorldSaveCoordinator(tempDir, 12345UL, 1U, 1U);
			var manager = new StreamingWorldManager(mockWorld, coordinator);
			manager.Open(0, 0);

			long cx = 10, cy = 10;
			byte planKind = WorldDeltaKind.BuildingPlan;
			byte doneKind = WorldDeltaKind.Building;

			// 1. Success case: oldKind -> newKind
			mockWorld.Blocks.Add((planKind, cx, cy));
			bool ok = manager.TryReplaceBlock(planKind, doneKind, cx, cy);
			if (!ok)
				throw new InvalidOperationException("TryReplaceBlock should succeed when oldKind is present.");
			if (mockWorld.HasBlock(planKind, cx, cy))
				throw new InvalidOperationException("oldKind should be removed upon successful replace.");
			if (!mockWorld.HasBlock(doneKind, cx, cy))
				throw new InvalidOperationException("newKind should be present upon successful replace.");

			// 2. Failure & Rollback case: simulate failure adding new block
			long cx2 = 12, cy2 = 12;
			mockWorld.Blocks.Add((planKind, cx2, cy2));
			mockWorld.FailKind = doneKind;

			bool okRollback = manager.TryReplaceBlock(planKind, doneKind, cx2, cy2);
			if (okRollback)
				throw new InvalidOperationException("TryReplaceBlock must fail when AddBlock returns false.");
			if (!mockWorld.HasBlock(planKind, cx2, cy2))
				throw new InvalidOperationException("oldKind must be rolled back and preserved upon failure (F-009).");

			// 3. Old block missing case
			mockWorld.FailKind = 0;
			bool okMissing = manager.TryReplaceBlock(planKind, doneKind, 99, 99);
			if (okMissing)
				throw new InvalidOperationException("TryReplaceBlock must return false when old block is absent.");

			// 4. Blocked by wall case
			long cx3 = 14, cy3 = 14;
			mockWorld.Blocks.Add((planKind, cx3, cy3));
			mockWorld.Blocks.Add((WorldDeltaKind.Wall, cx3, cy3)); // Obstacle
			bool okBlocked = manager.TryReplaceBlock(planKind, doneKind, cx3, cy3);
			if (okBlocked)
				throw new InvalidOperationException("TryReplaceBlock must return false when cell is blocked by another entity.");
			if (!mockWorld.HasBlock(planKind, cx3, cy3))
				throw new InvalidOperationException("oldKind must be preserved when replacement is rejected.");
		}
		finally
		{
			if (System.IO.Directory.Exists(tempDir))
			{
				try { System.IO.Directory.Delete(tempDir, true); } catch { }
			}
		}
	}

	private sealed class MockResidentWorld : IWorldCellSource
	{
		public readonly HashSet<(byte Kind, long X, long Y)> Blocks = new();
		public int TryCellInfoInvocations = 0;

		public bool IsLoaded(ChunkKey key) => true;
		public bool CoversChunk(long cellX, long cellY) => true;

		public bool TryCellInfo(long cellX, long cellY, out GeneratedCell cell)
		{
			TryCellInfoInvocations++;
			// Fast resident cell on grass with low moisture and moderate elevation
			cell = new GeneratedCell(
				Terrain: BaseTerrainKind.Grass,
				ElevationQ16: 32768,
				MoistureQ16: 35000,
				ForestQ16: 15000,
				StoneQ16: 0,
				TemperatureQ16: 30000,
				// Фикстура обязана быть честной: Biome по умолчанию 0 = Desert (пустыня),
				// а не «трава». Без этого поля гейт IsArableSoil (WorldGameRules) отвергает
				// клетку, и тест падает на дефекте фикстуры, а не на регрессе логики.
				Biome: (byte)BiomeType.Plains
			);
			return true;
		}

		public bool AddBlock(byte kind, long cellX, long cellY) => Blocks.Add((kind, cellX, cellY));
		public bool RemoveBlock(byte kind, long cellX, long cellY) => Blocks.Remove((kind, cellX, cellY));
		public bool HasBlock(byte kind, long cellX, long cellY) => Blocks.Contains((kind, cellX, cellY));
		public int BlockCount(byte kind) => Blocks.Count;

		public bool AddWall(long cellX, long cellY) => AddBlock(WorldDeltaKind.Wall, cellX, cellY);
		public bool RemoveWall(long cellX, long cellY) => RemoveBlock(WorldDeltaKind.Wall, cellX, cellY);
		public bool HasWall(long cellX, long cellY) => HasBlock(WorldDeltaKind.Wall, cellX, cellY);
		public int WallCount => BlockCount(WorldDeltaKind.Wall);
	}

	private void TestWorldGameRulesResidentCellFastPath()
	{
		var mockSource = new MockResidentWorld();
		var rules = new WorldGameRules(mockSource, 12345UL, 1U);

		// Check CanBuildAt
		bool canBuild = rules.CanBuildAt(10, 10);
		if (!canBuild)
			throw new InvalidOperationException("CanBuildAt should return true for grass cell.");

		if (mockSource.TryCellInfoInvocations == 0)
			throw new InvalidOperationException("WorldGameRules must use IWorldCellSource.TryCellInfo fast-path (F-011).");

		// Check TryPlantAt
		bool planted = rules.TryPlantAt(10, 10);
		if (!planted)
			throw new InvalidOperationException("TryPlantAt should succeed with valid moisture and resident cell.");
	}

	private void TestZonePriorityOverrideThreadSafetyAndSnapshot()
	{
		var tiles = new HashSet<(int X, int Y)> { (1, 1), (1, 2), (2, 2) };
		var zone = new Zone(1, "TestFarm", ZoneKind.Farm, tiles);

		// 1. Initial priority is null
		if (zone.PriorityOverride.HasValue)
			throw new InvalidOperationException("Initial PriorityOverride must be null.");

		// 2. Snapshot of tiles
		var snapshot = zone.GetTilesSnapshot();
		if (snapshot.Count != 3)
			throw new InvalidOperationException($"Tiles snapshot count mismatch: expected 3, got {snapshot.Count}.");

		// Empty zone snapshot returns non-null empty set
		var emptyZone = new Zone(2, "EmptyZone", ZoneKind.Work, new HashSet<(int X, int Y)>());
		var emptySnapshot = emptyZone.GetTilesSnapshot();
		if (emptySnapshot == null || emptySnapshot.Count != 0)
			throw new InvalidOperationException("Empty zone snapshot must return non-null empty set.");

		// 3. Multi-threaded stress test on PriorityOverride (Volatile.Read / Volatile.Write)
		int readers = 4;
		int writers = 4;
		int iterations = 5000;
		var tasks = new List<Task>();

		for (int w = 0; w < writers; w++)
		{
			int writerId = w;
			tasks.Add(Task.Run(() =>
			{
				for (int i = 0; i < iterations; i++)
				{
					zone.PriorityOverride = (i % 2 == 0) ? (writerId * 10 + i) : null;
				}
			}));
		}

		for (int r = 0; r < readers; r++)
		{
			tasks.Add(Task.Run(() =>
			{
				for (int i = 0; i < iterations; i++)
				{
					int? p = zone.PriorityOverride;
					if (p.HasValue && p.Value < 0)
						throw new InvalidOperationException("PriorityOverride returned invalid negative value.");
				}
			}));
		}

		Task.WaitAll(tasks.ToArray());
	}

	private void TestMapRendererTileSetCacheReset()
	{
		// S-049/S-058: verify ClearTileSetCaches does not throw and executes cleanly
		MapRenderer.ClearTileSetCaches();
		MapRenderer.ClearTileSetCaches();
	}
}
