using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core.WorldStreaming;

namespace Game.UI.Streaming;

/// <summary>
/// Мировая карта: чанки TileMapLayer + pre-baked генерация биомов.
/// Вся тяжёлая работа (SampleBlended+Pick — сотни hash'ей на клетку) выполняется
/// ОДИН РАЗ на старте в фоновых потоках и складывается в плоский массив
/// _bakedTiles (tileIndex на клетку). Рантайм: только SetCellsRect из памяти.
/// </summary>
public partial class WorldMapOverlay : Node2D
{
	private const int TileSizePx = 16;
	private const int GridWidth = 1000;
	private const int GridHeight = 2000;
	// Базовый чанк мелкого LOD: 64×64 клеток 16px (SetCellsRect батчем).
	private const int ChunkSize = 64;
	// Крупный LOD для дальних планов: coarse-чанк покрывает 8×8 базовых чанков,
	// т.е. 512×512 клеток. При зуме ниже CoarseZoomThreshold рисуем ТОЛЬКО их —
	// вместо ~1300 мелких чанков (2 млн клеток) получаем максимум 8×16=128 чанков
	// и ровно GridWidth×GridHeight клеток (по одной на клетку мира) независимо
	// от зума. Меш перестраивается в ~100 раз реже, draw-вызовы не растут.
	private const int CoarseChunkSize = ChunkSize * 8; // 512 клеток
	private const float CoarseZoomThreshold = 0.55f;   // zoom < этого → coarse-режим
	private const float CoarseSwitchHysteresis = 0.08f; // защита от флиппинга режимов
	private const float MinZoom = 0.12f;
	private const float MaxZoom = 4f;
	private const float ZoomFactor = 1.2f;
	private const float PanSpeed = 900f;

	// --- Pre-baked tiles: 1 байт на клетку (tileIndex 0..15), -1 = пусто. ---
	// 2 млн клеток = 2 МБ. Заполняется фоном (BakeAllAsync), до готовности клетки = -1.
	private sbyte[] _bakedTiles;
	private CancellationTokenSource _bakeCts;
	private volatile bool _bakeComplete;

	// --- Dirty-state: какие чанки уже лежат в TileMapLayer ---
	private readonly HashSet<Vector2I> _loadedChunks = new();
	// Гистерезис выгрузки: не переживаем чанки мгновенно при каждом движении
	// камеры (SetCellsRect/ClearCellsRect на 4096 клеток = до десятков мс за кадр).
	// Выгружаем только чанки, невидимые дольше UnloadDelay секунд.
	private const float UnloadDelay = 1.5f;
	private readonly Dictionary<Vector2I, float> _invisibleSince = new();
	private float _frameTime;
	private Camera2D _camera;
	private TileMapLayer _mapLayer;
	private CanvasLayer _uiLayer;
	private Button _closeBtn;
	private readonly HashSet<Vector2I> _visibleChunksThisFrame = new();
	private readonly HashSet<Vector2I> _pendingChunks = new();
	private ulong _seed;
	private uint _version;
	private bool _isDragging;

	// Пресчитанный atlas-coord lookup (16 элементов)
	private static readonly Vector2I[] AtlasCoordsCached = new Vector2I[16];
	// Переиспользуемые буферы для SetCellsRect: один на чанк любого уровня.
	// CoarseChunkSize*CoarseChunkSize не нужен — coarse-чанк режется на строки
	// по CoarseChunkSize клеток, поэтому буфер = ChunkSize*ChunkSize не покрывает
	// 512 ширину; берём размер по максимальному уровню, но с запасом по памяти
	// разумно: 512×512 int2 = 2 МБ одноразово, приемлемо для одного статичного буфера.
	private static readonly Vector2I[] ChunkCellBuf = new Vector2I[CoarseChunkSize * CoarseChunkSize];
	static WorldMapOverlay()
	{
		for (int i = 0; i < 16; i++)
			AtlasCoordsCached[i] = GetAtlasCoordsRaw(i);
	}

