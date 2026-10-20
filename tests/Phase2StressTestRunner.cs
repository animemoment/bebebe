using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Core.WorldClimate;
using Game.Core.WorldStreaming;
using Game.Simulation;

namespace Game.Tests;

public partial class Phase2StressTestRunner : Node
{
	public string RunAllTests()
	{
		try
		{
			TestClimateDomainUncoupling();
			TestJobSpatialIndexUncoupling();
			TestConstructionManagerStages();
			TestFacadeEventLeakPrevention();
			return ""; // Empty string indicates all assertions passed
		}
		catch (Exception ex)
		{
			return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
		}
	}

	private void TestClimateDomainUncoupling()
	{
		ulong seed = 0xDEADBEEFCAFE1234UL;
		// 1. Macro planetary climate (20 parameters)
		WorldMacroClimateParams macro = WorldClimateProvider.ParamsFor(seed);
		if (macro.SeaLevel == 0 && macro.LandFraction == 0)
			throw new InvalidOperationException("Macro climate returned uninitialized zeroes.");

		// 2. Sample local climate (18 parameters)
		Game.Core.WorldStreaming.WorldClimateParams local = WorldClimateGenerator.SampleRegion(seed, 1, new RegionKey(10, 20), in macro);
		if (local.ElevationQ16 == 0 && local.TemperatureQ16 == 0 && local.PrecipitationQ16 == 0)
			throw new InvalidOperationException("Local climate returned uninitialized zeroes.");

		// Verify bilinear blending works with macro params
		Game.Core.WorldStreaming.WorldClimateParams blended = WorldClimateGenerator.SampleBlendedCell(seed, 1, 1000, 2000);
		if (blended.ElevationQ16 == 0 && blended.TemperatureQ16 == 0 && blended.PrecipitationQ16 == 0)
			throw new InvalidOperationException("SampleBlendedCell failed.");
	}

	private void TestJobSpatialIndexUncoupling()
	{
		if (JobSpatialPyramid.ChunkGridDim != 32)
			throw new InvalidOperationException($"JobSpatialPyramid.ChunkGridDim is {JobSpatialPyramid.ChunkGridDim}, expected 32");

		var index = JobDispatcher.Instance.JobIndex;
		if (index == null)
			throw new InvalidOperationException("JobDispatcher.Instance.JobIndex is null");

		int chunkCount = JobSpatialPyramid.ChunkCount;
		if (chunkCount != 1024)
			throw new InvalidOperationException($"ChunkCount is {chunkCount}, expected 1024");
	}

	private void TestConstructionManagerStages()
	{
		var cm = ConstructionManager.Instance;
		// Outside any construction site, all jobs are allowed
		if (!cm.IsStageAllowed(999, 999, JobTypeId.BlueprintDelivery))
			throw new InvalidOperationException("BlueprintDelivery should be allowed outside sites");
		if (!cm.IsStageAllowed(999, 999, JobTypeId.Construction))
			throw new InvalidOperationException("Construction should be allowed outside sites");
		if (!cm.IsStageAllowed(999, 999, JobTypeId.TreeChopping))
			throw new InvalidOperationException("TreeChopping should be allowed outside sites");
	}

	private void TestFacadeEventLeakPrevention()
	{
		var zm = ZoneManager.Instance;
		zm.ClearAll();

		// Create a test farm zone
		var farmTiles = new List<(int X, int Y)> { (50, 50), (50, 51) };
		zm.CreateFarmZones(farmTiles);
		if (!zm.TryGetZoneAt(50, 50, out var createdFarm))
			throw new InvalidOperationException("Failed to create test farm zone");

		int farmCallCount = 0;
		Action<FarmZone> farmHandler = z => farmCallCount++;

		// Subscribe and immediately unsubscribe 100 times to verify removal logic
		for (int i = 0; i < 100; i++)
		{
			FarmZoneManager.Instance.OnZoneSelected += farmHandler;
			FarmZoneManager.Instance.OnZoneSelected -= farmHandler;
		}

		// Now select the zone — handler must NOT be called since it was unsubscribed
		zm.SelectZoneAt(50, 50);
		if (farmCallCount != 0)
			throw new InvalidOperationException($"FarmZoneManager leaked event handlers! Called {farmCallCount} times after unsubscribe.");

		// Work zone facade leak test
		zm.DeselectZone();
		zm.CreateWorkZoneAt(60, 60, 0x02, 10, 5, out var workZone, out string failReason);
		if (workZone == null)
			throw new InvalidOperationException($"Failed to create work zone: {failReason}");

		int workCallCount = 0;
		Action<WorkZone> workHandler = z => workCallCount++;

		for (int i = 0; i < 100; i++)
		{
			WorkZoneManager.Instance.OnZoneSelected += workHandler;
			WorkZoneManager.Instance.OnZoneSelected -= workHandler;
		}

		zm.SelectZoneAt(60, 60);
		if (workCallCount != 0)
			throw new InvalidOperationException($"WorkZoneManager leaked event handlers! Called {workCallCount} times after unsubscribe.");

		// Cleanup
		zm.DeselectZone();
		zm.ClearAll();
	}
}
