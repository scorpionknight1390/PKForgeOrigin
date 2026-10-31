
using System.Buffers.Binary;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldBlockSelectionTests
{
    private const int RawSaveSize = 0x80000;
    private const int PartitionSize = 0x40000;
    private const int GeneralSize = 0xF7CC;
    private const int StorageStart = 0xF800;
    private const int StorageSize = 0x18408;

    private const int PartyCapacityOffset = 0x90;
    private const int PartyCountOffset = 0x94;

    private const uint Magic = 0x20060623;

    [Fact]
    public void SelectsPartitionWithNewerMatchingCounter()
    {
        var data = new byte[RawSaveSize];

        WritePartition(data, 0, 3, 3, 0x11);
        WritePartition(data, PartitionSize, 4, 4, 0x22);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

        Assert.NotNull(save);
        Assert.Equal(PartitionSize, save!.GeneralOffset);
        Assert.Equal(PartitionSize + StorageStart, save.StorageOffset);
        Assert.Equal(4u, save.Counter);
        Assert.False(save.Tied);
    }

    [Fact]
    public void EqualCountersWithDifferentDataAreMarkedTied()
    {
        var data = new byte[RawSaveSize];

        WritePartition(data, 0, 7, 7, 0x11);
        WritePartition(data, PartitionSize, 7, 7, 0x22);

        Assert.NotEqual(data[0x100], data[PartitionSize + 0x100]);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

        Assert.NotNull(save);
        Assert.True(save!.Tied);
        Assert.Equal(0, save.GeneralOffset);
        Assert.Equal(7u, save.Counter);
    }

    [Fact]
    public void HandlesCounterRolloverFromMaxValueToZero()
    {
        var data = new byte[RawSaveSize];

        WritePartition(data, 0, uint.MaxValue, uint.MaxValue, 0x11);
        WritePartition(data, PartitionSize, 0, 0, 0x22);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

        Assert.NotNull(save);
        Assert.Equal(PartitionSize, save!.GeneralOffset);
        Assert.Equal(0u, save.Counter);
    }

    [Fact]
    public void RejectsPartitionsWithoutMatchingGeneralAndStorageCounters()
    {
        var data = new byte[RawSaveSize];

        WritePartition(data, 0, 10, 11, 0x11);
        WritePartition(data, PartitionSize, 20, 21, 0x22);

        Assert.False(
            OriginHeartGoldReadOnlyReader.TryOpen(data, out _));

        Assert.False(
            OriginHeartGoldDetector.HasValidBlockIntegrity(data));
    }

    private static void WritePartition(
        byte[] data,
        int partitionBase,
        uint generalCounter,
        uint storageCounter,
        byte marker)
    {
        data[partitionBase + 0x100] = marker;
        data[partitionBase + StorageStart + 0x100] = marker;

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(partitionBase + PartyCapacityOffset, 4), 6);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(partitionBase + PartyCountOffset, 4), 0);

        WriteBlock(data, partitionBase, GeneralSize, 0, generalCounter);

        WriteBlock(
            data,
            partitionBase + StorageStart,
            StorageSize,
            1,
            storageCounter);
    }

    private static void WriteBlock(
        byte[] data,
        int offset,
        int size,
        ushort blockId,
        uint counter)
    {
        int footer = offset + size - 16;

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(footer, 4), counter);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(footer + 4, 4), (uint)size);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(footer + 8, 4), Magic);

        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(footer + 12, 2), blockId);

        ushort crc = CalculateCrc16Ccitt(
            data.AsSpan(offset, size - 16));

        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(footer + 14, 2), crc);
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
}
