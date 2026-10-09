using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core.WorldStreaming;

namespace Game.UI.Streaming;

/// <summary>Мировая карта: overview-текстура (1 px = 1 клетка) + детальный TileMapLayer по чанкам.</summary>
public partial class WorldMapOverlay : Node2D
{
	private const int TileSizePx = 16;
	private const int GridWidth = 2000;
	private const int GridHeight = 1000;
	private const int ChunkSize = 64;
	private const int TileTypeCount = 16;
	private const float MinZoom = 0.12f;
	private const float MaxZoom = 4f;
	private const float ZoomFactor = 1.2f;
	private const float PanSpeed = 900f;
	private const string ToggleButtonName = "BtnOpenWorldMap";

	/// <summary>Ниже этого зума детальные тайлы не рисуются, достаточно overview.</summary>
	private const float DetailMinZoom = 0.35f;
	/// <summary>Бюджет применения чанков на кадр (мкс). Минимум один чанк за кадр.</summary>
	private const ulong ChunkBudgetUsec = 3000;

	private sealed class GenResult
	{
		public int Id;
		public short[] Tiles;
		public byte[] Pixels;
	}

	// --- Данные карты (заполняются фоновым потоком) ---
	private short[] _tileData;                    // GridWidth * GridHeight, -1 = нет тайла
	private volatile GenResult _pendingResult;
	private int _genId;
	private byte[] _tileColors = CreateGrayColors(); // TileTypeCount * 3 (RGB)
	private bool _hasSeed;

	// --- Детальный слой ---
	private readonly HashSet<Vector2I> _loadedChunks = new();
	private readonly List<Vector2I> _queue = new();      // ближайшие в конце списка
	private readonly List<Vector2I> _toUnload = new();
	private Vector2I _sortCenter;
	private Comparison<Vector2I> _farFirst;

	// --- Узлы ---
	private Camera2D _camera;
	private TileMapLayer _mapLayer;
	private Sprite2D _overviewSprite;
	private CanvasLayer _uiLayer;
	private Camera2D _gameCamera;
	private bool _savedGameCameraEnabled;
	private bool _gameLayerPendingRestore;
	private int _lastParentChildCount = -1;

	private readonly Dictionary<CanvasItem, bool> _savedCanvasItemVisibility = new();
	private readonly Dictionary<CanvasLayer, bool> _savedCanvasLayerVisibility = new();

	private ulong _seed;
	private uint _version;
	private bool _isDragging;
	private bool _assetsReady;
	private Vector2 _lastCameraPosition = new(float.NaN, float.NaN);
	private Vector2 _lastZoom = Vector2.Zero;
	private Vector2 _lastViewportSize = Vector2.Zero;

	private static readonly Vector2I[] AtlasCoordsCached = new Vector2I[TileTypeCount];
	static WorldMapOverlay()
	{
		for (int i = 0; i < TileTypeCount; i++)
			AtlasCoordsCached[i] = GetAtlasCoordsRaw(i);
	}

	public override void _Ready()
	{
		_farFirst = CompareFarFirst;

		_camera = GetNode<Camera2D>("Camera2D");
		_mapLayer = GetNode<TileMapLayer>("TileMapLayer");
		_uiLayer = GetNode<CanvasLayer>("UI");

		// Детальный слой поверх overview, оба выше игрового мира
		_mapLayer.ZIndex = 1000;
		_mapLayer.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
		_mapLayer.RenderingQuadrantSize = ChunkSize; // 1 чанк = 1 canvas item
		_mapLayer.CollisionEnabled = false;
		_mapLayer.NavigationEnabled = false;
		_mapLayer.OcclusionEnabled = false;

		_overviewSprite = new Sprite2D
		{
			Centered = false,
			Scale = new Vector2(TileSizePx, TileSizePx),
			ZIndex = 999,
			TextureFilter = CanvasItem.TextureFilterEnum.Nearest
		};
		AddChild(_overviewSprite);

		_camera.Position = new Vector2(GridWidth * TileSizePx / 2f, GridHeight * TileSizePx / 2f);
		_camera.Zoom = new Vector2(0.4f, 0.4f);
		_camera.Enabled = false;
		Visible = false;
		_uiLayer.Visible = false;

		// Кнопки внутри карты: без подключения они ничего не делали, карта не закрывалась.
		var btnClose = _uiLayer.GetNodeOrNull<Button>("Control/BtnClose");
		if (btnClose != null)
			btnClose.Pressed += CloseMap;
		var btnToggle = _uiLayer.GetNodeOrNull<Button>("Control/BtnOpenWorldMap");
		if (btnToggle != null)
			btnToggle.Pressed += ToggleMap;

		// Initialize мог быть вызван до _Ready
		if (_hasSeed)
			StartGeneration();
	}

