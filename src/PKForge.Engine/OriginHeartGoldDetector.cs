
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PKForge.Engine;

/// <summary>
/// Recognizes the expanded save layout used by Origin HeartGold v4.0.3.
/// This class only identifies the layout; it does not parse or edit the save.
/// </summary>
internal static class OriginHeartGoldDetector
{
    private const int RawSaveSize = 0x80000;
    private const int PartitionSize = 0x40000;

    private const int DeSmuMeFooterSize = 122;

    private const int GeneralSize = 0xF7CC;
    private const int StorageStart = 0xF800;
    private const int StorageSize = 0x18408;

    private const uint MagicPrimary = 0x20060623;
    private const uint MagicAlternate = 0x20070903;

    private const int FooterSize = 16;

    private static ReadOnlySpan<byte> DeSmuMeMarker =>
        "|-DESMUME SAVE-|"u8;

    /// <summary>
    /// Recognizes a raw Origin HeartGold save or a known DeSmuME DSV envelope.
    /// Requires a valid general-block signature and a valid storage-block
    /// signature. The duplicated partitions are checked independently,
    /// allowing RC8 saves whose first partition is uninitialized.
    /// </summary>
    internal static bool IsOriginHeartGold(ReadOnlySpan<byte> input)
    {
        ReadOnlySpan<byte> raw;

        if (input.Length == RawSaveSize)
        {
            raw = input;
        }
        else if (
            input.Length == RawSaveSize + DeSmuMeFooterSize &&
            input[^DeSmuMeMarker.Length..].SequenceEqual(DeSmuMeMarker))
        {
            // The tested DeSmuME files contain 512 KiB of save data
            // followed by a 122-byte footer.
            raw = input[..RawSaveSize];
        }
        else
        {
            return false;
        }

        
        for (int partitionBase = 0;
            partitionBase <= PartitionSize;
            partitionBase += PartitionSize)
        {
            bool generalValid = HasBlockSignature(
                raw,
                partitionBase,
                GeneralSize - 0x0C,
                GeneralSize);

            bool storageValid = HasBlockSignature(
                raw,
                partitionBase,
                StorageStart + StorageSize - 0x0C,
                StorageSize);

            if (generalValid && storageValid)
                return true;
        }

        return false;

    }

    private static bool HasBlockSignature(
        ReadOnlySpan<byte> data,
        int partitionBase,
        int footerOffset,
        int expectedSize)
    {
        int offset = partitionBase + footerOffset;

        if (offset < 0 || offset > data.Length - 8)
            return false;

        uint declaredSize =
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

        uint magic =
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));

        return declaredSize == (uint)expectedSize &&
               (magic == MagicPrimary || magic == MagicAlternate);
    }

        
    /// <summary>
    /// Validates the outer CRC16 checksums and verifies that a coherent
    /// general/storage generation exists in the same save mirror.
    /// This does not validate individual Pokémon records.
    /// </summary>
    internal static bool HasValidBlockIntegrity(ReadOnlySpan<byte> input)
    {
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

        var generalCopies = new List<ValidBlock>(2);
        var storageCopies = new Dictionary<int, uint>(2);

        foreach (int baseOffset in new[] { 0, PartitionSize })
        {
            if (TryValidateBlock(
                    raw, baseOffset, GeneralSize, 0,
                    out uint generalCounter))
            {
                generalCopies.Add(new ValidBlock(baseOffset, generalCounter));
            }

            if (TryValidateBlock(
                    raw, baseOffset + StorageStart, StorageSize, 1,
                    out uint storageCounter))
            {
                storageCopies.Add(baseOffset, storageCounter);
            }
        }

        if (generalCopies.Count == 0 || storageCopies.Count == 0)
            return false;

        // Match the native counter comparison, including FFFFFFFF -> 0 rollover.
        if (generalCopies.Count == 2 &&
            IsNewer(generalCopies[1].Counter, generalCopies[0].Counter))
        {
            var first = generalCopies[0];
            generalCopies[0] = generalCopies[1];
            generalCopies[1] = first;
        }

        foreach (var general in generalCopies)
        {
            // Do not accept a fallback that could diverge from native load behavior.
            if (generalCopies.Count == 2 &&
                storageCopies.Count == 1 &&
                !storageCopies.ContainsKey(general.BaseOffset) &&
                general.Counter == 0)
            {
                return false;
            }

            if (storageCopies.TryGetValue(
                    general.BaseOffset, out uint storageCounter) &&
                storageCounter == general.Counter)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct ValidBlock(int BaseOffset, uint Counter);

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

        uint storedSize =
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(footer + 4, 4));

        uint magic =
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(footer + 8, 4));

        ushort blockId =
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(footer + 12, 2));

        ushort storedChecksum =
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(footer + 14, 2));

        if (storedSize != (uint)blockSize ||
            magic != MagicPrimary ||
            blockId != expectedBlockId)
        {
            return false;
        }

        ushort calculatedChecksum =
            CalculateCrc16Ccitt(data.Slice(blockOffset, footer - blockOffset));

        if (storedChecksum != calculatedChecksum)
            return false;

        counter = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(footer, 4));
        return true;
    }

    /// <summary>
    /// CRC16-CCITT: polynomial 0x1021, initial value 0xFFFF,
    /// no reflection and no final XOR.
    /// </summary>
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
