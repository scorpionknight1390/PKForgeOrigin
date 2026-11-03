
using System;
using System.Buffers.Binary;
using System.IO;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldBoxMutationTests
{
    private const string Downloads = @"C:\PKForgeSaves";

    private const int RawSaveSize = 0x80000;
    private const int PartitionSize = 0x40000;
    private const int GeneralSize = 0xF7CC;
    private const int StorageOffset = 0xF800;
    private const int StorageSize = 0x18408;
    private const int StorageChunkSize = 0x183F4;
    private const int FooterSize = 16;

    private const uint Magic = 0x20060623;

    [Fact]
    public void RejectsEqualCounterMirrors()
    {
        byte[] data = new byte[RawSaveSize];

        WritePartition(data, 0, 7, 7, 0x11);
        WritePartition(data, PartitionSize, 7, 7, 0x22);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

        Assert.NotNull(save);
        Assert.True(save!.Tied);

        bool result = OriginHeartGoldBoxMutation.TryCreateCandidate(
            data,
            0,
            0,
            ReadOnlySpan<byte>.Empty,
            out byte[] candidate);

        Assert.False(result);
        Assert.Empty(candidate);
    }

    [Fact]
    public void RejectsEmptyDestinationSlot()
    {
        byte[] original = File.ReadAllBytes(
            Path.Combine(
                Downloads,
                "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var save));

        Assert.NotNull(save);

        Assert.Equal(
            0,
            new PK4(save!.GetBoxRecord(0, 0).ToArray()).Species);

        byte[] editedRecord = CreateEditedRc5Record();

        Assert.False(
            OriginHeartGoldBoxMutation.TryCreateCandidate(
                original,
                0,
                0,
                editedRecord,
                out byte[] candidate));

        Assert.Empty(candidate);
    }

    [Fact]
    public void RejectsEditedRecordWithInvalidChecksum()
    {
        byte[] original = ReadRc5Dsv();
        byte[] editedRecord = CreateEditedRc5Record();

        // Corrompe um byte dos dados criptografados.
        editedRecord[8] ^= 0x01;

        Assert.False(
            OriginHeartGoldBoxMutation.TryCreateCandidate(
                original,
                0,
                0,
                editedRecord,
                out byte[] candidate));

        Assert.Empty(candidate);
    }

    [Fact]
    public void RejectsChangedPokemonPid()
    {
        byte[] original = ReadRc5Dsv();

        var pokemon = new PK4(CreateEditedRc5Record());

        pokemon.PID ^= 1u;

        byte[] changedRecord = Pack(pokemon);

        Assert.True(new PK4(changedRecord.ToArray()).Valid);

        Assert.False(
            OriginHeartGoldBoxMutation.TryCreateCandidate(
                original,
                0,
                0,
                changedRecord,
                out byte[] candidate));

        Assert.Empty(candidate);
    }

    [Fact]
    public void RejectsChangedPokemonSpecies()
    {
        byte[] original = ReadRc5Dsv();

        var pokemon = new PK4(CreateEditedRc5Record());

        pokemon.Species = 960;

        byte[] changedRecord = Pack(pokemon);

        Assert.True(new PK4(changedRecord.ToArray()).Valid);

        Assert.False(
            OriginHeartGoldBoxMutation.TryCreateCandidate(
                original,
                0,
                0,
                changedRecord,
                out byte[] candidate));

        Assert.Empty(candidate);
    }

    [Fact]
    public void SupportsRawSaveWithoutDsvFooter()
    {
        byte[] original = File.ReadAllBytes(
            Path.Combine(
                Downloads,
                "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav"));

        byte[] originalSnapshot = original.ToArray();

        Assert.Equal(RawSaveSize, original.Length);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var save));

        Assert.NotNull(save);

        Assert.Equal(
            959,
            new PK4(save!.GetBoxRecord(0, 0).ToArray()).Species);

        byte[] editedRecord = CreateEditedRc5Record();

        Assert.True(
            OriginHeartGoldBoxMutation.TryCreateCandidate(
                original,
                0,
                0,
                editedRecord,
                out byte[] candidate));

        Assert.Equal(RawSaveSize, candidate.Length);

        Assert.False(original.AsSpan().SequenceEqual(candidate));

        // O arquivo original permanece intacto.
        Assert.True(original.AsSpan().SequenceEqual(originalSnapshot));

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updated));

        Assert.NotNull(updated);

        var reopened = new PK4(
            updated!.GetBoxRecord(0, 0).ToArray());

        Assert.Equal(959, reopened.Species);
        Assert.True(reopened.ChecksumValid);
    }

    [Fact]
    public void UpdatesInnerChecksumWhenItWasOriginallyValid()
    {
        byte[] original = ReadRc5Dsv();

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var save));

        Assert.NotNull(save);

        int storage = save!.StorageOffset;

        // Prepara uma cópia com CRC interno válido.
        // Também atualiza o CRC externo para manter a estrutura válida.
        WriteStorageChecksums(original, storage);

        ushort originalInnerChecksum =
            BinaryPrimitives.ReadUInt16LittleEndian(
                original.AsSpan(storage + StorageChunkSize, 2));

        Assert.Equal(
            originalInnerChecksum,
            CalculateCrc16Ccitt(
                original.AsSpan(storage, StorageChunkSize)));

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var prepared));

        Assert.NotNull(prepared);

        byte[] editedRecord = CreateEditedRc5Record();

        Assert.True(
            OriginHeartGoldBoxMutation.TryCreateCandidate(
                original,
                0,
                0,
                editedRecord,
                out byte[] candidate));

        // O gravador deve recalcular o CRC interno após a alteração.
        ushort candidateInnerChecksum =
            BinaryPrimitives.ReadUInt16LittleEndian(
                candidate.AsSpan(storage + StorageChunkSize, 2));

        ushort calculatedInnerChecksum =
            CalculateCrc16Ccitt(
                candidate.AsSpan(storage, StorageChunkSize));

        Assert.Equal(
            calculatedInnerChecksum,
            candidateInnerChecksum);

        // O CRC externo também deve continuar válido.
        int footer = storage + StorageSize - FooterSize;

        ushort candidateOuterChecksum =
            BinaryPrimitives.ReadUInt16LittleEndian(
                candidate.AsSpan(footer + 14, 2));

        ushort calculatedOuterChecksum =
            CalculateCrc16Ccitt(
                candidate.AsSpan(storage, StorageSize - FooterSize));

        Assert.Equal(
            calculatedOuterChecksum,
            candidateOuterChecksum);

        // A cópia preparada usada como entrada não deve ser modificada.
        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(
                original,
                out var stillValid));

        Assert.NotNull(stillValid);

        Assert.Equal(
            originalInnerChecksum,
            BinaryPrimitives.ReadUInt16LittleEndian(
                original.AsSpan(storage + StorageChunkSize, 2)));
    }

    private static byte[] ReadRc5Dsv()
    {
        return File.ReadAllBytes(
            Path.Combine(
                Downloads,
                "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));
    }

    private static byte[] CreateEditedRc5Record()
    {
        byte[] data = ReadRc5Dsv();

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

        Assert.NotNull(save);

        var pokemon = new PK4(
            save!.GetBoxRecord(0, 0).ToArray());

        Assert.Equal(959, pokemon.Species);
        Assert.True(pokemon.Valid);

        pokemon.OriginalTrainerFriendship =
            (byte)(pokemon.OriginalTrainerFriendship ^ 1);

        return Pack(pokemon);
    }

    private static byte[] Pack(PK4 pokemon)
    {
        byte[] result = new byte[136];

        pokemon.WriteEncryptedDataStored(result);

        return result;
    }

    private static void WriteStorageChecksums(
        byte[] data,
        int storage)
    {
        ushort inner = CalculateCrc16Ccitt(
            data.AsSpan(storage, StorageChunkSize));

        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(storage + StorageChunkSize, 2),
            inner);

        int footer = storage + StorageSize - FooterSize;

        ushort outer = CalculateCrc16Ccitt(
            data.AsSpan(storage, StorageSize - FooterSize));

        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(footer + 14, 2),
            outer);
    }

    private static void WritePartition(
        byte[] data,
        int partitionBase,
        uint generalCounter,
        uint storageCounter,
        byte marker)
    {
        data[partitionBase + 0x100] = marker;
        data[partitionBase + StorageOffset + 0x100] = marker;

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(partitionBase + 0x90, 4), 6);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(partitionBase + 0x94, 4), 0);

        WriteBlock(
            data,
            partitionBase,
            GeneralSize,
            0,
            generalCounter);

        WriteBlock(
            data,
            partitionBase + StorageOffset,
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
        int footer = offset + size - FooterSize;

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(footer, 4), counter);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(footer + 4, 4), (uint)size);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(footer + 8, 4), Magic);

        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(footer + 12, 2), blockId);

        ushort crc = CalculateCrc16Ccitt(
            data.AsSpan(offset, size - FooterSize));

        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(footer + 14, 2), crc);
    }

    private static ushort CalculateCrc16Ccitt(
        ReadOnlySpan<byte> data)
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


