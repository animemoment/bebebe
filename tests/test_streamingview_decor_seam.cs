using System;
using System.Diagnostics;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.UI.Streaming;

namespace Game.Tests;

// Имя класса обязано совпадать с базовым именем файла (lowercase — чтобы сохранить
// предписанный префикс test_streamingview_): иначе Godot-генератор не эмитит
// ScriptPath для res://tests/test_streamingview_decor_seam.cs и CSharpScript.new()
// из GDScript-раннера падает «Nonexistent function 'new'».

/// <summary>
/// §117/F4 (@developer dev-streaming-view): паритет прореживания декора
/// StreamingWorldView с островом (UnifiedWorldGenerator.GenerateColonyWindow).
///
/// 1) формула IslandDecorPass == формула GenerateColonyWindow (независимая копия
///    в тесте — защита от дрейфа при будущих правках вида);
/// 2) E2E-шов: GenerateColonyWindow (остров 512²) vs GenerateChunk + IslandDecorPass
///    (стриминг) на полосах вплотную к границе x=512 + автоматически выбранная
///    «лесная» колонка чанков — плотность деревьев/камней обязана совпадать клетка
///    в клетку. Плодородие — 16×16-сетка как у рендера (25 семплов на чанк), плюс
///    подвыборка живым статическим SoilFertilityAtCell;
/// 3) отрицательные мировые координаты не теряют декор (регресс-защита евклидова
///    остатка %100: знаковый % на отрицательных XOR выжигал бы лес на пол-планеты);
/// 4) замер стоимости SampleBlendedCell — 25 семплов на чанк в CellSoilFertilityQ16
///    должны оставаться дешёвыми для главного потока.
/// </summary>
public partial class test_streamingview_decor_seam : RefCounted
{
	private const ulong Seed = 0x5EED00000BADC0DEul;
	private const uint Version = 3;

	public bool Run()
	{
		GD.Print("==================================================");
		GD.Print("[TEST] StreamingWorldView decor seam parity (§117/F4)");
		GD.Print("==================================================");
		try
		{
			// Как Main.OnRegionSelected: ищем пригодный регион и ставим WorldOrigin —
			// иначе остров у (0,0) может оказаться безлесным (океан/пустыня) и тест
			// дерева будет слепым.
			RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(Seed, Version);
			UnifiedWorldGenerator.SetWorldOrigin(
				spawn.X * WorldRegions.RegionSize, spawn.Y * WorldRegions.RegionSize);
			GD.Print($"[TEST] WorldOrigin = region ({spawn.X},{spawn.Y})");
			TestFormulaParityWithIslandCopy();
			TestBoundaryParityWithGenerateColonyWindow();
			TestNegativeCoordinatesKeepDecor();
			MeasureFertilitySampleCost();
			GD.Print("==================================================");
			GD.Print("[TEST] ALL STREAMING DECOR SEAM TESTS PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[TEST FAILED] {ex}");
			return false;
		}
	}

	/// <summary>Независимая побитовая копия формулы GenerateColonyWindow (строки 544-562).</summary>
	private static (bool Tree, bool Stone) IslandReferenceFormula(long wx, long wy, in GeneratedCell cell, ushort fertility)
	{
		bool isGrass = cell.Terrain == BaseTerrainKind.Grass;
		bool inForestGrove = isGrass && cell.ForestQ16 >= WorldDecor.ForestMinQ16;
		int groveThreshold = 55 + (int)fertility * 25 / 65535;
		bool treePass = inForestGrove && (((wx ^ (wy * 3)) & 3) != 0)
			&& (((wx * 17 ^ wy * 31) % 100) < groveThreshold);
		bool hasTree = inForestGrove && treePass;
		bool inStoneDeposit = isGrass && !hasTree && cell.StoneQ16 >= WorldDecor.StoneMinQ16;
		int stoneThreshold = 45 + (int)cell.StoneQ16 * 20 / 65535;
		bool stonePass = inStoneDeposit && (((wx * 7 ^ wy * 13) % 100) < stoneThreshold);
		return (hasTree, inStoneDeposit && stonePass);
	}

