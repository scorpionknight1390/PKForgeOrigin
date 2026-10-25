using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Origin HeartGold v4.0.3 como sessão SOMENTE LEITURA. Lê time, caixas, treinador e bolsa
/// com o leitor validado contra o editor oficial; toda edição é recusada. Serialize devolve
/// exatamente os bytes recebidos, então nada pode ser gravado por esta sessão.
/// </summary>
internal sealed class OriginHeartGoldEngineSession : ISaveEngineSession
{
    public const string SnapshotTag = "ORIGINHEARTGOLD";

    private const int GeneralSize = 0xF7CC;        // igual ao leitor
    private const int TrainerOffset = 0x64;        // validado (nome, TID, SID, dinheiro, tempo)
    private const int BagOffset = 0x644;           // base da bolsa no bloco geral
    private const int Gen4MaxItemId = 536;
    private const int ProvisionalMaxSpecies = 1025; // PROVISÓRIO: máximo real do hack não confirmado

    // (nome, deslocamento a partir de BagOffset, nº de slots).
    // Inícios medidos nos saves; tamanhos deduzidos pela distância entre inícios
    // (TMs/HMs = 151 slots). Battle Items = 30 é suposição do padrão HGSS.
    private static readonly (string Name, int Rel, int Slots)[] Pouches =
    [
        ("Items",        0x000, 165),
        ("Key Items",    0x294,  50),
        ("TMs & HMs",    0x35C, 151),
        ("Mail",         0x5B8,  12),
        ("Medicine",     0x5E8,  40),
        ("Berries",      0x688,  64),
        ("Poké Balls",   0x788,  24),
        ("Battle Items", 0x7E8,  30),
    ];

    private readonly byte[] _originalBytes;
    private readonly OriginHeartGoldReadOnlySave _save;
    private readonly string? _displayName;
    
