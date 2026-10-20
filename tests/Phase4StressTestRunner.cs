using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Jobs;
using Game.UI;
using Game.UI.Controllers;

namespace Game.Tests;

public partial class Phase4StressTestRunner : Node
{
	public string RunAllTests()
	{
		try
		{
			TestCanAgentExecuteFastObstaclePath();
			TestPushAgentAwayFromDecoupling();
			TestHUDControllerSetupIdempotency();
			TestColonyResetLifecycle();
			return ""; // Empty string indicates all assertions passed
		}
		catch (Exception ex)
		{
			return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
		}
	}

	private void TestCanAgentExecuteFastObstaclePath()
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

		var cm = ConstructionManager.Instance;
		var gm = GroundItemManager.Instance;
		var sm = StockpileManager.Instance;
		cm.ClearAll();
		gm.ClearAll();
		sm.ClearAll();

		var mapData = new MapData(w, h);
		cm.StartSite(10, 10, StructureRegistry.WoodWall, mapData);
		if (cm.TryGetSite(10, 10, out var site))
		{
			site.Stage = ConstructionStage.Supplying;
		}

		gm.SpawnItems(2, 2, ItemId.Log, 20);

		var handler = new BlueprintDeliveryJobHandler();
		var pool = new AgentDataPool(2);
		var job = new JobData
		{
			Id = 1,
			TypeId = JobTypeId.BlueprintDelivery,
			TargetX = 10,
			TargetY = 10,
			TargetItemCount = 5,
			CurrentDeliveredCount = 0
		};

		// 1. Clear tile: can execute
		if (!handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be true for clear grass tile.");

		// 2. Tree obstacle on tile: cannot execute
		ctx.TreeOnGrass[10, 10] = true;
		if (handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be false when TreeOnGrass is true.");

		// Clear tree
		ctx.TreeOnGrass[10, 10] = false;
		if (!handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be true again after tree removed.");

		// 3. Stone obstacle on tile: cannot execute
		ctx.StoneOnGrass[10, 10] = true;
		if (handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be false when StoneOnGrass is true.");

		// Clear stone
		ctx.StoneOnGrass[10, 10] = false;
		if (!handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be true again after stone removed.");

		// 4. Ground not grass (e.g. Mountain): cannot execute
		ctx.Ground[10, 10] = TileType.Mountain;
		if (handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be false when Ground is Mountain.");

		ctx.Ground[10, 10] = TileType.Grass;
		if (!handler.CanAgentExecute(0, in job, pool, ctx))
			throw new InvalidOperationException("CanAgentExecute should be true again after ground restored to Grass.");

		cm.ClearAll();
		sm.ClearAll();
	}

	private void TestPushAgentAwayFromDecoupling()
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

		var pool = new AgentDataPool(2);
		pool.States[0] = AgentState.Idle;

		// PushAwayNeighbors are: (0, 1), (0, -1), (1, 0), (-1, 0)
		solidWalls[10, 11] = true; // (0, 1)
		solidWalls[10, 9] = true;  // (0, -1)
		solidWalls[9, 10] = true;  // (-1, 0)
		// (11, 10) remains free

		movement.PushAgentAwayFrom(0, 10, 10, pool, ctx);

		if (pool.States[0] != AgentState.Evacuating)
			throw new InvalidOperationException($"Agent state should be Evacuating, got {pool.States[0]}.");

		float expectedX = (11 << 6) + 32f;
		float expectedY = (10 << 6) + 32f;
		if (Math.Abs(pool.TargetPositionX[0] - expectedX) > 0.01f || Math.Abs(pool.TargetPositionY[0] - expectedY) > 0.01f)
			throw new InvalidOperationException($"Agent target pos should be ({expectedX}, {expectedY}), got ({pool.TargetPositionX[0]}, {pool.TargetPositionY[0]}).");
	}

	private void TestHUDControllerSetupIdempotency()
	{
		var hud = new HUDController();
		var pim = new PlayerInteractionManager();

		int resetCount = 0;
		pim.OnToolReset += () => resetCount++;

		// Multiple setups must not duplicate subscriptions
		hud.Setup(pim, null, 100);
		hud.Setup(pim, null, 100);
		hud.Setup(pim, null, 100);

		pim.ResetToDefault();
		if (resetCount != 1)
			throw new InvalidOperationException($"Expected resetCount == 1, got {resetCount}");

		hud._ExitTree();
		hud.Free();
		pim.Free();
	}

	private void TestColonyResetLifecycle()
	{
		var mainType = typeof(Game.Main.Main);
		var resetMethod = mainType.GetMethod("ResetColonyState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
		if (resetMethod == null)
			throw new InvalidOperationException("Main.ResetColonyState static method was not found.");

		// Setup some state
		ConstructionManager.Instance.StartSite(5, 5, StructureRegistry.WoodWall, new MapData(16, 16));
		GroundItemManager.Instance.SpawnItems(5, 5, ItemId.Log, 2);

		// Invoke reset
		resetMethod.Invoke(null, null);

		// Verify cleared
		if (ConstructionManager.Instance.TryGetSite(5, 5, out _))
			throw new InvalidOperationException("ConstructionManager was not cleared by ResetColonyState.");
		if (GroundItemManager.Instance.TotalGroundPilesCount > 0)
			throw new InvalidOperationException("GroundItemManager was not cleared by ResetColonyState.");
	}
}
