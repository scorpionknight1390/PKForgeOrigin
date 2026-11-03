
using System.IO;
using PKForge.Engine;
using Xunit;
using PKHeX.Core;
using Xunit.Abstractions;
using System.Buffers.Binary;
using System.Linq;
using PKForge.Domain;

namespace PKForge.Engine.Tests;

public sealed class OriginHeartGoldRealSaveTests
{
    private readonly ITestOutputHelper _output;

    public OriginHeartGoldRealSaveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string Downloads = @"C:\PKForgeSaves";

    [Theory]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv")]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav")]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv")]
    [InlineData("bolsa_teste.dsv")]
    public void RealOriginSavesHaveValidBlockIntegrity(string fileName)
    {
        string path = Path.Combine(Downloads, fileName);

        Assert.True(File.Exists(path), $"Arquivo não encontrado: {path}");

        byte[] data = File.ReadAllBytes(path);

        Assert.True(
            OriginHeartGoldDetector.IsOriginHeartGold(data),
            "O arquivo deveria ser reconhecido como Origin HeartGold.");

        Assert.True(
            OriginHeartGoldDetector.HasValidBlockIntegrity(data),
            "O checksum dos blocos ou a coerência entre as cópias falhou.");
    }

    [Fact]
    public void DamagedGeneralChecksumsAreRejectedWithoutLosingRecognition()
    {
        const int partitionSize = 0x40000;
        const int generalSize = 0xF7CC;

        string path = Path.Combine(
            Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav");

        byte[] data = File.ReadAllBytes(path);

        // Corrompe somente os campos de checksum das duas cópias gerais.
        data[generalSize - 2] ^= 0x01;
        data[partitionSize + generalSize - 2] ^= 0x01;

        Assert.True(
            OriginHeartGoldDetector.IsOriginHeartGold(data),
            "Um Origin danificado ainda deve ser identificado como Origin.");

        Assert.False(
            OriginHeartGoldDetector.HasValidBlockIntegrity(data),
            "O detector de integridade não deve aceitar os CRCs corrompidos.");

        Assert.False(
            new SaveEngine().Validate(data),
            "Um Origin com CRCs inválidos não pode passar na validação.");
    }

    [Fact]
    public void RecognizesOriginRc5Dsv()
    {
        AssertRecognition(
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv",
            expected: true);
    }

    [Fact]
    public void RecognizesOriginRc5RawSave()
    {
        AssertRecognition(
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav",
            expected: true);
    }

    [Fact]
    public void RecognizesOriginRc8Dsv()
    {
        AssertRecognition(
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8.dsv",
            expected: true);
    }

    [Fact]
    public void DoesNotMisidentifyStandardHeartGold()
    {
        AssertRecognition(
            "Pokemon - HeartGold Version (Europe) [patched].dsv",
            expected: false);
    }

    private static void AssertRecognition(
        string fileName,
        bool expected)
    {
        string path = Path.Combine(Downloads, fileName);

        Assert.True(
            File.Exists(path),
            $"Arquivo de teste não encontrado: {path}");

        byte[] data = File.ReadAllBytes(path);

        Assert.Equal(
            expected,
            OriginHeartGoldDetector.IsOriginHeartGold(data));
    }

    [Theory]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv")]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav")]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv")]
    public void ReadOnlyReaderOpensRealOriginSaves(string fileName)
    {
        string path = Path.Combine(Downloads, fileName);

        Assert.True(File.Exists(path), $"Arquivo não encontrado: {path}");

        byte[] data = File.ReadAllBytes(path);

        bool opened = OriginHeartGoldReadOnlyReader.TryOpen(
            data,
            out OriginHeartGoldReadOnlySave? save);

        Assert.True(opened, $"O leitor não conseguiu abrir: {fileName}");
        Assert.NotNull(save);

        Assert.InRange(save!.PartyCount, 0, 6);

        // Confirma que o leitor consegue acessar um registro das caixas.
        Assert.Equal(136, save.GetBoxRecord(0, 0).Length);

        // Se houver Pokémon na equipe, verifica também seu registro.
        if (save.PartyCount > 0)
        {
            Assert.Equal(236, save.GetPartyRecord(0).Length);
            Assert.Equal(136, save.GetPartyPokemonBytes(0).Length);
        }
    }

    [Theory]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv")]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5_raw_test.sav")]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv")]
    public void DiagnoseOriginPokemonRecords(string fileName)
    {
        string path = Path.Combine(Downloads, fileName);
        byte[] data = File.ReadAllBytes(path);

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(
                data,
                out OriginHeartGoldReadOnlySave? save),
            $"Não foi possível abrir: {fileName}");

        Assert.NotNull(save);

        int occupied = 0;
        int validChecksum = 0;
        int validSpecies = 0;
        int shown = 0;

        for (int box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
        {
            for (int slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
            {
                byte[] record = save!.GetBoxRecord(box, slot).ToArray();
                var pokemon = new PK4(record);

                if (pokemon.Species == 0)
                    continue;

                occupied++;

                bool speciesOk =
                    pokemon.Species <= pokemon.MaxSpeciesID;

                if (speciesOk)
                    validSpecies++;

                if (pokemon.ChecksumValid)
                    validChecksum++;

                if (box == 0 && slot < 3)
                {
                    _output.WriteLine(
                        $"Registro caixa {box + 1}, slot {slot + 1}: " +
                        Convert.ToHexString(record));
                }

                if (shown < 20)
                {
                    string level;
                    try { level = pokemon.CurrentLevel.ToString(); }
                    catch (Exception ex) { level = $"erro ({ex.GetType().Name})"; }

                    string nick;
                    try { nick = pokemon.Nickname; }
                    catch (Exception ex) { nick = $"erro ({ex.GetType().Name})"; }

                    _output.WriteLine(
                        $"Caixa {box + 1}, slot {slot + 1}: " +
                        $"Species={pokemon.Species}, " +
                        $"EXP={pokemon.EXP}, " +
                        $"Nivel(calculado)={level}, " +
                        $"Apelido={nick}, " +
                        $"TID={pokemon.TID16}, SID={pokemon.SID16}, " +
                        $"PID={pokemon.PID:X8}, " +
                        $"ChecksumValid={pokemon.ChecksumValid}, " +
                        $"Sanity={pokemon.Sanity}, " +
                        $"Hab={pokemon.Ability}, Nat={pokemon.Nature}, " +
                        $"IVs={pokemon.IV_HP}/{pokemon.IV_ATK}/{pokemon.IV_DEF}/{pokemon.IV_SPA}/{pokemon.IV_SPD}/{pokemon.IV_SPE}, " +
                        $"Golpes={pokemon.Move1}/{pokemon.Move2}/{pokemon.Move3}/{pokemon.Move4}, " +
                        $"OT={pokemon.OriginalTrainerName} ");
                    shown++;
                }
            }
        }

        _output.WriteLine($"Arquivo: {fileName}");
        _output.WriteLine($"Registros não vazios: {occupied}");
        _output.WriteLine($"Espécies dentro do intervalo: {validSpecies}");
        _output.WriteLine($"Checksums válidos: {validChecksum}");
    }

    [Fact]
    public void Rc5_Box1_ReadsNewGenSpeciesAndMoves()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        var pk = new PK4(save!.GetBoxRecord(0, 0).ToArray());

        // Registro estável (snapshot): espécie e golpes além do limite da 4ª geração.
        Assert.Equal(959, pk.Species);
        Assert.Equal("Tinkaton", pk.Nickname);
        Assert.Equal(893, (int)pk.Move1);
        Assert.Equal(583, (int)pk.Move2);
        Assert.True(pk.ChecksumValid);
    }


