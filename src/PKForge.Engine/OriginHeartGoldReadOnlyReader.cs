
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PKForge.Engine;

/// <summary>
/// Read-only view of an Origin HeartGold v4.0.3 save.
/// This class never modifies the input save.
/// </summary>
internal sealed class OriginHeartGoldReadOnlySave
{
    internal const int BoxCount = 24;
    internal const int BoxSlotCount = 30;
    internal const int BoxedSize = 136;
    internal const int PartyRecordSize = 236;
    internal const int BoxStride = 0x1000;

    private const int PartyOffset = 0x98;
    private const int PartyStride = 236;
    private const int BoxNamesOffset = 0x18008;
    private const int BoxNameStride = 40;
    private const int BoxWallpapersOffset = 0x183C8;

    private readonly byte[] _raw;

    internal int GeneralOffset { get; }
    internal int StorageOffset { get; }
    internal uint Counter { get; }
    internal bool Tied { get; }
    internal int PartyCount { get; }

    internal OriginHeartGoldReadOnlySave(
        byte[] raw,
        int generalOffset,
        int storageOffset,
        uint counter,
        bool tied,
        int partyCount)
    {
        _raw = raw;
        GeneralOffset = generalOffset;
        StorageOffset = storageOffset;
        Counter = counter;
        Tied = tied;
        PartyCount = partyCount;
    }

