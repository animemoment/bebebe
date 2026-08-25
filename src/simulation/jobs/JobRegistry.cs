using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Реестр всех доступных обработчиков профессий и задач.
/// </summary>
public static class JobRegistry
{
    private static readonly Dictionary<JobTypeId, IJobHandler> _handlers = new();

    public static void Register(IJobHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _handlers[handler.TypeId] = handler;
    }

    public static IJobHandler GetHandler(JobTypeId typeId)
    {
        return _handlers.TryGetValue(typeId, out var handler) ? handler : null;
    }

    public static bool TryGetHandler(JobTypeId typeId, out IJobHandler handler)
    {
        return _handlers.TryGetValue(typeId, out handler);
    }
}