[Fact]
public void Rc5_Box1_OriginSessionReadsMovePPFromOriginCatalog()
{
    byte[] data = File.ReadAllBytes(Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

    Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
    Assert.NotNull(save);

    var original = new PK4(save!.GetBoxRecord(0, 0).ToArray());
    Assert.True(original.Valid);
    Assert.Equal(959, original.Species);

    Assert.True(
        OriginHeartGoldMoveData.IsAvailable,
        "O catálogo local de movimentos Origin não foi carregado.");

    using var session = new OriginHeartGoldEngineSession(data, "teste");

    var details = session.GetMoveDetails(0, 0);

    Assert.Equal(4, details.Moves.Count);
    Assert.False(details.SupportsRelearn);

    ushort[] originalMoves =
    [
        original.Move1, original.Move2,
        original.Move3, original.Move4
    ];

    int[] originalPP =
    [
        original.Move1_PP, original.Move2_PP,
        original.Move3_PP, original.Move4_PP
    ];

    int[] originalUps =
    [
        original.Move1_PPUps, original.Move2_PPUps,
        original.Move3_PPUps, original.Move4_PPUps
    ];

    for (int i = 0; i < 4; i++)
    {
        var detail = details.Moves[i];

        Assert.Equal(originalMoves[i], detail.Move);

        if (originalMoves[i] == 0)
        {
            Assert.Equal(0, detail.PP);
            Assert.Equal(0, detail.MaxPP);
            Assert.Equal(0, detail.PPUps);
            continue;
        }

        Assert.True(
            OriginHeartGoldMoveData.TryGet(originalMoves[i], out var move),
            $"Movimento {originalMoves[i]} não encontrado no catálogo.");

        Assert.NotNull(move.Name);
        Assert.True(move.BasePp > 0);

        int expectedUps = Math.Clamp(originalUps[i], 0, 3);
        int expectedMaxPP =
            move.BasePp + (move.BasePp * expectedUps / 5);

        Assert.Equal(expectedUps, detail.PPUps);
        Assert.Equal(expectedMaxPP, detail.MaxPP);
        Assert.Equal(Math.Clamp(originalPP[i], 0, expectedMaxPP), detail.PP);
    }
// O serviço de informações também precisa reconhecer os movimentos Origin.
var infoService = new MonInfoService();

Assert.True(OriginHeartGoldMoveData.TryGet(893, out var originMove));

var moveInfo = infoService.GetMove(session, 893);

Assert.NotNull(moveInfo);
Assert.Equal("GigatonHammr", originMove.Name);
Assert.Equal(originMove.Type, moveInfo!.Type);
Assert.Equal(originMove.BasePp, moveInfo.PP);
Assert.Equal(originMove.Power, moveInfo.Power);
Assert.Equal(originMove.Accuracy, moveInfo.Accuracy);

// Os quatro valores de PP máximo precisam vir do catálogo Origin.
var expectedThunderclapMaxPP = Enumerable.Range(0, 4)
    .Select(ups => originMove.BasePp + originMove.BasePp * ups / 5)
    .ToArray();

Assert.Equal(
    expectedThunderclapMaxPP,
    infoService.GetMaxPPByUps(session, 0, 0, 893).ToArray());
    // Os dois movimentos personalizados do Tinkaton precisam estar disponíveis.
    Assert.Contains(details.Moves, m => m.Move == 893);
    Assert.Contains(details.Moves, m => m.Move == 583);

    // A consulta não pode alterar o save original.
    Assert.True(data.AsSpan().SequenceEqual(session.Snapshot.OriginalBytes.Span));
    Assert.True(data.AsSpan().SequenceEqual(session.Serialize().Span));
}

[Fact]
public void Rc5_OriginMonInfoServiceProvidesCustomMoveNamesAndChoices()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

    using var session = new OriginHeartGoldEngineSession(bytes, "teste");
    var info = new MonInfoService();

    // Nomes próprios do catálogo Origin.
    Assert.Equal("GigatonHammr", info.GetMoveName(session, 893));
    Assert.Equal("Thunderclap", info.GetMoveName(session, 909));

    // O seletor deve receber os 902 movimentos disponíveis.
    var choices = info.GetMoveChoices(session, 0, 0);

    Assert.Equal(902, choices.Count);
    Assert.Contains(choices, move => move.Id == 893);
    Assert.Contains(choices, move => move.Id == 583);
    Assert.Contains(choices, move => move.Id == 909);

    // Os dados técnicos também devem vir do catálogo Origin.
    Assert.True(OriginHeartGoldMoveData.TryGet(893, out var reference));

    var selected = Assert.Single(choices, move => move.Id == 893);

    Assert.Equal(reference.BasePp, selected.PP);
    Assert.Equal(reference.Type, selected.Type);
    Assert.Equal(reference.Power, selected.Power);
    Assert.Equal(reference.Accuracy, selected.Accuracy);
}

