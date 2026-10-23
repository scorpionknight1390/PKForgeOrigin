
using System.Buffers.Binary;
using System.Text;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldDetectorTests
{
    private const int RawSaveSize = 0x80000;
    private const int PartitionSize = 0x40000;
    private const int GeneralSize = 0xF7CC;
    private const int StorageStart = 0xF800;
    private const int StorageSize = 0x18408;

    private const uint Magic = 0x20060623;

    [Fact]
    public void RecognizesRc5WithBothPartitionsInitialized()
    {
        var data = new byte[RawSaveSize];
        WriteOriginPartition(data, 0);
        WriteOriginPartition(data, PartitionSize);

        Assert.True(OriginHeartGoldDetector.IsOriginHeartGold(data));
    }

    [Fact]
    public void RecognizesRc8WithOnlySecondaryPartitionInitialized()
    {
        var data = new byte[RawSaveSize];
        WriteOriginPartition(data, PartitionSize);

        Assert.True(OriginHeartGoldDetector.IsOriginHeartGold(data));
    }

    [Fact]
    public void RejectsGeneralAndStorageSignaturesInDifferentPartitions()
    {
        var data = new byte[RawSaveSize];

        WriteGeneralBlock(data, 0);
        WriteStorageBlock(data, PartitionSize);

        Assert.False(OriginHeartGoldDetector.IsOriginHeartGold(data));
    }

    [Fact]
    public void RecognizesOriginSaveWithDesmumeFooter()
    {
        var raw = new byte[RawSaveSize];
        WriteOriginPartition(raw, PartitionSize);

        var dsv = new byte[RawSaveSize + 122];
        raw.CopyTo(dsv, 0);

        byte[] marker = Encoding.ASCII.GetBytes("|-DESMUME SAVE-|");
        marker.CopyTo(dsv, dsv.Length - marker.Length);

        Assert.True(OriginHeartGoldDetector.IsOriginHeartGold(dsv));
    }

    [Fact]
    public void RejectsStandardHeartGoldLayout()
    {
        var data = new byte[RawSaveSize];

        // Conventional HGSS general block.
        WriteUInt32(data, 0xF61C, 0xF628);
        WriteUInt32(data, 0xF620, Magic);

        // Conventional HGSS storage block.
        WriteUInt32(data, 0x21A04, 0x12310);
        WriteUInt32(data, 0x21A08, Magic);

        Assert.False(OriginHeartGoldDetector.IsOriginHeartGold(data));
    }

    private static void WriteOriginPartition(byte[] data, int partitionBase)
    {
        WriteGeneralBlock(data, partitionBase);
        WriteStorageBlock(data, partitionBase);
    }

    private static void WriteGeneralBlock(byte[] data, int partitionBase)
    {
        WriteUInt32(data, partitionBase + GeneralSize - 0x0C, GeneralSize);
        WriteUInt32(data, partitionBase + GeneralSize - 0x08, Magic);
    }

    private static void WriteStorageBlock(byte[] data, int partitionBase)
    {
        int footer = StorageStart + StorageSize - 0x0C;

        WriteUInt32(data, partitionBase + footer, StorageSize);
        WriteUInt32(data, partitionBase + footer + 4, Magic);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(offset, sizeof(uint)), value);
    }
}
