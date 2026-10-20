using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Game.Core.WorldStreaming.Integration;
using Game.Simulation;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §119 P7б (task-17 п.5): типизированные payload'ы островных дельт.
//
// Раньше Data всех видов был opaque (null): при загрузке восстанавливалась
// только ЗАНИТОСТЬ клетки (метка), а СОСТОЯНИЕ терялось — стадия культуры,
// содержимое склада, доставка стройки, прогресс монтажа, прогресс рубки.
// Теперь payload — версия схемы + поля (схема: WorldDeltaKind.cs §P7б).
//
// Правила:
//  * Writer/reader живут ТОЛЬКО здесь (одна точка правды схемы).
//  * Читатель толерантен: null/битый/чужая версия → false + default, метка
//    всё равно остаётся (занятость важнее состояния; дефолт IslandDomainReplay).
//  * null payload легален = «метка без состояния» (готовая грядка/пустой тайл).
// ============================================================================

/// <summary>Запись «предмет → количество» из payload стройки.</summary>
public readonly struct IslandDeliveredMaterial
{
    public readonly ItemId Item;
    public readonly int Count;

    public IslandDeliveredMaterial(ItemId item, int count)
    {
        Item = item;
        Count = count;
    }
}

/// <summary>Читатель/писатель payload'ов островных дельт (схема в WorldDeltaKind).</summary>
public static class IslandPayload
{
    public const byte SchemaVersion = 1;

    // ---------------- Stockpile (0x04) ----------------

    public static byte[] WriteStockpile(ItemId item, int count)
    {
        if (item == ItemId.None || count <= 0)
            return null; // пустой слот = метка без payload
        byte[] buf = new byte[7];
        buf[0] = SchemaVersion;
        buf[1] = (byte)item;
        buf[2] = (byte)(count & 0xFF);
        buf[3] = (byte)((count >> 8) & 0xFF);
        buf[4] = (byte)((count >> 16) & 0xFF);
        buf[5] = (byte)((count >> 24) & 0xFF);
        buf[6] = 0; // padding: всего 7 байт — ver, item, count(4)
        return buf;
    }

    public static bool TryReadStockpile(byte[] payload, out ItemId item, out int count)
    {
        item = ItemId.None;
        count = 0;
        if (payload == null || payload.Length < 6 || payload[0] != SchemaVersion)
            return false;
        item = (ItemId)payload[1];
        count = payload[2] | (payload[3] << 8) | (payload[4] << 16) | (payload[5] << 24);
        if (item == ItemId.None || count <= 0)
        {
            item = ItemId.None;
            count = 0;
            return false;
        }
        return true;
    }

    // ---------------- Crop (0x05) ----------------

    public static byte[] WriteCrop(int stage, float growthTimer, int zoneId)
    {
        byte[] buf = new byte[10];
        buf[0] = SchemaVersion;
        buf[1] = (byte)stage;
        WriteInt(buf, 2, FloatToInt(growthTimer));
        WriteInt(buf, 6, zoneId);
        return buf;
    }

    public static bool TryReadCrop(byte[] payload, out int stage, out float growthTimer, out int zoneId)
    {
        stage = 1;
        growthTimer = 0f;
        zoneId = -1;
        if (payload == null || payload.Length < 10 || payload[0] != SchemaVersion)
            return false;
        stage = payload[1];
        if (stage < 1 || stage > 4)
            return false;
        growthTimer = IntToFloat(ReadInt(payload, 2));
        zoneId = ReadInt(payload, 6);
        return true;
    }

    // ---------------- BuildPlan (0x11/0x12) ----------------

    public static byte[] WriteBuildPlan(ConstructionStage stage, IReadOnlyList<(ItemId Item, int Count)> delivered, float workProgress)
    {
        using var writer = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(writer);
        bw.Write(SchemaVersion);
        bw.Write((byte)stage);
        int count = delivered?.Count ?? 0;
        bw.Write(count);
        for (int i = 0; i < count; i++)
        {
            bw.Write((byte)delivered[i].Item);
            bw.Write(delivered[i].Count);
        }
        bw.Write(workProgress);
        bw.Flush();
        return writer.ToArray();
    }

    public static bool TryReadBuildPlan(
        byte[] payload,
        out ConstructionStage stage,
        out List<IslandDeliveredMaterial> delivered,
        out float workProgress)
    {
        stage = ConstructionStage.Supplying;
        delivered = new List<IslandDeliveredMaterial>();
        workProgress = 0f;
        if (payload == null || payload.Length < 1 || payload[0] != SchemaVersion)
            return false;
        try
        {
            using var ms = new System.IO.MemoryStream(payload);
            using var br = new System.IO.BinaryReader(ms);
            br.ReadByte(); // ver
            byte st = br.ReadByte();
            if (st > (byte)ConstructionStage.Completed)
                return false;
            stage = (ConstructionStage)st;
            int count = br.ReadInt32();
            if (count < 0 || count > 64)
                return false;
            for (int i = 0; i < count; i++)
            {
                byte itemB = br.ReadByte();
                int cnt = br.ReadInt32();
                if (cnt > 0 && itemB != (byte)ItemId.None)
                    delivered.Add(new IslandDeliveredMaterial((ItemId)itemB, cnt));
            }
            workProgress = br.ReadSingle();
            if (float.IsNaN(workProgress) || float.IsInfinity(workProgress))
                workProgress = 0f;
            return true;
        }
        catch (Exception)
        {
            stage = ConstructionStage.Supplying;
            delivered = new List<IslandDeliveredMaterial>();
            workProgress = 0f;
            return false;
        }
    }

    // ---------------- Chop/Mine plan (0x15/0x16) ----------------

    public static byte[] WriteProgressFraction(float fraction)
    {
        byte[] buf = new byte[5];
        buf[0] = SchemaVersion;
        WriteInt(buf, 1, FloatToInt(fraction));
        return buf;
    }

    public static bool TryReadProgressFraction(byte[] payload, out float fraction)
    {
        fraction = 0f;
        if (payload == null || payload.Length < 5 || payload[0] != SchemaVersion)
            return false;
        fraction = IntToFloat(ReadInt(payload, 1));
        if (float.IsNaN(fraction) || float.IsInfinity(fraction))
        {
            fraction = 0f;
            return false;
        }
        return true;
    }

    // ---------------- GroundItems (0x06) ----------------

    public static byte[] WriteGroundItems(ItemId item, int count)
    {
        if (item == ItemId.None || count <= 0)
            return null;
        byte[] buf = new byte[6];
        buf[0] = SchemaVersion;
        buf[1] = (byte)item;
        WriteInt(buf, 2, count);
        return buf;
    }

    public static bool TryReadGroundItems(byte[] payload, out ItemId item, out int count)
    {
        item = ItemId.None;
        count = 0;
        if (payload == null || payload.Length < 6 || payload[0] != SchemaVersion)
            return false;
        item = (ItemId)payload[1];
        count = ReadInt(payload, 2);
        if (item == ItemId.None || count <= 0)
        {
            item = ItemId.None;
            count = 0;
            return false;
        }
        return true;
    }

    // ---------------- примитивы LE ----------------

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt(byte[] buf, int offset)
        => buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    private static int FloatToInt(float value) => BitConverter.SingleToInt32Bits(value);

    private static float IntToFloat(int value) => BitConverter.Int32BitsToSingle(value);
}