    /// <summary>Returns one complete encrypted party record, including cached stats.</summary>
    internal ReadOnlyMemory<byte> GetPartyRecord(int slot)
    {
        if (slot < 0 || slot >= PartyCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        int offset = GeneralOffset + PartyOffset + (slot * PartyStride);
        return _raw.AsMemory(offset, PartyRecordSize);
    }

    /// <summary>Returns the stored-Pokemon portion of a party record.</summary>
    internal ReadOnlyMemory<byte> GetPartyPokemonBytes(int slot)
    {
        return GetPartyRecord(slot).Slice(0, BoxedSize);
    }

    /// <summary>Returns one encrypted 136-byte PC-box record.</summary>
    internal ReadOnlyMemory<byte> GetBoxRecord(int box, int slot)
    {
        ValidateBox(box);
        ValidateSlot(slot);

        int offset = StorageOffset + (box * BoxStride) + (slot * BoxedSize);
        return _raw.AsMemory(offset, BoxedSize);
    }

    /// <summary>Returns the raw 40-byte name field; it does not decode the text.</summary>
    internal ReadOnlyMemory<byte> GetBoxNameBytes(int box)
    {
        ValidateBox(box);
        return _raw.AsMemory(
            StorageOffset + BoxNamesOffset + (box * BoxNameStride),
            BoxNameStride);
    }

    internal byte GetBoxWallpaper(int box)
    {
        ValidateBox(box);
        return _raw[StorageOffset + BoxWallpapersOffset + box];
    }

    private static void ValidateBox(int box)
    {
        if (box < 0 || box >= BoxCount)
            throw new ArgumentOutOfRangeException(nameof(box));
    }

    private static void ValidateSlot(int slot)
    {
        if (slot < 0 || slot >= BoxSlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
    }
}

/// <summary>
/// Opens Origin HeartGold saves for structural inspection only.
/// Selection follows the coherent general/storage block rules used by the
/// Origin v4.0.3 reference reader. No save-writing methods are provided.
/// </summary>
internal static class OriginHeartGoldReadOnlyReader
{
    private const int RawSaveSize = 0x80000;
    private const int PartitionSize = 0x40000;
    private const int GeneralSize = 0xF7CC;
    private const int StorageOffset = 0xF800;
    private const int StorageSize = 0x18408;
    private const int FooterSize = 16;

    private const uint MagicPrimary = 0x20060623;
    private const uint PartyCapacity = 6;
    private const int PartyCapacityOffset = 0x90;
    private const int PartyCountOffset = 0x94;

    private const int DeSmuMeFooterSize = 122;
    private static ReadOnlySpan<byte> DeSmuMeMarker => "|-DESMUME SAVE-|"u8;

    private readonly record struct ValidBlock(int BaseOffset, uint Counter);

    internal static bool TryOpen(
        ReadOnlySpan<byte> input,
        out OriginHeartGoldReadOnlySave? save)
    {
        save = null;

        ReadOnlySpan<byte> raw;

        if (input.Length == RawSaveSize)
        {
            raw = input;
        }
        else if (
            input.Length == RawSaveSize + DeSmuMeFooterSize &&
            input[^DeSmuMeMarker.Length..].SequenceEqual(DeSmuMeMarker))
        {
            raw = input[..RawSaveSize];
        }
        else
        {
            return false;
        }

        if (!OriginHeartGoldDetector.IsOriginHeartGold(raw))
            return false;

        if (!TrySelectBlocks(
                raw,
                out int generalOffset,
                out int storageOffset,
                out uint counter,
                out bool tied))
        {
            return false;
        }

        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(
            raw.Slice(generalOffset + PartyCapacityOffset, 4));

        uint partyCount = BinaryPrimitives.ReadUInt32LittleEndian(
            raw.Slice(generalOffset + PartyCountOffset, 4));

        if (capacity != PartyCapacity || partyCount > PartyCapacity)
            return false;

        // Own a private copy. This reader exposes no save-writing operation.
        save = new OriginHeartGoldReadOnlySave(
            raw.ToArray(),
            generalOffset,
            storageOffset,
            counter,
            tied,
            (int)partyCount);

        return true;
    }

    private static bool TrySelectBlocks(
        ReadOnlySpan<byte> raw,
        out int generalOffset,
        out int storageOffset,
        out uint selectedCounter,
        out bool tied)
    {
        generalOffset = 0;
        storageOffset = 0;
        selectedCounter = 0;
        tied = false;

        var generals = new List<ValidBlock>(2);
        var storages = new Dictionary<int, uint>(2);

        foreach (int baseOffset in new[] { 0, PartitionSize })
        {
            if (TryValidateBlock(
                    raw, baseOffset, GeneralSize, 0, out uint generalCounter))
            {
                generals.Add(new ValidBlock(baseOffset, generalCounter));
            }

            if (TryValidateBlock(
                    raw,
                    baseOffset + StorageOffset,
                    StorageSize,
                    1,
                    out uint storageCounter))
            {
                storages.Add(baseOffset, storageCounter);
            }
        }

        if (generals.Count == 0 || storages.Count == 0)
            return false;

        if (generals.Count == 2 &&
            IsNewer(generals[1].Counter, generals[0].Counter))
        {
            (generals[0], generals[1]) = (generals[1], generals[0]);
        }

        ValidBlock? selected = null;

        foreach (var general in generals)
        {
            // Reject the known unsafe recovery case rather than falling back
            // to a mirror that the game itself could fail to load safely.
            if (generals.Count == 2 &&
                storages.Count == 1 &&
                !storages.ContainsKey(general.BaseOffset) &&
                general.Counter == 0)
            {
                return false;
            }

            if (storages.TryGetValue(general.BaseOffset, out uint sc) &&
                sc == general.Counter)
            {
                selected = general;
                break;
            }
        }

        if (selected is null)
            return false;

        var active = selected.Value;

        tied = generals.Count == 2 &&
            generals[0].Counter == generals[1].Counter &&
            storages.TryGetValue(generals[0].BaseOffset, out uint c0) &&
            c0 == generals[0].Counter &&
            storages.TryGetValue(generals[1].BaseOffset, out uint c1) &&
            c1 == generals[1].Counter;

        generalOffset = active.BaseOffset;
        storageOffset = active.BaseOffset + StorageOffset;
        selectedCounter = active.Counter;

        return true;
    }

    private static bool TryValidateBlock(
        ReadOnlySpan<byte> data,
        int blockOffset,
        int blockSize,
        ushort expectedBlockId,
        out uint counter)
    {
        counter = 0;

        if (blockOffset < 0 ||
            blockSize < FooterSize ||
            blockOffset > data.Length - blockSize)
        {
            return false;
        }

        int footer = blockOffset + blockSize - FooterSize;

        uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(footer + 4, 4));

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(footer + 8, 4));

        ushort blockId = BinaryPrimitives.ReadUInt16LittleEndian(
            data.Slice(footer + 12, 2));

        ushort storedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(
            data.Slice(footer + 14, 2));

        if (storedSize != (uint)blockSize ||
            magic != MagicPrimary ||
            blockId != expectedBlockId)
        {
            return false;
        }

        ushort calculatedChecksum = CalculateCrc16Ccitt(
            data.Slice(blockOffset, footer - blockOffset));

        if (storedChecksum != calculatedChecksum)
            return false;

        counter = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(footer, 4));

        return true;
    }

    private static ushort CalculateCrc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (byte value in data)
        {
            crc ^= (ushort)(value << 8);

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)(
                    ((crc << 1) ^
                    ((crc & 0x8000) != 0 ? 0x1021 : 0))
                    & 0xFFFF);
            }
        }

        return crc;
    }

    private static bool IsNewer(uint a, uint b)
    {
        if (a == 0 && b == uint.MaxValue)
            return true;

        if (a == uint.MaxValue && b == 0)
            return false;

        return a > b;
    }
}
