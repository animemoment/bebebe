using Godot;
using Game.Core;
using Game.Simulation;
using System;

namespace Game.UI;

public partial class CropRenderer : Node2D
{
	private const int MaxCrops = 4096;
	private const float CropSize = 64f;

	// UID'ы фаз роста. ВАЖНО: эти четыре UID сейчас осиротели — самих PNG в
	// проекте нет (их нет ни в ui/assets/textures, ни в истории: репозиторий не
	// под git). Поэтому ниже есть резервные пути: положите файлы туда — код
	// править не нужно. Раньше Load() по неизвестному UID заставлял движок
	// писать ERROR «Unrecognized UID» + C++ backtrace на каждую фазу.
	private static readonly string[] StageTextureUids =
	{
		"uid://deq1c2pg5ln3o", // Фаза 1
		"uid://cq1k2nlma5qxs", // Фаза 2
		"uid://fn3w8iv243e7", // Фаза 3
		"uid://d4b1fcpa6txcf"  // Фаза 4
	};

	private static readonly string[][] StageTextureFallbacks =
	{
		new[] { "res://ui/assets/textures/crops/phase1.png" },
		new[] { "res://ui/assets/textures/crops/phase2.png" },
		new[] { "res://ui/assets/textures/crops/phase3.png" },
		new[] { "res://ui/assets/textures/crops/phase4.png" }
	};

	private readonly MultiMeshInstance2D[] _instances = new MultiMeshInstance2D[4];
	private readonly MultiMesh[] _multiMeshes = new MultiMesh[4];
	private readonly float[][] _renderBuffers = new float[4][];

	/// <summary>Тени ростков: общий рендерер ставит Main, тикает солнцем.</summary>
	public ItemShadowRenderer ItemShadows { get; set; }

	public override void _Ready()
	{
		ZIndex = 6; // Поверх грядок (ZIndex=0..5), под агентами (ZIndex=10)

		var quadMesh = new QuadMesh { Size = new Vector2(CropSize, CropSize) };
		float mapSizePx = MapRenderer.MapWidth * MapRenderer.TileSizePx;
		var mapAabb = new Aabb(Godot.Vector3.Zero, new Godot.Vector3(mapSizePx, mapSizePx, 1000f));

		// Собираем отсутствующие ассеты и предупреждаем ОДИН раз: Godot печатает
		// на каждый PushWarning свой стек, и 4 предупреждения подряд шумят так же,
		// как раньше 4 ERROR-а от resource_uid.cpp.
		string missingPaths = null;

		for (int i = 0; i < 4; i++)
		{
			_renderBuffers[i] = new float[MaxCrops * 8];

			// Грузим по первой существующей ссылке (UID → резервный путь) и НЕ
			// зовём движок с неизвестным UID — иначе ERROR + backtrace в консоли.
			var tex = ResourceRef.Load<Texture2D>(StageTextureUids[i], StageTextureFallbacks[i]);
			if (tex == null)
			{
				missingPaths = missingPaths == null
					? StageTextureFallbacks[i][0]
					: missingPaths + ", " + StageTextureFallbacks[i][0];
			}

			_multiMeshes[i] = new MultiMesh
			{
				Mesh = quadMesh,
				TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
				UseColors = false,
				UseCustomData = false,
				InstanceCount = MaxCrops,
				VisibleInstanceCount = 0,
				CustomAabb = mapAabb
			};

			_instances[i] = new MultiMeshInstance2D
			{
				Name = $"CropStage_{i + 1}",
				Multimesh = _multiMeshes[i],
				Texture = tex
			};
			AddChild(_instances[i]);
		}

		if (missingPaths != null)
		{
			GD.PushWarning($"[CropRenderer] Нет текстур фаз роста: {missingPaths}. " +
						   "Эти фазы рисуются без текстуры — положите PNG по указанным путям " +
						   "(UID'ы в StageTextureUids осиротели: ассетов нет в проекте).");
		}
	}

	public override void _Process(double delta)
	{
		if (!CropGrowthManager.Instance.SnapshotQueue.TryDequeue(out var latest))
			return;

		while (CropGrowthManager.Instance.SnapshotQueue.TryDequeue(out var newer))
		{
			latest = newer;
		}

		if (latest.PositionsByStage == null || latest.Counts == null) return;

		for (int stage = 0; stage < 4; stage++)
		{
			int count = Math.Min(latest.Counts[stage], MaxCrops);
			_multiMeshes[stage].VisibleInstanceCount = count;

			if (count > 0)
			{
				var positions = latest.PositionsByStage[stage];
				var buffer = _renderBuffers[stage];

				for (int i = 0; i < count; i++)
				{
					var pos = positions[i];
					int idx = i * 8;

					buffer[idx + 0] = 1.0f;
					buffer[idx + 1] = 0.0f;
					buffer[idx + 2] = 0.0f;
					buffer[idx + 3] = pos.X;

					buffer[idx + 4] = 0.0f;
					buffer[idx + 5] = 1.0f;
					buffer[idx + 6] = 0.0f;
					buffer[idx + 7] = pos.Y;
				}

				_multiMeshes[stage].Buffer = buffer;

				// Ростки 3-4 стадии выше — им тень, мелочь 1-2 пропускаем (Syx early-out).
				if (stage >= 2)
					ItemShadows?.PushSpots(positions, count, CropSize);
			}
		}
	}
}