	public override void _ExitTree()
	{
		Interlocked.Increment(ref _genId); // отменяет фоновую генерацию

		// Если узел удаляют при открытой карте, игровой слой не должен остаться скрытым
		if (Visible)
		{
			Visible = false;
			_isDragging = false;
			ShowGameLayer();
		}
	}

	public void Initialize(ulong seed, uint version)
	{
		_seed = seed;
		_version = version;
		_hasSeed = true;

		if (_mapLayer != null)
			StartGeneration();
	}

	/// <summary>Вызывается кнопкой UI/Control/BtnOpenWorldMap.</summary>
	public void ToggleMap()
	{
		if (Visible) CloseMap();
		else OpenMap();
	}

	public void OpenMap()
	{
		if (Visible || !EnsureAssets())
			return;

		// Если прошлое закрытие ещё не восстановило игровой слой (отложено), делаем это сейчас,
		// иначе HideGameLayer запомнит уже скрытые узлы как «исходные» и игра останется пустой.
		FlushPendingGameLayer();

		Node main = GetParent();
		_gameCamera = main?.GetNodeOrNull<Camera2D>("Camera");
		_savedGameCameraEnabled = _gameCamera != null && _gameCamera.Enabled;

		_savedCanvasItemVisibility.Clear();
		_savedCanvasLayerVisibility.Clear();
		_lastParentChildCount = -1;
		HideGameLayer();

		Visible = true;
		_uiLayer.Visible = true;
		_camera.Enabled = true;
		_camera.MakeCurrent();
		ClampCamera();
		_lastCameraPosition = new Vector2(float.NaN, float.NaN);
	}

