using System;
using System.Runtime.CompilerServices;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Реестр обработчиков профессий на базе плоского массива (O(1) доступ за 0.5 наносекунды без словарей).
/// </summary>
public static class JobRegistry
{
    private static readonly IJobHandler[] _handlersArray = new IJobHandler[32];

    public static void Register(IJobHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _handlersArray[(byte)handler.TypeId] = handler;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IJobHandler GetHandler(JobTypeId typeId)
    {
        return _handlersArray[(byte)typeId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetHandler(JobTypeId typeId, out IJobHandler handler)
    {
        handler = _handlersArray[(byte)typeId];
        return handler != null;
    }
}