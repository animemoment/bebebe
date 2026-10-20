using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Godot;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI.Streaming;

namespace Game.Tests;

/// <summary>
/// §118 фаза 2 (@destroyer-стиль): персистентность дельт при ВЫХОДЕ без выгрузки окна.
/// Аудит WorldDeltaApi: §6.4 — Shutdown без flush теряет дельты resident-чанков;
/// §6.5 — checkpoint времени никем не вызывается; §6.2 — нет перечисления дельт;
/// §6.7 — нет событий изменений. Проверяем всё:
/// 1) вид на врем. папке: грузим окно, ставим стену → события Add/Remove;
/// 2) FlushResidentDeltas(123) + manager.Checkpoint(123) + Shutdown БЕЗ выгрузки;
/// 3) новый вид на той же папке (без wipe) → стена на месте;
/// 4) новый координатор → ResumeState.GameTime == 123;
/// 5) EnumerateDeltas: фильтр по прямоугольнику и по kind.
/// </summary>
public partial class TestWorldStoreExit : RefCounted
{
	private const uint GenVersion = 3;
	private const ulong Seed = 0x5EED5EEDul;

	public bool Run()
	{
		GD.Print("==================================================");
		GD.Print("[STORE-EXIT] Starting world store exit-flush audit (§118 phase 2)");
		GD.Print("==================================================");
		try
		{
			UnifiedWorldGenerator.SetWorldOrigin(0, 0);

			TestFlushOnExitSurvives();
			TestEnumerateAndEvents();

			GD.Print("==================================================");
			GD.Print("[STORE-EXIT OK] ALL TESTS PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[STORE-EXIT FAILED] {ex.Message}\n{ex.StackTrace}");
			return false;
		}
	}

	// ---------------------------------------------------------------- helpers

	private static string NewTempDir()
		=> Path.Combine(OS.GetUserDataDir(), $"store_exit_{Guid.NewGuid():N}");

