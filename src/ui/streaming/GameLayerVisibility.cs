using Godot;

namespace Game.UI.Streaming;

/// <summary>
/// Единая точка переключения видимости игрового мира при открытии/закрытии мировой карты.
///
/// Иерархия Main.tscn: корневой узел Node2D (Main) содержит CanvasLayer с HUD
/// (hud_tscn) и игровые слои (wood_wall_tile, mountains_tile, ProcessOfWork, GardenBeds),
/// а также динамически добавляемые Node2D-рендереры (MapRenderer, AgentRenderer,
/// GroundItemRenderer, CropRenderer, ItemShadowRenderer, FarmZoneRenderer, SelectionBox,
/// StreamingWorldView, DayNightModulate, PerformanceOverlay).
///
/// Исторические баги, которые чинит этот класс:
/// 1) WorldMapOverlay искал ноды через GetTree().Root.GetChild(0) — это автозагруженный
///    корень Main.tscn ("Node2D"), а НЕ сам Main; в нём нет ни "CanvasLayer", ни "Camera"
///    (камера называется CameraController и лежит прямо в Main). В итоге после CloseMap()
///    видимость CanvasLayer не восстанавливалась → интерфейс игры «пропадал».
/// 2) HideGameLayer скрывал только прямых детей main c типом TileMapLayer + камеру по пути
///    "Camera" — агенты (AgentRenderer/MultiMeshInstance2D внутри MapRenderer и т.п.)
///    оставались видимыми и рисовались ПОВЕРХ интерфейса мировой карты.
/// 3) Поиск Main по GetScript().ResourcePath молча промахивался (C#-ноды из .tscn могут
///    возвращать скрипт с пустым путём), SetGameVisible ничего не переключал; HUD-слой
///    вообще исключался из переключения — после закрытия карты интерфейс мог остаться скрыт.
///
/// Решение: корневая игровая нода находится по ТОЧНОМУ ТИПУ Main (с фолбэками), переключа-
/// ются ВСЕ CanvasItem-дети Main (тайлы, Node2D-рендереры, агенты, CanvasModulate, камера)
/// И HUD-слой CanvasLayer (прячется при открытии карты, гарантированно возвращается при
/// закрытии). UI самой мировой карты (CanvasLayer с BtnClose внутри оверлея) не трогаем.
/// Открытие/закрытие симметричны: набор узлов задан одним методом, каждый узел получает
/// ровно одно значение Visible, поэтому «забыть вернуть рендер/интерфейс обратно» невоз-
/// можно.
/// </summary>
public static class GameLayerVisibility
{
	/// <summary>Игровая камера Main (CameraController, имя "Camera").</summary>
	public static Camera2D FindGameCamera(Node from)
	{
		Node root = FindMainRoot(from);
		return root?.GetNodeOrNull<Camera2D>("Camera");
	}

