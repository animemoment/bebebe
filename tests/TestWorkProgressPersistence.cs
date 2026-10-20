using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Simulation.Jobs;

namespace Game.Tests;

/// <summary>
/// P7б: прогресс работ переживает прерывание и переезжает из агента в клетку.
/// Приёмка (а): прерывание рубки агентом А → агент Б продолжает с fraction, не с нуля.
/// Плюс: Mining/Farming зеркально; Construction OnCancel НЕ обнуляет site.WorkProgress;
/// Complete/Unmark/ClearAll чистят per-cell store.
/// </summary>
public partial class TestWorkProgressPersistence : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[TEST P7b] Прогресс работ переживает прерывание (per-cell store)");
        GD.Print("==================================================");
        try
        {
            EnsureHandlers();
            TestTreeChopInterruptHandover();
            TestMiningInterruptHandover();
            TestTillInterruptHandover();
            TestConstructionResumeAndNoZeroingOnCancel();
            TestCompleteAndUnmarkClearStore();
            GD.Print("[TEST P7b] ALL PASSED (Code 0)");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TEST P7b FAILED] {ex}");
            return false;
        }
    }

    private static void EnsureHandlers()
    {
        // Идемпотентно: AgentSimulationThread.Start регистрирует те же типы.
        JobRegistry.Register(new TreeChoppingJobHandler());
        JobRegistry.Register(new MiningJobHandler());
        JobRegistry.Register(new ConstructionJobHandler());
        JobRegistry.Register(new FarmingJobHandler());
    }

    private static (SimulationContext ctx, AgentDataPool pool) MakeWorld()
    {
        const int w = 32, h = 32;
        var ground = new TileType[w, h];
        var trees = new bool[w, h];
        var stones = new bool[w, h];
        var walls = new bool[w, h];
        var walkable = new List<(int X, int Y)>(w * h);
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                ground[x, y] = TileType.Grass;
                walkable.Add((x, y));
            }
        }

        var grid = new AgentSpatialGrid(w, h);
        var movement = new AgentMovementService();
        var ctx = new SimulationContext(ground, null, trees, walls, walkable, grid, movement, new Random(1234), stoneOnGrass: stones);
        var pool = new AgentDataPool(4);
        return (ctx, pool);
    }

    private static void AssertTrue(bool condition, string what)
    {
        if (!condition)
            throw new Exception($"[P7b] {what}");
    }

    private static void TestTreeChopInterruptHandover()
    {
        TreeJobManager.Instance.ClearAll();
        var (ctx, pool) = MakeWorld();
        const int tx = 5, ty = 5;
        ctx.TreeOnGrass[tx, ty] = true;

        var job = new JobData
        {
            Id = 9001,
            TypeId = JobTypeId.TreeChopping,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.TreeChopping,
            TargetX = tx,
            TargetY = ty,
            StandX = tx,
            StandY = ty - 1,
            MaxWorkers = 1,
            IsActive = true
        };
        JobRegistry.TryGetHandler(JobTypeId.TreeChopping, out var handler);

        // Агент А начал рубку, поработал ~40% и был прерван (нужды/stuck/отмена).
        handler.OnStart(0, job, pool, ctx);
        pool.States[0] = AgentState.Working;
        float workedSec = WorldTime.SecondsPerHour * 2.4f;
        pool.WorkProgress[0] = workedSec;
        handler.OnCancel(0, pool, ctx);

        float fraction = TreeJobManager.Instance.ChopProgress.GetFraction(tx, ty);
        AssertTrue(fraction > 0.35f && fraction < 0.45f, $"OnCancel сохранил долю в клетку, fraction={fraction}");

        // Агент Б берёт ту же работу: продолжает с доли, а НЕ с нуля.
        handler.OnStart(1, job, pool, ctx);
        float resumed = pool.WorkProgress[1];
        AssertTrue(Math.Abs(resumed - workedSec) < 0.5f, $"агент Б продолжает с {resumed:F2}с (ожидалось ~{workedSec:F2}с)");
        AssertTrue(resumed > 0f, "прогресс НЕ с нуля");
        GD.Print($"[TEST P7b] Tree: interrupt at {fraction:P0} → agent B resumes at {resumed:F0} game-sec (не с нуля) OK");
    }

    private static void TestMiningInterruptHandover()
    {
        StoneJobManager.Instance.ClearAll();
        var (ctx, pool) = MakeWorld();
        const int sx = 6, sy = 5;
        ctx.StoneOnGrass[sx, sy] = true;

        var job = new JobData
        {
            Id = 9002,
            TypeId = JobTypeId.Mining,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.TreeChopping,
            TargetX = sx,
            TargetY = sy,
            StandX = sx,
            StandY = sy - 1,
            MaxWorkers = 1,
            IsActive = true
        };
        JobRegistry.TryGetHandler(JobTypeId.Mining, out var handler);

        handler.OnStart(0, job, pool, ctx);
        pool.States[0] = AgentState.Working;
        float workedSec = WorldTime.SecondsPerHour * 1.6f;
        pool.WorkProgress[0] = workedSec;
        handler.OnCancel(0, pool, ctx);

        float fraction = StoneJobManager.Instance.MineProgress.GetFraction(sx, sy);
        AssertTrue(fraction > 0.3f && fraction < 0.5f, $"Mine OnCancel сохранил долю, fraction={fraction}");

        handler.OnStart(1, job, pool, ctx);
        AssertTrue(Math.Abs(pool.WorkProgress[1] - workedSec) < 0.5f,
            $"Mining: агент Б продолжает с {pool.WorkProgress[1]:F2}с, а не с нуля");
        GD.Print($"[TEST P7b] Mining: interrupt at {fraction:P0} → agent B resumes OK");
    }

    private static void TestTillInterruptHandover()
    {
        FarmJobManager.Instance.ClearAll();
        var (ctx, pool) = MakeWorld();
        const int px = 7, py = 5;

        var job = new JobData
        {
            Id = 9003,
            TypeId = JobTypeId.Farming,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.Farming,
            TargetX = px,
            TargetY = py,
            StandX = px,
            StandY = py - 1,
            MaxWorkers = 1,
            IsActive = true
        };
        JobRegistry.TryGetHandler(JobTypeId.Farming, out var handler);

        handler.OnStart(0, job, pool, ctx);
        pool.States[0] = AgentState.Working;
        float workedSec = FarmingJobHandler.TillDuration * 0.4f;
        pool.WorkProgress[0] = workedSec;
        handler.OnCancel(0, pool, ctx);

        float fraction = FarmJobManager.Instance.TillProgress.GetFraction(px, py);
        AssertTrue(fraction > 0.3f && fraction < 0.5f, $"Till OnCancel сохранил долю, fraction={fraction}");

        handler.OnStart(1, job, pool, ctx);
        AssertTrue(Math.Abs(pool.WorkProgress[1] - workedSec) < 0.5f,
            $"Farming: агент Б продолжает с {pool.WorkProgress[1]:F2}с, а не с нуля");
        GD.Print($"[TEST P7b] Farming: interrupt at {fraction:P0} → agent B resumes OK");
    }

    private static void TestConstructionResumeAndNoZeroingOnCancel()
    {
        ConstructionManager.Instance.ClearAll();
        var (ctx, pool) = MakeWorld();
        const int sx = 9, sy = 5;

        var def = StructureRegistry.WoodWall;
        AssertTrue(ConstructionManager.Instance.StartSite(sx, sy, def, null), "старт стройплощадки");
        ConstructionManager.Instance.ReportWorkProgress(sx, sy, 0.5f);

        var job = new JobData
        {
            Id = 9004,
            TypeId = JobTypeId.Construction,
            ExecutionType = JobExecutionType.Stationary,
            PriorityTier = JobPriorityTier.Construction,
            TargetX = sx,
            TargetY = sy,
            StandX = sx,
            StandY = sy - 1,
            MaxWorkers = 1,
            IsActive = true
        };
        JobRegistry.TryGetHandler(JobTypeId.Construction, out var handler);

        // Новый строитель начинает с накопленных площадкой 50%.
        handler.OnStart(0, job, pool, ctx);
        float expected = BuildConfig.BuildDurationSec * 0.5f;
        AssertTrue(Math.Abs(pool.WorkProgress[0] - expected) < 1f,
            $"OnStart восстановил WorkProgress из site ({pool.WorkProgress[0]:F1}с, ожидалось {expected:F1}с)");

        // Прерывание НЕ обнуляет site.WorkProgress (старый код писал 0f).
        handler.OnCancel(0, pool, ctx);
        ConstructionManager.Instance.TryGetSite(sx, sy, out var site);
        AssertTrue(Math.Abs(site.WorkProgress - 0.5f) < 1e-4,
            $"OnCancel сохранил site.WorkProgress={site.WorkProgress}");

        // Второй строитель продолжает с той же доли.
        handler.OnStart(1, job, pool, ctx);
        AssertTrue(Math.Abs(pool.WorkProgress[1] - expected) < 1f, "второй строитель продолжает с 50%");
        GD.Print("[TEST P7b] Construction: site 50% пережил OnCancel, resume у агента Б OK");

        ConstructionManager.Instance.ClearAll();
    }

    private static void TestCompleteAndUnmarkClearStore()
    {
        TreeJobManager.Instance.ClearAll();
        StoneJobManager.Instance.ClearAll();
        FarmJobManager.Instance.ClearAll();

        // Complete чистит прогресс клетки.
        TreeJobManager.Instance.ChopProgress.SaveFraction(20, 20, 0.4f);
        TreeJobManager.Instance.CompleteTree(20, 20);
        AssertTrue(TreeJobManager.Instance.ChopProgress.GetFraction(20, 20) == 0f, "CompleteTree чистит store");

        // Unmark чистит прогресс клетки.
        TreeJobManager.Instance.MarkTree(21, 20);
        TreeJobManager.Instance.ChopProgress.SaveFraction(21, 20, 0.2f);
        TreeJobManager.Instance.UnmarkTree(21, 20);
        AssertTrue(TreeJobManager.Instance.ChopProgress.GetFraction(21, 20) == 0f, "UnmarkTree чистит store");

        StoneJobManager.Instance.MineProgress.SaveFraction(22, 20, 0.3f);
        StoneJobManager.Instance.CompleteStone(22, 20);
        AssertTrue(StoneJobManager.Instance.MineProgress.GetFraction(22, 20) == 0f, "CompleteStone чистит store");

        FarmJobManager.Instance.TillProgress.SaveFraction(23, 20, 0.3f);
        FarmJobManager.Instance.UnmarkPlot(23, 20);
        AssertTrue(FarmJobManager.Instance.TillProgress.GetFraction(23, 20) == 0f, "UnmarkPlot чистит store");

        // ClearAll чистит всё.
        TreeJobManager.Instance.ChopProgress.SaveFraction(24, 20, 0.9f);
        TreeJobManager.Instance.ClearAll();
        AssertTrue(TreeJobManager.Instance.ChopProgress.Count == 0, "ClearAll чистит весь store");

        GD.Print("[TEST P7b] Complete/Unmark/ClearAll чистят per-cell store OK");
    }
}