	private void TestFormulaParityWithIslandCopy()
	{
		GD.Print("[TEST 1] IslandDecorPass == island formula (independent copy)...");
		var rng = new Random(12345);
		// Синтетические клетки с полным разбросом порогов; домен острова — wx,wy >= 0
		// (там формулы обязаны совпадать побитово, включая поведение остатка).
		for (int i = 0; i < 6000; i++)
		{
			var cell = new GeneratedCell(
				(BaseTerrainKind)(i % 3 == 0 ? 1 : 0),
				0,
				0,
				(ushort)rng.Next(25000, 40000), // вокруг порога леса
				(ushort)rng.Next(30000, 45000), // вокруг порога камня
				0);
			ushort fertility = (ushort)rng.Next(65536);
			long wx = rng.Next(0, 2000);
			long wy = rng.Next(0, 2000);
			var mine = StreamingWorldView.IslandDecorPass(wx, wy, in cell, fertility);
			var reference = IslandReferenceFormula(wx, wy, in cell, fertility);
			if (mine != reference)
				throw new Exception(
					$"Formula drift at wx={wx},wy={wy},forest={cell.ForestQ16},stone={cell.StoneQ16},fert={fertility}: mine={mine}, island={reference}");
		}
		GD.Print("[TEST 1] OK: 6000 synthetic cells - bitwise identical.");
	}

	private void TestBoundaryParityWithGenerateColonyWindow()
	{
		GD.Print("[TEST 2] E2E: island 512^2 vs streaming at x=512 boundary...");
		MapData map = UnifiedWorldGenerator.GenerateColonyWindow(Seed, Version, 0, 0, 512, 512);

		// Автовыбор «лесной» колонки чанков: E2E-проверка дерева не должна быть слепой.
		int bestTreeX = 0, bestTreeCount = -1;
		for (int chunkX = 0; chunkX < 8; chunkX++)
		{
			int count = 0;
			for (int y = 0; y < 512; y++)
				for (int x = chunkX * 64; x < chunkX * 64 + 64; x++)
					if (map.TreeOnGrass[x, y])
						count++;
			if (count > bestTreeCount)
			{
				bestTreeCount = count;
				bestTreeX = chunkX;
			}
		}
		if (bestTreeCount <= 0)
			throw new Exception("Island has no trees at this seed - tree side of the test is blind (change seed).");

		// Кромка (чанки X=7: клетки 448..512) + подходы (X=6) + лесная колонка (2 чанка).
		// Стартовая поляна (центр 256,256, r=14) в эти полосы не попадает: X=7/X=6/X=1
		// далеки от центра; из лесной колонки исключаем клетки внутри поляны.
		ChunkKey[] keys =
		{
			new(7, 0), new(7, 1), new(6, 0), new(6, 1),
			new(bestTreeX, 0), new(bestTreeX, 1)
		};
		int trees = 0, stones = 0, mismatches = 0, cells = 0, liveChecks = 0;
		foreach (ChunkKey key in keys)
			mismatches += CompareChunkWithIsland(map, key, ref trees, ref stones, ref cells, ref liveChecks);
		if (mismatches > 0)
			throw new Exception($"Seam mismatch: {mismatches}/{cells} cells near x=512 differ from the island.");
		if (trees == 0)
			throw new Exception("No trees compared - test is blind on the tree side.");
		GD.Print($"[TEST 2] OK: {cells} cells, trees {trees}, stones {stones}, live fertility " +
			$"checks {liveChecks} (forest column X={bestTreeX}, {bestTreeCount} island trees) - 0 mismatches.");
	}

