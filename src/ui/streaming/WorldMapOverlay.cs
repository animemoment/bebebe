using System;
using System.Collections.Generic;
using Godot;
using Game.Core.WorldStreaming;

namespace Game.UI.Streaming;

/// <summary>Мировая карта: чанки TileMapLayer с dirty-state кэшем.</summary>
public partial class WorldMapOverlay : Node2D
{
	private const int TileSizePx = 16;
	private const int GridWidth = 1000;
	private const int GridHeight = 2000;
	private const int ChunkSize = 64;
	private const float MinZoom = 0.12f;
	private const float MaxZoom = 4f;
	private const float ZoomFactor = 1.2f;
	private const float PanSpeed = 900f;

	// --- Dirty-state cache: на каждый загруженный чанк храним cached tile index ---
	private readonly Dictionary<Vector2I, short[]> _chunkTileCache = new();
	private Camera2D _camera;
	private TileMapLayer _mapLayer;
	private CanvasLayer _uiLayer;
	private Button _closeBtn;
	private readonly HashSet<Vector2I> _visibleChunksThisFrame = new();
	private ulong _seed;
	private uint _version;
	private bool _isDragging;

	// Пресчитанный atlas-coord lookup (16 элементов)
	private static readonly Vector2I[] AtlasCoordsCached = new Vector2I[16];
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
	}

	public void Initialize(ulong seed, uint version)
	{
		_seed = seed;
		_version = version;
		ClearAllCache();
	}

	public void OpenMap()
	{
		if (!EnsureTileSet())
			return;

		HideGameLayer();
		ClearAllCache();
		Visible = true;
		_uiLayer.Visible = true;
		_camera.Enabled = true;
		_camera.MakeCurrent();
		ClampCamera();
		SetGameCanvasVisible(false);
		LoadVisibleChunks();
	}

	public void CloseMap()
	{
		Visible = false;
		_mapLayer.Visible = false;
		_uiLayer.Visible = false;
		_camera.Enabled = false;
		_isDragging = false;
		SetGameCanvasVisible(true);
		ShowGameLayer();

		Node main = GetTree().Root.GetChild(0);
		Camera2D baseCam = main.GetNodeOrNull<Camera2D>("Camera");
		if (baseCam != null)
		{
			baseCam.Enabled = true;
			baseCam.MakeCurrent();
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

	private Vector2 _lastCameraPosition = Vector2.Zero;

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

		if (!_lastCameraPosition.Equals(_camera.Position))
		{
			_lastCameraPosition = _camera.Position;
			LoadVisibleChunks();
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

		// Удаление невидимых чанков
		foreach (var chk in _chunkTileCache.Keys)
		{
			if (!_visibleChunksThisFrame.Contains(chk))
			{
				UnloadChunk(chk);
			}
		}

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
		Node main = GetTree().Root.GetChild(0);
		main.GetNodeOrNull<CanvasLayer>("CanvasLayer")?.Set("visible", visible);
	}

	private void HideGameLayer()
	{
		Node main = GetTree().Root.GetChild(0);
		if (main == null) return;

		foreach (var child in main.GetChildren())
		{
			if (child is TileMapLayer tml)
				tml.Visible = false;
		}

		var cam = main.GetNodeOrNull<Camera2D>("Camera");
		if (cam != null && cam.IsInsideTree())
			cam.Visible = false;
	}

	private void ShowGameLayer()
	{
		Node main = GetTree().Root.GetChild(0);
		if (main == null) return;

		foreach (var child in main.GetChildren())
		{
			if (child is TileMapLayer tml)
				tml.Visible = true;
		}

		var cam = main.GetNodeOrNull<Camera2D>("Camera");
		if (cam != null && cam.IsInsideTree())
			cam.Visible = true;
	}

	private static Vector2I GetAtlasCoordsRaw(int tileIndex)
	{
		if (tileIndex < 3) return new Vector2I(0, tileIndex);
		if (tileIndex < 6) return new Vector2I(1, tileIndex - 3);
		if (tileIndex < 9) return new Vector2I(2, tileIndex - 6);
		return new Vector2I(tileIndex - 6, 0);
	}
}
