
using System.IO;
using PKForge.Engine;
using Xunit;
using PKHeX.Core;
using Xunit.Abstractions;

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

private const string Downloads = @"C:\Users\PROATI\Downloads";

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
   public void Rc8_Box1Slot1_IsLillipup()
   {
       byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
           "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));
       Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
       var pk = new PK4(save!.GetBoxRecord(0, 0).ToArray());

       Assert.Equal(506, pk.Species);
       Assert.Equal("Lillipup", pk.Nickname);
       Assert.InRange(pk.EXP, 135u, 178u);
       Assert.Equal(62484, (int)pk.TID16);
       Assert.Equal(64557, (int)pk.SID16);
       Assert.Equal(33, (int)pk.Move1);
       Assert.Equal(43, (int)pk.Move2);
       Assert.Equal(526, (int)pk.Move3);
   }
[Fact]
public void Rc8_Box1Slot1_Lillipup_MatchesOfficialEditor()
{
    byte[] data = File.ReadAllBytes(Path.Combine(Downloads,
        "Origin_HeartGold_v4.0.3_EN_v1.0.0-rc8 (1).dsv"));

    Assert.True(OriginHeartGoldReadOnlyReader.TryOpen(data, out var save));
    var pk = new PK4(save!.GetBoxRecord(0, 0).ToArray());

    // Confirmado contra o editor oficial
    Assert.Equal(506, pk.Species);
    Assert.Equal("Lillipup", pk.Nickname);
    Assert.Equal("AAAAAAA", pk.OriginalTrainerName);
    Assert.Equal(Nature.Calm, pk.Nature);

    Assert.Equal(17, pk.IV_HP);
    Assert.Equal(26, pk.IV_ATK);
    Assert.Equal(19, pk.IV_DEF);
    Assert.Equal(30, pk.IV_SPA);
    Assert.Equal(6, pk.IV_SPD);
    Assert.Equal(18, pk.IV_SPE);

    Assert.Equal(33, (int)pk.Move1);
    Assert.Equal(43, (int)pk.Move2);
    Assert.Equal(526, (int)pk.Move3);
    Assert.Equal(0, (int)pk.Move4);

    // Nível 5 = EXP de 135 até 178 (curva Medium Slow)
    Assert.InRange(pk.EXP, 135u, 178u);
    Assert.Equal(62484, (int)pk.TID16);
    Assert.Equal(64557, (int)pk.SID16);
    Assert.True(pk.ChecksumValid);

    // Valor bruto do byte de habilidade; NÃO interpretar como ID de habilidade
    Assert.Equal(1, (int)pk.Ability);
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

}
