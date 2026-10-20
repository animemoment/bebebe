using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §118 фазы 4-5: ПРИМЕНЕНИЕ островных дельт к свежесгенерированной зоне.
//
// Зачем: стор без читателя — мёртвые данные (юзер это ненавидит). Порядок
// загрузки колонии: ActiveArea.Fill(сид) → ЭТОТ применитель → MapDataShim.ToMapData.
// Так срубленное дерево, стена, склад и грядка переживают перезапуск: база
// генерится заново, дельта её «правит».
//
// ФАЗА 4 (массивы зоны, без побочных эффектов):
//   * CutTree    → TreeOnGrass = false;
//   * MinedStone → StoneOnGrass = false.
//
// ФАЗА 5 (DOMAIN REPLAY, IslandDomainReplay): постройки/фермы/склады/планы
// живут в менеджерах домена — восстановление через их ПУБЛИЧНЫЕ API:
//   Wall/Building → StructureManager.AddStructure; *Plan → ConstructionManager.
// StartSitesBatch; Stockpile(+Plan) → StockpileManager батчи зон; Crop →
// FarmJobManager.CompletePlot; CropPlan → MarkPlot; CutTreePlan →
// TreeJobManager.MarkTreesBatch; MinedStonePlan → StoneJobManager.MarkStonesBatch.
// Дельта НЕ пишет в чанк-стриминг (аудит §6.1: AddBlock работает только с
// загруженными чанками — остров 4096 чанков не резидентен целиком).
// Идемпотентность §6.6: повторный Apply не дублирует домены.
// Исключения домена ловятся (GD.PushWarning) — дельта не убивает мир; НЕ
// восстановленное честно считается (SkippedDomainKinds), не замалчивается.
//
// ctx (SimulationContext) опционален: если симуляция ещё не стартовала, реплей
// восстанавливает занятость/события, а блокировка SolidWalls ложится, когда
// Lead передаст контекст (или пересчитается при старте сима).
// ============================================================================

/// <summary>Результат применения дельт к зоне (для лога и тестов).</summary>
public readonly struct IslandDeltaApplyResult
{
    /// <summary>Сколько клеток реально изменено/восстановлено этим вызовом.</summary>
    public readonly int Applied;
    /// <summary>Сколько меток всё ещё НЕ восстановлено (unknown kind / ошибка домена).</summary>
    public readonly int SkippedDomainKinds;

    public IslandDeltaApplyResult(int applied, int skippedDomainKinds)
    {
        Applied = applied;
        SkippedDomainKinds = skippedDomainKinds;
    }

    public override string ToString()
        => $"applied={Applied} domainSkipped={SkippedDomainKinds}";
}

/// <summary>Применяет островные дельты к базовой зоне (база + дельта = состояние мира).</summary>
public static class IslandDeltaApplicator
{
    /// <summary>
    /// Применить дельты стора к зоне. Дельты вне зоны игнорируются (стор может
    /// содержать их после смены региона — RebindSeed чистит, но защита нужна).
    /// <paramref name="ctx"/> — симуляционный контекст, если уже существует
    /// (реплей стен запишет SolidWalls + выселит агентов); иначе null.
    /// </summary>
    public static IslandDeltaApplyResult Apply(
        ActiveArea area,
        IslandOverrideStore store,
        List<(byte Kind, long CellX, long CellY)> scratch = null,
        SimulationContext ctx = null)
    {
        if (area == null)
            throw new ArgumentNullException(nameof(area));
        if (store == null)
            return new IslandDeltaApplyResult(0, 0);

        long minX = area.ZoneOriginX;
        long minY = area.ZoneOriginY;
        long maxX = minX + ActiveArea.Size - 1;
        long maxY = minY + ActiveArea.Size - 1;

        var cells = scratch ?? new List<(byte Kind, long CellX, long CellY)>(store.Count);
        store.Enumerate(minX, minY, maxX, maxY, 0, cells);

        int applied = 0;
        int skipped = 0;
        List<(byte Kind, long X, long Y)> domainCells = null;

        foreach ((byte kind, long wx, long wy) in cells)
        {
            switch (kind)
            {
                case WorldDeltaKind.CutTree:
                {
                    long lx = wx - minX;
                    long ly = wy - minY;
                    // Защита на случай смены контракта Enumerate: в норме сюда не попадает.
                    if (lx < 0 || ly < 0 || lx >= ActiveArea.Size || ly >= ActiveArea.Size)
                        break;
                    int idx = (int)(ly * ActiveArea.Size + lx);
                    if (area.TreeOnGrass[idx])
                    {
                        area.TreeOnGrass[idx] = false;
                        applied++;
                    }
                    break;
                }
                case WorldDeltaKind.MinedStone:
                {
                    long lx = wx - minX;
                    long ly = wy - minY;
                    if (lx < 0 || ly < 0 || lx >= ActiveArea.Size || ly >= ActiveArea.Size)
                        break;
                    int idx = (int)(ly * ActiveArea.Size + lx);
                    if (area.StoneOnGrass[idx])
                    {
                        area.StoneOnGrass[idx] = false;
                        applied++;
                    }
                    break;
                }
                case WorldDeltaKind.GroundItems:
                {
                    // §119 P7б: куча предметов на земле — «массивный» вид, фаза 4
                    // ничего не меняет в массивах зоны (не занятость), состояние
                    // восстанавливает IslandStateReplay ПОСЛЕ ResetColonyState.
                    // Явный кейс (не skipped!): держим domainSkipped==0 контракта.
                    break;
                }
                default:
                {
                    // Доменная метка: восстановление в менеджере домена (фаза 5).
                    switch (IslandDomainReplay.Classify(kind))
                    {
                        case IslandReplayDomain.NotDomain:
                            // Вне зоны/мусорный kind из массивного слоя — не домен, не unknown:
                            // в норме не попадает (фильтр Enumerate по прямоугольнику зоны).
                            skipped++;
                            break;
                        case IslandReplayDomain.Unknown:
                            // Новый kind без домена восстановления — честный skip.
                            skipped++;
                            break;
                        default:
                            (domainCells ??= new List<(byte, long, long)>()).Add((kind, wx, wy));
                            break;
                    }
                    break;
                }
            }
        }

        if (domainCells != null)
        {
            (int domainApplied, int domainSkipped) =
                IslandDomainReplay.ReplayAll(domainCells, ctx);
            applied += domainApplied;
            skipped += domainSkipped;
        }

        return new IslandDeltaApplyResult(applied, skipped);
    }
}