[Fact]
public void ExportRc5CandidateForEmulator_WhenExplicitlyEnabled()
{
    // A exportação só acontece quando explicitamente autorizada.
    if (Environment.GetEnvironmentVariable(
            "PKFORGE_ORIGIN_EXPORT_TEST") != "1")
        return;

    string inputPath = Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");

    string outputPath = Path.Combine(
        Downloads,
       "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_PKForge_TEST_PPUP.sav");

    // Nunca sobrescreve um candidato existente.
    Assert.False(
        File.Exists(outputPath),
        $"O arquivo de teste já existe: {outputPath}");

    byte[] original = File.ReadAllBytes(inputPath);

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(
            original, out var save));

    Assert.NotNull(save);
    Assert.False(save!.Tied);

    var pokemon = new PK4(
        save.GetBoxRecord(0, 0).ToArray());

    Assert.Equal(959, pokemon.Species);
    Assert.True(pokemon.Valid);

    int oldPP = pokemon.Move4_PP;
    int oldPPUps = pokemon.Move4_PPUps;

    Assert.True(oldPPUps < 3,
        "Não há espaço para adicionar outro PP Up.");

    int expectedPPUps = oldPPUps + 1;

    // Aumenta somente o PP máximo.
    // Preserva o PP atual original.
    pokemon.Move4_PPUps = expectedPPUps;

    byte[] editedRecord = Pack(pokemon);

    Assert.True(
        OriginHeartGoldBoxMutation.TryCreateCandidate(
            original,
            0,
            0,
            editedRecord,
            out byte[] candidate));

    Assert.Equal(original.Length, candidate.Length);

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(
            candidate, out var verification));

    Assert.NotNull(verification);

    var reopened = new PK4(
        verification!.GetBoxRecord(0, 0).ToArray());

    Assert.Equal(959, reopened.Species);
    Assert.Equal(oldPP, reopened.Move4_PP);
    Assert.Equal(expectedPPUps, reopened.Move4_PPUps);
    Assert.True(reopened.ChecksumValid);

    // Extrai somente os 512 KiB do save RAW.
    // Os 122 bytes do rodapé DeSmuME não entram no SAV.
    const int RawSaveSize = 0x80000;
    const int DsvFooterSize = 122;

    Assert.Equal(RawSaveSize + DsvFooterSize, candidate.Length);

    using (var stream = new FileStream(
        outputPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None))
    {
        stream.Write(candidate.AsSpan(0, RawSaveSize));
        stream.Flush(true);
    }

    Console.WriteLine($"Golpe: {pokemon.Move4}");
    Console.WriteLine($"PP atual preservado: {oldPP}");
    Console.WriteLine($"PP Ups anteriores: {oldPPUps}");
    Console.WriteLine($"PP Ups novos: {expectedPPUps}");
    Console.WriteLine($"Arquivo criado: {outputPath}");
    Console.WriteLine($"Tamanho: {candidate.Length}");
}
[Fact]
public void DiagnoseRc5TinkatonMovePPUps()
{
    byte[] data = ReadRc5Dsv();

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

    Assert.NotNull(save);

    var pokemon = new PK4(
        save!.GetBoxRecord(0, 0).ToArray());

    Console.WriteLine($"Tinkaton: {pokemon.Nickname}");

    Console.WriteLine($"Golpe 1: {pokemon.Move1}, PP={pokemon.Move1_PP}, PP Ups={pokemon.Move1_PPUps}");
    Console.WriteLine($"Golpe 2: {pokemon.Move2}, PP={pokemon.Move2_PP}, PP Ups={pokemon.Move2_PPUps}");
    Console.WriteLine($"Golpe 3: {pokemon.Move3}, PP={pokemon.Move3_PP}, PP Ups={pokemon.Move3_PPUps}");
    Console.WriteLine($"Golpe 4: {pokemon.Move4}, PP={pokemon.Move4_PP}, PP Ups={pokemon.Move4_PPUps}");
}