	/// <summary>Сравнение чанка: клетки GenerateChunk + IslandDecorPass против MapData острова.</summary>
	private int CompareChunkWithIsland(MapData map, ChunkKey key, ref int trees, ref int stones, ref int cells, ref int liveChecks)
	{
		WorldChunk chunk = UnifiedWorldGenerator.GenerateChunk(Seed, Version, key);
		ReadOnlySpan<GeneratedCell> span = chunk.Cells.Span;
		long originX = key.X * (long)WorldCoordinates.ChunkSize;
		long originY = key.Y * (long)WorldCoordinates.ChunkSize;

		// Плодородие сеткой 5×5 (якоря = якорям рендера: WorldOrigin+чанк+16·g),
		// интерполяция — копия LerpQ16-билинейки; каждая 8-я клетка зоны леса
		// подпроверяется живым статическим SoilFertilityAtCell (та же точка входа,
		// те же числа — ловит расхождение кэш-пути и одноразового).
		ushort[] grid = BuildTestFertilityGrid(key);
		int mismatches = 0;
		for (int local = 0; local < span.Length; local++)
		{
			int lx = local % WorldCoordinates.ChunkSize;
			int ly = local / WorldCoordinates.ChunkSize;
			long x = originX + lx;
			long y = originY + ly;
			if (x >= 512 || y >= 512)
				continue;
			// Стартовая поляна острова: генератор вычистил там декор после формулы —
			// такие клетки сравнивать нельзя (это не шов, а пост-обработка центра).
			long dx = x - 256, dy = y - 256;
			if (dx * dx + dy * dy <= 14L * 14L)
				continue;
			cells++;
			ushort fertility = 0;
			if (span[local].ForestQ16 >= WorldDecor.ForestMinQ16)
			{
				fertility = InterpolateTestFertility(grid, lx, ly);
				if ((local & 7) == 0)
				{
					ushort live = StreamingWorldView.SoilFertilityAtCell(Seed, Version, x, y);
					if (live != fertility)
						throw new Exception($"Fertility mismatch at ({x},{y}): grid-copy={fertility}, live={live}");
					liveChecks++;
				}
			}
			var decor = StreamingWorldView.IslandDecorPass(
				UnifiedWorldGenerator.WorldOriginX + x,
				UnifiedWorldGenerator.WorldOriginY + y,
				in span[local], fertility);
			bool islandTree = map.TreeOnGrass[x, y];
			bool islandStone = map.StoneOnGrass[x, y];
			if (decor.HasTree != islandTree || decor.HasStone != islandStone)
			{
				if (mismatches < 5)
					GD.PrintErr($"[SEAM] ({x},{y}) world({UnifiedWorldGenerator.WorldOriginX + x},{UnifiedWorldGenerator.WorldOriginY + y}): " +
						$"stream tree={decor.HasTree} stone={decor.HasStone}; " +
						$"island tree={islandTree} stone={islandStone}; forest={span[local].ForestQ16} " +
						$"stoneQ16={span[local].StoneQ16} fert={fertility}");
				mismatches++;
				continue;
			}
			if (decor.HasTree)
				trees++;
			if (decor.HasStone)
				stones++;
		}
		return mismatches;
	}

	private const int GridStep = 16;
	private const int GridSide = WorldCoordinates.ChunkSize / GridStep + 1;

	/// <summary>25 якорей семплами SampleBlendedCell — копия BuildFertilityGrid вида.</summary>
	private ushort[] BuildTestFertilityGrid(ChunkKey key)
	{
		var grid = new ushort[GridSide * GridSide];
		long originX = UnifiedWorldGenerator.WorldOriginX + key.X * (long)WorldCoordinates.ChunkSize;
		long originY = UnifiedWorldGenerator.WorldOriginY + key.Y * (long)WorldCoordinates.ChunkSize;
		for (int gy = 0; gy < GridSide; gy++)
			for (int gx = 0; gx < GridSide; gx++)
				grid[gy * GridSide + gx] = WorldClimateGenerator.SampleBlendedCell(
					Seed, Version, originX + gx * GridStep, originY + gy * GridStep).SoilFertilityQ16;
		return grid;
	}