[Fact]
public void Rc5_Box1_OriginSessionReplacesMoveInMemory()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

    byte[] originalCopy = bytes.ToArray();

    Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(bytes, out var originalSave));
    Assert.NotNull(originalSave);

    var original = new PK4(originalSave!.GetBoxRecord(0, 0).ToArray());
    Assert.True(original.Valid);
    Assert.Equal(959, original.Species);
    Assert.Equal(893, (int)original.Move1);

    using var session = new OriginHeartGoldEngineSession(bytes, "teste");

    var before = session.GetMoveDetails(0, 0);

    // ID 921 não pertence ao catálogo: deve ser recusado sem alterar a sessão.
    Assert.Throws<NotSupportedException>(() =>
        session.ApplyEdit(0, 0, new EntityEdit(Move1: 921)));

    Assert.True(originalCopy.AsSpan().SequenceEqual(session.Serialize().Span));

    // Substitui o primeiro golpe por Thunderclap.
    session.ApplyEdit(0, 0, new EntityEdit(Move1: 909));

    var after = session.GetMoveDetails(0, 0);
    var replacement = after.Moves[0];

    Assert.Equal(909, replacement.Move);
    Assert.Equal("Thunderclap", new MonInfoService().GetMoveName(session, 909));
    Assert.True(OriginHeartGoldMoveData.TryGet(909, out var reference));

    Assert.Equal(0, replacement.PPUps);
    Assert.Equal(reference.BasePp, replacement.MaxPP);
    Assert.Equal(reference.BasePp, replacement.PP);

    // Os outros três golpes precisam permanecer intactos.
    for (int i = 1; i < 4; i++)
    {
        Assert.Equal(before.Moves[i].Move, after.Moves[i].Move);
        Assert.Equal(before.Moves[i].PP, after.Moves[i].PP);
        Assert.Equal(before.Moves[i].PPUps, after.Moves[i].PPUps);
    }

    byte[] candidate = session.Serialize().ToArray();

    Assert.False(originalCopy.AsSpan().SequenceEqual(candidate));
    Assert.True(originalCopy.AsSpan().SequenceEqual(bytes));
    Assert.True(originalCopy.AsSpan().SequenceEqual(
        session.Snapshot.OriginalBytes.Span));

    // Reabre o candidato e verifica a integridade do Pokémon.
    Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updatedSave));
    Assert.NotNull(updatedSave);

    var reopened = new PK4(updatedSave!.GetBoxRecord(0, 0).ToArray());

    Assert.True(reopened.Valid);
    Assert.Equal(original.Species, reopened.Species);
    Assert.Equal(original.PID, reopened.PID);
    Assert.Equal((ushort)909, reopened.Move1);
    Assert.Equal(original.Move2, reopened.Move2);
    Assert.Equal(original.Move3, reopened.Move3);
    Assert.Equal(original.Move4, reopened.Move4);

    // Os campos especiais do Origin precisam continuar intactos.
    Assert.Equal(original.Data[0x15], reopened.Data[0x15]);
    Assert.Equal(
        original.Data.Slice(0x42, 2).ToArray(),
        reopened.Data.Slice(0x42, 2).ToArray());
}

