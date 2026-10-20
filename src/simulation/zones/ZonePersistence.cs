using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Game.Simulation;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §119 P7б (task-17 п.3): персистентность Work-зон колонии —
// user://colonies/{zoneId}.zones.json.
//
// Формат (v1):
//   { "version": 1, "seed": <ulong как строка>, "nextId": int,
//     "zones": [ { "id", "name", "center": [cx, cy], "radius": 64..512,
//                  "jobMask": "<ulong как строка>", "desiredWorkers",
//                  "priorityOverride": 1..10 | -1, "enabled": bool,
//                  "anchorBlockId": int } ] }
//
// Правила:
//  * Атомарная запись: .tmp рядом → File.Move (паттерн IslandOverrideStore.Save).
//  * Очереди работ НЕ сериализуются (v1): после загрузки их соберёт репуш P1 (≤30с).
//  * Seed-ключ: файл другого сида — мусор, чистится ФАЙЛ и зоны менеджера
//    («RebindSeed чистит и зоны»).
//  * Farm-зоны в v1 НЕ сохраняются (их тайлы — отдельная задача; после загрузки
//    их не будет, только Work-зоны).
//  * Сейв читает снапшот GetAllZones ПОД Lock менеджера; чужих локов нет.
// ============================================================================

public static class ZonePersistence
{
    private const int FormatVersion = 1;

    /// <summary>Путь файла зон колонии (user://).</summary>
    public static string ZonesPath(string zoneId)
        => ProjectSettings.GlobalizePath($"user://colonies/{zoneId}.zones.json");

    /// <summary>
    /// Сохранить Work-зоны менеджера. seed — «чистый» сид мира (без примеси
    /// версии генератора: зоны не зависят от формул генерации).
    /// </summary>
    public static bool SaveZones(string zoneId, ulong seed, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(zoneId))
        {
            error = "пустой zoneId";
            return false;
        }

        List<Zone> snapshot = ZoneManager.Instance.GetAllZones();
        var zonesArray = new Godot.Collections.Array();
        int nextId = 1;
        foreach (Zone z in snapshot)
        {
            if (z == null || z.Kind != ZoneKind.Work)
                continue;
            var d = new Godot.Collections.Dictionary
            {
                ["id"] = z.Id,
                ["name"] = z.Name ?? string.Empty,
                ["center"] = new Godot.Collections.Array { z.CenterX, z.CenterY },
                ["radius"] = z.RadiusTiles,
                ["jobMask"] = z.JobMask.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["desiredWorkers"] = z.DesiredWorkers,
                ["priorityOverride"] = z.PriorityOverride ?? -1,
                ["enabled"] = z.Enabled,
                ["anchorBlockId"] = z.AnchorBlockId
            };
            zonesArray.Add(d);
            if (z.Id >= nextId)
                nextId = z.Id + 1;
        }

        var root = new Godot.Collections.Dictionary
        {
            ["version"] = FormatVersion,
            ["seed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["nextId"] = nextId,
            ["zones"] = zonesArray
        };

        try
        {
            string json = Json.Stringify(root, "  ");
            string path = ZonesPath(zoneId);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Загрузить Work-зоны. false — файла нет (не ошибка) или seed не совпал
    /// (файл и зоны менеджера ЧИСТЯТСЯ — «RebindSeed чистит и зоны»). Битый
    /// JSON — исключение наружу (тихо терять зоны нельзя).
    /// </summary>
    public static bool TryLoadZones(string zoneId, ulong seed, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(zoneId))
        {
            error = "пустой zoneId";
            return false;
        }

        string path = ZonesPath(zoneId);
        if (!File.Exists(path))
            return false; // первая сессия колонии

        string json = File.ReadAllText(path);
        Variant parsed = Json.ParseString(json);
        if (parsed.VariantType == Variant.Type.Nil)
        {
            error = "битый JSON зон";
            return false;
        }

        Godot.Collections.Dictionary root = parsed.AsGodotDictionary();
        if (root == null || (int)root["version"].AsInt64() != FormatVersion)
        {
            error = "неизвестная версия формата .zones.json";
            return false;
        }

        // Seed-ключ: несовпадение = зоны прошлого мира → чистим файл и менеджер.
        string storedSeed = root["seed"].AsString();
        if (storedSeed != seed.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            ZoneManager.Instance.ClearAll();
            try { File.Delete(path); } catch { /* лучшее усилие */ }
            error = null;
            return false;
        }

        // Раз zones НЕ Farm — ClearAll перед восстановлением обязателен
        // (диспатч ещё не стартовал: гонок нет).
        ZoneManager.Instance.ClearAll();

        Godot.Collections.Array zones = root["zones"].AsGodotArray();
        if (zones != null)
        {
            foreach (Variant zv in zones)
            {
                Godot.Collections.Dictionary z = zv.AsGodotDictionary();
                if (z == null)
                    continue;
                Godot.Collections.Array center = z["center"].AsGodotArray();
                if (center == null || center.Count < 2)
                    continue;
                ulong jobMask;
                if (!ulong.TryParse(z["jobMask"].AsString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out jobMask)
                    || jobMask == 0)
                    continue; // битая маска — зона пропускается, остальные живут

                int? priority = null;
                int pRaw = (int)z["priorityOverride"].AsInt64();
                if (pRaw >= 1 && pRaw <= 10)
                    priority = pRaw;

                ZoneManager.Instance.RestoreWorkZoneForPersistence(
                    (int)z["id"].AsInt64(),
                    z["name"].AsString(),
                    (int)center[0].AsInt64(),
                    (int)center[1].AsInt64(),
                    (int)z["radius"].AsInt64(),
                    (int)z["desiredWorkers"].AsInt64(),
                    jobMask,
                    priority,
                    z["enabled"].AsBool(),
                    (int)z["anchorBlockId"].AsInt64());
            }
        }

        return true;
    }
}
