using System;
using System.Collections.Generic;
using Godot;
using Game.Core.WorldStreaming;

namespace Game.UI.Streaming;

/// <summary>Мировая карта: чанки TileMapLayer с dirty-state кэшем.
/// Наследуется от CanvasLayer — весь контент карты (включая TileMapLayer) рендерится
/// поверх игровых CanvasItem-узлов (агенты, стриминг-мир), а не в общей с ними Canvas-иерархии.</summary>
public partial class WorldMapOverlay : CanvasLayer
{
	private const int TileSizePx = 16;
	// §23/§24: мир = 2048×1024 регионов по 512 клеток.
	// При тайле 16 px: 32768 × 16384 px, сетка 32768 × 16384 клеток.
	private const int GridWidth = 32768;
	private const int GridHeight = 16384;
	private const int ChunkSize = 64;
	private const float MinZoom = 0.02f;
	private const float MaxZoom = 4f;
	private const float ZoomFactor = 1.2f;
	private const float PanSpeed = 900f;

	/// <summary>Событие: игрок выбрал регион на мировой карте (ЛКМ).</summary>
	[Signal] public delegate void RegionSelectedEventHandler(long regionX, long regionY);

	// --- Dirty-state cache: на каждый загруженный чанк храним cached tile index ---
	private readonly Dictionary<Vector2I, short[]> _chunkTileCache = new();
	private readonly List<Vector2I> _chunksToRemove = new();
	private Camera2D _camera;
	private Node2D _worldRoot;
	private TileMapLayer _mapLayer;
	private Control _uiRoot;
	private Button _closeBtn;
	private readonly HashSet<Vector2I> _visibleChunksThisFrame = new();
	private ulong _seed;
	private uint _version;
	private bool _isDragging;
	private Vector2 _dragStartMouse;
	private Vector2 _dragStartCamPos;
	private bool _clickCandidate;
	private bool _isOpen;

	// Пресчитанный atlas-coord lookup (16 элементов)
	private static readonly Vector2I[] AtlasCoordsCached = new Vector2I[16];
	static WorldMapOverlay()
	{
		for (int i = 0; i < 16; i++)
			AtlasCoordsCached[i] = GetAtlasCoordsRaw(i);
	}

	public override void _Ready()
	{
		// CanvasLayer сам по себе невидим; видимость контента управляется через дочерние узлы.
		_worldRoot = GetNode<Node2D>("WorldRoot");
		_camera = GetNode<Camera2D>("WorldRoot/Camera2D");
		_mapLayer = GetNode<TileMapLayer>("WorldRoot/TileMapLayer");
		_uiRoot = GetNode<Control>("UI");
		_closeBtn = GetNode<Button>("UI/BtnClose");

		// Камера CanvasLayer без привязки к Viewport — обязательна, иначе не будет current.
		_camera.TopLeft = Vector2.Zero;

		_camera.Position = new Vector2(GridWidth * TileSizePx / 2f, GridHeight * TileSizePx / 2f);
		_camera.Zoom = new Vector2(0.4f, 0.4f);
		_camera.Enabled = false;
		_worldRoot.Visible = false;
		_uiRoot.Visible = false;
		ProcessMode = ProcessModeEnum.Always; // карта работает на паузе игры
		_closeBtn.Pressed += CloseMap;
	}

	public void Initialize(ulong seed, uint version)
	{
		_seed = seed;
		_version = version;
		ClearAllCache();
	}

	public bool IsOpen => _isOpen;

	public void OpenMap()
	{
		if (_isOpen)
			return; // идемпотентность: повторный вызов не пересобирает состояние

		if (!EnsureTileSet())
			return;

		HideGameLayer();
		_isOpen = true;
		_worldRoot.Visible = true;
		_uiRoot.Visible = true;
		Visible = true;
		_camera.Enabled = true;
		_camera.MakeCurrent();
		ClampCamera();
		LoadVisibleChunks();
	}

