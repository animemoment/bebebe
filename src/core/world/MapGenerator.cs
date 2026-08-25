using System;

namespace Game.Core;

/// <summary>
/// Генератор карты мира.
/// Чистая логика без зависимостей от Godot API.
/// Потокобезопасен — не использует статическое состояние.
/// </summary>
public static class MapGenerator
{
    private const float WaterThreshold = 0.25f;
    private const float ForestThreshold = 0.55f;
    private const float HeightScale = 40f;
    private const float ForestScale = 30f;

    /// <summary>
    /// Генерирует полные данные карты: тип поверхности и расположение деревьев.
    /// </summary>
    /// <param name="width">Ширина карты в тайлах.</param>
    /// <param name="height">Высота карты в тайлах.</param>
    /// <param name="seed">Seed для воспроизводимости генерации.</param>
    /// <returns>MapData с заполненными Ground и TreeOnGrass.</returns>
    public static MapData Generate(int width, int height, uint seed)
    {
        var data = new MapData(width, height);

        // Шаг 1: генерация карты высот
        float[,] heightMap = NoiseGenerator.GenerateHeightMap(width, height, seed, HeightScale);

        // Шаг 2: определение воды/суши по порогу
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                data.Ground[x, y] = heightMap[x, y] < WaterThreshold ? TileType.Water : TileType.Grass;
            }
        }

        // Шаг 3: генерация рек (1 или 2, случайно)
        var rng = new Random((int)seed + 5000);
        int riverCount = 1 + rng.Next(2); // 1 или 2
        RiverGenerator.GenerateRivers(data.Ground, width, height, seed, riverCount);

        // Шаг 4: генерация леса
        float[,] forestMap = NoiseGenerator.GenerateForestMap(width, height, seed, ForestScale);

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                data.TreeOnGrass[x, y] = data.Ground[x, y] == TileType.Grass && forestMap[x, y] > ForestThreshold;
            }
        }

        return data;
    }
}