	/// <summary>
	/// Скрыть (false) / показать (true) весь игровой мир: все CanvasItem-дети корневой
	/// ноды Main (тайлы, Node2D-рендереры включая AgentRenderer с агентами, CanvasModulate,
	/// камера), а также HUD-слой (CanvasLayer с hud_tscn): прячется при открытии карты и
	/// ОБЯЗАТЕЛЬНО возвращается при закрытии. UI самой мировой карты (CanvasLayer внутри
	/// WorldMapOverlay с BtnClose) не трогаем — им владеет оверлей.
	/// Вызывается парно из OpenMap()/CloseMap(); набор переключаемых узлов задан ОДНИМ
	/// методом, поэтому «забыть вернуть рендер/интерфейс обратно» структурно невозможно.
	/// </summary>
	public static void SetGameVisible(Node from, bool visible)
	{
		Node main = FindMainRoot(from);
		if (main == null)
		{
			// Диагностический дамп детей SceneRoot: молчаливый промах поиска раньше
			// приводил к «миру без обратного рендера» после закрытия карты.
			GD.PrintErr("[WORLD MAP] Main root not found — game layer visibility NOT switched. Root children:");
			if (from?.GetTree() is SceneTree tree)
				foreach (Node n in tree.Root.GetChildren())
					GD.Print($"  [WORLD MAP] root child: '{n.Name}' ({n.GetType().Name})");
			return;
		}

		CanvasLayer hudLayer = FindHudLayer(main);

		foreach (Node child in main.GetChildren())
		{
			if (child is CanvasLayer cl)
			{
				// Слой с UI мировой карты (содержит BtnClose) — управляется самим
				// оверлеем (OpenMap/CloseMap), не трогаем.
				if (cl.FindChild("BtnClose", false, false) != null)
					continue;

				// HUD-слой (содержит hud_tscn/HUDController): скрываем при открытии
				// карты (элементы интерфейса игры не должны накладываться на карту)
				// и ПОКАЗЫВАЕМ ОБЯЗАТЕЛЬНО при закрытии (visible==true). Именно этот
				// узел раньше «терялся»: он исключался из переключения целиком, а
				// при промахе поиска Main оставался скрытым навсегда — «интерфейс
				// игры пропадал». Теперь восстановление HUD вшито в тот же метод,
				// что и скрытие: забыть вернуть рендер структурно невозможно.
				// Кнопка повторного открытия карты живёт внутри HUDController и
				// возвращается вместе со слоем.
				if (cl == hudLayer && cl.Visible != visible)
					cl.Visible = visible;
				continue;
			}

			// Node2D-рендереры (в т.ч. AgentRenderer с агентами), TileMapLayer'ы,
			// CanvasModulate, Camera2D — всё скрываем/показываем одним прогоном.
			// Именно здесь агенты исчезают при открытии мировой карты (не рисуются
			// поверх интерфейса) и возвращаются при закрытии — набор скрываемых
			// узлов задан ОДНИМ методом, симметрия open/close структурная.
			if (child is CanvasItem ci && ci.Visible != visible)
				ci.Visible = visible;
		}

		// Камера: Enabled держим включённой всегда. Если её выключить на время просмотра
		// карты, при возврате MakeCurrent() может не перенять активную камеру обратно
		// (Godot: выключенная камера теряет статус current без явного включения) —
		// мир остался бы без вида. WorldMapOverlay.Camera2D делает MakeCurrent() сам.
		Camera2D cam = FindGameCamera(from);
		if (cam != null)
			cam.Enabled = true;
	}

	/// <summary>
	/// Корневая нода игры: потомок SceneRoot, загруженный из Main.tscn.
	/// Ищем от <paramref name="from"/> вверх (tree.Root), затем DFS по детям Root —
	/// надёжнее, чем Root.GetChild(0) (там может быть любой autoload-узел).
	/// </summary>
	private static Node FindMainRoot(Node from)
	{
		SceneTree tree = from?.GetTree();
		if (tree == null)
			return null;

		Node root = tree.Root;

		// Быстрый путь: точное имя корня Main.tscn ("Node2D") или "Main".
		Node direct = root.GetNodeOrNull("Main") ?? root.GetNodeOrNull("Node2D");
		if (direct != null && IsMainCandidate(direct))
			return direct;

		// Fallback: DFS на два уровня (root → children → grandchildren).
		foreach (Node n in root.GetChildren())
		{
			if (IsMainCandidate(n))
				return n;
			foreach (Node c in n.GetChildren())
			{
				if (IsMainCandidate(c))
					return c;
			}
		}
		return null;
	}

	private static bool IsMainCandidate(Node n)
	{
		// Основной путь: точный тип — класс Main (partial-скрипт res://scenes/main/Main.cs).
		// ВАЖНО: сравниваем Type, а не полагаемся на GetScript().ResourcePath: у узлов,
		// пришедших из .tscn с C#-скриптами, в рантайме GetScript() может вернуть объект
		// с пустым ResourcePath — такой поиск молча промахивался, FindMainRoot возвращал
		// null, SetGameVisible печатал ошибку и НИЧЕГО не менял: после CloseMap() мир и
		// интерфейс оставались скрытыми («интерфейс игры пропадал»).
		if (n is Game.Main.Main)
			return true;

		// Fallback 1: путь скрипта (редактор/тесты со связанными ресурсами).
		string scriptPath = n.GetScript()?.ResourcePath ?? string.Empty;
		if (scriptPath.EndsWith("Main.cs"))
			return true;

		// Fallback 2: имя корня сцены + наличие CanvasLayer с HUD внутри.
		return (n.Name == "Node2D" || n.Name == "Main") && FindHudLayer(n) != null;
	}

	/// <summary>Первый дочерний CanvasLayer Main, содержащий HUD (hud_tscn/HUDController).</summary>
	private static CanvasLayer FindHudLayer(Node main)
	{
		foreach (Node child in main.GetChildren())
		{
			if (child is CanvasLayer cl
				&& (cl.FindChild("hud_tscn", false, false) != null
					|| cl.FindChild("HUDController", false, false) != null))
				return cl;
		}
		return null;
	}
}