	public void CloseMap()
	{
		if (!Visible)
			return;

		Visible = false;
		_uiLayer.Visible = false;
		_camera.Enabled = false;
		_isDragging = false;

		if (GodotObject.IsInstanceValid(_gameCamera) && _gameCamera.IsInsideTree() && _savedGameCameraEnabled)
		{
			_gameCamera.Enabled = true;
			_gameCamera.MakeCurrent();
		}

		// Восстановление видимости игрового слоя (сотни CanvasItem) откладываем на конец кадра,
		// чтобы закрытие не блокировало кадр — иначе ощущается как фриз.
		if (!_gameLayerPendingRestore)
		{
			_gameLayerPendingRestore = true;
			Callable.From(FlushPendingGameLayer).CallDeferred();
		}
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

	public override void _Process(double delta)
	{
		// Забираем результат фоновой генерации (даже если карта закрыта)
		TryFinalizeGeneration();

		if (!Visible || !IsInsideTree())
			return;

		Node main = GetParent();
		if (main != null && main.GetChildCount() != _lastParentChildCount)
			HideGameLayer();

		if (_isDragging
			&& !Input.IsMouseButtonPressed(MouseButton.Right)
			&& !Input.IsMouseButtonPressed(MouseButton.Middle))
			_isDragging = false;

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

		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		if (_lastCameraPosition != _camera.Position
			|| _lastZoom != _camera.Zoom
			|| _lastViewportSize != viewportSize)
		{
			_lastCameraPosition = _camera.Position;
			_lastZoom = _camera.Zoom;
			_lastViewportSize = viewportSize;
			UpdateDetailChunks(viewportSize);
		}

		PumpChunkQueue();
	}

	// ───────────────────────── Ресурсы ─────────────────────────

	private bool EnsureAssets()
	{
		if (_assetsReady)
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
		for (int tileIndex = 0; tileIndex < TileTypeCount; tileIndex++)
			atlas.CreateTile(AtlasCoordsCached[tileIndex]);
		tileSet.AddSource(atlas, 0);
		_mapLayer.TileSet = tileSet;

		_tileColors = BuildTileColors(atlasTexture);
		_assetsReady = true;
		return true;
	}

	/// <summary>Средний цвет каждого тайла атласа, используется для overview-текстуры.</summary>
	private static byte[] BuildTileColors(Texture2D atlasTexture)
	{
		byte[] colors = CreateGrayColors();
		Image img = atlasTexture.GetImage();
		if (img == null)
			return colors;
		if (img.IsCompressed())
			img.Decompress();

		int w = img.GetWidth();
		int h = img.GetHeight();
		for (int t = 0; t < TileTypeCount; t++)
		{
			Vector2I c = AtlasCoordsCached[t];
			int x0 = c.X * TileSizePx;
			int y0 = c.Y * TileSizePx;
			if (x0 + TileSizePx > w || y0 + TileSizePx > h)
				continue;

			float r = 0, g = 0, b = 0, n = 0;
			for (int y = 0; y < TileSizePx; y++)
			{
				for (int x = 0; x < TileSizePx; x++)
				{
					Color px = img.GetPixel(x0 + x, y0 + y);
					if (px.A < 0.5f) continue;
					r += px.R; g += px.G; b += px.B; n += 1f;
				}
			}
			if (n <= 0f) continue;
			colors[t * 3] = (byte)Mathf.Clamp(Mathf.RoundToInt(r / n * 255f), 0, 255);
			colors[t * 3 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(g / n * 255f), 0, 255);
			colors[t * 3 + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(b / n * 255f), 0, 255);
		}
		return colors;
	}

	private static byte[] CreateGrayColors()
	{
		var colors = new byte[TileTypeCount * 3];
		Array.Fill(colors, (byte)128);
		return colors;
	}

	// ───────────────────────── Фоновая генерация ─────────────────────────

	private void StartGeneration()
	{
		EnsureAssets(); // цвета тайлов нужны до старта потока (GD.Load только в главном потоке)

		int id = Interlocked.Increment(ref _genId);
		_pendingResult = null;
		_tileData = null;
		ResetDetail();
		if (_overviewSprite != null)
			_overviewSprite.Texture = null;

		ulong seed = _seed;
		uint version = _version;
		byte[] colors = _tileColors;

		Task.Run(() =>
		{
			try
			{
				var tiles = new short[GridWidth * GridHeight];
				var pixels = new byte[GridWidth * GridHeight * 4];

				// Характеристики регионов детерминированы и одинаковы для всех клеток региона:
				// считаем их один раз (регионов на карте единицы), а не 4 раза на клетку.
				// Индексы регионов: клетка x в [0, GridWidth) → region в [0, GridWidth/RegionSize],
				// +1 для соседа, поэтому размер сетки с запасом на 2.
				int regionsW = GridWidth / WorldRegions.RegionSize + 2;
				// (regionsW/regionsH — с запасом, чтобы индекс nw+1 / sw+1 не выходил за границы)
				int regionsH = GridHeight / WorldRegions.RegionSize + 2;
				var regionTraits = new RegionTraits[regionsW * regionsH];
				for (int ry = 0; ry < regionsH; ry++)
					for (int rx = 0; rx < regionsW; rx++)
						regionTraits[ry * regionsW + rx] =
							RegionTraitProvider.Sample(seed, version, new RegionKey(rx, ry));

				// Строки независимы (запись в непересекающиеся срезы массивов) — параллелим.
				Parallel.For(0, GridHeight, y =>
				{
					if (id != Volatile.Read(ref _genId))
						return; // пришёл новый Initialize, результат не нужен

					for (int x = 0; x < GridWidth; x++)
					{
						RegionKey r = WorldRegions.RegionForCell(x, y);
						int nw = (int)r.Y * regionsW + (int)r.X;
						int sw = nw + regionsW;
						(uint u, uint v) = RegionTraitProvider.BlendWeights(x, y);
						RegionTraits traits = RegionTraitProvider.BlendBilinear(
							regionTraits[nw], regionTraits[nw + 1],
							regionTraits[sw], regionTraits[sw + 1],
							u, v);

						TilePick pick = BiomeMapper.Pick(seed, version, x, y, traits);
						int t = WorldMapTileMapper.GetTileId(pick.Biome, pick.Variant);

						int i = y * GridWidth + x;
						if ((uint)t >= TileTypeCount)
						{
							tiles[i] = -1;
							continue;
						}

						tiles[i] = (short)t;
						int p = i * 4;
						int c = t * 3;
						pixels[p] = colors[c];
						pixels[p + 1] = colors[c + 1];
						pixels[p + 2] = colors[c + 2];
						pixels[p + 3] = 255;
					}
				});

				if (id == Volatile.Read(ref _genId))
					_pendingResult = new GenResult { Id = id, Tiles = tiles, Pixels = pixels };
			}
			catch (Exception e)
			{
				GD.PrintErr($"[WORLD MAP] Ошибка фоновой генерации: {e}");
			}
		});
	}

	private void TryFinalizeGeneration()
	{
		GenResult r = _pendingResult;
		if (r == null)
			return;
		_pendingResult = null;
		if (r.Id != Volatile.Read(ref _genId))
			return;

		_tileData = r.Tiles;
		var img = Image.CreateFromData(GridWidth, GridHeight, false, Image.Format.Rgba8, r.Pixels);
		_overviewSprite.Texture = ImageTexture.CreateFromImage(img);

		_lastCameraPosition = new Vector2(float.NaN, float.NaN); // форсируем загрузку чанков
	}

	// ───────────────────────── Детальные чанки ─────────────────────────

	private void UpdateDetailChunks(Vector2 viewportSize)
	{
		if (_tileData == null)
			return;

		// Мелкий масштаб: хватает overview, детальный слой не нужен
		if (_camera.Zoom.X < DetailMinZoom)
		{
			if (_loadedChunks.Count > 0 || _queue.Count > 0)
				ResetDetail();
			return;
		}

		float halfW = viewportSize.X / (2f * _camera.Zoom.X * TileSizePx) + 1f;
		float halfH = viewportSize.Y / (2f * _camera.Zoom.Y * TileSizePx) + 1f;
		float camCellX = _camera.Position.X / TileSizePx;
		float camCellY = _camera.Position.Y / TileSizePx;

		int minX = Math.Max(0, Mathf.FloorToInt(camCellX - halfW));
		int maxX = Math.Min(GridWidth - 1, Mathf.CeilToInt(camCellX + halfW));
		int minY = Math.Max(0, Mathf.FloorToInt(camCellY - halfH));
		int maxY = Math.Min(GridHeight - 1, Mathf.CeilToInt(camCellY + halfH));

		int cx0 = minX / ChunkSize, cx1 = maxX / ChunkSize;
		int cy0 = minY / ChunkSize, cy1 = maxY / ChunkSize;

		// Выгрузка с запасом в 1 чанк, чтобы не дёргать границу туда-сюда
		_toUnload.Clear();
		foreach (Vector2I c in _loadedChunks)
		{
			if (c.X < cx0 - 1 || c.X > cx1 + 1 || c.Y < cy0 - 1 || c.Y > cy1 + 1)
				_toUnload.Add(c);
		}
		foreach (Vector2I c in _toUnload)
		{
			_loadedChunks.Remove(c);
			EraseChunk(c);
		}

		// Очередь пересобирается каждый раз: устаревшие чанки сами отваливаются
		_queue.Clear();
		for (int cy = cy0; cy <= cy1; cy++)
			for (int cx = cx0; cx <= cx1; cx++)
			{
				var c = new Vector2I(cx, cy);
				if (!_loadedChunks.Contains(c))
					_queue.Add(c);
			}

		_sortCenter = new Vector2I(
			Math.Clamp((int)camCellX / ChunkSize, 0, (GridWidth - 1) / ChunkSize),
			Math.Clamp((int)camCellY / ChunkSize, 0, (GridHeight - 1) / ChunkSize));
		_queue.Sort(_farFirst);
	}

	private int CompareFarFirst(Vector2I a, Vector2I b)
	{
		int da = (a.X - _sortCenter.X) * (a.X - _sortCenter.X) + (a.Y - _sortCenter.Y) * (a.Y - _sortCenter.Y);
		int db = (b.X - _sortCenter.X) * (b.X - _sortCenter.X) + (b.Y - _sortCenter.Y) * (b.Y - _sortCenter.Y);
		return db.CompareTo(da);
	}

	private void PumpChunkQueue()
	{
		if (_queue.Count == 0 || _tileData == null)
			return;

		ulong start = Time.GetTicksUsec();
		while (_queue.Count > 0)
		{
			Vector2I c = _queue[^1];
			_queue.RemoveAt(_queue.Count - 1);

			if (_loadedChunks.Add(c))
				ApplyChunk(c);

			if (Time.GetTicksUsec() - start > ChunkBudgetUsec)
				break;
		}
	}

	private void ApplyChunk(Vector2I chunk)
	{
		int startX = chunk.X * ChunkSize;
		int endX = Math.Min(startX + ChunkSize, GridWidth);
		int startY = chunk.Y * ChunkSize;
		int endY = Math.Min(startY + ChunkSize, GridHeight);

		for (int y = startY; y < endY; y++)
		{
			int row = y * GridWidth;
			for (int x = startX; x < endX; x++)
			{
				short t = _tileData[row + x];
				if (t < 0) continue;
				_mapLayer.SetCell(new Vector2I(x, y), 0, AtlasCoordsCached[t]);
			}
		}
	}

	private void EraseChunk(Vector2I chunk)
	{
		int startX = chunk.X * ChunkSize;
		int endX = Math.Min(startX + ChunkSize, GridWidth);
		int startY = chunk.Y * ChunkSize;
		int endY = Math.Min(startY + ChunkSize, GridHeight);
		for (int y = startY; y < endY; y++)
			for (int x = startX; x < endX; x++)
				_mapLayer.EraseCell(new Vector2I(x, y));
	}

	private void ResetDetail()
	{
		_mapLayer?.Clear();
		_loadedChunks.Clear();
		_queue.Clear();
	}

	// ───────────────────────── Камера ─────────────────────────

	private void ZoomAtMouse(float factor)
	{
		// Мировая позиция под курсором считается из экранных координат напрямую:
		// GetGlobalMousePosition() использует трансформ камеры, который обновляется отложенно
		// и после смены зума даёт сдвиг. Камера центрирована (Position = центр вида).
		Viewport vp = GetViewport();
		Vector2 viewportSize = vp.GetVisibleRect().Size;
		Vector2 mouseScreen = vp.GetMousePosition();
		Vector2 offsetBefore = (mouseScreen - viewportSize / 2f) / _camera.Zoom;

		float zoom = Mathf.Clamp(_camera.Zoom.X * factor, MinZoom, MaxZoom);
		_camera.Zoom = new Vector2(zoom, zoom);

		Vector2 offsetAfter = (mouseScreen - viewportSize / 2f) / _camera.Zoom;
		_camera.Position += offsetBefore - offsetAfter;
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

	// ───────────────────────── Скрытие игрового слоя ─────────────────────────

	private void HideGameLayer()
	{
		Node main = GetParent();
		if (main == null || !main.IsInsideTree())
			return;

		_lastParentChildCount = main.GetChildCount();

		// Скрываем весь игровой UI (в том числе CanvasLayer с HUD): кнопка карты открыта
		// внутри оверлея, поэтому HUD-кнопка здесь не нужна. Все пути закрытия идут через CloseMap.
		foreach (Node child in main.GetChildren())
		{
			if (child == this)
				continue;

			if (child is CanvasLayer layer)
			{
				if (_savedCanvasLayerVisibility.ContainsKey(layer))
					continue;
				_savedCanvasLayerVisibility[layer] = layer.Visible;
				layer.Visible = false;
			}
			else if (child is CanvasItem item)
			{
				if (_savedCanvasItemVisibility.ContainsKey(item))
					continue;
				_savedCanvasItemVisibility[item] = item.Visible;
				item.Visible = false;
			}
		}
	}

	private void FlushPendingGameLayer()
	{
		if (!_gameLayerPendingRestore)
			return;
		_gameLayerPendingRestore = false;
		ShowGameLayer();
	}

	private void ShowGameLayer()
	{
		foreach (var entry in _savedCanvasItemVisibility)
			if (GodotObject.IsInstanceValid(entry.Key))
				entry.Key.Visible = entry.Value;
		_savedCanvasItemVisibility.Clear();

		foreach (var entry in _savedCanvasLayerVisibility)
			if (GodotObject.IsInstanceValid(entry.Key))
				entry.Key.Visible = entry.Value;
		_savedCanvasLayerVisibility.Clear();
	}

	private static Vector2I GetAtlasCoordsRaw(int tileIndex)
	{
		if (tileIndex < 3) return new Vector2I(0, tileIndex);
		if (tileIndex < 6) return new Vector2I(1, tileIndex - 3);
		if (tileIndex < 9) return new Vector2I(2, tileIndex - 6);
		return new Vector2I(tileIndex - 6, 0);
	}
}
