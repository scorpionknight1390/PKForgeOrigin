
using System;
using System.IO;
using System.Linq;
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
            LayoutRiskKind.PartiallySupported,
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
    public async Task SessionEditedOriginCandidateIsRefusedBeforeBackupOrDiskWrite()
    {
        var original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        var originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(
            original, "Origin HeartGold test");

        // Confirma o Pokémon que será editado.
        var before = session.ReadEntity(0, 0);

        Assert.False(before.IsEmpty);
        Assert.Equal(959, before.Species);

        int expectedFriendship = before.Friendship == 255
            ? 254
            : before.Friendship + 1;

        // Produz uma alteração real pela sessão integrada.
        session.ApplyEdit(0, 0, new EntityEdit(
            Friendship: expectedFriendship));

        byte[] candidate = session.Serialize().ToArray();

        Assert.False(original.AsSpan().SequenceEqual(candidate));
        Assert.True(Engine.Validate(candidate));

        Assert.Equal(
            LayoutRiskKind.PartiallySupported,
            Engine.AssessLayoutRisk(candidate)?.Kind);

        // Tenta gravar o candidato usando a proteção real.
        var backups = new CountingBackupService();
        var access = new CountingFileAccess();

        var writer = new SafeSaveWriter(Engine, backups, access);

        var refusal = await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteScopedAsync(
                "origin-session-edited",
                Snapshot(original),
                candidate,
                WriteScope.Only(new SlotRef(0, 0))).AsTask());

        // Não pode pedir confirmação para uma operação Origin sem autorização.
        Assert.False(refusal.RequiresConfirmation);

        // Não pode criar backup nem gravar no disco.
        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, access.Writes);

        // A edição permanece apenas na memória da sessão.
        Assert.Equal(expectedFriendship,
            session.ReadEntity(0, 0).Friendship);

        // Os bytes originais permanecem intactos.
        Assert.Equal(originalBefore, original);
        Assert.True(
            session.Snapshot.OriginalBytes.Span.SequenceEqual(originalBefore));
}
    [Fact]
    public async Task AuthorizedBoxedPokemonPPEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        const int box = 0;
        const int slot = 7;

        using var session = new OriginHeartGoldEngineSession(original, "Origin PP test");

        Assert.Equal(629, session.ReadEntity(box, slot).Species);

        var beforeDetails = session.GetMoveDetails(box, slot);
        int moveIndex = Array.FindIndex(beforeDetails.Moves.ToArray(), move => move.Move > 0);
        Assert.InRange(moveIndex, 0, 3);

        var currentMove = beforeDetails.Moves[moveIndex];
        int newPP = currentMove.PP > 0
            ? currentMove.PP - 1
            : Math.Min(1, currentMove.MaxPP);

        Assert.NotEqual(currentMove.PP, newPP);

        int[] requestedPP = beforeDetails.Moves.Select(move => move.PP).ToArray();
        requestedPP[moveIndex] = newPP;

        session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PP: requestedPP));

        Assert.Equal(newPP, session.GetMoveDetails(box, slot).Moves[moveIndex].PP);

        byte[] candidate = session.Serialize().ToArray();

        Assert.False(originalBefore.AsSpan().SequenceEqual(candidate));
        Assert.True(Engine.Validate(candidate));
        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            WriteScope.Only(new SlotRef(box, slot)),
            SaveAction.EditMon));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedScopedAsync(
            "origin-move-pp",
            Snapshot(original),
            candidate,
            WriteScope.Only(new SlotRef(box, slot)),
            SaveAction.EditMon,
            "Origin HeartGold PP edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task AuthorizedBoxedPokemonPPUpsEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        const int box = 0;
        const int slot = 7;

        using var session = new OriginHeartGoldEngineSession(original, "Origin PP Ups test");

        Assert.Equal(629, session.ReadEntity(box, slot).Species);

        var beforeDetails = session.GetMoveDetails(box, slot);
        int moveIndex = Array.FindIndex(beforeDetails.Moves.ToArray(), move => move.Move > 0);
        Assert.InRange(moveIndex, 0, 3);

        var currentMove = beforeDetails.Moves[moveIndex];
        int newUps = currentMove.PPUps == 3
            ? 2
            : currentMove.PPUps + 1;

        int[] requestedUps = beforeDetails.Moves.Select(move => move.PPUps).ToArray();
        requestedUps[moveIndex] = newUps;

        session.ApplyMoveDetails(box, slot, new MoveDetailsEdit(PPUps: requestedUps));

        var afterMove = session.GetMoveDetails(box, slot).Moves[moveIndex];
        Assert.Equal(newUps, afterMove.PPUps);
        Assert.Equal(afterMove.MaxPP, afterMove.PP);

        byte[] candidate = session.Serialize().ToArray();

        Assert.False(originalBefore.AsSpan().SequenceEqual(candidate));
        Assert.True(Engine.Validate(candidate));
        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            WriteScope.Only(new SlotRef(box, slot)),
            SaveAction.EditMon));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedScopedAsync(
            "origin-move-ppups",
            Snapshot(original),
            candidate,
            WriteScope.Only(new SlotRef(box, slot)),
            SaveAction.EditMon,
            "Origin HeartGold PP Ups edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }
    [Fact]
    public async Task AuthorizedBoxedPokemonEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        var before = session.ReadEntity(0, 0);
        Assert.False(before.IsEmpty);
        Assert.Equal(959, before.Species);

        int expectedFriendship = before.Friendship == 255
            ? 254
            : before.Friendship + 1;

        session.ApplyEdit(0, 0, new EntityEdit(
            Friendship: expectedFriendship));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));
        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            WriteScope.Only(new SlotRef(0, 0)),
            SaveAction.EditMon));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedScopedAsync(
            "origin-authorized-pokemon",
            Snapshot(original),
            candidate,
            WriteScope.Only(new SlotRef(0, 0)),
            SaveAction.EditMon,
            "Origin HeartGold Pokémon edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task AuthorizedTrainerEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        var before = session.GetTrainer();

        session.SetTrainer(new TrainerInfo(
            "X",
            before.TID,
            before.SID,
            before.Money,
            before.Gender));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));
        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            scope: null,
            action: SaveAction.EditTrainer));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedAsync(
            "origin-authorized-trainer",
            Snapshot(original),
            candidate,
            SaveAction.EditTrainer,
            "Origin HeartGold trainer edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }
    [Fact]
    public async Task AuthorizedInventoryEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        const string pouchName = "Items";

        var existingIds = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Select(i => i.Id)
            .ToHashSet();

        int itemId = session.GetPouchLegalItems(pouchName)
            .First(id => !existingIds.Contains(id));

        Assert.True(itemId > 0);

        Assert.Equal(3, session.SetItemCount(pouchName, itemId, 3));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));
        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            scope: null,
            action: SaveAction.EditInventory));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedAsync(
            "origin-authorized-inventory",
            Snapshot(original),
            candidate,
            SaveAction.EditInventory,
            "Origin HeartGold inventory edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task AuthorizedDexEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        const int species = 1025;

        var before = session.GetDexEntry(species);

        // Garante uma mudança real sem criar a condição capturado e não visto.
        session.SetDexEntry(species, true, !before.Caught);

        var after = session.GetDexEntry(species);
        Assert.True(after.Seen);
        Assert.NotEqual(before.Caught, after.Caught);

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));
        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            scope: null,
            action: SaveAction.EditDex));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedAsync(
            "origin-authorized-dex",
            Snapshot(original),
            candidate,
            SaveAction.EditDex,
            "Origin HeartGold Pokédex edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }
    [Fact]
    public async Task AuthorizedInventoryBatchEditBacksUpBeforeWriting()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        var existingItemIds = session.GetBag()
            .Single(p => p.Name == "Items")
            .Items.Select(i => i.Id)
            .ToHashSet();

        int[] itemIds = session.GetPouchLegalItems("Items")
            .Where(id => !existingItemIds.Contains(id))
            .Take(2)
            .ToArray();

        Assert.Equal(2, itemIds.Length);

        var existingKeyItemIds = session.GetBag()
            .Single(p => p.Name == "Key Items")
            .Items.Select(i => i.Id)
            .ToHashSet();

        int keyItemId = session.GetPouchLegalItems("Key Items")
            .First(id => !existingKeyItemIds.Contains(id));

        Assert.True(keyItemId > 0);

        Assert.Equal(1, session.SetItemCount("Items", itemIds[0], 1));
        Assert.Equal(1, session.SetItemCount("Items", itemIds[1], 1));
        Assert.Equal(1, session.SetItemCount("Key Items", keyItemId, 1));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));

        Assert.Null(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            scope: null,
            action: SaveAction.EditInventory));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await writer.WriteAuthorizedAsync(
            "origin-authorized-inventory-batch",
            Snapshot(original),
            candidate,
            SaveAction.EditInventory,
            "Origin HeartGold inventory batch edit test");

        Assert.Equal(1, backups.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(originalBefore, backups.LastSourceBytes);
        Assert.Equal(candidate, access.LastWrittenBytes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task AuthorizedPokemonEditRejectsAdditionalTrainerChangesBeforeBackupOrWrite()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        var pokemon = session.ReadEntity(0, 0);
        Assert.False(pokemon.IsEmpty);

        int friendship = pokemon.Friendship == 255
            ? 254
            : pokemon.Friendship + 1;

        session.ApplyEdit(0, 0, new EntityEdit(Friendship: friendship));

        var trainer = session.GetTrainer();
        string newName = trainer.Name == "X" ? "Y" : "X";

        session.SetTrainer(new TrainerInfo(
            newName,
            trainer.TID,
            trainer.SID,
            trainer.Money,
            trainer.Gender));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));

        Assert.NotNull(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            WriteScope.Only(new SlotRef(0, 0)),
            SaveAction.EditMon));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteAuthorizedScopedAsync(
                "origin-pokemon-extra-trainer",
                Snapshot(original),
                candidate,
                WriteScope.Only(new SlotRef(0, 0)),
                SaveAction.EditMon,
                "Invalid combined Pokémon and trainer edit").AsTask());

        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, access.Writes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task AuthorizedTrainerEditRejectsAdditionalDexChangesBeforeBackupOrWrite()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        var trainer = session.GetTrainer();
        string newName = trainer.Name == "X" ? "Y" : "X";

        session.SetTrainer(new TrainerInfo(
            newName,
            trainer.TID,
            trainer.SID,
            trainer.Money,
            trainer.Gender));

        var dexBefore = session.GetDexEntry(1025);
        session.SetDexEntry(1025, true, !dexBefore.Caught);

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));

        Assert.NotNull(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            scope: null,
            action: SaveAction.EditTrainer));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteAuthorizedAsync(
                "origin-trainer-extra-dex",
                Snapshot(original),
                candidate,
                SaveAction.EditTrainer,
                "Invalid combined trainer and Dex edit").AsTask());

        Assert.Equal(0, backups.Calls);
        Assert.Equal(0, access.Writes);
        Assert.Equal(originalBefore, original);
    }

    [Fact]
    public async Task AuthorizedDexEditRejectsAdditionalTrainerChangesBeforeBackupOrWrite()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        using var session = new OriginHeartGoldEngineSession(original, "Origin test");

        var dexBefore = session.GetDexEntry(1025);
        session.SetDexEntry(1025, true, !dexBefore.Caught);

        var trainer = session.GetTrainer();
        string newName = trainer.Name == "X" ? "Y" : "X";

        session.SetTrainer(new TrainerInfo(
            newName,
            trainer.TID,
            trainer.SID,
            trainer.Money,
            trainer.Gender));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(Engine.Validate(candidate));

        Assert.NotNull(Engine.CheckAuthorizedWriteSafety(
            originalBefore,
            candidate,
            scope: null,
            action: SaveAction.EditDex));

        var backups = new CountingBackupService();
        var access = new CountingFileAccess();
        var writer = new SafeSaveWriter(Engine, backups, access);

        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() =>
            writer.WriteAuthorizedAsync(
                "origin-dex-extra-trainer",
                Snapshot(original),
                candidate,
                SaveAction.EditDex,
                "Invalid combined Dex and trainer edit").AsTask());

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


    [Fact]
    public async Task AuthorizedInventoryEditWritesToDiskAndReopensWithBagChanges()
    {
        byte[] original = Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");
        byte[] originalBefore = original.ToArray();

        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"PKForge-OriginInventory-{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempRoot);

        try
        {
            string tempSavePath = Path.Combine(tempRoot, "origin-inventory.dsv");
            string backupRoot = Path.Combine(tempRoot, "backups");

            await File.WriteAllBytesAsync(tempSavePath, original);

            using var session = new OriginHeartGoldEngineSession(
                original, "Origin inventory disk test");

            const string pouchName = "Items";

            var existingIds = session.GetBag()
                .Single(p => p.Name == pouchName)
                .Items.Select(i => i.Id)
                .ToHashSet();

            int itemId = session.GetPouchLegalItems(pouchName)
                .First(id => !existingIds.Contains(id));

            Assert.True(itemId > 0);
            Assert.Equal(3, session.SetItemCount(pouchName, itemId, 3));

            byte[] candidate = session.Serialize().ToArray();

            var backupService = new FileBackupService(backupRoot);
            var access = new TemporaryDiskFileAccess();
            var writer = new SafeSaveWriter(Engine, backupService, access);

            var receipt = await writer.WriteAuthorizedAsync(
                tempSavePath,
                Snapshot(original),
                candidate,
                SaveAction.EditInventory,
                "Origin HeartGold inventory disk persistence test");

            Assert.True(receipt.Changed);

            byte[] diskBytes = (await access.ReadAsync(tempSavePath)).ToArray();

            Assert.Equal(candidate, diskBytes);
            Assert.True(Engine.Validate(diskBytes));

            var backupInfo = Assert.Single(await backupService.ListAsync());

            byte[] backupBytes =
                (await backupService.ReadAsync(backupInfo.BackupId)).ToArray();

            Assert.Equal(originalBefore, backupBytes);

            using var reopened = Engine.OpenSession(
                diskBytes, "Origin HeartGold inventory disk reopen test");

            var persistedItem = reopened.GetBag()
                .Single(p => p.Name == pouchName)
                .Items.Single(i => i.Id == itemId);

            Assert.Equal(3, persistedItem.Count);

            Assert.Equal(originalBefore, original);
            Assert.Equal(originalBefore, Load("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class TemporaryDiskFileAccess : ISaveFileAccess
    {
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            string documentId,
            CancellationToken cancellationToken = default)
        {
            return await File.ReadAllBytesAsync(
                documentId, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask WriteAtomicallyAsync(
            string documentId,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string targetPath = Path.GetFullPath(documentId);
            string directory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("Diretório de destino inválido.");

            string tempPath = Path.Combine(
                directory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await File.WriteAllBytesAsync(
                    tempPath, bytes.ToArray(), cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
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