	public override void _Ready()
	{
		_camera = GetNode<Camera2D>("Camera2D");
		_mapLayer = GetNode<TileMapLayer>("TileMapLayer");
		_uiLayer = GetNode<CanvasLayer>("UI");
		_closeBtn = GetNode<Button>("UI/Control/BtnClose");

		_camera.Position = new Vector2(GridWidth * TileSizePx / 2f, GridHeight * TileSizePx / 2f);
		_camera.Zoom = new Vector2(0.4f, 0.4f);
		_camera.Enabled = false;
		Visible = false;
		_uiLayer.Visible = false;
		_closeBtn.Pressed += CloseMap;

		// Пиксель-арт атлас: nearest-фильтр убирает bilinear-размытие на зуме <1
		// и исключает mip-map выборки на гигантском тайловом меше (источник
		// GPU-просадок при сильном отдалении).
		_mapLayer.TextureFilter = (int)Godot.CanvasItem.TextureFilterEnum.Nearest;

		// Карта невидима — процесс выключен до открытия (нулевая стоимость в кадре).
		SetProcess(false);
	}

	public void Initialize(ulong seed, uint version)
	{
		_seed = seed;
		_version = version;
		StartBake();
	}

	public override void _ExitTree()
	{
		_bakeCts?.Cancel();
		_bakeCts?.Dispose();
		_bakeCts = null;
	}

