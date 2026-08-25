using System;

namespace Game.Core;

/// <summary>
/// Битовые флаги требований к инструментам или квалификации.
/// </summary>
[Flags]
public enum ToolRequirement : byte
{
    None = 0,
    Axe = 1 << 0,
    Hammer = 1 << 1,
    Hoe = 1 << 2,
    Pickaxe = 1 << 3
}