	public void CloseMap()
	{
		if (!_isOpen)
			return;

		_isOpen = false;
		_worldRoot.Visible = false;
		_uiRoot.Visible = false;
		Visible = false;
		_camera.Enabled = false;
		_isDragging = false;
		_clickCandidate = false;
		SetGameCanvasVisible(true);
		ShowGameLayer();

		Node main = GetTree().CurrentScene;
		Camera2D baseCam = main?.FindChild("Camera", true, false) as Camera2D;
		if (baseCam != null)
		{
			baseCam.Enabled = true;
			baseCam.MakeCurrent();
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_isOpen)
			return;

		if (@event is InputEventMouseButton mouse)
		{
			if (mouse.ButtonIndex is MouseButton.Middle or MouseButton.Right)
			{
				if (mouse.Pressed)
				{
					_isDragging = true;
					_dragStartMouse = mouse.GlobalPosition;
					_dragStartCamPos = _camera.Position;
				}
				else
				{
					_isDragging = false;
				}
				GetViewport().SetInputAsHandled();
				return;
			}

			if (mouse.ButtonIndex == MouseButton.Left)
			{
				if (mouse.Pressed)
				{
					_clickCandidate = true;
				}
				else if (_clickCandidate)
				{
					_clickCandidate = false;
					TrySelectRegion(mouse.GlobalPosition);
					GetViewport().SetInputAsHandled();
				}
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
			// Панорамирование от точки старта drag — стабильно при любом зуме.
			_camera.Position = _dragStartCamPos - (motion.GlobalPosition - _dragStartMouse) / _camera.Zoom;
			ClampCamera();
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>Клик по карте → выбор региона (512×512 игровых клеток).</summary>
	private void TrySelectRegion(Vector2 screenPos)
	{
		Vector2 world = _camera.Position + (screenPos - GetViewport().GetVisibleRect().Size / 2f) / _camera.Zoom;
		long cellX = Mathf.FloorToInt(world.X / TileSizePx);
		long cellY = Mathf.FloorToInt(world.Y / TileSizePx);
		if (cellX < 0 || cellY < 0 || cellX >= GridWidth || cellY >= GridHeight)
			return;

		const int CellsPerRegionAxis = 512; // §23: регион 512×512 мировых клеток
		int regionX = (int)(cellX / CellsPerRegionAxis);
		int regionY = (int)(cellY / CellsPerRegionAxis);
		EmitSignal(SignalName.RegionSelected, regionX, regionY);
		CloseMap();
	}

	private Vector2 _lastCameraPosition = Vector2.Zero;
	private Vector2 _lastCameraZoom = Vector2.Zero;

	public override void _Process(double delta)
	{
		if (!_isOpen)
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

		// Чанки нужно перезагружать и при зуме, а не только при движении камеры.
		if (!_lastCameraPosition.Equals(_camera.Position) || !_lastCameraZoom.Equals(_camera.Zoom))
		{
			_lastCameraPosition = _camera.Position;
			_lastCameraZoom = _camera.Zoom;
			LoadVisibleChunks();
		}
	}

	private bool EnsureTileSet()
	{
		if (_mapLayer.TileSet != null)
			return true;

		Texture2D atlasTexture = GD.Load<Texture2D>("uid://dr60ndnocpdxe");
		if (atlasTexture == null)
		{
			GD.PrintErr("[WORLD MAP] Не удалось загрузить атлас карты (uid://dr60ndnocpdxe — ForWorldMap.png). Проверьте, что .import сгенерирован в Godot.");
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
		_mapLayer.TileSet = tileSet;
		return true;
	}

	private void LoadVisibleChunks()
	{
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		float zoomX = _camera.Zoom.X;
		float zoomY = _camera.Zoom.Y;

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

		// Удаление невидимых чанков (сбор в отдельный список — нельзя мутировать словарь во время обхода)
		_chunksToRemove.Clear();
		foreach (var chk in _chunkTileCache.Keys)
		{
			if (!_visibleChunksThisFrame.Contains(chk))
				_chunksToRemove.Add(chk);
		}
		foreach (var chk in _chunksToRemove)
			UnloadChunk(chk);

		// Загрузка/обновление видимых чанков
		foreach (var chk in _visibleChunksThisFrame)
			PullChunk(chk);
	}

	/// <summary>Загружает или обновляет один чанк. Если чанк ранее был выгружен — все клетки «чистые» (need redraw).</summary>
	private void PullChunk(Vector2I chunk)
	{
		bool isNew = !_chunkTileCache.TryGetValue(chunk, out var tiles);

		if (isNew)
		{
			tiles = new short[ChunkSize * ChunkSize]; // -1 = пусто/needs draw
			for (int i = 0; i < tiles.Length; i++) tiles[i] = -1;
			_chunkTileCache[chunk] = tiles;
		}

		int startX = chunk.X * ChunkSize;
		int endX = Math.Min(startX + ChunkSize, GridWidth);
		int startY = chunk.Y * ChunkSize;
		int endY = Math.Min(startY + ChunkSize, GridHeight);

		int size = ChunkSize * ChunkSize;
		for (int idx = 0; idx < size; idx++)
		{
			int lx = idx % ChunkSize;
			int ly = idx / ChunkSize;
			int wx = startX + lx;
			int wy = startY + ly;

			if (wx >= endX || wy >= endY) continue; // outside this chunk's valid bounds

			short cachedTile = tiles[idx];

			TilePick pick = BiomeMapper.Pick(_seed, _version, wx, wy, RegionTraitProvider.SampleBlended(_seed, _version, wx, wy));
			short tileIdx = (short)WorldMapTileMapper.GetTileId(pick.Biome, pick.Variant);

			// Защита от выхода за границы atlas-lookup (16 тайлов атласа)
			if (tileIdx < 0 || tileIdx >= AtlasCoordsCached.Length)
				tileIdx = 10; // Plains как безопасный fallback

			if (cachedTile != tileIdx)
			{
				_mapLayer.SetCell(new Vector2I(wx, wy), 0, AtlasCoordsCached[tileIdx]);
				tiles[idx] = tileIdx;
			}
		}
	}

	private void UnloadChunk(Vector2I chunk)
	{
		_chunkTileCache.Remove(chunk);

		int startX = chunk.X * ChunkSize;
		int endX = Math.Min(startX + ChunkSize, GridWidth);
		int startY = chunk.Y * ChunkSize;
		int endY = Math.Min(startY + ChunkSize, GridHeight);
		for (int y = startY; y < endY; y++)
		{
			for (int x = startX; x < endX; x++)
				_mapLayer.SetCell(new Vector2I(x, y), -1, new Vector2I(-1, -1));
		}
	}

	private void ClearAllCache()
	{
		// Очищаем TileMapLayer для всех ранее загруженных чанков
		foreach (var kvp in _chunkTileCache)
		{
			Vector2I chunk = kvp.Key;
			int startX = chunk.X * ChunkSize;
			int endX = Math.Min(startX + ChunkSize, GridWidth);
			int startY = chunk.Y * ChunkSize;
			int endY = Math.Min(startY + ChunkSize, GridHeight);
			for (int y = startY; y < endY; y++)
			{
				for (int x = startX; x < endX; x++)
					_mapLayer.SetCell(new Vector2I(x, y), -1, new Vector2I(-1, -1));
			}
		}
		_chunkTileCache.Clear();
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

	private void SetGameCanvasVisible(bool visible)
	{
		Node main = GetTree().CurrentScene;
		CanvasLayer canvas = main?.GetNodeOrNull<CanvasLayer>("CanvasLayer");
		if (canvas == null)
			return;
		// CanvasLayer не имеет свойства visible — только Show/Hide.
		if (visible) canvas.Show();
		else canvas.Hide();
	}

	private void HideGameLayer()
	{
		Node main = GetTree().CurrentScene;
		if (main == null) return;

		// Рекурсивно скрываем ВСЕ игровые CanvasItem, кроме камеры и самой карты.
		// Прежняя версия скрывала только прямые дети TileMapLayer — агенты и стриминг-мир оставались видимыми.
		HideCanvasItemsRecursive(main);

		SetGameCanvasVisible(false);
	}

	private void ShowGameLayer()
	{
		Node main = GetTree().CurrentScene;
		if (main == null) return;

		ShowCanvasItemsRecursive(main);

		SetGameCanvasVisible(true);
	}

	private static bool IsInsideWorldMap(Node node)
	{
		Node p = node;
		while (p != null)
		{
			if (p is WorldMapOverlay)
				return true;
			p = p.GetParent();
		}
		return false;
	}

	private static void HideCanvasItemsRecursive(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (IsInsideWorldMap(child))
				continue;
			if (child is CanvasItem ci && !(child is Camera2D))
				ci.Hide();
			HideCanvasItemsRecursive(child);
		}
	}

	private static void ShowCanvasItemsRecursive(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (IsInsideWorldMap(child))
				continue;
			if (child is CanvasItem ci && !(child is Camera2D))
				ci.Show();
			ShowCanvasItemsRecursive(child);
		}
	}

	private static Vector2I GetAtlasCoordsRaw(int tileIndex)
	{
		// Атлас ForWorldMap.png: 10 колонок × 3 ряда, source index = col*3 + row.
		return new Vector2I(tileIndex / 3, tileIndex % 3);
	}
}
