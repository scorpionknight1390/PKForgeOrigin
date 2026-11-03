
using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Experimental in-memory mutation of one existing Origin HeartGold box Pokémon.
/// Never writes to disk.
/// </summary>
internal static class OriginHeartGoldBoxMutation
{
    private const int RawSaveSize = 0x80000;
    private const int DsvFooterSize = 122;

    private const int BoxCount = 24;
    private const int BoxSlotCount = 30;
    private const int BoxedSize = 136;
    private const int BoxStride = 0x1000;

    private const int StorageSize = 0x18408;
    private const int StorageChunkSize = 0x183F4;
    private const int BoxModifiedOffset = 0x18004;

    private const int FooterSize = 16;
    private const int FooterChecksumOffset = 14;

    /// <summary>
    /// Creates a modified save in memory.
    /// The supplied Pokémon record must already be encrypted.
    /// Only an existing Pokémon with the same PID and species can be edited.
    /// </summary>
    internal static bool TryCreateCandidate(
        ReadOnlySpan<byte> input,
        int box,
        int slot,
        ReadOnlySpan<byte> editedEncryptedRecord,
        out byte[] candidate)
    {
        candidate = Array.Empty<byte>();

        if (input.Length != RawSaveSize &&
            input.Length != RawSaveSize + DsvFooterSize)
        {
            return false;
        }

        if (!OriginHeartGoldReadOnlyReader.TryOpen(input, out var save) ||
            save is null)
        {
            return false;
        }

        // Equal-counter mirrors are not supported for editing.
        if (save.Tied)
            return false;

        if (box < 0 || box >= BoxCount ||
            slot < 0 || slot >= BoxSlotCount)
        {
            return false;
        }

        if (editedEncryptedRecord.Length != BoxedSize)
            return false;

        byte[] proposedRecord = editedEncryptedRecord.ToArray();

        if (!PokeCrypto.IsEncrypted45(proposedRecord))
            return false;

        // PK4 decrypts its own copy, never the supplied record.
        var editedPokemon = new PK4(proposedRecord.ToArray());

        if (!editedPokemon.Valid || editedPokemon.Species == 0)
            return false;

        byte[] originalRecord = save.GetBoxRecord(box, slot).ToArray();
        var originalPokemon = new PK4(originalRecord.ToArray());

        // This first version edits an existing Pokémon only.
        // It does not support replacing its identity or species.
        if (!originalPokemon.Valid ||
            originalPokemon.Species == 0 ||
            originalPokemon.PID != editedPokemon.PID ||
            originalPokemon.Species != editedPokemon.Species)
        {
            return false;
        }

        int storage = save.StorageOffset;

        // Determine whether the original inner Storage CRC was valid.
        // This decision must use the original, unmodified input.
        int innerChecksumOffset = storage + StorageChunkSize;

        ushort storedInnerChecksum =
            BinaryPrimitives.ReadUInt16LittleEndian(
                input.Slice(innerChecksumOffset, 2));

        ushort calculatedOriginalInnerChecksum =
            CalculateCrc16Ccitt(
                input.Slice(storage, StorageChunkSize));

        bool originalInnerChecksumWasValid =
            storedInnerChecksum == calculatedOriginalInnerChecksum;

        // Work exclusively on a new byte array.
        byte[] output = input.ToArray();

        int recordOffset =
            storage + (box * BoxStride) + (slot * BoxedSize);

        proposedRecord.CopyTo(output, recordOffset);

        // Mark this box as modified.
        int modifiedOffset = storage + BoxModifiedOffset;

        uint modifiedMask =
            BinaryPrimitives.ReadUInt32LittleEndian(
                output.AsSpan(modifiedOffset, 4));

        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(modifiedOffset, 4),
            modifiedMask | (1u << box));

        // Match the official Origin editor:
        // only maintain an inner CRC that was already valid.
        if (originalInnerChecksumWasValid)
        {
            ushort newInnerChecksum =
                CalculateCrc16Ccitt(
                    output.AsSpan(storage, StorageChunkSize));

            BinaryPrimitives.WriteUInt16LittleEndian(
                output.AsSpan(innerChecksumOffset, 2),
                newInnerChecksum);
        }

        // Always refresh the outer Storage block CRC.
        int outerFooter =
            storage + StorageSize - FooterSize;

        int outerChecksumOffset =
            outerFooter + FooterChecksumOffset;

        ushort newOuterChecksum =
            CalculateCrc16Ccitt(
                output.AsSpan(storage, StorageSize - FooterSize));

        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(outerChecksumOffset, 2),
            newOuterChecksum);

        // Reopen the candidate and verify mirror selection.
        if (!OriginHeartGoldReadOnlyReader.TryOpen(
                output,
                out var verification) ||
            verification is null ||
            verification.Tied ||
            verification.GeneralOffset != save.GeneralOffset ||
            verification.StorageOffset != save.StorageOffset ||
            verification.Counter != save.Counter)
        {
            return false;
        }

        if (!verification.GetBoxRecord(box, slot).Span
                .SequenceEqual(proposedRecord))
        {
            return false;
        }

        // Preserve the complete DeSmuME footer byte-for-byte.
        if (!output.AsSpan(RawSaveSize)
                .SequenceEqual(input[RawSaveSize..]))
        {
            return false;
        }

        candidate = output;
        return true;
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
}
