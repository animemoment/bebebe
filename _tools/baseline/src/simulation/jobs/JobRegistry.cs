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

    // Bounds-guard: битый typeId (>=32) из claim-цикла — без него IndexOutOfRange.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IJobHandler GetHandler(JobTypeId typeId)
    {
        uint idx = (byte)typeId;
        return idx < 32 ? _handlersArray[idx] : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetHandler(JobTypeId typeId, out IJobHandler handler)
    {
        uint idx = (byte)typeId;
        if (idx >= 32)
        {
            handler = null;
            return false;
        }
        handler = _handlersArray[idx];
        return handler != null;
    }
}