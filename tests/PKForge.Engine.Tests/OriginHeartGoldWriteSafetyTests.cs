
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldWriteSafetyTests
{
    private const string SaveDirectory = @"C:\PKForgeSaves";
    private const int PartitionSize = 0x40000;
    private const int GeneralSize = 0xF7CC;

    private static readonly SaveEngine Engine = new();

    private static byte[] Load(string fileName)
    {
        var path = Path.Combine(SaveDirectory, fileName);

        Assert.True(File.Exists(path), $"Arquivo de teste não encontrado: {path}");

        return File.ReadAllBytes(path);
    }

    private static SaveSnapshot Snapshot(byte[] bytes) =>
        new("Origin HeartGold", 4, bytes, [], "Origin HeartGold test save");

    private static void CorruptGeneralChecksums(byte[] bytes)
    {
        bytes[GeneralSize - 2] ^= 0x01;
        bytes[PartitionSize + GeneralSize - 2] ^= 0x01;
    }

    [Fact]
    public async Task NormalSlotWriteIsRefusedWithoutBackupOrDiskWrite()
    {
        var original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        var candidate = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv");

        Assert.True(Engine.Validate(original));
        Assert.True(Engine.Validate(candidate));
        Assert.False(original.AsSpan().SequenceEqual(candidate));

        Assert.Equal(
            LayoutRiskKind.CorruptingLayout,
            Engine.AssessLayoutRisk(original)?.Kind);

        var originalBefore = original.ToArray();
        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        var refusal = await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteScopedAsync(
                "origin-normal-write",
                Snapshot(original),
                candidate,
                WriteScope.Only(new SlotRef(0, 0))).AsTask());

        Assert.False(refusal.RequiresConfirmation);
        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, access.Writes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task ValidWholeFileRestoreIsAllowedAndBacksUpCurrentBytesFirst()
    {
        var validBackup = Load(
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav");

        Assert.True(Engine.Validate(validBackup));

        // Simula o estado atual danificado, sem modificar o arquivo original.
        var damagedCurrent = validBackup.ToArray();
        CorruptGeneralChecksums(damagedCurrent);

        Assert.False(Engine.Validate(damagedCurrent));

        var damagedBefore = damagedCurrent.ToArray();
        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.RestoreAsync(
            "origin-restore",
            Snapshot(damagedCurrent),
            validBackup,
            "Origin HeartGold restore test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);

        // A cópia de segurança deve conter o estado anterior à restauração.
        Assert.Equal(damagedBefore, backups.LastSourceBytes);

        // A gravação simulada deve receber exatamente o backup válido.
        Assert.Equal(validBackup, access.LastWrittenBytes);

        // O array que representa o estado anterior não pode ser modificado.
        Assert.Equal(damagedBefore, damagedCurrent);
    }

    [Fact]
    public async Task InvalidWholeFileRestoreIsRejectedBeforeBackupOrWrite()
    {
        var validOriginal = Load(
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav");

        Assert.True(Engine.Validate(validOriginal));

        var invalidCandidate = validOriginal.ToArray();
        CorruptGeneralChecksums(invalidCandidate);

        Assert.False(Engine.Validate(invalidCandidate));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.RestoreAsync(
                "origin-invalid-restore",
                Snapshot(validOriginal),
                invalidCandidate,
                "Invalid Origin restore test").AsTask());

        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, access.Writes);
    }

    private sealed class CountingBackupService : IBackupService
    {
        public int Calls { get; private set; }

        public byte[]? LastSourceBytes { get; private set; }

        public ValueTask<BackupReceipt> CreateAsync(
            SaveSnapshot source,
            string? changeDescription = null,
            string? documentId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;
            LastSourceBytes = source.OriginalBytes.ToArray();

            return ValueTask.FromResult(
                new BackupReceipt(
                    $"origin-backup-{Calls}",
                    DateTimeOffset.UtcNow,
                    "test-hash"));
        }

        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            string backupId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingFileAccess : ISaveFileAccess
    {
        public int Writes { get; private set; }

        public byte[]? LastWrittenBytes { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            string documentId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WriteAtomicallyAsync(
            string documentId,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Writes++;
            LastWrittenBytes = bytes.ToArray();

            return ValueTask.CompletedTask;
        }
    }
}
