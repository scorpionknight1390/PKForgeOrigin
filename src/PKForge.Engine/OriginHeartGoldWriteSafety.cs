using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

internal static class OriginHeartGoldWriteSafety
{
    private const int GeneralSize = 0xF7CC;
    private const int BoxedSize = 136;

    private const int TrainerOffset = 0x64;
    private const int BagOffset = 0x644;
    private const int DexOffset = 0x13A8;

    private const uint DexMarker = 0xBEEFCAFE;
    private const int DexMaximum = 1025;
    private const int DexFlagBytes = (DexMaximum + 7) / 8;

    private const int ShortcutOffset = 0xEA4;

    private readonly record struct Range(int Offset, int Length);
    private readonly record struct Pouch(int Relative, int Slots, string Pocket, int Maximum);
    private readonly record struct BagEntry(int Id, int Count);

    private static readonly Range[] TrainerRanges =
    [
        new(0x64, 0x19),
        new(0x80, 1),
        new(0x83, 1),
        new(0x84, 5),
    ];

    private static readonly Range[] InventoryRanges =
    [
        new(0x644, 0x864),
    ];

    private static readonly Range[] DexRanges =
    [
        new(DexOffset + 0x04, DexFlagBytes),
        new(DexOffset + 0xCC, DexFlagBytes),
    ];

    private static readonly Pouch[] Pouches =
    [
        new(0x000, 165, "items", 999),
        new(0x294, 50, "keyItems", 999),
        new(0x35C, 151, "tmHm", 99),
        new(0x5B8, 12, "mail", 999),
        new(0x5E8, 40, "medicine", 999),
        new(0x688, 64, "berries", 999),
        new(0x788, 24, "balls", 999),
        new(0x7E8, 30, "battle", 999),
    ];

    internal static bool IsSupportedAction(SaveAction? action) =>
        action is SaveAction.EditMon
            or SaveAction.EditInventory
            or SaveAction.EditTrainer
            or SaveAction.EditDex;

    internal static string? Check(
        ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> candidate,
        WriteScope? scope,
        SaveAction? action)
    {
        if (action is null)
            return "Origin HeartGold requires an explicitly supported editing operation.";

        SaveAction authorizedAction = action.Value;

        if (!IsSupportedAction(authorizedAction))
            return "Origin HeartGold requires an explicitly supported editing operation.";

        if (original.Length != candidate.Length)
            return "Origin HeartGold save length changed.";

        if (!OriginHeartGoldReadOnlyReader.TryOpen(original.Span, out var before) ||
            before is null)
            return "The original Origin HeartGold save could not be opened.";

        if (!OriginHeartGoldReadOnlyReader.TryOpen(candidate.Span, out var after) ||
            after is null)
            return "The candidate is not a valid Origin HeartGold save.";

        if (before.Tied || after.Tied)
            return "Origin HeartGold saves with tied partition counters cannot be edited.";

        if (before.GeneralOffset != after.GeneralOffset ||
            before.StorageOffset != after.StorageOffset ||
            before.Counter != after.Counter)
            return "The candidate changed the active Origin HeartGold partition.";

        if (authorizedAction == SaveAction.EditMon)
            return CheckPokemon(original, candidate, before, after, scope);

        if (scope is not null)
            return "General-block Origin edits cannot use a Pokémon slot scope.";

        return CheckGeneral(original, candidate, before, after, authorizedAction);
    }

