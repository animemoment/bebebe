namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Пороги декора мира (лес/камни), Q0.16. Один источник истины: по ним рисует
/// стриминговый вид и по ним же отвечает доступ к миру (§26.1) — иначе «вижу дерево,
/// но игра говорит, что его нет».
/// </summary>
public static class WorldDecor
{
    public const ushort ForestMinQ16 = 30_000;
    public const ushort StoneMinQ16 = 35_000;
}