[Fact]
public void Rc5_Box1_OriginSessionEditsPPUpsAndPPInMemory()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

    byte[] originalCopy = bytes.ToArray();

    Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(bytes, out var save));
    Assert.NotNull(save);

    var originalPokemon = new PK4(save!.GetBoxRecord(0, 0).ToArray());
    Assert.True(originalPokemon.Valid);
    Assert.Equal(959, originalPokemon.Species);

    using var session = new OriginHeartGoldEngineSession(bytes, "teste");

    var before = session.GetMoveDetails(0, 0);

    int index = Enumerable.Range(0, 4)
        .First(i => before.Moves[i].Move > 0);

    var selectedMove = before.Moves[index];

    Assert.True(
        OriginHeartGoldMoveData.TryGet(selectedMove.Move, out var move));

    // Escolhe um valor de PP Ups diferente do atual.
    int requestedUps = selectedMove.PPUps == 3
        ? 2
        : selectedMove.PPUps + 1;

    int[] ups = before.Moves.Select(m => m.PPUps).ToArray();
    ups[index] = requestedUps;

    session.ApplyMoveDetails(
        0, 0, new MoveDetailsEdit(PPUps: ups));

    var afterUps = session.GetMoveDetails(0, 0);
    var updatedMove = afterUps.Moves[index];

    int expectedMaxPP =
        move.BasePp + (move.BasePp * requestedUps / 5);

    Assert.Equal(requestedUps, updatedMove.PPUps);
    Assert.Equal(expectedMaxPP, updatedMove.MaxPP);

    // Alterar PP acima do limite deve resultar no máximo permitido.
    int[] requestedPP = afterUps.Moves.Select(m => m.PP).ToArray();
    requestedPP[index] = 255;

    session.ApplyMoveDetails(
        0, 0, new MoveDetailsEdit(PP: requestedPP));

    var afterPP = session.GetMoveDetails(0, 0);

    Assert.Equal(expectedMaxPP, afterPP.Moves[index].PP);
    Assert.Equal(requestedUps, afterPP.Moves[index].PPUps);

    // A alteração deve existir somente na cópia em memória.
    byte[] candidate = session.Serialize().ToArray();

    Assert.False(originalCopy.AsSpan().SequenceEqual(candidate));
    Assert.True(originalCopy.AsSpan().SequenceEqual(bytes));
    Assert.True(
        originalCopy.AsSpan().SequenceEqual(session.Snapshot.OriginalBytes.Span));

    Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(
        candidate, out var updatedSave));

    Assert.NotNull(updatedSave);

    var reopened = new PK4(updatedSave!.GetBoxRecord(0, 0).ToArray());

    Assert.True(reopened.Valid);
    Assert.Equal(originalPokemon.Species, reopened.Species);
    Assert.Equal(originalPokemon.PID, reopened.PID);

    Assert.Equal(originalPokemon.Move1, reopened.Move1);
    Assert.Equal(originalPokemon.Move2, reopened.Move2);
    Assert.Equal(originalPokemon.Move3, reopened.Move3);
    Assert.Equal(originalPokemon.Move4, reopened.Move4);

    Assert.Equal(originalPokemon.Data[0x15], reopened.Data[0x15]);
    Assert.Equal(
        originalPokemon.Data.Slice(0x42, 2).ToArray(),
        reopened.Data.Slice(0x42, 2).ToArray());

    Assert.Equal(expectedMaxPP, afterPP.Moves[index].PP);
}

    [Fact]
    public void Rc8_DiagnoseParty()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);

        _output.WriteLine($"PartyCount={save!.PartyCount}");

        for (int slot = 0; slot < save.PartyCount; slot++)
        {
            byte[] record = save.GetPartyRecord(slot).ToArray();
            byte[] pokeBytes = save.GetPartyPokemonBytes(slot).ToArray();
            _output.WriteLine(
                $"Time {slot + 1}: GetPartyRecord={record.Length} bytes, " +
                $"GetPartyPokemonBytes={pokeBytes.Length} bytes");
            _output.WriteLine($"Hex: {Convert.ToHexString(record)}");

            var pk = new PK4(record);

            string level;
            try { level = pk.CurrentLevel.ToString(); }
            catch (Exception ex) { level = $"erro ({ex.GetType().Name})"; }

            _output.WriteLine(
                $"Time {slot + 1}: Species={pk.Species}, " +
                $"Apelido={pk.Nickname}, OT={pk.OriginalTrainerName}, " +
                $"EXP={pk.EXP}, NivelCalculado={level}, " +
                $"NivelGuardado={pk.Stat_Level}, " +
                $"HP={pk.Stat_HPCurrent}/{pk.Stat_HPMax}, " +
                $"ATK={pk.Stat_ATK}, DEF={pk.Stat_DEF}, SPA={pk.Stat_SPA}, " +
                $"SPD={pk.Stat_SPD}, SPE={pk.Stat_SPE}, " +
                $"Nat={pk.Nature}, " +
                $"IVs={pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}, " +
                $"Golpes={pk.Move1}/{pk.Move2}/{pk.Move3}/{pk.Move4}, " +
                $"EVs={pk.EV_HP}/{pk.EV_ATK}/{pk.EV_DEF}/{pk.EV_SPA}/{pk.EV_SPD}/{pk.EV_SPE}, " +
                $"Checksum={pk.ChecksumValid}");
        }
    }

    [Fact]
    public void Rc8_DiagnoseTrainer()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);

        // Assumindo que GeneralOffset é a posição do bloco geral no arquivo.
        // O teste confirma ou desmente isso comparando TID/SID com o Pikachu.
        int g = save!.GeneralOffset;
        const int T = 0x64; // Trainer1 no HGSS padrão.
        ReadOnlySpan<byte> raw = data;

        string nameHex = Convert.ToHexString(raw.Slice(g + T, 16));
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(g + T + 0x10, 2));
        ushort sid = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(g + T + 0x12, 2));
        uint money = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(g + T + 0x14, 4));
        byte gender = raw[g + T + 0x18];
        byte language = raw[g + T + 0x19];
        byte badges = raw[g + T + 0x1A];
        ushort hours = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(g + T + 0x22, 2));
        byte minutes = raw[g + T + 0x24];
        byte seconds = raw[g + T + 0x25];

        _output.WriteLine($"GeneralOffset=0x{g:X}");
        _output.WriteLine($"NomeHex={nameHex}");
        _output.WriteLine($"TID={tid}, SID={sid}, Dinheiro={money}");
        _output.WriteLine($"Genero={gender}, Idioma={language}, Insignias=0x{badges:X2}");
        _output.WriteLine($"Tempo={hours}:{minutes:D2}:{seconds:D2}");

        // Conferência cruzada com o Pikachu do time.
        var pk = new PK4(save.GetPartyRecord(0).ToArray());
        _output.WriteLine($"Pikachu: TID={pk.TID16}, SID={pk.SID16}");

        Assert.Equal(pk.TID16, tid);
        Assert.Equal(pk.SID16, sid);
    }

    [Fact]
    public void Rc8_FindBagItems()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);

        ReadOnlySpan<byte> general = data.AsSpan(save!.GeneralOffset, 0xF7CC);

        int found = 0;
        for (int i = 0; i + 12 <= general.Length; i += 2)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(i, 2));
            ushort qty = BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(i + 2, 2));
            if (id != 4 || qty < 1 || qty > 999) continue;

            // Os próximos 8 bytes devem ser slots vazios.
            bool vazio = true;
            for (int k = 4; k < 12; k++)
                if (general[i + k] != 0) { vazio = false; break; }
            if (!vazio) continue;

            found++;
            int from = Math.Max(0, i - 16);
            int len = Math.Min(40, general.Length - from);
            _output.WriteLine(
                $"Acerto em 0x{i:X}: qty={qty}, contexto={Convert.ToHexString(general.Slice(from, len))}");
        }

        _output.WriteLine($"Total de acertos: {found}");
    }

    [Fact]
    public void Rc8_ListBagPairs()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));

        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);

        ReadOnlySpan<byte> general = data.AsSpan(save!.GeneralOffset, 0xF7CC);

        for (int i = 0x400; i + 4 <= 0xE00; i += 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(i, 2));
            ushort qty = BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(i + 2, 2));
            if (id == 0 || id > 2000 || qty == 0 || qty > 999) continue;
            _output.WriteLine($"0x{i:X}: id={id}, qty={qty}");
        }
    }

    [Theory]
    [InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv")]
    [InlineData("bolsa_teste.dsv")]
    public void DumpBagPairs(string fileName)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads, fileName));
        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);

        ReadOnlySpan<byte> general = data.AsSpan(save!.GeneralOffset, 0xF7CC);

        (string nome, int ini)[] padrao =
        {
            ("Items", 0x000), ("KeyItems", 0x294), ("TMHMs", 0x35C),
            ("Mail", 0x4F0), ("Medicine", 0x520), ("Berries", 0x5C0),
            ("Balls", 0x6C0), ("BattleItems", 0x720),
        };

        _output.WriteLine($"Arquivo: {fileName} ({data.Length} bytes)");

        for (int abs = 0x644; abs + 4 <= 0xF00; abs += 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(abs, 2));
            ushort qty = BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(abs + 2, 2));
            if (id == 0 || id > 2000 || qty == 0 || qty > 999) continue;

            int rel = abs - 0x644;
            string pouch = padrao.Last(p => p.ini <= rel).nome;
            _output.WriteLine(
                $"abs=0x{abs:X} rel=0x{rel:X} (padrao: {pouch}) id={id} qty={qty}");
        }
    }

    [Fact]
    public void Rc8_Party_Pikachu_MatchesOfficialEditor()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads, "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);
        Assert.Equal(1, save!.PartyCount);

        var pk = new PK4(save.GetPartyRecord(0).ToArray());

        Assert.Equal(25, pk.Species);
        Assert.Equal("Pikachu", pk.Nickname);
        Assert.Equal("AAAAAAA", pk.OriginalTrainerName);
        Assert.Equal(5, pk.Stat_Level);
        Assert.Equal(Nature.Impish, pk.Nature);

        Assert.Equal(22, pk.IV_HP);
        Assert.Equal(31, pk.IV_ATK);
        Assert.Equal(31, pk.IV_DEF);
        Assert.Equal(31, pk.IV_SPA);
        Assert.Equal(1, pk.IV_SPD);
        Assert.Equal(12, pk.IV_SPE);

        Assert.Equal(0, pk.EV_HP);
        Assert.Equal(0, pk.EV_ATK);
        Assert.Equal(1, pk.EV_DEF);
        Assert.Equal(0, pk.EV_SPA);
        Assert.Equal(0, pk.EV_SPD);
        Assert.Equal(0, pk.EV_SPE);

        Assert.Equal(39, (int)pk.Move1);
        Assert.Equal(84, (int)pk.Move2);
        Assert.Equal(98, (int)pk.Move3);
        Assert.Equal(86, (int)pk.Move4);

        Assert.Equal(20, pk.Stat_HPCurrent);
        Assert.Equal(20, pk.Stat_HPMax);
        Assert.Equal(12, pk.Stat_ATK);
        Assert.Equal(12, pk.Stat_DEF);
        Assert.Equal(10, pk.Stat_SPA);
        Assert.Equal(10, pk.Stat_SPD);
        Assert.Equal(16, pk.Stat_SPE);

        Assert.True(pk.ChecksumValid);
    }

    [Fact]
    public void Rc8_TrainerBoxesAndBag_MatchOfficialEditor()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Downloads, "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
        Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
        Assert.NotNull(save);

        // Bloco ativo na segunda partição espelhada.
        Assert.Equal(0x40000, save!.GeneralOffset);

        int g = save.GeneralOffset;
        const int T = 0x64;
        ReadOnlySpan<byte> raw = data;

        Assert.Equal("2B012B012B012B012B012B012B01FFFF",
            Convert.ToHexString(raw.Slice(g + T, 16)));
        Assert.Equal(62484, (int)BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(g + T + 0x10, 2)));
        Assert.Equal(64557, (int)BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(g + T + 0x12, 2)));
        Assert.Equal(3800u, BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(g + T + 0x14, 4)));
        Assert.Equal(0, (int)raw[g + T + 0x18]);   // gênero
        Assert.Equal(0, (int)raw[g + T + 0x1A]);   // insígnias
        Assert.Equal(0, (int)BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(g + T + 0x22, 2))); // horas
        Assert.Equal(2, (int)raw[g + T + 0x24]);   // minutos

        // Caixas vazias.
        for (int box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
            for (int slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
                Assert.Equal(0, new PK4(save.GetBoxRecord(box, slot).ToArray()).Species);

        // Bolsa: key items e Poké Ball x5.
        ReadOnlySpan<byte> general = data.AsSpan(g, 0xF7CC);
        (int rel, ushort id, ushort qty)[] esperado =
        {
            (0x294, 744, 1),
            (0x298, 745, 1),
            (0x788, 4, 5),
        };

        foreach (var (rel, id, qty) in esperado)
        {
            int abs = 0x644 + rel;
            Assert.Equal(id, BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(abs, 2)));
            Assert.Equal(qty, BinaryPrimitives.ReadUInt16LittleEndian(general.Slice(abs + 2, 2)));
        }
    }

    [Fact]
    public void OriginHeartGold_IsStructurallyValidWithPartialSupport()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
        var engine = new SaveEngine();

        var risk = engine.AssessLayoutRisk(bytes);
        Assert.NotNull(risk);
        Assert.Equal(LayoutRiskKind.PartiallySupported, risk!.Kind);
        Assert.False(risk.UserMayProceed);

        // A estrutura é válida, mas isso não autoriza a gravação.
        Assert.True(engine.Validate(bytes));
    }

    [Fact]
    public void OriginSession_ReadsHomeSave_LikeOfficialEditor()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
        using var session = new OriginHeartGoldEngineSession(bytes, "teste");

        // Snapshot: 24 caixas x 30 + 1 do time, caixas vazias.
        var snap = session.Snapshot;
        Assert.Equal(4, snap.Generation);
        Assert.Equal(24 * 30 + 1, snap.Slots.Count);
        Assert.All(snap.Slots.Where(s => s.Box >= 0), s => Assert.Null(s.Species));
        var party = Assert.Single(snap.Slots, s => s.Box == -1);
        Assert.Equal(25, party.Species);

        // Pikachu.
        var pk = session.ReadEntity(-1, 0);
        Assert.False(pk.IsEmpty);
        Assert.Equal("Pikachu", pk.Nickname);
        Assert.Equal(5, pk.Level);
        Assert.Equal(new[] { 22, 31, 31, 31, 1, 12 }, pk.IVs.ToArray());
        Assert.Equal(new[] { 0, 0, 1, 0, 0, 0 }, pk.EVs.ToArray());
        Assert.Equal(new[] { 39, 84, 98, 86 }, new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 });
        Assert.Equal(new[] { 20, 12, 12, 10, 10, 16 }, pk.Stats!.ToArray());

        // Treinador.
        var t = session.GetTrainer();
        Assert.Equal("AAAAAAA", t.Name);
        Assert.Equal(62484, t.TID);
        Assert.Equal(64557, t.SID);
        Assert.Equal(3800u, t.Money);
        var stats = session.GetTrainerStats();
        Assert.Equal(0, stats.PlayTimeHours);
        Assert.Equal(2, stats.PlayTimeMinutes);

        // Bolsa: só 2 key items e 5 Poké Balls.
        var items = session.GetBag().SelectMany(p => p.Items).ToList();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Id == 744 && i.Count == 1);
        Assert.Contains(items, i => i.Id == 745 && i.Count == 1);
        Assert.Contains(items, i => i.Id == 4 && i.Count == 5);
    }

    [Fact]
    public void OriginSession_AllowsTrainerEditButRejectsUnsupportedAndInvalidEdits()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));
        byte[] originalCopy = bytes.ToArray();

        using var session = new OriginHeartGoldEngineSession(bytes, "teste");

        // Pokémon da equipe ainda não pode ser editado.
        Assert.Throws<NotSupportedException>(() =>
            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "X")));

        // O item 4 pertence a Poké Balls, não à bolsa Items.
        Assert.Throws<ArgumentException>(() =>
            session.SetItemCount("Items", 4, 9));

        // A edição do treinador é suportada em memória.
        var trainerBefore = session.GetTrainer();

        session.SetTrainer(new TrainerInfo(
            "X",
            trainerBefore.TID,
            trainerBefore.SID,
            trainerBefore.Money,
            trainerBefore.Gender));

        Assert.Equal("X", session.GetTrainer().Name);

        byte[] candidate = session.Serialize().ToArray();
        var engine = new SaveEngine();

        Assert.False(candidate.AsSpan().SequenceEqual(originalCopy));
        Assert.True(engine.Validate(candidate));

        // A operação identificada como EditTrainer deve passar pelo validador especializado.
        Assert.Null(engine.CheckAuthorizedWriteSafety(
            originalCopy,
            candidate,
            scope: null,
            action: SaveAction.EditTrainer));

        Assert.Equal(
            LayoutRiskKind.PartiallySupported,
            engine.AssessLayoutRisk(candidate)!.Kind);

        // O array original e o snapshot de referência permanecem intactos.
        Assert.True(bytes.AsSpan().SequenceEqual(originalCopy));
        Assert.True(session.Snapshot.OriginalBytes.Span.SequenceEqual(originalCopy));
    }
    [Fact]
    public void OriginSession_EditsFriendshipInMemoryAndPreservesOriginalBytes()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(
            Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

        byte[] originalCopy = bytes.ToArray();

        Assert.True(
            OriginHeartGoldReadOnlyReader.TryOpen(bytes, out var originalSave));

        Assert.NotNull(originalSave);
        Assert.False(originalSave!.Tied);

        var originalPokemon = new PK4(
            originalSave.GetBoxRecord(0, 0).ToArray());

        Assert.Equal(959, originalPokemon.Species);
        Assert.True(originalPokemon.ChecksumValid);

        byte oldFriendship = originalPokemon.CurrentFriendship;
        byte expectedFriendship = oldFriendship == 255
            ? (byte)254
            : (byte)(oldFriendship + 1);

        using var session = new OriginHeartGoldEngineSession(bytes, "teste");

        session.ApplyEdit(0, 0, new EntityEdit(
            Friendship: expectedFriendship));

        // A sessão precisa mostrar a nova amizade.
        var updatedDetail = session.ReadEntity(0, 0);
        Assert.Equal(expectedFriendship, updatedDetail.Friendship);

        // A identidade do Pokémon deve continuar igual.
        byte[] candidate = session.Serialize().ToArray();

        Assert.False(bytes.AsSpan().SequenceEqual(candidate));
        Assert.Equal(originalPokemon.PID,
            new PK4(
                OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updatedSave)
                    ? updatedSave!.GetBoxRecord(0, 0).ToArray()
                    : throw new InvalidOperationException("O candidato não abriu."))
            .PID);

        Assert.NotNull(updatedSave);
        var reopenedPokemon = new PK4(
            updatedSave!.GetBoxRecord(0, 0).ToArray());

        Assert.Equal(959, reopenedPokemon.Species);
        Assert.Equal(expectedFriendship, reopenedPokemon.CurrentFriendship);
        Assert.True(reopenedPokemon.ChecksumValid);

        // Os campos especiais do Origin precisam continuar intactos.
        Assert.Equal(originalPokemon.Data[0x15], reopenedPokemon.Data[0x15]);
        Assert.Equal(
            originalPokemon.Data.Slice(0x42, 2).ToArray(),
            reopenedPokemon.Data.Slice(0x42, 2).ToArray());

        // Nem o array original nem o Snapshot de referência podem mudar.
        Assert.True(bytes.AsSpan().SequenceEqual(originalCopy));
        Assert.True(
            session.Snapshot.OriginalBytes.Span.SequenceEqual(originalCopy));

        // A estrutura continua válida e as gravações exigem uma operação autorizada.
        var engine = new SaveEngine();
        Assert.True(engine.Validate(candidate));
        Assert.Equal(
            LayoutRiskKind.PartiallySupported,
            engine.AssessLayoutRisk(candidate)!.Kind);
    }
    [Fact]
    public void SaveEngine_OpensOriginHeartGoldWithPartialSupport()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
        var engine = new SaveEngine();

        var snap = engine.Open(bytes, "teste");
        Assert.Equal("ORIGINHEARTGOLD", snap.Format);
        Assert.Equal(4, snap.Generation);
        Assert.Equal(24 * 30 + 1, snap.Slots.Count);

        using var session = engine.OpenSession(bytes, "teste");
        Assert.IsType<OriginHeartGoldEngineSession>(session);
        Assert.Equal("AAAAAAA", session.GetTrainer().Name);

        var description = engine.TryDescribe(bytes, "teste");
        Assert.NotNull(description);
        Assert.Equal("AAAAAAA", description!.TrainerName);

        // A validação estrutural passa, mas o layout exige operações explicitamente autorizadas.
        Assert.True(engine.Validate(bytes));
        Assert.Equal(LayoutRiskKind.PartiallySupported, engine.AssessLayoutRisk(bytes)!.Kind);
    }

    [Fact]
    public void DetectionToOpen_UsesAutoRouteForOrigin()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
            "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
        var engine = new SaveEngine();

        var description = engine.TryDescribe(bytes, "teste");
        Assert.NotNull(description);

        var guess = SaveIdentityRules.Guess(description!.GameName, description.Generation,
            "teste.dsv", null, "Nintendo DS");
        Assert.Equal(SaveFormat.Auto, guess.Format);

        using var session = engine.OpenSession(bytes, "teste", guess.Format);
        Assert.IsType<OriginHeartGoldEngineSession>(session);
    }