    private static string? CheckPokemon(
        ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> candidate,
        OriginHeartGoldReadOnlySave before,
        OriginHeartGoldReadOnlySave after,
        WriteScope? scope)
    {
        int changedBox = -1;
        int changedSlot = -1;
        int changedCount = 0;

        for (int box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
        {
            for (int slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
            {
                if (before.GetBoxRecord(box, slot).Span.SequenceEqual(
                        after.GetBoxRecord(box, slot).Span))
                    continue;

                changedBox = box;
                changedSlot = slot;
                changedCount++;

                if (changedCount > 1)
                    return "An Origin EditMon operation may change only one existing boxed Pokémon.";
            }
        }

        if (changedCount != 1)
            return "The Origin EditMon operation did not change exactly one boxed Pokémon.";

        var target = new SlotRef(changedBox, changedSlot);

        if (scope is not null &&
            (scope.Slots.Count != 1 || !scope.Slots.Contains(target)))
            return "The changed Pokémon does not match the declared write scope.";

        byte[] oldRecord = before.GetBoxRecord(changedBox, changedSlot).ToArray();
        byte[] newRecord = after.GetBoxRecord(changedBox, changedSlot).ToArray();

        if (!HasOnlySupportedPokemonChanges(oldRecord, newRecord, out var rejection))
            return $"The Pokémon edit was refused: {rejection}";

        if (!OriginHeartGoldBoxMutation.TryCreateCandidate(
                original.Span,
                changedBox,
                changedSlot,
                newRecord,
                out byte[] expected))
            return "The Origin box mutator rejected the Pokémon candidate.";

        if (!expected.AsSpan().SequenceEqual(candidate.Span))
            return "The Pokémon candidate contains changes outside the validated box mutation.";

        return null;
    }

    private static bool HasOnlySupportedPokemonChanges(
        byte[] originalRecord,
        byte[] candidateRecord,
        out string reason)
    {
        reason = "Unknown validation failure.";

        if (originalRecord.Length != BoxedSize ||
            candidateRecord.Length != BoxedSize)
        {
            reason = $"Record size mismatch: original={originalRecord.Length}, candidate={candidateRecord.Length}.";
            return false;
        }

        var oldPokemon = new PK4(originalRecord.ToArray());
        var newPokemon = new PK4(candidateRecord.ToArray());

        if (!oldPokemon.Valid || !newPokemon.Valid)
        {
            reason = $"Invalid Pokémon checksum: original valid={oldPokemon.Valid}, candidate valid={newPokemon.Valid}.";
            return false;
        }

        if (oldPokemon.Species == 0 ||
            newPokemon.Species != oldPokemon.Species ||
            newPokemon.PID != oldPokemon.PID)
        {
            reason = $"Identity mismatch: original species={oldPokemon.Species}, candidate species={newPokemon.Species}, same PID={newPokemon.PID == oldPokemon.PID}.";
            return false;
        }

        byte[] oldData = oldPokemon.Data[..BoxedSize].ToArray();
        byte[] newData = newPokemon.Data[..BoxedSize].ToArray();

        for (int i = 0; i < BoxedSize; i++)
        {
            if (oldData[i] == newData[i])
                continue;

            bool allowed =
                i is 0x06 or 0x07 or 0x14 ||
                i is >= 0x18 and <= 0x1D ||
                i is >= 0x28 and <= 0x3B;

            if (!allowed)
            {
                reason = $"Unsupported byte changed at offset 0x{i:X2}: {oldData[i]:X2} -> {newData[i]:X2}.";
                return false;
            }
        }

        if ((oldData[0x3B] & 0xC0) != (newData[0x3B] & 0xC0))
        {
            reason = "Egg or Nicknamed flag changed inside IV data.";
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            ushort oldMove = BinaryPrimitives.ReadUInt16LittleEndian(
                oldData.AsSpan(0x28 + i * 2, 2));

            ushort newMove = BinaryPrimitives.ReadUInt16LittleEndian(
                newData.AsSpan(0x28 + i * 2, 2));

            int oldPP = oldData[0x30 + i];
            int newPP = newData[0x30 + i];
            int oldUps = oldData[0x34 + i];
            int newUps = newData[0x34 + i];

            if (oldMove == newMove)
            {
                if (oldPP == newPP && oldUps == newUps)
                    continue;

                if (newMove == 0)
                {
                    if (newPP != 0 || newUps != 0)
                    {
                        reason = $"Move slot {i + 1} is empty but has PP={newPP} and PP Ups={newUps}.";
                        return false;
                    }

                    continue;
                }

                if (newUps > 3)
                {
                    reason = $"Move slot {i + 1}, move {newMove}: PP Ups={newUps}, maximum is 3.";
                    return false;
                }

                if (!OriginHeartGoldMoveData.TryGet(newMove, out var sameMoveData))
                {
                    reason = $"Move slot {i + 1}: move ID {newMove} is missing from the Origin catalog.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(sameMoveData.Name) || sameMoveData.BasePp <= 0)
                {
                    reason = $"Move slot {i + 1}, move {newMove}: invalid catalog entry.";
                    return false;
                }

                int maximumPP = sameMoveData.BasePp + (sameMoveData.BasePp * newUps / 5);

                if (newPP > maximumPP)
                {
                    reason = $"Move slot {i + 1}, move {newMove}: PP={newPP} exceeds maximum {maximumPP}; old PP={oldPP}, PP Ups={oldUps}->{newUps}.";
                    return false;
                }

                continue;
            }

            if (newMove == 0)
            {
                if (newPP != 0 || newUps != 0)
                {
                    reason = $"Removed move in slot {i + 1} still has PP={newPP} or PP Ups={newUps}.";
                    return false;
                }

                continue;
            }

            if (!OriginHeartGoldMoveData.TryGet(newMove, out var moveData))
            {
                reason = $"New move in slot {i + 1}: move ID {newMove} is missing from the Origin catalog.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(moveData.Name) || moveData.BasePp <= 0)
            {
                reason = $"New move in slot {i + 1}, move {newMove}: invalid catalog entry.";
                return false;
            }

            if (newPP != moveData.BasePp || newUps != 0)
            {
                reason = $"New move in slot {i + 1}, move {newMove}: expected base PP={moveData.BasePp} and PP Ups=0, received PP={newPP} and PP Ups={newUps}.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static string? CheckGeneral(
        ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> candidate,
        OriginHeartGoldReadOnlySave before,
        OriginHeartGoldReadOnlySave after,
        SaveAction action)
    {
        Range[] ranges = action switch
        {
            SaveAction.EditTrainer => TrainerRanges,
            SaveAction.EditInventory => InventoryRanges,
            SaveAction.EditDex => DexRanges,
            _ => [],
        };

        if (ranges.Length == 0)
            return "This Origin HeartGold action has no supported General-block editor.";

        ReadOnlySpan<byte> oldGeneral =
            original.Span.Slice(before.GeneralOffset, GeneralSize);

        ReadOnlySpan<byte> newGeneral =
            candidate.Span.Slice(after.GeneralOffset, GeneralSize);

        bool valuesValid = action switch
        {
            SaveAction.EditTrainer => ValidateTrainer(oldGeneral, newGeneral),
            SaveAction.EditInventory => ValidateInventory(oldGeneral, newGeneral),
            SaveAction.EditDex => ValidateDex(oldGeneral, newGeneral),
            _ => false,
        };

        if (!valuesValid)
            return "The Origin HeartGold fields contain unsupported or inconsistent values.";

        byte[] requestedGeneral = newGeneral.ToArray();

        if (!OriginHeartGoldGeneralMutation.TryCreateCandidate(
                original.Span,
                general =>
                {
                    foreach (var range in ranges)
                    {
                        requestedGeneral.AsSpan(range.Offset, range.Length)
                            .CopyTo(general.AsSpan(range.Offset, range.Length));
                    }

                    return true;
                },
                out byte[] expected))
            return "The Origin General mutator rejected the candidate.";

        if (!expected.AsSpan().SequenceEqual(candidate.Span))
            return "The candidate contains changes outside the validated Origin General fields.";

        return null;
    }

    private static bool ValidateTrainer(
        ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> candidate)
    {
        if (!original.Slice(TrainerOffset, 16).SequenceEqual(
                candidate.Slice(TrainerOffset, 16)))
        {
            try
            {
                byte[] encoded = candidate.Slice(TrainerOffset, 16).ToArray();
                string name = StringConverter4.GetString(encoded);

                if (string.IsNullOrWhiteSpace(name) || name.Length > 7)
                    return false;

                byte[] roundTrip = new byte[16];

                StringConverter4.SetString(
                    roundTrip,
                    name,
                    7,
                    original[TrainerOffset + 0x19],
                    StringConverterOption.ClearZero);

                if (!roundTrip.AsSpan().SequenceEqual(encoded))
                    return false;
            }
            catch
            {
                return false;
            }
        }

        if (!original.Slice(TrainerOffset + 0x14, 4).SequenceEqual(
                candidate.Slice(TrainerOffset + 0x14, 4)) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                candidate.Slice(TrainerOffset + 0x14, 4)) > 9_999_999)
            return false;

        if (original[TrainerOffset + 0x18] != candidate[TrainerOffset + 0x18] &&
            candidate[TrainerOffset + 0x18] > 1)
            return false;

        if (!original.Slice(0x84, 2).SequenceEqual(candidate.Slice(0x84, 2)) &&
            BinaryPrimitives.ReadUInt16LittleEndian(candidate.Slice(0x84, 2)) > 50_000)
            return false;

        if (!original.Slice(TrainerOffset + 0x22, 2).SequenceEqual(
                candidate.Slice(TrainerOffset + 0x22, 2)) &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                candidate.Slice(TrainerOffset + 0x22, 2)) > 999)
            return false;

        if (original[TrainerOffset + 0x24] != candidate[TrainerOffset + 0x24] &&
            candidate[TrainerOffset + 0x24] > 59)
            return false;

        return true;
    }

    private static bool ValidateInventory(
        ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> candidate)
    {
        if (!OriginHeartGoldItemData.IsAvailable)
            return false;

        int changedPouchCount = 0;

        foreach (var pouch in Pouches)
        {
            int offset = BagOffset + pouch.Relative;
            int length = pouch.Slots * 4;

            if (original.Slice(offset, length).SequenceEqual(
                    candidate.Slice(offset, length)))
                continue;

            changedPouchCount++;

            if (!IsValidPouch(original, pouch) ||
                !IsValidPouch(candidate, pouch))
                return false;
        }

        if (changedPouchCount == 0)
            return false;
        for (int i = 0; i < 2; i++)
        {
            int offset = ShortcutOffset + i * 2;

            ushort oldShortcut = BinaryPrimitives.ReadUInt16LittleEndian(
                original.Slice(offset, 2));

            ushort newShortcut = BinaryPrimitives.ReadUInt16LittleEndian(
                candidate.Slice(offset, 2));

            if (oldShortcut != 0 &&
                !ContainsBagItem(candidate, oldShortcut) &&
                newShortcut != 0)
                return false;

            if (oldShortcut == newShortcut)
                continue;

            if (oldShortcut == 0 ||
                newShortcut != 0 ||
                !ContainsBagItem(original, oldShortcut) ||
                ContainsBagItem(candidate, oldShortcut))
                return false;
        }

        return true;
    }

    private static bool IsSingleItemPouchEdit(
        ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> candidate,
        Pouch pouch)
    {
        List<BagEntry> oldItems = ReadPouchEntries(original, pouch);
        List<BagEntry> newItems = ReadPouchEntries(candidate, pouch);

        var changedIds = new HashSet<int>();

        foreach (var item in oldItems)
        {
            int index = newItems.FindIndex(x => x.Id == item.Id);

            if (index < 0 || newItems[index].Count != item.Count)
                changedIds.Add(item.Id);
        }

        foreach (var item in newItems)
        {
            if (oldItems.FindIndex(x => x.Id == item.Id) < 0)
                changedIds.Add(item.Id);
        }

        if (changedIds.Count != 1)
            return false;

        int changedId = changedIds.First();

        int oldIndex = oldItems.FindIndex(x => x.Id == changedId);
        int newIndex = newItems.FindIndex(x => x.Id == changedId);

        var expected = new List<BagEntry>(oldItems);

        if (oldIndex >= 0)
        {
            if (newIndex >= 0)
                expected[oldIndex] = newItems[newIndex];
            else
                expected.RemoveAt(oldIndex);
        }
        else
        {
            if (newIndex < 0)
                return false;

            expected.Add(newItems[newIndex]);

            if (pouch.Pocket is "tmHm" or "berries")
                expected.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        return expected.SequenceEqual(newItems);
    }

    private static List<BagEntry> ReadPouchEntries(
        ReadOnlySpan<byte> general,
        Pouch pouch)
    {
        var entries = new List<BagEntry>(pouch.Slots);

        for (int slot = 0; slot < pouch.Slots; slot++)
        {
            int offset = BagOffset + pouch.Relative + slot * 4;

            int id = BinaryPrimitives.ReadUInt16LittleEndian(
                general.Slice(offset, 2));

            int count = BinaryPrimitives.ReadUInt16LittleEndian(
                general.Slice(offset + 2, 2));

            if (id == 0 && count == 0)
                continue;

            entries.Add(new BagEntry(id, count));
        }

        return entries;
    }

    private static bool IsValidPouch(
        ReadOnlySpan<byte> general,
        Pouch pouch)
    {
        var knownIds = new HashSet<int>();
        bool foundEmpty = false;

        for (int slot = 0; slot < pouch.Slots; slot++)
        {
            int offset = BagOffset + pouch.Relative + slot * 4;

            int id = BinaryPrimitives.ReadUInt16LittleEndian(
                general.Slice(offset, 2));

            int count = BinaryPrimitives.ReadUInt16LittleEndian(
                general.Slice(offset + 2, 2));

            if (id == 0 && count == 0)
            {
                foundEmpty = true;
                continue;
            }

            if (foundEmpty ||
                id == 0 ||
                count == 0 ||
                count > pouch.Maximum ||
                !knownIds.Add(id) ||
                !OriginHeartGoldItemData.TryGet(id, out var item) ||
                !string.Equals(item.Pocket, pouch.Pocket, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool ContainsBagItem(
        ReadOnlySpan<byte> general,
        int itemId)
    {
        foreach (var pouch in Pouches)
        {
            for (int slot = 0; slot < pouch.Slots; slot++)
            {
                int offset = BagOffset + pouch.Relative + slot * 4;

                int id = BinaryPrimitives.ReadUInt16LittleEndian(
                    general.Slice(offset, 2));

                int count = BinaryPrimitives.ReadUInt16LittleEndian(
                    general.Slice(offset + 2, 2));

                if (id == itemId && count != 0)
                    return true;
            }
        }

        return false;
    }

    private static bool ValidateDex(
        ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> candidate)
    {
        uint oldMarker = BinaryPrimitives.ReadUInt32LittleEndian(
            original.Slice(DexOffset, 4));

        uint newMarker = BinaryPrimitives.ReadUInt32LittleEndian(
            candidate.Slice(DexOffset, 4));

        if (oldMarker != DexMarker || newMarker != DexMarker)
            return false;

        foreach (int flagOffset in new[] { 0x04, 0xCC })
        {
            int lastByte = DexOffset + flagOffset + DexFlagBytes - 1;

            // Species 1025 uses only bit zero of the last byte.
            if ((original[lastByte] & 0xFE) != (candidate[lastByte] & 0xFE))
                return false;
        }

        for (int species = 1; species <= DexMaximum; species++)
        {
            int index = species - 1;
            int mask = 1 << (index & 7);
            int byteIndex = index >> 3;

            bool seen = (candidate[DexOffset + 0xCC + byteIndex] & mask) != 0;
            bool caught = (candidate[DexOffset + 0x04 + byteIndex] & mask) != 0;

            if (caught && !seen)
                return false;
        }

        return true;
    }
}