	private static void CleanupDir(string dir)
	{
		try
		{
			if (Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
		}
		catch
		{
			// мягкая уборка: хвосты I/O воркеров не должны валить тест
		}
	}

	private static StreamingWorldView CreateView(ulong seed, string dir, bool wipe)
	{
		var view = new StreamingWorldView
		{
			Name = "StoreExitView",
			SaveDirectory = dir,
			WipeStoreOnStart = wipe,
			WorldSeed = seed,
			GeneratorVersion = GenVersion,
			MaxConcurrency = 4,
			MinWindowChunks = 3,
			MaxWindowChunks = 6,
			MaxReadyChunksPerFrame = 16,
			MaxUnloadSchedulesPerFrame = 16,
		};
		view._Ready();
		return view;
	}

	private static bool Pump(StreamingWorldView view, WorldRect rect, Func<bool> done, int timeoutMs, string what)
	{
		var sw = Stopwatch.StartNew();
		while (!done())
		{
			view.Update(rect);
			view._Process(0.016);
			if (sw.ElapsedMilliseconds > timeoutMs)
			{
				GD.PrintErr($"[STORE-EXIT] Pump timeout ({timeoutMs} ms): {what}");
				return false;
			}
			Thread.Sleep(15);
		}
		return true;
	}

	private static void DestroyView(StreamingWorldView view)
	{
		try
		{
			view.Shutdown();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[STORE-EXIT] Shutdown exception: {ex.Message}");
		}
		view.Free();
	}

	// ---------------------------------------------------------------- tests

	/// <summary>«Поставил стену — вышел без выгрузки — зашёл»: дельта обязана выжить.</summary>
	private void TestFlushOnExitSurvives()
	{
		string dir = NewTempDir();
		try
		{
			var view = CreateView(Seed, dir, wipe: true);
			var rect = new WorldRect(0, 0, 128, 128);
			if (!Pump(view, rect,
				() => view.IsLoaded(new ChunkKey(0, 0)) && view.IsLoaded(new ChunkKey(1, 1)),
				30000, "window load"))
				throw new Exception("окно не загрузилось");

			long x = 5, y = 5;
			if (!view.AddBlock(WorldDeltaKind.Wall, x, y))
				throw new Exception("AddBlock вернул false — стена не поставлена");

			// Менеджер + чекпоинт времени (аудит §6.5).
			var coordinator = new WorldSaveCoordinator(dir, Seed, GenVersion,
				UnifiedWorldGenerator.FeatureSchemaVersion);
			var manager = new StreamingWorldManager(view, coordinator);
			manager.Open(0, 0);
			manager.Checkpoint(123.0);

			// Flush БЕЗ выгрузки окна — ровно как при выходе из игры.
			int saved = view.FlushResidentDeltas(123.0);
			if (saved < 1)
				throw new Exception($"FlushResidentDeltas сохранил {saved} чанков (ожидал >= 1)");

			DestroyView(view); // Shutdown БЕЗ выгрузки: дельты обязаны уже быть на диске

			// Новый вид на той же папке (БЕЗ wipe) — стена обязана вернуться.
			var view2 = CreateView(Seed, dir, wipe: false);
			if (!Pump(view2, rect,
				() => view2.IsLoaded(new ChunkKey(0, 0)) && view2.IsLoaded(new ChunkKey(1, 1)),
				30000, "re-open window load"))
				throw new Exception("повторное окно не загрузилось");
			if (!view2.HasBlock(WorldDeltaKind.Wall, x, y))
				throw new Exception("дельта стены НЕ пережила flush-on-exit (аудит §6.4)");
			DestroyView(view2);

			// Манифест: время чекпоинта обязано вернуться (аудит §6.5).
			var coordinator2 = new WorldSaveCoordinator(dir, Seed, GenVersion,
				UnifiedWorldGenerator.FeatureSchemaVersion);
			ResumeState resume = coordinator2.OpenOrCreate(0, 0);
			if (Math.Abs(resume.GameTime - 123.0) > 0.001)
				throw new Exception($"ResumeState.GameTime = {resume.GameTime} (ожидал 123.0)");

			GD.Print("[STORE-EXIT] flush-on-exit + checkpoint: OK");
		}
		finally
		{
			CleanupDir(dir);
		}
	}

	/// <summary>События OnBlock*/OnWall* и перечисление дельт по области/kind.</summary>
	private void TestEnumerateAndEvents()
	{
		string dir = NewTempDir();
		try
		{
			var view = CreateView(Seed, dir, wipe: true);
			var rect = new WorldRect(0, 0, 128, 128);
			if (!Pump(view, rect,
				() => view.IsLoaded(new ChunkKey(0, 0)) && view.IsLoaded(new ChunkKey(1, 1)),
				30000, "window load"))
				throw new Exception("окно не загрузилось");

			int added = 0, removed = 0, wallAdded = 0, wallRemoved = 0;
			byte lastKind = 0;
			view.OnBlockAdded += (k, cx, cy) => { added++; lastKind = k; };
			view.OnBlockRemoved += (k, cx, cy) => removed++;
			view.OnWallAdded += (cx, cy) => wallAdded++;
			view.OnWallRemoved += (cx, cy) => wallRemoved++;

			if (!view.AddBlock(WorldDeltaKind.Wall, 9, 9))
				throw new Exception("AddBlock(Wall 9,9) false");
			if (!view.AddBlock(WorldDeltaKind.CutTreePlan, 10, 10))
				throw new Exception("AddBlock(CutTreePlan 10,10) false");
			view.RemoveBlock(WorldDeltaKind.CutTreePlan, 10, 10);
			view.RemoveBlock(WorldDeltaKind.Wall, 9, 9);

			if (added != 2 || removed != 2 || wallAdded != 1 || wallRemoved != 1)
				throw new Exception(
					$"события: added={added} removed={removed} wallAdded={wallAdded} wallRemoved={wallRemoved}");
			if (lastKind != WorldDeltaKind.CutTreePlan)
				throw new Exception($"lastKind={lastKind} (ожидал CutTreePlan {WorldDeltaKind.CutTreePlan})");

			// EnumerateDeltas: фильтр по прямоугольнику и по kind.
			if (!view.AddBlock(WorldDeltaKind.Wall, 5, 5))
				throw new Exception("AddBlock(Wall 5,5) false");
			if (!view.AddBlock(WorldDeltaKind.Wall, 6, 5))
				throw new Exception("AddBlock(Wall 6,5) false");
			if (!view.AddBlock(WorldDeltaKind.Wall, 100, 100))
				throw new Exception("AddBlock(Wall 100,100) false");

			var walls = new List<(byte Kind, long CellX, long CellY)>();
			int wallCount = view.EnumerateDeltas(new WorldRect(0, 0, 63, 63), WorldDeltaKind.Wall, walls);
			if (wallCount != 2 || walls.Exists(r => r.Kind != WorldDeltaKind.Wall))
				throw new Exception($"EnumerateDeltas(Wall 0..63): count={wallCount}");
			if (!walls.Exists(r => r.CellX == 5 && r.CellY == 5) || !walls.Exists(r => r.CellX == 6 && r.CellY == 5))
				throw new Exception("EnumerateDeltas: клетки (5,5)/(6,5) не найдены");

			var all = new List<(byte Kind, long CellX, long CellY)>();
			int allCount = view.EnumerateDeltas(new WorldRect(0, 0, 127, 127), 0, all);
			if (allCount != 3)
				throw new Exception($"EnumerateDeltas(all 0..127): count={allCount} (ожидал 3 — без (9,9)/(10,10))");

			DestroyView(view);
			GD.Print("[STORE-EXIT] events + EnumerateDeltas: OK");
		}
		finally
		{
			CleanupDir(dir);
		}
	}
}