[Fact]
public void Rc5_BoxPokemon_PK4RoundTripAndEditPreserveOriginFields()
{
    string path = Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");

    byte[] file = File.ReadAllBytes(path);

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(file, out var save));

    Assert.NotNull(save);

    // Obtém uma cópia do Pokémon da primeira posição da primeira caixa.
    byte[] original = save!.GetBoxRecord(0, 0).ToArray();

    var pokemon = new PK4(original.ToArray());

    Assert.Equal(959, pokemon.Species);
    Assert.True(pokemon.ChecksumValid);

    // Guarda os campos especiais utilizados pelo Origin.
    byte originalAbilitySlot = pokemon.Data[0x15];

    byte[] originalEffectiveAbility =
        pokemon.Data.Slice(0x42, 2).ToArray();

    // Primeira etapa: descriptografar e criptografar sem editar.
    byte[] roundTrip = new byte[136];

    pokemon.WriteEncryptedDataStored(roundTrip);

    Assert.True(
        original.SequenceEqual(roundTrip),
        "O PK4 modificou bytes mesmo sem nenhuma edição.");

    // Segunda etapa: reabrir e alterar somente a amizade.
    var editable = new PK4(roundTrip.ToArray());

    byte expectedFriendship =
        (byte)(editable.OriginalTrainerFriendship ^ 1);

    editable.OriginalTrainerFriendship = expectedFriendship;

    byte[] editedRecord = new byte[136];

    editable.WriteEncryptedDataStored(editedRecord);

    // Terceira etapa: abrir novamente o Pokémon editado.
    var reopened = new PK4(editedRecord.ToArray());

    Assert.True(reopened.ChecksumValid);
    Assert.Equal(959, reopened.Species);
    Assert.Equal(pokemon.PID, reopened.PID);

    Assert.Equal(
        expectedFriendship,
        reopened.OriginalTrainerFriendship);

    Assert.Equal(
        originalAbilitySlot,
        reopened.Data[0x15]);

    Assert.Equal(
        originalEffectiveAbility,
        reopened.Data.Slice(0x42, 2).ToArray());
}
[Theory]
[InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv")]
[InlineData("Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv")]
public void DiagnoseMirrorStateBeforeBoxEditing(string fileName)
{
    byte[] data = File.ReadAllBytes(
        Path.Combine(Downloads, fileName));

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));

    Assert.NotNull(save);

    const int storageBlockSize = 0x18408;
    const int storageChunkSize = 0x183F4;
    const int footerSize = 16;

    int storage = save!.StorageOffset;

    ushort storedInnerCrc =
        BinaryPrimitives.ReadUInt16LittleEndian(
            data.AsSpan(storage + storageChunkSize, 2));

    ushort calculatedInnerCrc =
        CalculateCrc16Ccitt(
            data.AsSpan(storage, storageChunkSize));

    int footer = storage + storageBlockSize - footerSize;

    ushort storedOuterCrc =
        BinaryPrimitives.ReadUInt16LittleEndian(
            data.AsSpan(footer + 14, 2));

    ushort calculatedOuterCrc =
        CalculateCrc16Ccitt(
            data.AsSpan(storage, storageBlockSize - footerSize));

    _output.WriteLine($"Arquivo: {fileName}");
    _output.WriteLine($"Tied: {save.Tied}");
    _output.WriteLine($"GeneralOffset: 0x{save.GeneralOffset:X}");
    _output.WriteLine($"StorageOffset: 0x{storage:X}");
    _output.WriteLine($"Counter: {save.Counter}");
    _output.WriteLine(
        $"CRC interno: armazenado={storedInnerCrc:X4}, calculado={calculatedInnerCrc:X4}, válido={storedInnerCrc == calculatedInnerCrc}");
    _output.WriteLine(
        $"CRC externo: armazenado={storedOuterCrc:X4}, calculado={calculatedOuterCrc:X4}, válido={storedOuterCrc == calculatedOuterCrc}");
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
public void Rc5_BoxMutation_PreservesOriginalAndChangesOnlyAllowedBytes()
{
    string path = Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv");

    byte[] original = File.ReadAllBytes(path);
    byte[] originalSnapshot = original.ToArray();

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(original, out var save));

    Assert.NotNull(save);
    Assert.False(save!.Tied);

    byte[] originalRecord = save.GetBoxRecord(0, 0).ToArray();

    var pokemon = new PK4(originalRecord.ToArray());

    Assert.Equal(959, pokemon.Species);
    Assert.True(pokemon.ChecksumValid);

    byte abilitySlot = pokemon.Data[0x15];
    byte[] effectiveAbility = pokemon.Data.Slice(0x42, 2).ToArray();

    byte expectedFriendship =
        (byte)(pokemon.OriginalTrainerFriendship ^ 1);

    pokemon.OriginalTrainerFriendship = expectedFriendship;

    byte[] editedRecord = new byte[136];
    pokemon.WriteEncryptedDataStored(editedRecord);

    Assert.True(
        OriginHeartGoldBoxMutation.TryCreateCandidate(
            original,
            0,
            0,
            editedRecord,
            out byte[] candidate),
        "O gravador experimental recusou o registro.");

    Assert.NotEmpty(candidate);
    Assert.False(original.AsSpan().SequenceEqual(candidate));

    // O arquivo original continua intacto em memória.
    Assert.True(original.AsSpan().SequenceEqual(originalSnapshot));

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updated));

    Assert.NotNull(updated);
    Assert.False(updated!.Tied);
    Assert.Equal(save.GeneralOffset, updated.GeneralOffset);
    Assert.Equal(save.StorageOffset, updated.StorageOffset);
    Assert.Equal(save.Counter, updated.Counter);

    var reopened = new PK4(
        updated.GetBoxRecord(0, 0).ToArray());

    Assert.Equal(959, reopened.Species);
    Assert.True(reopened.ChecksumValid);
    Assert.Equal(expectedFriendship, reopened.OriginalTrainerFriendship);

    Assert.Equal(abilitySlot, reopened.Data[0x15]);
    Assert.Equal(
        effectiveAbility,
        reopened.Data.Slice(0x42, 2).ToArray());

    const int storageSize = 0x18408;
    const int storageChunkSize = 0x183F4;
    const int footerSize = 16;

    int storage = save.StorageOffset;

    // O CRC interno inválido original do rc5 deve ser preservado.
    ushort oldInner = BinaryPrimitives.ReadUInt16LittleEndian(
        original.AsSpan(storage + storageChunkSize, 2));

    ushort newInner = BinaryPrimitives.ReadUInt16LittleEndian(
        candidate.AsSpan(storage + storageChunkSize, 2));

    Assert.Equal(oldInner, newInner);

    // O CRC externo precisa continuar válido após a alteração.
    int footer = storage + storageSize - footerSize;

    ushort storedOuter = BinaryPrimitives.ReadUInt16LittleEndian(
        candidate.AsSpan(footer + 14, 2));

    ushort calculatedOuter = CalculateCrc16Ccitt(
        candidate.AsSpan(storage, storageSize - footerSize));

    Assert.Equal(storedOuter, calculatedOuter);

    // O espelho inativo deve permanecer completamente intacto.
    int inactiveStorage =
        storage == 0xF800 ? 0x4F800 : 0xF800;

    Assert.True(
        original.AsSpan(inactiveStorage, storageSize)
            .SequenceEqual(candidate.AsSpan(inactiveStorage, storageSize)));

    // O footer DeSmuME deve ser idêntico.
    Assert.True(
        original.AsSpan(0x80000)
            .SequenceEqual(candidate.AsSpan(0x80000)));

    // Somente estes intervalos podem sofrer alterações.
    bool IsAllowedOffset(int offset)
    {
        int recordOffset =
            storage + (0 * 0x1000) + (0 * 136);

        bool record = offset >= recordOffset &&
                      offset < recordOffset + 136;

        bool modifiedFlag = offset >= storage + 0x18004 &&
                            offset < storage + 0x18008;

        bool innerChecksum = offset >= storage + storageChunkSize &&
                             offset < storage + storageChunkSize + 2;

        bool outerChecksum = offset >= footer + 14 &&
                             offset < footer + 16;

        return record || modifiedFlag || innerChecksum || outerChecksum;
    }

    for (int i = 0; i < original.Length; i++)
    {
        if (!IsAllowedOffset(i))
            Assert.Equal(original[i], candidate[i]);
    }
}

