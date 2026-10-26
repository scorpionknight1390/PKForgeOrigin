
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
}

private const string Downloads = @"C:\PKForgeSaves";

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

    // Registro estável (snapshot): espécie e golpes além do limite da 4ª geração
    Assert.Equal(959, pk.Species);
    Assert.Equal("Tinkaton", pk.Nickname);
    Assert.Equal(893, (int)pk.Move1);
    Assert.Equal(583, (int)pk.Move2);
    Assert.True(pk.ChecksumValid);
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
    const int T = 0x64; // Trainer1 no HGSS padrão
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

    // Conferência cruzada com o Pikachu do time
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

        // os próximos 8 bytes devem ser slots vazios
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

    // Bloco ativo na segunda partição espelhada
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

    // Caixas vazias
    for (int box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
        for (int slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
            Assert.Equal(0, new PK4(save.GetBoxRecord(box, slot).ToArray()).Species);

    // Bolsa: key items e Poké Ball x5
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
public void OriginHeartGold_IsAlwaysReadOnly()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
    var engine = new SaveEngine();

    var risk = engine.AssessLayoutRisk(bytes);
    Assert.NotNull(risk);
    Assert.Equal(LayoutRiskKind.CorruptingLayout, risk!.Kind);
    Assert.False(risk.UserMayProceed);

    Assert.False(engine.Validate(bytes));
}

[Fact]
public void OriginSession_ReadsHomeSave_LikeOfficialEditor()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
    using var session = new OriginHeartGoldEngineSession(bytes, "teste");

    // Snapshot: 24 caixas x 30 + 1 do time, caixas vazias
    var snap = session.Snapshot;
    Assert.Equal(4, snap.Generation);
    Assert.Equal(24 * 30 + 1, snap.Slots.Count);
    Assert.All(snap.Slots.Where(s => s.Box >= 0), s => Assert.Null(s.Species));
    var party = Assert.Single(snap.Slots, s => s.Box == -1);
    Assert.Equal(25, party.Species);

    // Pikachu
    var pk = session.ReadEntity(-1, 0);
    Assert.False(pk.IsEmpty);
    Assert.Equal("Pikachu", pk.Nickname);
    Assert.Equal(5, pk.Level);
    Assert.Equal(new[] { 22, 31, 31, 31, 1, 12 }, pk.IVs.ToArray());
    Assert.Equal(new[] { 0, 0, 1, 0, 0, 0 }, pk.EVs.ToArray());
    Assert.Equal(new[] { 39, 84, 98, 86 }, new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 });
    Assert.Equal(new[] { 20, 12, 12, 10, 10, 16 }, pk.Stats!.ToArray());

    // Treinador
    var t = session.GetTrainer();
    Assert.Equal("AAAAAAA", t.Name);
    Assert.Equal(62484, t.TID);
    Assert.Equal(64557, t.SID);
    Assert.Equal(3800u, t.Money);
    var stats = session.GetTrainerStats();
    Assert.Equal(0, stats.PlayTimeHours);
    Assert.Equal(2, stats.PlayTimeMinutes);

    // Bolsa: só 2 key items e 5 Poké Balls
    var items = session.GetBag().SelectMany(p => p.Items).ToList();
    Assert.Equal(3, items.Count);
    Assert.Contains(items, i => i.Id == 744 && i.Count == 1);
    Assert.Contains(items, i => i.Id == 745 && i.Count == 1);
    Assert.Contains(items, i => i.Id == 4 && i.Count == 5);
}

[Fact]
public void OriginSession_RefusesEveryEditAndNeverChangesBytes()
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
    using var session = new OriginHeartGoldEngineSession(bytes, "teste");

    Assert.Throws<NotSupportedException>(() => session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "X")));
    Assert.Throws<NotSupportedException>(() => session.SetItemCount("Items", 4, 9));
    Assert.Throws<NotSupportedException>(() => session.SetTrainer(new TrainerInfo("X", 1, 1, 1u, 0)));

    Assert.True(session.Serialize().Span.SequenceEqual(bytes));
}

[Fact]
public void SaveEngine_OpensOriginHeartGold_ReadOnly()
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

    // gravação continua barrada
    Assert.False(engine.Validate(bytes));
    Assert.Equal(LayoutRiskKind.CorruptingLayout, engine.AssessLayoutRisk(bytes)!.Kind);
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

}