[Fact]
public void DiagnoseDeSmuMEAfterGameSave()
{
    string path =
        @"C:\PKForgeDeSmuME-Test\Backup-After-GameSave\Origin_After_GameSave.dsv";

    byte[] data = File.ReadAllBytes(path);

    Console.WriteLine($"Tamanho: {data.Length}");
    Console.WriteLine(
        $"Origin reconhecido: {OriginHeartGoldDetector.IsOriginHeartGold(data)}");

    Console.WriteLine(
        $"CRC dos blocos válido: {OriginHeartGoldDetector.HasValidBlockIntegrity(data)}");

    bool opened = OriginHeartGoldReadOnlyReader.TryOpen(
        data, out var save);

    Console.WriteLine($"Leitor abriu: {opened}");

    Assert.True(opened, "O leitor não conseguiu abrir o save pós-jogo.");

    Assert.NotNull(save);

    Console.WriteLine($"Tied: {save!.Tied}");
    Console.WriteLine($"GeneralOffset: 0x{save.GeneralOffset:X}");
    Console.WriteLine($"StorageOffset: 0x{save.StorageOffset:X}");
    Console.WriteLine($"Counter: {save.Counter}");
    Console.WriteLine($"PartyCount: {save.PartyCount}");

    int found = 0;

    for (int box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
    {
        for (int slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
        {
            var pk = new PK4(
                save.GetBoxRecord(box, slot).ToArray());

            if (pk.Species == 0)
                continue;

            if (pk.Species == 959)
            {
                found++;

                Console.WriteLine(
                    $"Tinkaton: Caixa={box + 1}, Slot={slot + 1}, " +
                    $"PP4={pk.Move4_PP}, PP Ups={pk.Move4_PPUps}, " +
                    $"Checksum={pk.ChecksumValid}");
            }
        }
    }

    Console.WriteLine($"Tinkatons encontrados nas caixas: {found}");
}
}