	/// <summary>Билинейная интерполяция из сетки — копия CellSoilFertilityQ16/LerpQ16 вида.</summary>
	private static ushort InterpolateTestFertility(ushort[] grid, int lx, int ly)
	{
		int row = (ly / GridStep) * GridSide;
		int col = lx / GridStep;
		uint u = (uint)((lx % GridStep) * 65535 / GridStep);
		uint v = (uint)((ly % GridStep) * 65535 / GridStep);
		ushort top = LerpQ16Copy(grid[row + col], grid[row + col + 1], u);
		ushort bottom = LerpQ16Copy(grid[row + GridSide + col], grid[row + GridSide + col + 1], u);
		return LerpQ16Copy(top, bottom, v);
	}

	private static ushort LerpQ16Copy(ushort a, ushort b, uint t)
	{
		uint clampedT = Math.Min(t, 65535);
		long value = (long)a * (65535 - clampedT) + (long)b * clampedT + 32768;
		return (ushort)Math.Clamp(value / 65535, 0, 65535);
	}

	private void TestNegativeCoordinatesKeepDecor()
	{
		GD.Print("[TEST 3] Negative coords: decor must not disappear (euclidean %100)...");
		var forestCell = new GeneratedCell(BaseTerrainKind.Grass, 20000, 30000, 60000, 0, 30000);
		var stoneCell = new GeneratedCell(BaseTerrainKind.Grass, 20000, 30000, 0, 60000, 30000);
		int trees = 0, stones = 0, total = 0;
		for (long wx = -2048; wx < -1024; wx += 7)
		{
			for (long wy = 64; wy < 1088; wy += 7)
			{
				if (StreamingWorldView.IslandDecorPass(wx, wy, in forestCell, 40000).HasTree)
					trees++;
				if (StreamingWorldView.IslandDecorPass(wx, wy, in stoneCell, 0).HasStone)
					stones++;
				total++;
			}
		}
		// Ожидаемо: дерево ≈ 0.75 × (55 + 40000·25/65535 ≈ 70%) ≈ 52%, камень ≈ 63%.
		// Знаковый % дал бы здесь ровно 0 — этот тест ловит регресс к «лысой» половине планеты.
		if (trees < total / 10)
			throw new Exception($"Trees vanished in negative coords: {trees}/{total} (expected ~50%).");
		if (stones < total / 10)
			throw new Exception($"Stones vanished in negative coords: {stones}/{total} (expected ~60%).");
		GD.Print($"[TEST 3] OK: trees {trees}/{total}, stones {stones}/{total} - decor alive.");
	}

	/// <summary>
	/// Замер для бюджета главного потока: SoilFertilityAtCell (4 SampleBlendedCell)
	/// на «горячем» чанке (кэш регионов разогрет) и сетка 5×5 «нового» чанка
	/// (столько живых семплов BuildFertilityGrid делает на чанк у рендера).
	/// </summary>
	private void MeasureFertilitySampleCost()
	{
		GD.Print("[TEST 4] SampleBlendedCell cost measurement (main thread)...");
		var sw = Stopwatch.StartNew();
		StreamingWorldView.SoilFertilityAtCell(Seed, Version, 500, 500); // прогрев кэша
		sw.Restart();
		const int WarmCalls = 400;
		for (int i = 0; i < WarmCalls; i++)
			StreamingWorldView.SoilFertilityAtCell(Seed, Version, 480 + (i & 63), 480 + (i >> 6));
		sw.Stop();
		double warmPerCallUs = sw.Elapsed.TotalMilliseconds * 1000.0 / WarmCalls;

		sw.Restart();
		const int GridCalls = 125; // 5 чанков × 25 якорей — как 5 BuildFertilityGrid
		for (int i = 0; i < GridCalls; i++)
			StreamingWorldView.SoilFertilityAtCell(Seed, Version, 4096 + (i % 25) * 16, 4096 + (i / 25) * 16);
		sw.Stop();
		double perChunkGridMs = sw.Elapsed.TotalMilliseconds / 5.0;

		GD.Print($"[TEST 4] INFO: warm call ≈ {warmPerCallUs:F1} µs; render-side 5×5 grid per chunk " +
			$"≈ {perChunkGridMs:F2} ms — negligible vs the 4 ms paint-slice budget.");
	}
}
