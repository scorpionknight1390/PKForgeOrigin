
using System.Buffers.Binary;
using System.IO;
using PKForge.Engine;
using PKForge.Domain;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldGeneralMutationTests
{
    private const string SavePath =
        @"C:\PKForgeSaves\Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv";

    private const int RawSaveSize = 0x80000;
    private const int DsvFooterSize = 122;
    private const int PartitionSize = 0x40000;
    private const int StorageSize = 0x18408;
    private const int MoneyOffset = 0x78;
    private const uint MaximumMoney = 9_999_999;

    [Fact]
    public void CandidateChangesMoneyAndPreservesOtherRegions()
    {
        Assert.True(File.Exists(SavePath), $"Save não encontrado: {SavePath}");

        byte[] original = File.ReadAllBytes(SavePath);

        Assert.Equal(RawSaveSize + DsvFooterSize, original.Length);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var before),
            "O leitor não conseguiu abrir o save original.");

        Assert.NotNull(before);
        Assert.False(before!.Tied, "O save possui espelhos empatados.");

        uint oldMoney = BinaryPrimitives.ReadUInt32LittleEndian(
            original.AsSpan(before.GeneralOffset + MoneyOffset, 4));

        uint newMoney = oldMoney == MaximumMoney
            ? oldMoney - 1
            : oldMoney + 1;

        bool created = OriginHeartGoldGeneralMutation.TryCreateCandidate(
            original,
            general =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    general.AsSpan(MoneyOffset, 4),
                    newMoney);

                return true;
            },
            out byte[] candidate);

        Assert.True(created, "O mutador não criou o candidato.");
        Assert.Equal(original.Length, candidate.Length);

        Assert.True(
            OriginHeartGoldDetector.HasValidBlockIntegrity(candidate),
            "O candidato não manteve a integridade dos blocos.");

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var after),
            "O leitor não conseguiu abrir o candidato.");

        Assert.NotNull(after);
        Assert.False(after!.Tied);

        Assert.Equal(before.GeneralOffset, after.GeneralOffset);
        Assert.Equal(before.StorageOffset, after.StorageOffset);
        Assert.Equal(before.Counter, after.Counter);

        uint candidateMoney = BinaryPrimitives.ReadUInt32LittleEndian(
            candidate.AsSpan(after.GeneralOffset + MoneyOffset, 4));

        Assert.Equal(newMoney, candidateMoney);
        Assert.NotEqual(oldMoney, candidateMoney);

        // A Storage ativa deve permanecer byte por byte igual.
        Assert.Equal(
            original.AsSpan(before.StorageOffset, StorageSize).ToArray(),
            candidate.AsSpan(after.StorageOffset, StorageSize).ToArray());

        // A partição inativa deve permanecer byte por byte igual.
        int inactiveOffset = before.GeneralOffset == 0
            ? PartitionSize
            : 0;

        Assert.Equal(
            original.AsSpan(inactiveOffset, PartitionSize).ToArray(),
            candidate.AsSpan(inactiveOffset, PartitionSize).ToArray());

        // O rodapé do arquivo DeSmuME deve permanecer intacto.
        Assert.Equal(
            original.AsSpan(RawSaveSize, DsvFooterSize).ToArray(),
            candidate.AsSpan(RawSaveSize, DsvFooterSize).ToArray());

        // O arquivo original no disco não pode ter sido modificado.
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void RejectsNoOperation()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        bool created = OriginHeartGoldGeneralMutation.TryCreateCandidate(
            original,
            _ => true,
            out byte[] candidate);

        Assert.False(created);
        Assert.Empty(candidate);
    }

    [Fact]
    public void RejectsChangesToInternalChecksumBytes()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        bool created = OriginHeartGoldGeneralMutation.TryCreateCandidate(
            original,
            general =>
            {
                // 0x8C é a posição do CRC interno do chunk iniciado em 0x60.
                general[0x8C] ^= 0xFF;
                return true;
            },
            out byte[] candidate);

        Assert.False(created);
        Assert.Empty(candidate);
    }

    [Fact]
    public void SessionCanEditTrainerCardInMemory()
    {
        Assert.True(File.Exists(SavePath));

        byte[] original = File.ReadAllBytes(SavePath);
        using var session = new OriginHeartGoldEngineSession(original, "teste");

        var current = session.GetTrainer();

        uint newMoney = current.Money >= 9_999_999u
            ? 9_999_998u
            : current.Money + 1u;

        var edited = new TrainerInfo(
            "TESTER",
            (current.TID + 1) % 65536,
            (current.SID + 1) % 65536,
            newMoney,
            current.Gender == 0 ? 1 : 0);

        session.SetTrainer(edited);

        var result = session.GetTrainer();

        Assert.Equal("TESTER", result.Name);
        Assert.Equal(edited.TID, result.TID);
        Assert.Equal(edited.SID, result.SID);
        Assert.Equal(edited.Money, result.Money);
        Assert.Equal(edited.Gender, result.Gender);

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionCanEditTrainerStatsInMemory()
    {
        Assert.True(File.Exists(SavePath));

        byte[] original = File.ReadAllBytes(SavePath);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var layout));

        Assert.NotNull(layout);

        using var session = new OriginHeartGoldEngineSession(original, "teste");

        var trainerBefore = session.GetTrainer();
        var statsBefore = session.GetTrainerStats();

        int newHours = statsBefore.PlayTimeHours >= 999
            ? 998
            : statsBefore.PlayTimeHours + 1;

        int newMinutes = statsBefore.PlayTimeMinutes >= 59
            ? 0
            : statsBefore.PlayTimeMinutes + 1;

        int newCoins = statsBefore.Coins >= 50_000
            ? 49_999
            : statsBefore.Coins + 1;

        session.SetTrainerStats(new TrainerStatsEdit(
            PlayTimeHours: newHours,
            PlayTimeMinutes: newMinutes,
            Coins: newCoins));

        var statsAfter = session.GetTrainerStats();

        Assert.Equal(newHours, statsAfter.PlayTimeHours);
        Assert.Equal(newMinutes, statsAfter.PlayTimeMinutes);
        Assert.Equal(newCoins, statsAfter.Coins);
        Assert.Equal(trainerBefore.Money, session.GetTrainer().Money);

        byte[] candidate = session.Serialize().ToArray();

        // Os segundos do relógio de jogo não foram alterados.
        int secondsOffset = layout!.GeneralOffset + 0x64 + 0x25;
        Assert.Equal(original[secondsOffset], candidate[secondsOffset]);

        Assert.True(OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionCanAddChangeAndRemoveBagItemInMemory()
    {
        byte[] original = File.ReadAllBytes(SavePath);
        using var session = new OriginHeartGoldEngineSession(original, "teste");

        const string pouchName = "Poké Balls";

        var existingIds = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Select(i => i.Id).ToHashSet();

        int itemId = session.GetPouchLegalItems(pouchName)
            .First(id => !existingIds.Contains(id));

        Assert.Equal(3, session.SetItemCount(pouchName, itemId, 3));

        var added = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Single(i => i.Id == itemId);

        Assert.Equal(3, added.Count);

        Assert.Equal(7, session.SetItemCount(pouchName, itemId, 7));

        var changed = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Single(i => i.Id == itemId);

        Assert.Equal(7, changed.Count);

        Assert.Equal(0, session.SetItemCount(pouchName, itemId, 0));

        Assert.DoesNotContain(
            session.GetBag().Single(p => p.Name == pouchName).Items,
            i => i.Id == itemId);

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionRejectsItemInWrongPouch()
    {
        byte[] original = File.ReadAllBytes(SavePath);
        using var session = new OriginHeartGoldEngineSession(original, "teste");

        Assert.Throws<ArgumentException>(
            () => session.SetItemCount("Medicine", 4, 1));

        Assert.Equal(
            original,
            session.Serialize().ToArray());
    }

    [Fact]
    public void SessionClampsTmQuantityTo99()
    {
        byte[] original = File.ReadAllBytes(SavePath);
        using var session = new OriginHeartGoldEngineSession(original, "teste");

        const string pouchName = "TMs & HMs";

        var existingIds = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Select(i => i.Id).ToHashSet();

        int itemId = session.GetPouchLegalItems(pouchName)
            .First(id => !existingIds.Contains(id));

        int stored = session.SetItemCount(pouchName, itemId, 5000);

        Assert.Equal(99, stored);

        var item = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Single(i => i.Id == itemId);

        Assert.Equal(99, item.Count);

        Assert.True(
            OriginHeartGoldDetector.HasValidBlockIntegrity(
                session.Serialize().ToArray()));

        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionSortsBerriesWhenAddingItems()
    {
        byte[] original = File.ReadAllBytes(SavePath);
        using var session = new OriginHeartGoldEngineSession(original, "teste");

        const string pouchName = "Berries";

        var existingIds = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Select(i => i.Id).ToHashSet();

        int[] newIds = session.GetPouchLegalItems(pouchName)
            .Where(id => !existingIds.Contains(id))
            .OrderBy(id => id)
            .Take(2)
            .ToArray();

        Assert.Equal(2, newIds.Length);

        int lowerId = newIds[0];
        int higherId = newIds[1];

        session.SetItemCount(pouchName, higherId, 1);
        session.SetItemCount(pouchName, lowerId, 1);

        int[] actualIds = session.GetBag()
            .Single(p => p.Name == pouchName)
            .Items.Select(i => i.Id).ToArray();

        Assert.Equal(
            actualIds.OrderBy(id => id).ToArray(),
            actualIds);

        Assert.True(
            OriginHeartGoldDetector.HasValidBlockIntegrity(
                session.Serialize().ToArray()));

        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionClearsOnlyShortcutForRemovedItem()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var layout));

        Assert.NotNull(layout);

        ushort preservedShortcut =
            BinaryPrimitives.ReadUInt16LittleEndian(
                original.AsSpan(layout!.GeneralOffset + 0xEA6, 2)) == 744
                    ? (ushort)745
                    : (ushort)744;

        Assert.True(
            OriginHeartGoldGeneralMutation.TryCreateCandidate(
                original,
                general =>
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        general.AsSpan(0xEA4, 2), 4);

                    BinaryPrimitives.WriteUInt16LittleEndian(
                        general.AsSpan(0xEA6, 2), preservedShortcut);

                    return true;
                },
                out byte[] seeded));

        using var session = new OriginHeartGoldEngineSession(seeded, "teste");

        Assert.Contains(
            session.GetBag().Single(p => p.Name == "Poké Balls").Items,
            i => i.Id == 4);

        Assert.Equal(0, session.SetItemCount("Poké Balls", 4, 0));

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var after));

        Assert.NotNull(after);

        ushort firstShortcut =
            BinaryPrimitives.ReadUInt16LittleEndian(
                candidate.AsSpan(after!.GeneralOffset + 0xEA4, 2));

        ushort secondShortcut =
            BinaryPrimitives.ReadUInt16LittleEndian(
                candidate.AsSpan(after.GeneralOffset + 0xEA6, 2));

        Assert.Equal((ushort)0, firstShortcut);
        Assert.Equal(preservedShortcut, secondShortcut);

        Assert.True(OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionReadsOriginPokedexFlagsAndProgress()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(original, out var layout));

        Assert.NotNull(layout);

        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(
            original.AsSpan(layout!.GeneralOffset + 0x13A8, 4));

        Assert.Equal(0xBEEFCAFEu, marker);

        using var session = new OriginHeartGoldEngineSession(original, "teste");

        var progress = session.GetDexProgress();

        Assert.Equal(1025, progress.Total);

        Assert.Equal(
            CountOriginDexTestFlags(original, layout.GeneralOffset, 0xCC),
            progress.Seen);

        Assert.Equal(
            CountOriginDexTestFlags(original, layout.GeneralOffset, 0x04),
            progress.Caught);

        Assert.Equal(
            ReadOriginDexTestFlag(original, layout.GeneralOffset, 0xCC, 1),
            session.GetDexEntry(1).Seen);

        Assert.Equal(
            ReadOriginDexTestFlag(original, layout.GeneralOffset, 0x04, 1),
            session.GetDexEntry(1).Caught);

        Assert.True(session.IsDexSpecies(1));
        Assert.True(session.IsDexSpecies(1025));
        Assert.False(session.IsDexSpecies(0));
        Assert.False(session.IsDexSpecies(1026));

        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionUpdatesSeenAndCaughtFlagsCorrectly()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        using var session = new OriginHeartGoldEngineSession(original, "teste");

        const int species = 1025;

        session.SetDexEntry(species, true, false);

        var seenOnly = session.GetDexEntry(species);

        Assert.True(seenOnly.Seen);
        Assert.False(seenOnly.Caught);

        session.SetDexEntry(species, true, true);

        var caught = session.GetDexEntry(species);

        Assert.True(caught.Seen);
        Assert.True(caught.Caught);

        // Capturado sem visto deve ser normalizado para ambos falsos.
        session.SetDexEntry(species, false, true);

        var cleared = session.GetDexEntry(species);

        Assert.False(cleared.Seen);
        Assert.False(cleared.Caught);

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(
            OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));

        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionCanCompleteOriginPokedexInMemory()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        using var session = new OriginHeartGoldEngineSession(original, "teste");

        session.CompleteDex();

        var progress = session.GetDexProgress();

        Assert.Equal(1025, progress.Total);
        Assert.Equal(1025, progress.Seen);
        Assert.Equal(1025, progress.Caught);

        Assert.True(session.GetDexEntry(1).Seen);
        Assert.True(session.GetDexEntry(1).Caught);
        Assert.True(session.GetDexEntry(1025).Seen);
        Assert.True(session.GetDexEntry(1025).Caught);

        byte[] candidate = session.Serialize().ToArray();

        Assert.True(
            OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));

        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionMissingSpeciesMatchesBoxContents()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        using var session = new OriginHeartGoldEngineSession(original, "teste");

        var present = session.Snapshot.Slots
            .Where(slot => slot.Box >= 0 && slot.Species is > 0)
            .Select(slot => slot.Species!.Value)
            .Distinct()
            .ToHashSet();

        int[] expected = Enumerable.Range(1, 1025)
            .Where(species => !present.Contains(species))
            .ToArray();

        Assert.Equal(expected, session.GetMissingSpecies());

        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    private static bool ReadOriginDexTestFlag(
        byte[] data,
        int generalOffset,
        int flagOffset,
        int species)
    {
        int index = species - 1;
        int offset = generalOffset + 0x13A8 + flagOffset + (index >> 3);

        return (data[offset] & (1 << (index & 7))) != 0;
    }

    private static int CountOriginDexTestFlags(
        byte[] data,
        int generalOffset,
        int flagOffset)
    {
        int count = 0;

        for (int species = 1; species <= 1025; species++)
        {
            if (ReadOriginDexTestFlag(
                    data, generalOffset, flagOffset, species))
                count++;
        }

        return count;
    }
    [Fact]
    public void SessionReadsAllOriginTrainerBadges()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(original, out var layout));
        Assert.NotNull(layout);

        using var session = new OriginHeartGoldEngineSession(original, "teste");
        var info = session.GetTrainerBadges();

        string[] expectedNames =
        [
            "Boulder", "Cascade", "Thunder", "Rainbow",
            "Marsh", "Soul", "Volcano", "Earth",
            "Zephyr", "Hive", "Plain", "Fog",
            "Storm", "Mineral", "Glacier", "Rising"
        ];

        Assert.True(info.Supported);
        Assert.Equal(16, info.Badges.Count);
        Assert.Equal(expectedNames, info.Badges.Select(badge => badge.Name));

        for (int id = 0; id < 16; id++)
        {
            int offset = id < 8 ? 0x80 : 0x83;
            int bit = id & 7;
            bool expected = (original[layout!.GeneralOffset + offset] & (1 << bit)) != 0;

            Assert.Equal(id, info.Badges[id].Id);
            Assert.Equal(id < 8 ? "Kanto" : "Johto", info.Badges[id].Region);
            Assert.Equal(expected, info.Badges[id].Earned);
        }

        Assert.Equal(info.Badges.Count(badge => badge.Earned), info.EarnedCount);
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionCanToggleKantoAndJohtoBadgesWithoutChangingOtherBits()
    {
        byte[] original = File.ReadAllBytes(SavePath);

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(original, out var layout));
        Assert.NotNull(layout);

        using var session = new OriginHeartGoldEngineSession(original, "teste");

        var before = session.GetTrainerBadges();

        const int kantoId = 2;
        const int johtoId = 11;

        session.SetTrainerBadge(kantoId, !before.Badges[kantoId].Earned);
        session.SetTrainerBadge(johtoId, !before.Badges[johtoId].Earned);

        var after = session.GetTrainerBadges();

        Assert.Equal(!before.Badges[kantoId].Earned, after.Badges[kantoId].Earned);
        Assert.Equal(!before.Badges[johtoId].Earned, after.Badges[johtoId].Earned);

        for (int id = 0; id < 16; id++)
        {
            if (id == kantoId || id == johtoId)
                continue;

            Assert.Equal(before.Badges[id].Earned, after.Badges[id].Earned);
        }

        byte[] candidate = session.Serialize().ToArray();

        int kantoOffset = layout!.GeneralOffset + 0x80;
        int johtoOffset = layout.GeneralOffset + 0x83;

        Assert.Equal(
            (byte)(original[kantoOffset] ^ (1 << kantoId)),
            candidate[kantoOffset]);

        Assert.Equal(
            (byte)(original[johtoOffset] ^ (1 << (johtoId - 8))),
            candidate[johtoOffset]);

        Assert.True(OriginHeartGoldDetector.HasValidBlockIntegrity(candidate));
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }

    [Fact]
    public void SessionRejectsTrainerBadgeIdsOutsideSupportedRange()
    {
        byte[] original = File.ReadAllBytes(SavePath);
        using var session = new OriginHeartGoldEngineSession(original, "teste");

        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetTrainerBadge(-1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetTrainerBadge(16, true));

        Assert.Equal(original, session.Serialize().ToArray());
        Assert.Equal(original, File.ReadAllBytes(SavePath));
    }
}