	/// <summary>
	/// Фоновый pre-bake всей карты (GridWidth×GridHeight клеток) в _bakedTiles.
	/// Параллельно по полосам, батчами по 8 рядов: переиспользуем траекторию региона
	/// (SampleBlended на клетку = ~24 hash; клетки одного региона дают тот же набор
	/// из 4 регионов — считаем его один раз на регион и билинейно смешиваем).
	/// Повторный вызов (смена seed) отменяет предыдущую задачу и пересобирает.
	/// </summary>
	private void StartBake()
	{
		_bakeCts?.Cancel();
		_bakeCts?.Dispose();
		_bakeCts = new CancellationTokenSource();
		_bakeComplete = false;
		int cells = GridWidth * GridHeight;
		var tiles = new sbyte[cells]; // заполняем нулями, -1 не нужен: bake пишет все клетки до ready
		_bakedTiles = tiles;

		ulong seed = _seed;
		uint version = _version;
		var token = _bakeCts.Token;
		Task.Run(() =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			const int RowBatch = 8;
			Parallel.For(0, (GridHeight + RowBatch - 1) / RowBatch, new ParallelOptions { CancellationToken = token }, batch =>
			{
				int y0 = batch * RowBatch;
				int y1 = Math.Min(y0 + RowBatch, GridHeight);
				for (int y = y0; y < y1; y++)
					BakeRow(seed, version, y, tiles);
			}, token);
			if (!token.IsCancellationRequested)
			{
				_bakeComplete = true;
				GD.Print($"[WORLD MAP] Bake complete: {cells} cells in {sw.ElapsedMilliseconds} ms");
			}
		}, token).ContinueWith(t =>
		{
			if (t.IsFaulted)
				GD.PrintErr($"[WORLD MAP] Bake faulted: {t.Exception?.GetBaseException().Message}");
		}, TaskScheduler.Default);
	}

	/// <summary>
	/// Пёк одной строки с region-инкрементальным кэшем: для каждого нового региона X
	/// считаем SampleBlended-четвёрки один раз; при движении по X внутри региона
	/// (512 клеток) меняются только веса u — v и траектории постоянны.
	/// Результат побитово совпадает с поcellевым SampleBlended+Pick.
	/// </summary>
	private static void BakeRow(ulong seed, uint version, int y, sbyte[] tiles)
	{
		const int RS = WorldRegions.RegionSize; // 512
		int rowBase = y * GridWidth;

		long regionY = y >> 9; // RegionSize = 512 = 2^9 → floor-div для неотрицательных
		uint v = ((uint)(y - regionY * RS) * 2u + 1u) * (ushort.MaxValue) / (2u * (uint)RS);

		// Кэш четырёх угловых регионов текущей "пары" (regionX, regionX+1) × (regionY, regionY+1)
		long cachedRX = long.MinValue;
		RegionTraits nw = default, ne = default, sw = default, se = default;

		for (int x = 0; x < GridWidth; x++)
		{
			long regionX = x >> 9;
			if (regionX != cachedRX)
			{
				cachedRX = regionX;
				nw = RegionTraitProvider.Sample(seed, version, new RegionKey(regionX, regionY));
				ne = RegionTraitProvider.Sample(seed, version, new RegionKey(regionX + 1, regionY));
				sw = RegionTraitProvider.Sample(seed, version, new RegionKey(regionX, regionY + 1));
				se = RegionTraitProvider.Sample(seed, version, new RegionKey(regionX + 1, regionY + 1));
			}

			uint u = ((uint)(x - regionX * RS) * 2u + 1u) * (ushort.MaxValue) / (2u * (uint)RS);
			RegionTraits traits = RegionTraitProvider.BlendBilinear(nw, ne, sw, se, u, v);
			TilePick pick = BiomeMapper.Pick(seed, version, x, y, traits);
			tiles[rowBase + x] = (sbyte)WorldMapTileMapper.GetTileId(pick.Biome, pick.Variant);
		}
	}

	/// <summary>
	/// Открыть карту. Видимостью игрового мира/HUD и активной камерой управляет
	/// ТОЛЬКО Main.ToggleWorldMap (единый источник истины). Раньше здесь были
	/// SetGameCanvasVisible + MakeCurrent на "Camera" — при закрытии они
	/// конфликтовали с логикой Main (двойной MakeCurrent, рассинхрон Enabled),
	/// из-за чего после закрытия карты не работали движение/зум и «слетал» HUD.
	/// </summary>
	public void OpenMap()
	{
		if (!EnsureTileSet())
			return;

		Visible = true;
		_uiLayer.Visible = true;
		_camera.Enabled = true;
		_camera.MakeCurrent();
		ClampCamera();
		SetProcess(true);
		_lastCameraPosition = _camera.Position;
		_lastCameraZoom = _camera.Zoom;
		LoadVisibleChunks();
	}

	/// <summary>Закрыть карту. Камеру игры НЕ трогаем — её восстанавливает Main.</summary>
	public void CloseMap()
	{
		Visible = false;
		_uiLayer.Visible = false;
		_camera.Enabled = false;
		_isDragging = false;
		SetProcess(false); // карта закрыта — ноль работы в кадре (плюс скрытый TileMapLayer не рисуется)
		// Полная очистка слоя: при следующем открытии окно чанков другое; скрытый
		// TileMapLayer с тысячами клеток — лишняя память и перестройки при загрузке.
		if (_mapLayer != null && (_loadedChunks.Count > 0 || _loadedCoarseChunks.Count > 0))
		{
			_mapLayer.Clear();
			_loadedChunks.Clear();
			_invisibleSince.Clear();
			_deferredChunks.Clear();
			_loadedCoarseChunks.Clear();
			_coarseInvisibleSince.Clear();
			_deferredCoarseChunks.Clear();
		}
		_lastCoarseMode = null; // при следующем открытии режим определится заново
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!Visible || !IsInsideTree())
			return;

		if (@event is InputEventMouseButton mouse)
		{
			if (mouse.ButtonIndex is MouseButton.Middle or MouseButton.Right)
			{
				_isDragging = mouse.Pressed;
				GetViewport().SetInputAsHandled();
				return;
			}

			if (mouse.Pressed && mouse.ButtonIndex == MouseButton.WheelUp)
			{
				ZoomAtMouse(ZoomFactor);
				GetViewport().SetInputAsHandled();
			}
			else if (mouse.Pressed && mouse.ButtonIndex == MouseButton.WheelDown)
			{
				ZoomAtMouse(1f / ZoomFactor);
				GetViewport().SetInputAsHandled();
			}
			return;
		}

		if (_isDragging && @event is InputEventMouseMotion motion)
		{
			_camera.Position -= motion.Relative / _camera.Zoom;
			ClampCamera();
			GetViewport().SetInputAsHandled();
		}
	}

	private Vector2 _lastCameraPosition = Vector2.Zero;
	private Vector2 _lastCameraZoom = Vector2.Zero;

	public override void _Process(double delta)
	{
		if (!Visible || !IsInsideTree())
			return;

		Vector2 direction = Vector2.Zero;
		if (Input.IsKeyPressed(Key.A)) direction.X -= 1f;
		if (Input.IsKeyPressed(Key.D)) direction.X += 1f;
		if (Input.IsKeyPressed(Key.W)) direction.Y -= 1f;
		if (Input.IsKeyPressed(Key.S)) direction.Y += 1f;
		if (direction != Vector2.Zero)
		{
			_camera.Position += direction.Normalized() * (PanSpeed * (float)delta / _camera.Zoom.X);
			ClampCamera();
		}

		// Zoom меняет видимый rect без движения камеры — учитываем оба.
		if (!_lastCameraPosition.Equals(_camera.Position) || !_lastCameraZoom.Equals(_camera.Zoom))
		{
			_lastCameraPosition = _camera.Position;
			_lastCameraZoom = _camera.Zoom;
			LoadVisibleChunks();
		}
		else if (_deferredChunks.Count > 0 && _bakeComplete)
		{
			// Камера стоит, но остались отложенные чанки из шторма — дотягиваем
			// их в следующих кадрах (иначе при паузе карта остаётся с дырками).
			FillDeferredChunks(_bakedTiles);
		}
	}

	private bool EnsureTileSet()
	{
		if (_mapLayer.TileSet != null)
			return true;

		Texture2D atlasTexture = GD.Load<Texture2D>("res://ui/assets/textures/map/ForWorldMap.png");
		if (atlasTexture == null)
		{
			GD.PrintErr("[WORLD MAP] Не удалось загрузить ForWorldMap.png");
			return false;
		}

		var tileSet = new TileSet { TileSize = new Vector2I(TileSizePx, TileSizePx) };
		var atlas = new TileSetAtlasSource
		{
			Texture = atlasTexture,
			TextureRegionSize = new Vector2I(TileSizePx, TileSizePx)
		};
		for (int tileIndex = 0; tileIndex < 16; tileIndex++)
			atlas.CreateTile(GetAtlasCoordsRaw(tileIndex));
		tileSet.AddSource(atlas, 0);
		// Мировая карта — статичный декоративный слой: навигация/физика/автотайлы не нужны.
		// Отключаем per-cell источники данных, чтобы SetCell-пути Godot не строили лишнее.
		tileSet.NavigationLayers = null;
		tileSet.PhysicsLayersCount = 0;
		_mapLayer.TileSet = tileSet;
		return true;
	}

	private void LoadVisibleChunks()
	{
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		float zoomX = _camera.Zoom.X;
		float zoomY = _camera.Zoom.Y;

		// --- LOD-режим: на дальних зумах рисуем coarse-чанки (по 1 тайлу на клетку
		// мира, но чанки в 8 раз крупнее → в ~64 раза меньше SetCellsRect-перестроек
		// и фиксированный потолок клеток). Гистерезис вокруг порога не даёт режимам
		// флипаться колёсиком туда-сюда. ---
		bool wantCoarse = zoomX < CoarseZoomThreshold - CoarseSwitchHysteresis;
		if (_lastCoarseMode == true)
			wantCoarse = zoomX < CoarseZoomThreshold + CoarseSwitchHysteresis;
		if (wantCoarse != _lastCoarseMode)
		{
			_lastCoarseMode = wantCoarse;
			SwitchLodMode(wantCoarse);
		}

		if (_lastCoarseMode == true)
		{
			LoadVisibleCoarseChunks(viewportSize, zoomX, zoomY);
			return;
		}

		float halfWidthCells = viewportSize.X / (zoomX * TileSizePx) + 1;
		float halfHeightCells = viewportSize.Y / (zoomY * TileSizePx) + 1;

		int minCellX = Mathf.FloorToInt(_camera.Position.X / TileSizePx - halfWidthCells);
		int maxCellX = Mathf.CeilToInt(_camera.Position.X / TileSizePx + halfWidthCells);
		int minCellY = Mathf.FloorToInt(_camera.Position.Y / TileSizePx - halfHeightCells);
		int maxCellY = Mathf.CeilToInt(_camera.Position.Y / TileSizePx + halfHeightCells);

		minCellX = Math.Max(0, minCellX);
		maxCellX = Math.Min(GridWidth - 1, maxCellX);
		minCellY = Math.Max(0, minCellY);
		maxCellY = Math.Min(GridHeight - 1, maxCellY);

		_visibleChunksThisFrame.Clear();
		int startCX = minCellX / ChunkSize;
		int startCY = minCellY / ChunkSize;
		int endCX = maxCellX / ChunkSize;
		int endCY = maxCellY / ChunkSize;

		for (int cy = startCY; cy <= endCY; cy++)
		{
			for (int cx = startCX; cx <= endCX; cx++)
			{
				_visibleChunksThisFrame.Add(new Vector2I(cx, cy));
			}
		}

		// При уменьшении зума окно чанков сужается: мгновенно выбрасываем чанки,
		// полностью за его пределами. Иначе на минимальном зуме в TileMapLayer
		// остаются висеть все ~50 загруженных ранее чанков (сотни тысяч клеток),
		// и каждый SetCellsRect перестраивает гигантский меш — 1 FPS / 1000 мс.
		EvictChunksOutsideWindow(startCX, startCY, endCX, endCY);

		// Выгрузка невидимых чанков — с гистерезисом (UnloadDelay): мгновенная
		// выгрузка при панорамировании вызывала шторм ClearCellsRect+SetCellsRect
		// (перестройка меша TileMapLayer) и роняла FPS при движении карты.
		_frameTime += Time.GetProcessDeltaTime();
		if (_loadedChunks.Count > 0)
		{
			_staleChunks.Clear();
			foreach (var chk in _loadedChunks)
			{
				if (_visibleChunksThisFrame.Contains(chk))
				{
					_invisibleSince.Remove(chk);
					continue;
				}
				if (!_invisibleSince.TryGetValue(chk, out float since))
					_invisibleSince[chk] = _frameTime;
				else if (_frameTime - since >= UnloadDelay)
					_staleChunks.Add(chk);
			}
			for (int i = 0; i < _staleChunks.Count; i++)
			{
				UnloadChunk(_staleChunks[i]);
				_invisibleSince.Remove(_staleChunks[i]);
			}
			// Страховка от роста словаря: чанки, загруженные вне видимости, редки,
			// но при закрытии/смене seed чистим всё.
			if (_invisibleSince.Count > _loadedChunks.Count + 64)
				_invisibleSince.Clear();
		}

		sbyte[] baked = _bakedTiles;
		bool ready = _bakeComplete && baked != null;

		_pendingChunks.Clear();
		foreach (var chk in _visibleChunksThisFrame)
		{
			if (!ready) continue; // bake идёт в фоне — рисуем после готовности
			if (IsChunkLoaded(chk)) continue; // уже в TileMapLayer — 0 работы
			_pendingChunks.Add(chk);
		}

		// Ограничение шторма: при сильном отдалении видимых чанков может быть
		// сотни — заполняем в несколько кадров, ближние к центру первыми
		// (сортировка по расстоянию до центра видимого диапазона).
		const int MaxChunksPerFrame = 48;
		if (_pendingChunks.Count > MaxChunksPerFrame)
		{
			_sortedPending.Clear();
			_sortedPending.AddRange(_pendingChunks);
			int centerX = (minCellX + maxCellX) / 2 / ChunkSize;
			int centerY = (minCellY + maxCellY) / 2 / ChunkSize;
			_sortedPending.Sort((a, b) =>
			{
				int da = (a.X - centerX) * (a.X - centerX) + (a.Y - centerY) * (a.Y - centerY);
				int db = (b.X - centerX) * (b.X - centerX) + (b.Y - centerY) * (b.Y - centerY);
				return da.CompareTo(db);
			});
			for (int i = 0; i < MaxChunksPerFrame; i++)
				FillChunkFromBaked(baked, _sortedPending[i]);
			// Остальные дозаполним в следующих кадрах: не помечаем их загруженными,
			// но и не выгружаем — вернёмся к ним через _deferredChunks.
			for (int i = MaxChunksPerFrame; i < _sortedPending.Count; i++)
				_deferredChunks.Add(_sortedPending[i]);
		}
		else
		{
			foreach (var chk in _pendingChunks)
				FillChunkFromBaked(baked, chk);
		}

		// Отложенные чанки из предыдущих кадров, если ещё видны и не загружены.
		FillDeferredChunks(baked);
	}

	/// <summary>
	/// Coarse-режим (низкий зум): окно считается в coarse-чанках (512 клеток).
	/// Всё остальное как у мелких чанков: бюджет на кадр + гистерезис выгрузки.
	/// Потолок: GridWidth/512 × GridHeight/512 = 8×16 = 128 чанков на всю карту —
	/// даже полное приближение-отдаление не создаёт тысячи меш-перестроек.
	/// </summary>
	private void LoadVisibleCoarseChunks(Vector2 viewportSize, float zoomX, float zoomY)
	{
		sbyte[] baked = _bakedTiles;
		bool ready = _bakeComplete && baked != null;

		float halfW = viewportSize.X / (zoomX * CoarseChunkSize) + 1f;
		float halfH = viewportSize.Y / (zoomY * CoarseChunkSize) + 1f;

		int startCX = Math.Max(0, Mathf.FloorToInt(_camera.Position.X / (TileSizePx * CoarseChunkSize) - halfW));
		int endCX = Math.Min(CoarseCols - 1, Mathf.CeilToInt(_camera.Position.X / (TileSizePx * CoarseChunkSize) + halfW));
		int startCY = Math.Max(0, Mathf.FloorToInt(_camera.Position.Y / (TileSizePx * CoarseChunkSize) - halfH));
		int endCY = Math.Min(CoarseRows - 1, Mathf.CeilToInt(_camera.Position.Y / (TileSizePx * CoarseChunkSize) + halfH));

		_visibleCoarseThisFrame.Clear();
		for (int cy = startCY; cy <= endCY; cy++)
			for (int cx = startCX; cx <= endCX; cx++)
				_visibleCoarseThisFrame.Add(new Vector2I(cx, cy));

		// Мгновенная evict всего, что за окном (в fine-окне это стоило шторма
		// Clear/Set, но здесь чанков мало и они крупные — один проход дешёв).
		_staleCoarse.Clear();
		foreach (var chk in _loadedCoarseChunks)
			if (!_visibleCoarseThisFrame.Contains(chk))
				_staleCoarse.Add(chk);
		for (int i = 0; i < _staleCoarse.Count; i++)
		{
			UnloadCoarseChunk(_staleCoarse[i]);
			_coarseInvisibleSince.Remove(_staleCoarse[i]);
			_deferredCoarseChunks.Remove(_staleCoarse[i]);
		}

		_pendingCoarse.Clear();
		if (ready)
		{
			foreach (var chk in _visibleCoarseThisFrame)
				if (!_loadedCoarseChunks.Contains(chk))
					_pendingCoarse.Add(chk);
		}

		// Бюджет: coarse-чанк = 512×512 клеток, поэтому за кадр тянем меньше.
		const int MaxCoarsePerFrame = 12;
		if (_pendingCoarse.Count > MaxCoarsePerFrame)
		{
			_sortedCoarse.Clear();
			_sortedCoarse.AddRange(_pendingCoarse);
			int centerX = (startCX + endCX) / 2;
			int centerY = (startCY + endCY) / 2;
			_sortedCoarse.Sort((a, b) =>
			{
				int da = (a.X - centerX) * (a.X - centerX) + (a.Y - centerY) * (a.Y - centerY);
				int db = (b.X - centerX) * (b.X - centerX) + (b.Y - centerY) * (b.Y - centerY);
				return da.CompareTo(db);
			});
			for (int i = 0; i < MaxCoarsePerFrame; i++)
				FillCoarseChunkFromBaked(baked, _sortedCoarse[i]);
			for (int i = MaxCoarsePerFrame; i < _sortedCoarse.Count; i++)
				_deferredCoarseChunks.Add(_sortedCoarse[i]);
		}
		else
		{
			foreach (var chk in _pendingCoarse)
				FillCoarseChunkFromBaked(baked, chk);
		}

		// Дотягиваем отложенные coarse-чанки (бюджет тот же).
		if (_deferredCoarseChunks.Count > 0 && ready)
		{
			int budget = MaxCoarsePerFrame;
			_staleCoarse.Clear();
			foreach (var chk in _deferredCoarseChunks)
			{
				if (!_loadedCoarseChunks.Contains(chk) && _visibleCoarseThisFrame.Contains(chk) && budget-- > 0)
					FillCoarseChunkFromBaked(baked, chk);
				if (!_loadedCoarseChunks.Contains(chk))
					_staleCoarse.Add(chk);
			}
			_deferredCoarseChunks.Clear();
			foreach (var chk in _staleCoarse)
				_deferredCoarseChunks.Add(chk);
		}
	}

	private readonly HashSet<Vector2I> _visibleCoarseThisFrame = new();
	private readonly HashSet<Vector2I> _loadedCoarseChunks = new();
	private readonly HashSet<Vector2I> _pendingCoarse = new();
	private readonly HashSet<Vector2I> _deferredCoarseChunks = new();
	private readonly List<Vector2I> _staleCoarse = new();
	private readonly List<Vector2I> _sortedCoarse = new();
	private readonly Dictionary<Vector2I, float> _coarseInvisibleSince = new();
	private bool? _lastCoarseMode;
	private static readonly int CoarseCols = (GridWidth + CoarseChunkSize - 1) / CoarseChunkSize;
	private static readonly int CoarseRows = (GridHeight + CoarseChunkSize - 1) / CoarseChunkSize;

	/// <summary>
	/// Переключение fine↔coarse: один _mapLayer.Clear() вместо тысяч ClearCellsRect
	/// поштучно — дешевле и атомарно (никаких промежуточных состояний с обоими
	/// уровнями одновременно).
	/// </summary>
	private void SwitchLodMode(bool toCoarse)
	{
		_mapLayer.Clear();
		_loadedChunks.Clear();
		_invisibleSince.Clear();
		_deferredChunks.Clear();
		_loadedCoarseChunks.Clear();
		_coarseInvisibleSince.Clear();
		_deferredCoarseChunks.Clear();
		// Заполнение произойдёт в текущем же проходе LoadVisible* — сразу нужного уровня.
	}

	/// <summary>
	/// Заполняет coarse-чанк (512×512 клеток мира, тайлы по-прежнему 16px —
	/// это НЕ даунсэмплинг, а крупный батчинг: в ~64 раза меньше вызовов
	/// SetCellsRect/перестроек меша на то же покрытие экрана). Чанк режется
	/// на горизонтальные строки: SetCellsRect требует ровно w*h элементов,
	/// поэтому одна строка = один batch (512 значений), без garbage-хвостов.
	/// </summary>
	private void FillCoarseChunkFromBaked(sbyte[] baked, Vector2I chunk)
	{
		int startX = chunk.X * CoarseChunkSize;
		int startY = chunk.Y * CoarseChunkSize;
		int w = Math.Min(CoarseChunkSize, GridWidth - startX);
		int h = Math.Min(CoarseChunkSize, GridHeight - startY);
		if (w <= 0 || h <= 0) return;

		var coords = AtlasCoordsCached;
		for (int y = 0; y < h; y++)
		{
			int rowBase = (startY + y) * GridWidth + startX;
			int n = 0;
			for (int x = 0; x < w; x++)
			{
				sbyte t = baked[rowBase + x];
				if (t < 0) continue;
				ChunkCellBuf[n++] = coords[t];
			}
			if (n > 0)
			{
				var rect = new Rect2I(startX, startY + y, w, 1);
				_mapLayer.SetCellsRect(rect, 0, ChunkCellBuf[..n]);
			}
		}
		_loadedCoarseChunks.Add(chunk);
	}

	private void UnloadCoarseChunk(Vector2I chunk)
	{
		if (!_loadedCoarseChunks.Remove(chunk))
			return;
		int startX = chunk.X * CoarseChunkSize;
		int startY = chunk.Y * CoarseChunkSize;
		int w = Math.Min(CoarseChunkSize, GridWidth - startX);
		int h = Math.Min(CoarseChunkSize, GridHeight - startY);
		if (w <= 0 || h <= 0) return;
		_mapLayer.ClearCellsRect(new Rect2I(startX, startY, w, h));
	}

	private readonly List<Vector2I> _staleChunks = new();
	private readonly List<Vector2I> _sortedPending = new();
	private readonly HashSet<Vector2I> _deferredChunks = new();

	/// <summary>
	/// Дотягивает отложенные после шторма зума чанки (бюджет на кадр).
	/// Вызывается и при движении камеры, и когда камера стоит — иначе на
	/// максимальном отдалении карта навсегда остаётся с дырками.
	/// </summary>
	private void FillDeferredChunks(sbyte[] baked)
	{
		if (_deferredChunks.Count == 0 || !_bakeComplete || baked == null)
			return;

		const int MaxChunksPerFrame = 48;
		int budget = MaxChunksPerFrame;
		_staleChunks.Clear();
		foreach (var chk in _deferredChunks)
		{
			if (!IsChunkLoaded(chk) && IsChunkVisible(chk) && budget-- > 0)
				FillChunkFromBaked(baked, chk);
			if (!IsChunkLoaded(chk))
				_staleChunks.Add(chk); // остался невыполненным или скрылся
		}
		_deferredChunks.Clear();
		foreach (var chk in _staleChunks)
			_deferredChunks.Add(chk); // скрытые уберутся сами при следующей проверке видимости
	}

	private bool IsChunkLoaded(Vector2I chunk) => _loadedChunks.Contains(chunk);

	/// <summary>
	/// Заполняет один чанк из pre-baked массива ОДНИМ SetCellsRect (batched-путь Godot:
	/// одна перестройка меша на чанк вместо 4096 SetCell). 0 генерации, только memcpy-подобный проход.
	/// </summary>
	private void FillChunkFromBaked(sbyte[] baked, Vector2I chunk)
	{
		int startX = chunk.X * ChunkSize;
		int startY = chunk.Y * ChunkSize;
		int w = Math.Min(ChunkSize, GridWidth - startX);
		int h = Math.Min(ChunkSize, GridHeight - startY);
		if (w <= 0 || h <= 0) return;

		var coords = AtlasCoordsCached;
		int n = 0;
		for (int y = 0; y < h; y++)
		{
			int rowBase = (startY + y) * GridWidth + startX;
			for (int x = 0; x < w; x++)
			{
				sbyte t = baked[rowBase + x];
				if (t < 0) continue; // клетка ещё не запечена
				ChunkCellBuf[n++] = coords[t];
			}
		}

		if (n > 0)
		{
			var rect = new Rect2I(startX, startY, w, h);
			// Slice обязателен: ChunkCellBuf теперь sized под coarse-чанк (512²),
			// а SetCellsRect требует ровно w*h элементов — лихва переписал бы клетки
			// за пределами чанка garbage-координатами.
			_mapLayer.SetCellsRect(rect, 0, ChunkCellBuf[..n]);
		}
		_loadedChunks.Add(chunk);
	}

	private bool IsChunkVisible(Vector2I chunk) => _visibleChunksThisFrame.Contains(chunk);

	private void UnloadChunk(Vector2I chunk)
	{
		if (!_loadedChunks.Remove(chunk))
			return;

		int startX = chunk.X * ChunkSize;
		int startY = chunk.Y * ChunkSize;
		int w = Math.Min(ChunkSize, GridWidth - startX);
		int h = Math.Min(ChunkSize, GridHeight - startY);
		if (w <= 0 || h <= 0) return;
		_mapLayer.ClearCellsRect(new Rect2I(startX, startY, w, h));
	}

	/// <summary>
	/// Выгружает чанки, полностью лежащие ЗА пределами текущего окна загрузки.
	/// Используется после уменьшения зума: мгновенно освобождает меш-память
	/// ставших ненужными чанков вместо ожидания UnloadDelay.
	/// </summary>
	private void EvictChunksOutsideWindow(int startCX, int startCY, int endCX, int endCY)
	{
		if (_loadedChunks.Count == 0)
			return;

		_staleChunks.Clear();
		foreach (var chk in _loadedChunks)
		{
			if (chk.X < startCX || chk.X > endCX || chk.Y < startCY || chk.Y > endCY)
				_staleChunks.Add(chk);
		}

		for (int i = 0; i < _staleChunks.Count; i++)
		{
			UnloadChunk(_staleChunks[i]);
			_invisibleSince.Remove(_staleChunks[i]);
			_deferredChunks.Remove(_staleChunks[i]);
		}
	}

	private void ZoomAtMouse(float factor)
	{
		Vector2 worldPosBefore = GetGlobalMousePosition();
		float zoom = Mathf.Clamp(_camera.Zoom.X * factor, MinZoom, MaxZoom);
		_camera.Zoom = new Vector2(zoom, zoom);
		_camera.Position += worldPosBefore - GetGlobalMousePosition();
		ClampCamera();
	}

	private void ClampCamera()
	{
		Vector2 viewport = GetViewport().GetVisibleRect().Size;
		float mapWidth = GridWidth * TileSizePx;
		float mapHeight = GridHeight * TileSizePx;
		float halfWidth = viewport.X / (2f * _camera.Zoom.X);
		float halfHeight = viewport.Y / (2f * _camera.Zoom.Y);
		_camera.Position = new Vector2(
			halfWidth * 2f >= mapWidth ? mapWidth / 2f : Mathf.Clamp(_camera.Position.X, halfWidth, mapWidth - halfWidth),
			halfHeight * 2f >= mapHeight ? mapHeight / 2f : Mathf.Clamp(_camera.Position.Y, halfHeight, mapHeight - halfHeight));
	}

	private static Vector2I GetAtlasCoordsRaw(int tileIndex)
	{
		if (tileIndex < 3) return new Vector2I(0, tileIndex);
		if (tileIndex < 6) return new Vector2I(1, tileIndex - 3);
		if (tileIndex < 9) return new Vector2I(2, tileIndex - 6);
		return new Vector2I(tileIndex - 6, 0);
	}
}