[Fact]
public void OriginSession_EditsIVsAndEVsInAppOrderAndClampsValues()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(
        Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc5.dsv"));

    byte[] originalCopy = bytes.ToArray();

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(bytes, out var originalSave));

    Assert.NotNull(originalSave);

    var originalPokemon = new PK4(
        originalSave!.GetBoxRecord(0, 0).ToArray());

    Assert.Equal(959, originalPokemon.Species);

    int[] requestedIVs = [-5, 31, 7, 99, 0, 19];
    int[] expectedIVs = [0, 31, 7, 31, 0, 19];

    int[] requestedEVs = [-1, 300, 123, 1, 254, 17];
    int[] expectedEVs = [0, 255, 123, 1, 254, 17];

    using var session = new OriginHeartGoldEngineSession(bytes, "teste");

    session.ApplyEdit(0, 0, new EntityEdit(
        IVs: requestedIVs,
        EVs: requestedEVs));

    var detail = session.ReadEntity(0, 0);

    Assert.Equal(expectedIVs, detail.IVs.ToArray());
    Assert.Equal(expectedEVs, detail.EVs.ToArray());

    byte[] candidate = session.Serialize().ToArray();

    Assert.True(
        OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updatedSave));

    Assert.NotNull(updatedSave);

    var reopened = new PK4(
        updatedSave!.GetBoxRecord(0, 0).ToArray());

    Assert.Equal(959, reopened.Species);
    Assert.Equal(originalPokemon.PID, reopened.PID);
    Assert.Equal(expectedIVs,
        new[] { reopened.IV_HP, reopened.IV_ATK, reopened.IV_DEF,
                reopened.IV_SPA, reopened.IV_SPD, reopened.IV_SPE });

    Assert.Equal(expectedEVs,
        new[] { reopened.EV_HP, reopened.EV_ATK, reopened.EV_DEF,
                reopened.EV_SPA, reopened.EV_SPD, reopened.EV_SPE });

    Assert.True(reopened.ChecksumValid);

    Assert.Equal(originalPokemon.Data[0x15], reopened.Data[0x15]);
    Assert.Equal(
        originalPokemon.Data.Slice(0x42, 2).ToArray(),
        reopened.Data.Slice(0x42, 2).ToArray());

    Assert.True(bytes.AsSpan().SequenceEqual(originalCopy));
    Assert.True(session.Snapshot.OriginalBytes.Span.SequenceEqual(originalCopy));
}
}
