using System.Buffers.Binary;

namespace PKForge.Engine;

/// <summary>
/// Mutação controlada do bloco General do Origin HeartGold.
/// Atua somente em uma cópia em memória.
/// </summary>
internal static class OriginHeartGoldGeneralMutation
{
    private const int RawSaveSize = 0x80000;
    private const int DsvFooterSize = 122;
    private const int PartitionSize = 0x40000;
    private const int GeneralSize = 0xF7CC;
    private const int StorageSize = 0x18408;
    private const int FooterSize = 16;
    private const int GeneralFooter = GeneralSize - FooterSize;

    // Trechos nativos do bloco General e seus respectivos CRCs internos.
    private static readonly (int Offset, int Size)[] GeneralChunks =
    [
        (0x0000, 0x005C), (0x0060, 0x002C),
        (0x0090, 0x05B0), (0x0644, 0x0864),
        (0x0EAC, 0x0474), (0x1324, 0x0080),
        (0x13A8, 0x0370), (0x171C, 0x01E0),
        (0x1900, 0x0880), (0x2184, 0x02F8),
        (0x2480, 0x1400), (0x3884, 0x0020),
        (0x38A8, 0x0834), (0x40E0, 0x0460),
        (0x4544, 0x0108), (0x4650, 0x0620),
        (0x4C74, 0x01C0), (0x4E38, 0x0170),
        (0x4FAC, 0x03EC), (0x539C, 0x1694),
        (0x6A34, 0x0010), (0x6A48, 0x0068),
        (0x6AB4, 0x00F8), (0x6BB0, 0x0BC8),
        (0x777C, 0x0EA0), (0x8620, 0x08C0),
        (0x8EE4, 0x0FF8), (0x9EE0, 0x1680),
        (0xB564, 0x0688), (0xBBF0, 0x0028),
        (0xBC1C, 0x0008), (0xBC28, 0x0040),
        (0xBC6C, 0x0008), (0xBC78, 0x0008),
        (0xBC84, 0x0658), (0xC2E0, 0x05FC),
        (0xC8E0, 0x1294), (0xDB78, 0x0B80),
        (0xE6FC, 0x0080), (0xE780, 0x0134),
        (0xE8B8, 0x0F00),
    ];

    /// <summary>
    /// Cria um candidato alterando somente o conteúdo do General selecionado.
    /// O callback recebe uma cópia completa do bloco General.
    /// </summary>
    internal static bool TryCreateCandidate(
        ReadOnlySpan<byte> input,
        Func<byte[], bool> editGeneral,
        out byte[] candidate)
    {
        candidate = [];

        if (input.Length != RawSaveSize &&
            input.Length != RawSaveSize + DsvFooterSize)
            return false;

        if (editGeneral is null ||
            !OriginHeartGoldReadOnlyReader.TryOpen(input, out var save) ||
            save is null ||
            save.Tied)
            return false;

        byte[] originalGeneral =
            input.Slice(save.GeneralOffset, GeneralSize).ToArray();

        byte[] editedGeneral = (byte[])originalGeneral.Clone();

        if (!editGeneral(editedGeneral))
            return false;
        // Somente os dados dos chunks podem ser alterados pelo callback.
// CRCs internos, preenchimentos e footer são controlados pelo mutador.
bool[] editableBytes = new bool[GeneralFooter];

foreach (var (offset, size) in GeneralChunks)
{
    if (offset < 0 || size <= 0 ||
        offset + size + 4 > GeneralFooter)
        return false;

    Array.Fill(editableBytes, true, offset, size);
}

for (int i = 0; i < GeneralFooter; i++)
{
    if (!editableBytes[i] &&
        editedGeneral[i] != originalGeneral[i])
        return false;
}
        // O callback não pode alterar o cabeçalho ou os campos do footer.
        if (!editedGeneral.AsSpan(GeneralFooter, FooterSize)
                .SequenceEqual(originalGeneral.AsSpan(GeneralFooter, FooterSize)))
            return false;

        // Não cria candidato para uma operação sem alterações.
        if (editedGeneral.AsSpan(0, GeneralFooter)
                .SequenceEqual(originalGeneral.AsSpan(0, GeneralFooter)))
            return false;

        // Atualiza somente os CRCs internos que já eram válidos.
        foreach (var (offset, size) in GeneralChunks)
        {
            int checksumOffset = offset + size;

            if (checksumOffset + 2 > GeneralFooter)
                return false;

            ushort originalStored =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    originalGeneral.AsSpan(checksumOffset, 2));

            ushort originalCalculated =
                CalculateCrc16Ccitt(
                    originalGeneral.AsSpan(offset, size));

            if (originalStored != originalCalculated)
                continue;

            bool chunkChanged =
                !editedGeneral.AsSpan(offset, size)
                    .SequenceEqual(originalGeneral.AsSpan(offset, size));

            if (!chunkChanged)
                continue;

            ushort updated =
                CalculateCrc16Ccitt(editedGeneral.AsSpan(offset, size));

            BinaryPrimitives.WriteUInt16LittleEndian(
                editedGeneral.AsSpan(checksumOffset, 2),
                updated);
        }

        byte[] output = input.ToArray();

        // Copia somente o conteúdo anterior ao footer.
        editedGeneral.AsSpan(0, GeneralFooter).CopyTo(
            output.AsSpan(save.GeneralOffset, GeneralFooter));

        // Atualiza o CRC externo do General.
        int outerChecksumOffset =
            save.GeneralOffset + GeneralFooter + 14;

        ushort outerChecksum =
            CalculateCrc16Ccitt(
                output.AsSpan(save.GeneralOffset, GeneralFooter));

        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(outerChecksumOffset, 2),
            outerChecksum);

        // Confirma que o leitor continua selecionando a mesma geração.
        if (!OriginHeartGoldReadOnlyReader.TryOpen(
                output, out var verification) ||
            verification is null ||
            verification.Tied ||
            verification.GeneralOffset != save.GeneralOffset ||
            verification.StorageOffset != save.StorageOffset ||
            verification.Counter != save.Counter)
            return false;

        // Storage ativo permanece byte por byte intacto.
        if (!output.AsSpan(save.StorageOffset,
                StorageSize)
            .SequenceEqual(input.Slice(save.StorageOffset,
                StorageSize)))
            return false;

        // Espelho inativo permanece intacto.
        int inactiveOffset =
            save.GeneralOffset == 0 ? PartitionSize : 0;

        if (!output.AsSpan(inactiveOffset, PartitionSize)
                .SequenceEqual(input.Slice(inactiveOffset, PartitionSize)))
            return false;

        // Preserva o footer completo do DeSmuME.
        if (input.Length == RawSaveSize + DsvFooterSize &&
            !output.AsSpan(RawSaveSize)
                .SequenceEqual(input[RawSaveSize..]))
            return false;

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