    public OriginHeartGoldEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        if (!OriginHeartGoldReadOnlyReader.TryOpen(_originalBytes, out var save) || save is null)
            throw new InvalidDataException("These bytes are not an Origin HeartGold save PKForge can read.");
        _save = save;
        _displayName = displayName;
    }

    private static NotSupportedException NotYet(string what) =>
        new($"Origin HeartGold is read-only in PKForge for now: {what} is not available yet. The original save was not touched.");

    private ReadOnlySpan<byte> General => _originalBytes.AsSpan(_save.GeneralOffset, GeneralSize);

    // ── Leitura de Pokémon ──

    private PK4? TryMon(int box, int slot)
    {
        byte[] data;
        if (box == -1)
        {
            if ((uint)slot >= (uint)_save.PartyCount) return null;
            data = _save.GetPartyRecord(slot).ToArray();
        }
        else
        {
            if ((uint)box >= OriginHeartGoldReadOnlySave.BoxCount ||
                (uint)slot >= OriginHeartGoldReadOnlySave.BoxSlotCount) return null;
            data = _save.GetBoxRecord(box, slot).ToArray();
        }
        var pk = new PK4(data);
        return pk.Species == 0 ? null : pk;
    }

    private static string SpeciesName(int species)
    {
        var names = GameInfo.GetStrings("en").specieslist;
        return (uint)species < (uint)names.Length ? names[species] : $"#{species}";
    }

    private static int LevelOf(PK4 pk, bool party)
    {
        try { return party && pk.Stat_Level > 0 ? pk.Stat_Level : pk.CurrentLevel; }
        catch (Exception) { return 0; }
    }

    private static SpriteTraits SafeTraits(PK4 pk)
    {
        try { return EntitySprite.Traits(pk); }
        catch (Exception) { return default; }
    }

    private static SlotSummary Summary(int box, int slot, PK4? pk, bool party)
    {
        if (pk is null) return new SlotSummary(box, slot, null, null, false, true);
        var nick = (party || pk.IsNicknamed) ? pk.Nickname : null;
        return new SlotSummary(box, slot, pk.Species, nick, pk.IsShiny, true,
            pk.Form, pk.IsEgg, pk.HeldItem, SafeTraits(pk));
    }

    public SaveSnapshot Snapshot => BuildSnapshot();

    private SaveSnapshot BuildSnapshot()
    {
        var slots = new List<SlotSummary>(OriginHeartGoldReadOnlySave.BoxCount * OriginHeartGoldReadOnlySave.BoxSlotCount + 6);
        for (var box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
        for (var slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
            slots.Add(Summary(box, slot, TryMon(box, slot), party: false));
        for (var i = 0; i < _save.PartyCount; i++)
            slots.Add(Summary(-1, i, TryMon(-1, i), party: true));
        return new SaveSnapshot(SnapshotTag, 4, _originalBytes, slots, _displayName);
    }

    public EntityDetail ReadEntity(int box, int slot)
    {
        var pk = TryMon(box, slot);
        if (pk is null)
            return new EntityDetail(box, slot, true, 0, "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0,
                new int[6], new int[6], false, 0, "");

        var party = box == -1;
        int[]? stats = party
            ? new[] { pk.Stat_HPMax, pk.Stat_ATK, pk.Stat_DEF, pk.Stat_SPA, pk.Stat_SPD, pk.Stat_SPE }
            : null;

        return new EntityDetail(
            box, slot, false, pk.Species, SpeciesName(pk.Species), pk.Form, pk.Nickname,
            LevelOf(pk, party), (int)pk.Nature,
            0, // Ability: o byte bruto do Origin NÃO é um id de habilidade (valores 0..2)
            pk.HeldItem, pk.Move1, pk.Move2, pk.Move3, pk.Move4,
            new[] { pk.IV_HP, pk.IV_ATK, pk.IV_DEF, pk.IV_SPA, pk.IV_SPD, pk.IV_SPE },
            new[] { pk.EV_HP, pk.EV_ATK, pk.EV_DEF, pk.EV_SPA, pk.EV_SPD, pk.EV_SPE },
            pk.IsShiny, pk.Ball, pk.OriginalTrainerName,
            Types: null, Gender: pk.Gender, Friendship: pk.CurrentFriendship,
            Stats: stats, CurrentHp: party ? pk.Stat_HPCurrent : 0,
            StatusCondition: 0, Traits: SafeTraits(pk));
    }

    public RngInfo GetRngInfo(int box, int slot)
    {
        var pk = TryMon(box, slot);
        if (pk is null) return new RngInfo(0, null, 0, false, false, new int[6], 0, 2);
        return new RngInfo(pk.PID, null, (int)pk.Nature, pk.IsShiny, false,
            new[] { pk.IV_HP, pk.IV_ATK, pk.IV_DEF, pk.IV_SPA, pk.IV_SPD, pk.IV_SPE }, 0, pk.Gender);
    }

    public MetInfo GetMetInfo(int box, int slot)
    {
        var pk = TryMon(box, slot);
        return new MetInfo(0, "", 0, "", false, pk?.IsEgg ?? false, 0, "", "", false,
            0, "", 0, "", false, pk?.TID16 ?? 0, pk?.SID16 ?? 0);
    }

    // ── Treinador ──

    public TrainerInfo GetTrainer()
    {
        var g = General;
        var name = StringConverter4.GetString(g.Slice(TrainerOffset, 16));
        var tid = BinaryPrimitives.ReadUInt16LittleEndian(g.Slice(TrainerOffset + 0x10, 2));
        var sid = BinaryPrimitives.ReadUInt16LittleEndian(g.Slice(TrainerOffset + 0x12, 2));
        var money = BinaryPrimitives.ReadUInt32LittleEndian(g.Slice(TrainerOffset + 0x14, 4));
        return new TrainerInfo(name, tid, sid, money, g[TrainerOffset + 0x18]);
    }

    public TrainerStats GetTrainerStats()
    {
        var g = General;
        var hours = BinaryPrimitives.ReadUInt16LittleEndian(g.Slice(TrainerOffset + 0x22, 2));
        int minutes = g[TrainerOffset + 0x24];
        return new TrainerStats(true, hours, minutes, 65535, false, 0, 0, false, 0, 0);
    }

    // ── Bolsa ──

    public IReadOnlyList<BagPouch> GetBag()
    {
        var g = General;
        var result = new List<BagPouch>(Pouches.Length);
        foreach (var (name, rel, slots) in Pouches)
        {
            var items = new List<BagItem>();
            for (var i = 0; i < slots; i++)
            {
                var at = BagOffset + rel + i * 4;
                int id = BinaryPrimitives.ReadUInt16LittleEndian(g.Slice(at, 2));
                int count = BinaryPrimitives.ReadUInt16LittleEndian(g.Slice(at + 2, 2));
                if (id != 0 && count != 0) items.Add(new BagItem(id, count));
            }
            result.Add(new BagPouch(name, items));
        }
        return result;
    }

    public IReadOnlyList<string> GetItemNames()
    {
        // Lista moderna do PKHeX: nomes de ids de 4ª geração podem estar trocados, e ids
        // acima de 536 são itens do hack (ex.: 744 e 745 nos key items), sem nome conhecido.
        var names = (string[])GameInfo.GetStrings("en").itemlist.Clone();
        if (names.Length < 1024) Array.Resize(ref names, 1024);
        for (var i = Gen4MaxItemId + 1; i < names.Length; i++)
            names[i] = $"Origin item #{i}";
        return names;
    }

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => [];
    public int SetItemCount(string pouchName, int itemId, int count) => throw NotYet("editar a bolsa");

    // ── Metadados da sessão ──

    public int Generation => 4;
    public IReadOnlyList<string> GameNames => ["Origin HeartGold"];
    public int MaxSpeciesId => ProvisionalMaxSpecies;
    public string GetBoxName(int box) => $"BOX {box + 1}"; // provisório: nomes reais ainda não decodificados
    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    public bool SupportsRTCRepair => false;
    public bool SupportsLegalFashionUnlock => false;
    public bool SupportsStylePoints => false;
    public bool SupportsCompassSettings => false;

    public ReadOnlyMemory<byte> Serialize() => _originalBytes;

    // ── Consultas sem suporte: valores neutros ──

    public IReadOnlyList<int> GetAbilityChoices(int species, int form) => [];
    public IReadOnlyList<string> GetFormChoices(int species) => [];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => [];
    public BaseStats GetBaseStats(int species) => new(0, 0, 0, 0, 0, 0);
    public string GetShowdownText(int box, int slot) => "";
    public string ExportBoxShowdown(int box) => "";
    public TrainingCaps GetTrainingCaps() => new(31, 255);
    public IReadOnlyList<int> GetMissingSpecies() => [];
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public IReadOnlyList<CountedEntry> GetPokeBeans() => [];
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() => [];
    public DaycareInfo GetDaycare() => new(false, []);
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];
    public PotentialInfo GetPotential(int box, int slot) =>
        new(false, 0, "", "", false, false, [], false, 0, [], false, [], false, [], []);
    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];
    public MoveDetails GetMoveDetails(int box, int slot) => new([], false, []);
    public MoveShopInfo GetMoveShop(int box, int slot) => new(false, []);
    public CosmeticInfo GetCosmetics(int box, int slot) =>
        new([], [], false, 0, 0, false, 0, false, 0, 0, false, 0, 0, false, false, false, 0, false, false, false, false, 0u);
    public PokerusInfo GetPokerus(int box, int slot) => new(false, PokerusStatus.Susceptible, 0, 0);
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) => [];
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) => new(false, -1, "", []);
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) => new Dictionary<string, int>();
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public int GetStylePoints() => 0;

    // ── Tudo o que altera ou exporta é recusado ──

    public void ApplyEdit(int box, int slot, EntityEdit edit) => throw NotYet("editar Pokémon");
    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot) => throw NotYet("mover Pokémon");
    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot) => throw NotYet("duplicar Pokémon");
    public SlotExport ExportSlot(int box, int slot) => throw NotYet("exportar Pokémon");
    public bool RerollNatureKeepShiny(int box, int slot, int nature) => throw NotYet("editar natureza");
    public void SetDexEntry(int species, bool seen, bool caught) => throw NotYet("editar a Pokédex");
    public void CompleteDex() => throw NotYet("completar a Pokédex");
    public void ReleaseSlot(int box, int slot) => throw NotYet("soltar Pokémon");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("living dex");
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes, bool reverse) => throw NotYet("ordenar caixas");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes) => throw NotYet("edição em lote");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("edição em lote");
    public void SwapBoxes(int a, int b) => throw NotYet("trocar caixas");
    public void DeleteBox(int box) => throw NotYet("apagar caixa");
    public void ClearBox(int box) => throw NotYet("limpar caixa");
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format) => throw NotYet("importar Pokémon");
    public void SetTrainer(TrainerInfo trainer) => throw NotYet("editar treinador");
    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile) => throw NotYet("trocar dono");
    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("editar estatísticas do treinador");
    public void RepairRTC() => throw NotYet("reparar relógio");
    public int SetPokeBeanCount(int index, int count) => throw NotYet("editar feijões");
    public int SetGrandUndergroundItemCount(int itemId, int count) => throw NotYet("editar itens");
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("retirar da creche");
    public void UnlockAllLegalFashion() => throw NotYet("desbloquear roupas");
    public void SetStylePoints(int value) => throw NotYet("editar pontos de estilo");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("editar origem");
    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit) => throw NotYet("editar potencial");
    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit) => throw NotYet("editar golpes");
    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) => throw NotYet("editar loja de golpes");
    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) => throw NotYet("editar cosméticos");
    public void SetPokerus(int box, int slot, PokerusStatus status) => throw NotYet("editar Pokérus");
    public void SetRibbon(int box, int slot, string id, int value) => throw NotYet("editar fitas");
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) => throw NotYet("editar fitas");
    public int AwardAllObtainableRibbons(int box, int slot) => throw NotYet("dar fitas");
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) => throw NotYet("gerar Pokémon");

    public void Dispose() { }
}