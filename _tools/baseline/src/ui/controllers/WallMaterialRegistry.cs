using Godot;
using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.UI;

/// <summary>
/// Реестр материалов стен для попапа WallMaterialButton.
/// Пока 1 материал: дерево (WoodWall). Новые — добавлением одной строки.
/// </summary>
public static class WallMaterialRegistry
{
    public readonly struct Entry
    {
        public readonly string Id;
        public readonly string Title;
        public readonly BuildingType Type;
        public readonly int SourceId;
        public Entry(string id, string title, BuildingType type, int sourceId)
        {
            Id = id;
            Title = title;
            Type = type;
            SourceId = sourceId;
        }
    }

    public static readonly IReadOnlyList<Entry> All = new List<Entry>
    {
        new("wood", "Дерево", BuildingType.WoodWall, MapRenderer.SourceWall),
    };

    public static Entry ById(string id)
    {
        foreach (var e in All)
            if (e.Id == id)
                return e;
        return All[0];
    }
}
