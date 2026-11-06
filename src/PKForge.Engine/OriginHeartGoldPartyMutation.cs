using System;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Creates a safe in-memory candidate for editing the stored-Pokémon
/// portion of one existing Origin HeartGold Party record.
/// The 100 additional Party bytes are preserved unchanged.
/// </summary>
internal static class OriginHeartGoldPartyMutation
{
    private const int RawSaveSize = 0x80000;
    private const int DsvFooterSize = 122;
    private const int PartyOffset = 0x98;
    private const int PartyRecordSize = OriginHeartGoldReadOnlySave.PartyRecordSize;
    private const int BoxedSize = OriginHeartGoldReadOnlySave.BoxedSize;

    internal static bool TryCreateCandidate(
        ReadOnlySpan<byte> input,
        int slot,
        ReadOnlySpan<byte> editedEncryptedRecord,
        out byte[] candidate)
    {
        candidate = Array.Empty<byte>();

        if (input.Length != RawSaveSize &&
            input.Length != RawSaveSize + DsvFooterSize)
            return false;

        if (editedEncryptedRecord.Length != BoxedSize)
            return false;

        if (!OriginHeartGoldReadOnlyReader.TryOpen(input, out var save) ||
            save is null ||
            save.Tied ||
            (uint)slot >= (uint)save.PartyCount)
            return false;

        if (!PokeCrypto.IsEncrypted45(editedEncryptedRecord))
            return false;

        byte[] proposedRecord = editedEncryptedRecord.ToArray();
        byte[] originalRecord = save.GetPartyPokemonBytes(slot).ToArray();

        var originalPokemon = new PK4(originalRecord.ToArray());
        var editedPokemon = new PK4(proposedRecord.ToArray());

        if (!originalPokemon.Valid ||
            !editedPokemon.Valid ||
            originalPokemon.Species == 0 ||
            editedPokemon.Species != originalPokemon.Species ||
            editedPokemon.PID != originalPokemon.PID)
            return false;

        int recordOffset = PartyOffset + (slot * PartyRecordSize);

        if (!OriginHeartGoldGeneralMutation.TryCreateCandidate(
                input,
                general =>
                {
                    proposedRecord.AsSpan().CopyTo(
                        general.AsSpan(recordOffset, BoxedSize));

                    return true;
                },
                out byte[] output))
            return false;

        if (!OriginHeartGoldReadOnlyReader.TryOpen(output, out var verification) ||
            verification is null ||
            verification.Tied ||
            verification.GeneralOffset != save.GeneralOffset ||
            verification.StorageOffset != save.StorageOffset ||
            verification.Counter != save.Counter ||
            verification.PartyCount != save.PartyCount)
            return false;

        ReadOnlySpan<byte> oldParty =
            save.GetPartyRecord(slot).Span;

        ReadOnlySpan<byte> newParty =
            verification.GetPartyRecord(slot).Span;

        if (!newParty[..BoxedSize].SequenceEqual(proposedRecord) ||
            !newParty[BoxedSize..].SequenceEqual(oldParty[BoxedSize..]))
            return false;

        for (int i = 0; i < save.PartyCount; i++)
        {
            if (i == slot)
                continue;

            if (!save.GetPartyRecord(i).Span.SequenceEqual(
                    verification.GetPartyRecord(i).Span))
                return false;
        }

        candidate = output;
        return true;
    }
}