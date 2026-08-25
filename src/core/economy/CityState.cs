using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Core;

/// <summary>
/// Состояние города: население, занятость, запасы ресурсов.
/// Не наследуется от Godot-нод — чистая модель данных.
/// </summary>
public class CityState
{
    /// <summary>
    /// Общая численность населения города (вычисляется как Employed + Unemployed).
    /// </summary>
    public int Population => Employed + Unemployed;

    /// <summary>
    /// Количество работающих жителей.
    /// </summary>
    public int Employed { get; set; }

    /// <summary>
    /// Количество безработных жителей.
    /// </summary>
    public int Unemployed { get; set; }

    /// <summary>
    /// Словарь запасов ресурсов города.
    /// Ключ — тип ресурса, значение — количество единиц.
    /// </summary>
    public Dictionary<ResourceType, int> Resources { get; private set; }

    /// <summary>
    /// Создаёт новый город с населением 0 и стартовыми ресурсами по 50 единиц каждого типа.
    /// </summary>
    public CityState()
    {
        Employed = 0;
        Unemployed = 0;
        InitializeResources();
    }

    /// <summary>
    /// Заполняет словарь ресурсов, устанавливая каждому типу начальное значение 50.
    /// None пропускается, так как это значение-заглушка.
    /// </summary>
    private void InitializeResources()
    {
        Resources = Enum.GetValues(typeof(ResourceType))
            .Cast<ResourceType>()
            .Where(t => t != ResourceType.None)
            .ToDictionary(t => t, _ => 50);
    }

    /// <summary>
    /// Добавляет указанное количество ресурса в хранилище города.
    /// </summary>
    /// <param name="type">Тип ресурса.</param>
    /// <param name="amount">Количество для добавления (должно быть больше 0).</param>
    public void AddResource(ResourceType type, int amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));
        if (!Resources.ContainsKey(type))
            throw new ArgumentException($"Resource type {type} is not tracked", nameof(type));

        Resources[type] += amount;
    }

    /// <summary>
    /// Удаляет указанное количество ресурса из хранилища города.
    /// </summary>
    /// <param name="type">Тип ресурса.</param>
    /// <param name="amount">Количество для удаления (должно быть больше 0).</param>
    /// <returns>true, если ресурса хватило и операция выполнена; false, если ресурсов недостаточно.</returns>
    public bool RemoveResource(ResourceType type, int amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (Resources[type] < amount)
            return false;

        Resources[type] -= amount;
        return true;
    }
}