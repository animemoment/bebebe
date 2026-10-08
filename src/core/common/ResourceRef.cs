using Godot;

namespace Game.Core;

/// <summary>
/// Загрузка ресурсов по ссылке «uid://…», «res://…» или обычному пути —
/// С ПРОВЕРКОЙ существования ссылки до вызова движка.
///
/// Зачем отдельный помощник: если скормить ResourceLoader.Load() неизвестный
/// UID (файл удалён, .import потерян, ассет не положен в проект), Godot пишет в
/// консоль ERROR «Unrecognized UID» вместе с C++ backtrace из
/// core/io/resource_uid.cpp:get_id_path — это выглядит как поломка игры, хотя
/// это всего лишь отсутствующий ассет. Здесь UID сначала проверяется через
/// ResourceUid.HasId (без обращения к движку), затем перебираются резервные
/// пути, и только существующая ссылка уходит в ResourceLoader.
/// </summary>
public static class ResourceRef
{
    /// <summary>Существует ли ссылка: uid:// — есть в кэше UID; иначе — есть файл.</summary>
    public static bool Exists(string pathOrUid)
    {
        if (string.IsNullOrEmpty(pathOrUid))
            return false;
        if (IsUid(pathOrUid))
        {
            long id = ResourceUid.TextToId(pathOrUid);
            return ResourceUid.HasId(id);
        }
        return ResourceLoader.Exists(pathOrUid);
    }

    /// <summary>
    /// Загружает ресурс по первой существующей ссылке: основной путь/UID, затем
    /// резервные. Ничего не нашлось — null (и ни одной строки ERROR от движка).
    /// </summary>
    public static T Load<T>(string pathOrUid, params string[] fallbacks) where T : Resource
    {
        var resource = TryLoad<T>(pathOrUid);
        if (resource != null)
            return resource;
        if (fallbacks != null)
        {
            for (int i = 0; i < fallbacks.Length; i++)
            {
                resource = TryLoad<T>(fallbacks[i]);
                if (resource != null)
                    return resource;
            }
        }
        return null;
    }

    private static T TryLoad<T>(string pathOrUid) where T : Resource
    {
        if (string.IsNullOrEmpty(pathOrUid))
            return null;
        if (IsUid(pathOrUid))
        {
            long id = ResourceUid.TextToId(pathOrUid);
            // Неизвестный UID: движок звать НЕЛЬЗЯ — он залогирует ERROR + backtrace.
            if (!ResourceUid.HasId(id))
                return null;
            return ResourceLoader.Load<T>(pathOrUid);
        }
        if (!ResourceLoader.Exists(pathOrUid))
            return null;
        return ResourceLoader.Load<T>(pathOrUid);
    }

    private static bool IsUid(string reference)
    {
        return reference.StartsWith("uid://", System.StringComparison.Ordinal);
    }
}
