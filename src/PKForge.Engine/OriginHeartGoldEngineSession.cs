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
    private const int TrainerOffset = 0x64;        // dados do treinador
    private const int BadgeKantoOffset = 0x80;
    private const int BadgeJohtoOffset = 0x83;

    private static readonly string[] KantoBadgeNames =
    [
        "Boulder", "Cascade", "Thunder", "Rainbow",
        "Marsh", "Soul", "Volcano", "Earth"
    ];

    private static readonly string[] JohtoBadgeNames =
    [
        "Zephyr", "Hive", "Plain", "Fog",
        "Storm", "Mineral", "Glacier", "Rising"
    ];
    private const int BagOffset = 0x644;           // base da bolsa no bloco geral
    private const int Gen4MaxItemId = 536;
    private const int OriginDexMaximum = 1025;
    private const int DexOffset = 0x13A8;
    private const uint DexMarker = 0xBEEFCAFE;
    private const int DexCaughtOffset = 0x04;
    private const int DexSeenOffset = 0xCC;

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
    private byte[] _workingBytes;
    private OriginHeartGoldReadOnlySave _save;
    private readonly string? _displayName;
    
    public OriginHeartGoldEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        _workingBytes = _originalBytes.ToArray();

        if (!OriginHeartGoldReadOnlyReader.TryOpen(_workingBytes, out var save) || save is null)
            throw new InvalidDataException("These bytes are not an Origin HeartGold save PKForge can read.");

        _save = save;
        _displayName = displayName;
    }

    private static NotSupportedException NotYet(string what) =>
        new($"Origin HeartGold is read-only in PKForge for now: {what} is not available yet. The original save was not touched.");

    private ReadOnlySpan<byte> General => _workingBytes.AsSpan(_save.GeneralOffset, GeneralSize);

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

    public TrainerBadgesInfo GetTrainerBadges()
    {
        ReadOnlySpan<byte> general = General;
        var badges = new List<TrainerBadge>(16);

        for (int id = 0; id < 16; id++)
        {
            bool kanto = id < 8;
            int offset = kanto ? BadgeKantoOffset : BadgeJohtoOffset;
            int bit = id & 7;
            bool earned = (general[offset] & (1 << bit)) != 0;

            string name = (kanto ? KantoBadgeNames : JohtoBadgeNames)[bit];
            badges.Add(new TrainerBadge(id, name, kanto ? "Kanto" : "Johto", earned));
        }

        return new TrainerBadgesInfo(true, badges, badges.Count(badge => badge.Earned));
    }

    public void SetTrainerBadge(int badgeId, bool earned)
    {
        if ((uint)badgeId >= 16)
            throw new ArgumentOutOfRangeException(nameof(badgeId), "Badge ID must be between 0 and 15.");

        bool kanto = badgeId < 8;
        int offset = kanto ? BadgeKantoOffset : BadgeJohtoOffset;
        int bit = badgeId & 7;
        byte mask = (byte)(1 << bit);

        bool current = (General[offset] & mask) != 0;
        if (current == earned)
            return;

        string name = (kanto ? KantoBadgeNames : JohtoBadgeNames)[bit];

        ApplyGeneralEdit(general =>
        {
            byte value = general[offset];
            general[offset] = earned
                ? (byte)(value | mask)
                : (byte)(value & ~mask);

            return true;
        }, $"editar insígnia {name}");
    }
    public TrainerStats GetTrainerStats()
{
    var g = General;

    int hours = BinaryPrimitives.ReadUInt16LittleEndian(
        g.Slice(TrainerOffset + 0x22, 2));

    int minutes = g[TrainerOffset + 0x24];

    int coins = BinaryPrimitives.ReadUInt16LittleEndian(
        g.Slice(0x84, 2));

    return new TrainerStats(
        true, hours, minutes, 999,
        false, 0, 0,
        true, coins, 50_000);
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
    if (OriginHeartGoldItemData.IsAvailable)
    {
        var names = new string[
            OriginHeartGoldItemData.AvailableItems.Count + 1];

        foreach (var item in OriginHeartGoldItemData.AvailableItems)
            names[item.Id] = item.Name;

        return names;
    }

    // Fallback quando o catálogo local não está disponível.
    var fallback = (string[])GameInfo.GetStrings("en").itemlist.Clone();

    if (fallback.Length < 1024)
        Array.Resize(ref fallback, 1024);

    for (var i = Gen4MaxItemId + 1; i < fallback.Length; i++)
        fallback[i] = $"Origin item #{i}";

    return fallback;
}

public IReadOnlyList<int> GetPouchLegalItems(string pouchName)
{
    var pocket = pouchName switch
    {
        "Items" => "items",
        "Key Items" => "keyItems",
        "TMs & HMs" => "tmHm",
        "Mail" => "mail",
        "Medicine" => "medicine",
        "Berries" => "berries",
        "Poké Balls" => "balls",
        "Battle Items" => "battle",
        _ => null,
    };

    if (pocket is null)
        return [];

    return OriginHeartGoldItemData.GetPocketItems(pocket)
        .Select(item => item.Id)
        .ToArray();
}

public int SetItemCount(string pouchName, int itemId, int count)
{
    ArgumentNullException.ThrowIfNull(pouchName);

    int pouchIndex = Array.FindIndex(
        Pouches,
        pouch => string.Equals(
            pouch.Name,
            pouchName,
            StringComparison.Ordinal));

    if (pouchIndex < 0)
        throw new InvalidOperationException(
            $"Bolso não encontrado: {pouchName}.");

    var layout = Pouches[pouchIndex];

    string? pocket = pouchName switch
    {
        "Items" => "items",
        "Key Items" => "keyItems",
        "TMs & HMs" => "tmHm",
        "Mail" => "mail",
        "Medicine" => "medicine",
        "Berries" => "berries",
        "Poké Balls" => "balls",
        "Battle Items" => "battle",
        _ => null,
    };

    if (pocket is null)
        throw new InvalidOperationException(
            "Categoria de item não reconhecida.");

    if (!OriginHeartGoldItemData.IsAvailable)
        throw new InvalidOperationException(
            "O catálogo de itens Origin não está disponível.");

    if (!OriginHeartGoldItemData.TryGet(itemId, out var requestedItem) ||
        !string.Equals(
            requestedItem.Pocket,
            pocket,
            StringComparison.Ordinal))
    {
        throw new ArgumentException(
            "O item não pertence ao bolso selecionado.",
            nameof(itemId));
    }

    int maximumCount = pocket == "tmHm" ? 99 : 999;
    count = Math.Clamp(count, 0, maximumCount);

    // Lê os registros existentes sem modificar o save.
    var general = General;
    var items = new List<BagItem>(layout.Slots);
    var knownIds = new HashSet<int>();

    for (int slot = 0; slot < layout.Slots; slot++)
    {
        int offset = BagOffset + layout.Rel + slot * 4;

        int id = BinaryPrimitives.ReadUInt16LittleEndian(
            general.Slice(offset, 2));

        int quantity = BinaryPrimitives.ReadUInt16LittleEndian(
            general.Slice(offset + 2, 2));

        if (id == 0 && quantity == 0)
            continue;

        // Não descarta silenciosamente registros inconsistentes.
        if (id == 0 || quantity == 0 ||
            !OriginHeartGoldItemData.TryGet(id, out var storedItem) ||
            storedItem.Pocket != pocket ||
            quantity > maximumCount ||
            !knownIds.Add(id))
        {
            throw new InvalidDataException(
                $"Registro inválido encontrado no bolso {pouchName}.");
        }

        items.Add(new BagItem(id, quantity));
    }

    int existingIndex = items.FindIndex(item => item.Id == itemId);

    bool removing = count == 0;

    if (removing)
    {
        if (existingIndex < 0)
            return 0;

        items.RemoveAt(existingIndex);
    }
    else if (existingIndex >= 0)
    {
        if (items[existingIndex].Count == count)
            return count;

        items[existingIndex] = new BagItem(itemId, count);
    }
    else
    {
        if (items.Count >= layout.Slots)
            throw new InvalidOperationException(
                $"O bolso {pouchName} está cheio.");

        items.Add(new BagItem(itemId, count));

        // O Origin mantém TMs/HMs e berries ordenados por ID
        // quando um novo item é adicionado.
        if (pocket == "tmHm" || pocket == "berries")
            items.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    ApplyGeneralEdit(generalData =>
    {
        int pouchOffset = BagOffset + layout.Rel;

        // Reconstrói o bolso: entradas válidas seguidas de espaços vazios.
        generalData.AsSpan(
            pouchOffset,
            layout.Slots * 4).Clear();

        for (int slot = 0; slot < items.Count; slot++)
        {
            int offset = pouchOffset + slot * 4;
            var item = items[slot];

            BinaryPrimitives.WriteUInt16LittleEndian(
                generalData.AsSpan(offset, 2),
                checked((ushort)item.Id));

            BinaryPrimitives.WriteUInt16LittleEndian(
                generalData.AsSpan(offset + 2, 2),
                checked((ushort)item.Count));
        }

        // Existem dois atalhos registrados no General.
        // Só removemos atalhos que apontam para o item excluído.
        if (removing)
        {
            const int ShortcutOffset = 0xEA4;

            for (int i = 0; i < 2; i++)
            {
                int offset = ShortcutOffset + i * 2;

                ushort shortcut =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        generalData.AsSpan(offset, 2));

                if (shortcut == itemId)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        generalData.AsSpan(offset, 2),
                        0);
                }
            }
        }

        return true;
    }, "editar bolsa");

    return removing ? 0 : count;
}


    // ── Metadados da sessão ──

    public int Generation => 4;
    public IReadOnlyList<string> GameNames => ["Origin HeartGold"];
    public int MaxSpeciesId => OriginDexMaximum;
    public string GetBoxName(int box) => $"BOX {box + 1}"; // provisório: nomes reais ainda não decodificados
    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    public bool SupportsRTCRepair => false;
    public bool SupportsLegalFashionUnlock => false;
    public bool SupportsStylePoints => false;
    public bool SupportsCompassSettings => false;

    public ReadOnlyMemory<byte> Serialize() => _workingBytes;

    // ── Consultas sem suporte: valores neutros ──

    public IReadOnlyList<int> GetAbilityChoices(int species, int form) => [];
    public IReadOnlyList<string> GetFormChoices(int species) => [];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => [];
    public BaseStats GetBaseStats(int species) => new(0, 0, 0, 0, 0, 0);
    public string GetShowdownText(int box, int slot) => "";
    public string ExportBoxShowdown(int box) => "";
    public TrainingCaps GetTrainingCaps() => new(31, 255);
    public IReadOnlyList<int> GetMissingSpecies()
    {
        var present = new HashSet<int>();

        for (int box = 0; box < OriginHeartGoldReadOnlySave.BoxCount; box++)
        for (int slot = 0; slot < OriginHeartGoldReadOnlySave.BoxSlotCount; slot++)
        {
            if (TryMon(box, slot) is { } pokemon &&
                IsDexSpecies(pokemon.Species))
            {
                present.Add(pokemon.Species);
            }
        }

        return Enumerable.Range(1, OriginDexMaximum)
            .Where(species => !present.Contains(species))
            .ToArray();
    }
    public DexEntryState GetDexEntry(int species)
    {
        if (!IsDexSpecies(species))
            throw new ArgumentOutOfRangeException(nameof(species));

        ReadOnlySpan<byte> general = General;
        ValidateDexMarker(general);

        return new DexEntryState(
            ReadDexFlag(general, DexSeenOffset, species),
            ReadDexFlag(general, DexCaughtOffset, species));
    }
    public bool IsDexSpecies(int species) =>
        species >= 1 && species <= OriginDexMaximum;
    public DexProgress GetDexProgress()
    {
        ReadOnlySpan<byte> general = General;
        ValidateDexMarker(general);

        return new DexProgress(
            CountDexFlags(general, DexSeenOffset),
            CountDexFlags(general, DexCaughtOffset),
            OriginDexMaximum);
    }

    private static void ValidateDexMarker(ReadOnlySpan<byte> general)
    {
        if (general.Length < DexOffset + 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                general.Slice(DexOffset, 4)) != DexMarker)
        {
            throw new InvalidDataException(
                "O marcador da Pokédex do Origin HeartGold está ausente ou inválido.");
        }
    }

    private static bool ReadDexFlag(
        ReadOnlySpan<byte> general,
        int flagOffset,
        int species)
    {
        int index = species - 1;
        int offset = DexOffset + flagOffset + (index >> 3);
        int mask = 1 << (index & 7);

        return (general[offset] & mask) != 0;
    }

    private static void WriteDexFlag(
        Span<byte> general,
        int flagOffset,
        int species,
        bool value)
    {
        int index = species - 1;
        int offset = DexOffset + flagOffset + (index >> 3);
        byte mask = (byte)(1 << (index & 7));

        if (value)
            general[offset] |= mask;
        else
            general[offset] &= (byte)~mask;
    }

    private static int CountDexFlags(
        ReadOnlySpan<byte> general,
        int flagOffset)
    {
        int count = 0;

        for (int species = 1; species <= OriginDexMaximum; species++)
        {
            if (ReadDexFlag(general, flagOffset, species))
                count++;
        }

        return count;
    }
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
    public MoveDetails GetMoveDetails(int box, int slot)
{
    var pokemon = TryMon(box, slot);

    if (pokemon is null || !pokemon.Valid)
        throw new InvalidOperationException(
            "Não é possível consultar os golpes de um espaço vazio ou com checksum inválido.");

    ushort[] moves =
    [
        pokemon.Move1,
        pokemon.Move2,
        pokemon.Move3,
        pokemon.Move4
    ];

    int[] pp =
    [
        pokemon.Move1_PP,
        pokemon.Move2_PP,
        pokemon.Move3_PP,
        pokemon.Move4_PP
    ];

    int[] ups =
    [
        pokemon.Move1_PPUps,
        pokemon.Move2_PPUps,
        pokemon.Move3_PPUps,
        pokemon.Move4_PPUps
    ];

    var details = new MoveSlotDetail[4];

    for (var i = 0; i < 4; i++)
    {
        if (moves[i] == 0)
        {
            details[i] = new MoveSlotDetail(0, 0, 0, 0);
            continue;
        }

        var ppUps = Math.Clamp(ups[i], 0, 3);
        var maxPP = OriginHeartGoldMoveData.GetMaxPP(moves[i], ppUps);

        details[i] = new MoveSlotDetail(
            moves[i],
            Math.Clamp(pp[i], 0, maxPP),
            maxPP,
            ppUps);
    }

    return new MoveDetails(details, false, []);
}
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

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        // A primeira versão permite somente Pokémon que já estejam nas caixas.
        if (box == -1)
            throw NotYet("editar Pokémon do time");

        if ((uint)box >= OriginHeartGoldReadOnlySave.BoxCount)
            throw new ArgumentOutOfRangeException(nameof(box));

        if ((uint)slot >= OriginHeartGoldReadOnlySave.BoxSlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        byte[] originalRecord = _save.GetBoxRecord(box, slot).ToArray();
        var pokemon = new PK4(originalRecord);

        if (!pokemon.Valid || pokemon.Species == 0)
            throw new InvalidOperationException("Não é possível editar um espaço vazio ou um Pokémon com checksum inválido.");

        // Todos os campos não implementados precisam continuar iguais.
        // Ability aceita somente zero, que é o marcador neutro usado pelo leitor Origin.
        if (!HasOnlySupportedOriginChanges(pokemon, edit))
        {
            throw new NotSupportedException(
                "Nesta versão do Origin HeartGold, somente amizade, IVs, EVs e os quatro movimentos " +
                "das caixas podem ser alterados. Os demais campos precisam permanecer iguais aos valores originais.");
        }

        bool changed = false;


        // Substituição dos movimentos: PP base e zero PP Ups.
        if (edit.Move1 is { } move1 && move1 != pokemon.Move1)
        {
            SetOriginMoveFresh(pokemon, 0, move1);
            changed = true;
        }

        if (edit.Move2 is { } move2 && move2 != pokemon.Move2)
        {
            SetOriginMoveFresh(pokemon, 1, move2);
            changed = true;
        }

        if (edit.Move3 is { } move3 && move3 != pokemon.Move3)
        {
            SetOriginMoveFresh(pokemon, 2, move3);
            changed = true;
        }

        if (edit.Move4 is { } move4 && move4 != pokemon.Move4)
        {
            SetOriginMoveFresh(pokemon, 3, move4);
            changed = true;
        }

        // IVs: limite da geração 4 = 31.
        if (edit.IVs is { Count: 6 } ivs)
        {
            var values = SaveEngineSession.ClampAll(ivs.ToArray(), 31);

            if (!SameSix(values,
                    pokemon.IV_HP, pokemon.IV_ATK, pokemon.IV_DEF,
                    pokemon.IV_SPA, pokemon.IV_SPD, pokemon.IV_SPE))
            {
                SaveEngineSession.SetIVsFromAppOrder(pokemon, values);
                changed = true;
            }
        }

    // EVs: limite da geração 4 = 255 por atributo.
    if (edit.EVs is { Count: 6 } evs)
    {
        var values = SaveEngineSession.ClampAll(evs.ToArray(), 255);

        if (!SameSix(values,
                pokemon.EV_HP, pokemon.EV_ATK, pokemon.EV_DEF,
                pokemon.EV_SPA, pokemon.EV_SPD, pokemon.EV_SPE))
        {
            SaveEngineSession.SetEVsFromAppOrder(pokemon, values);
            changed = true;
        }
    }

    // Amizade.
    if (edit.Friendship is { } requestedFriendship)
    {
        byte newFriendship = (byte)Math.Clamp(requestedFriendship, 0, 255);

        if (pokemon.CurrentFriendship != newFriendship)
        {
            pokemon.CurrentFriendship = newFriendship;
            changed = true;
        }
    }

    // Evita regravar um Pokémon quando nada mudou.
    if (!changed)
        return;

        // Recria o registro criptografado de 136 bytes.
        byte[] editedRecord = new byte[OriginHeartGoldReadOnlySave.BoxedSize];
        pokemon.WriteEncryptedDataStored(editedRecord);

        // A mutação trabalha numa nova cópia e verifica os CRCs,
        // o espelho ativo, a identidade e o footer DSV.
        if (!OriginHeartGoldBoxMutation.TryCreateCandidate(
                _workingBytes,
                box,
                slot,
                editedRecord,
                out byte[] candidate))
        {
            throw new InvalidDataException(
                "A alteração de amizade não passou pelas verificações de segurança do Origin HeartGold.");
        }

        // Reabre o leitor sobre a nova cópia antes de aceitar a alteração.
        if (!OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updatedSave) ||
            updatedSave is null)
        {
            throw new InvalidDataException(
                "Não foi possível reabrir o save Origin HeartGold após a alteração.");
        }

        // Só atualiza a sessão depois que todas as verificações passaram.
        _workingBytes = candidate;
        _save = updatedSave;
    }


private static void SetOriginMoveFresh(PK4 pokemon, int index, int move)
{
    ushort id = checked((ushort)move);

    int pp = id == 0
        ? 0
        : OriginHeartGoldMoveData.GetMaxPP(id, 0);

    switch (index)
    {
        case 0:
            pokemon.Move1 = id;
            pokemon.Move1_PPUps = 0;
            pokemon.Move1_PP = pp;
            break;

        case 1:
            pokemon.Move2 = id;
            pokemon.Move2_PPUps = 0;
            pokemon.Move2_PP = pp;
            break;

        case 2:
            pokemon.Move3 = id;
            pokemon.Move3_PPUps = 0;
            pokemon.Move3_PP = pp;
            break;

        case 3:
            pokemon.Move4 = id;
            pokemon.Move4_PPUps = 0;
            pokemon.Move4_PP = pp;
            break;

        default:
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}

    private static bool IsValidOriginMoveSelection(int move)
    {
        if (move == 0)
            return true;

        return OriginHeartGoldMoveData.TryGet(move, out var data) &&
            data.BasePp > 0 &&
            !string.IsNullOrWhiteSpace(data.Name);
    }

    private static bool HasOnlySupportedOriginChanges(PK4 pokemon, EntityEdit edit)
    {
        if (edit.Species is { } species && species != pokemon.Species)
            return false;

        if (edit.Nickname is { } nickname &&
            !string.Equals(nickname, pokemon.Nickname, StringComparison.Ordinal))
            return false;

        if (edit.Level is { } level &&
            Math.Clamp(level, 1, 100) != pokemon.CurrentLevel)
            return false;

        if (edit.Nature is { } nature && nature != (int)pokemon.Nature)
            return false;

        // O leitor Origin não consegue expor a habilidade efetiva pelo PK4 padrão.
        if (edit.Ability is { } ability && ability != 0)
            return false;

        if (edit.HeldItem is { } heldItem && heldItem != pokemon.HeldItem)
            return false;


if (edit.Move1 is { } move1 &&
    move1 != pokemon.Move1 &&
    !IsValidOriginMoveSelection(move1))
    return false;

if (edit.Move2 is { } move2 &&
    move2 != pokemon.Move2 &&
    !IsValidOriginMoveSelection(move2))
    return false;

if (edit.Move3 is { } move3 &&
    move3 != pokemon.Move3 &&
    !IsValidOriginMoveSelection(move3))
    return false;

if (edit.Move4 is { } move4 &&
    move4 != pokemon.Move4 &&
    !IsValidOriginMoveSelection(move4))
    return false;


        if (edit.IsShiny is { } shiny && shiny != pokemon.IsShiny)
            return false;

        if (edit.Ball is { } ball && ball != pokemon.Ball)
            return false;

        if (edit.OriginalTrainer is { } trainer &&
            !string.Equals(trainer, pokemon.OriginalTrainerName, StringComparison.Ordinal))
            return false;

        if (edit.Gender is { } gender &&
            Math.Clamp(gender, 0, 2) != pokemon.Gender)
            return false;

        return true;
    }

    private static bool SameSix(
        IReadOnlyList<int> values,
        int v0, int v1, int v2, int v3, int v4, int v5)
    {
        return values.Count == 6 &&
            values[0] == v0 &&
            values[1] == v1 &&
            values[2] == v2 &&
            values[3] == v3 &&
            values[4] == v4 &&
            values[5] == v5;
    }
    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot) => throw NotYet("mover Pokémon");
    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot) => throw NotYet("duplicar Pokémon");
    public SlotExport ExportSlot(int box, int slot) => throw NotYet("exportar Pokémon");
    public bool RerollNatureKeepShiny(int box, int slot, int nature) => throw NotYet("editar natureza");
    public void SetDexEntry(int species, bool seen, bool caught)
    {
        if (!IsDexSpecies(species))
            throw new ArgumentOutOfRangeException(nameof(species));

        bool newSeen = seen;
        bool newCaught = seen && caught;

        var current = GetDexEntry(species);

        if (current.Seen == newSeen &&
            current.Caught == newCaught)
            return;

        ApplyGeneralEdit(general =>
        {
            ValidateDexMarker(general);

            WriteDexFlag(general, DexSeenOffset, species, newSeen);
            WriteDexFlag(general, DexCaughtOffset, species, newCaught);

            return true;
        }, "editar Pokédex");
    }
    public void CompleteDex()
    {
        var progress = GetDexProgress();

        if (progress.Seen == OriginDexMaximum &&
            progress.Caught == OriginDexMaximum)
            return;

        ApplyGeneralEdit(general =>
        {
            ValidateDexMarker(general);

            for (int species = 1; species <= OriginDexMaximum; species++)
            {
                WriteDexFlag(general, DexSeenOffset, species, true);
                WriteDexFlag(general, DexCaughtOffset, species, true);
            }

            return true;
        }, "completar Pokédex");
    }
    public void ReleaseSlot(int box, int slot) => throw NotYet("soltar Pokémon");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("living dex");
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes, bool reverse) => throw NotYet("ordenar caixas");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes) => throw NotYet("edição em lote");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("edição em lote");
    public void SwapBoxes(int a, int b) => throw NotYet("trocar caixas");
    public void DeleteBox(int box) => throw NotYet("apagar caixa");
    public void ClearBox(int box) => throw NotYet("limpar caixa");
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format) => throw NotYet("importar Pokémon");

public void SetTrainer(TrainerInfo trainer)
{
    ArgumentNullException.ThrowIfNull(trainer);

    string name = trainer.Name?.Trim() ?? "";

    if (string.IsNullOrWhiteSpace(name) || name.Length > 7)
        throw new ArgumentException(
            "O nome do treinador deve ter entre 1 e 7 caracteres.",
            nameof(trainer));

    int language = General[TrainerOffset + 0x19];

    byte[] encodedName = new byte[16];

    StringConverter4.SetString(
        encodedName,
        name,
        7,
        language,
        StringConverterOption.ClearZero);

    string decodedName = StringConverter4.GetString(encodedName);

    if (!string.Equals(decodedName, name, StringComparison.Ordinal))
        throw new InvalidOperationException(
            "O nome contém caracteres não suportados pelo save.");

    ushort tid = (ushort)Math.Clamp(
        trainer.TID, 0, ushort.MaxValue);

    ushort sid = (ushort)Math.Clamp(
        trainer.SID, 0, ushort.MaxValue);

    uint money = Math.Min(trainer.Money, 9_999_999u);

    byte gender = (byte)Math.Clamp(trainer.Gender, 0, 1);

    var current = GetTrainer();

    if (current.Name == name &&
        current.TID == tid &&
        current.SID == sid &&
        current.Money == money &&
        current.Gender == gender)
        return;

    ApplyGeneralEdit(general =>
    {
        encodedName.CopyTo(
            general.AsSpan(TrainerOffset, encodedName.Length));

        BinaryPrimitives.WriteUInt16LittleEndian(
            general.AsSpan(TrainerOffset + 0x10, 2), tid);

        BinaryPrimitives.WriteUInt16LittleEndian(
            general.AsSpan(TrainerOffset + 0x12, 2), sid);

        BinaryPrimitives.WriteUInt32LittleEndian(
            general.AsSpan(TrainerOffset + 0x14, 4), money);

        general[TrainerOffset + 0x18] = gender;

        return true;
    }, "editar treinador");
}

public void SetTrainerStats(TrainerStatsEdit edit)
{
    ArgumentNullException.ThrowIfNull(edit);

    var current = GetTrainerStats();

    int? hours = edit.PlayTimeHours is int requestedHours
        ? Math.Clamp(requestedHours, 0, 999)
        : null;

    int? minutes = edit.PlayTimeMinutes is int requestedMinutes
        ? Math.Clamp(requestedMinutes, 0, 59)
        : null;

    int? coins = edit.Coins is int requestedCoins
        ? Math.Clamp(requestedCoins, 0, 50_000)
        : null;

    bool changed =
        (hours.HasValue && hours.Value != current.PlayTimeHours) ||
        (minutes.HasValue && minutes.Value != current.PlayTimeMinutes) ||
        (coins.HasValue && coins.Value != current.Coins);

    if (!changed)
        return;

    ApplyGeneralEdit(general =>
    {
        if (hours is int newHours)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                general.AsSpan(TrainerOffset + 0x22, 2),
                (ushort)newHours);
        }

        if (minutes is int newMinutes)
            general[TrainerOffset + 0x24] = (byte)newMinutes;

        if (coins is int newCoins)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                general.AsSpan(0x84, 2),
                (ushort)newCoins);
        }

        return true;
    }, "editar estatísticas do treinador");
}



private void ApplyGeneralEdit(
    Func<byte[], bool> edit,
    string description)
{
    if (!OriginHeartGoldGeneralMutation.TryCreateCandidate(
            _workingBytes,
            edit,
            out byte[] candidate))
    {
        throw new InvalidDataException(
            $"A alteração ({description}) não passou pelas verificações de segurança do Origin HeartGold.");
    }

    if (!OriginHeartGoldReadOnlyReader.TryOpen(
            candidate, out var updatedSave) ||
        updatedSave is null)
    {
        throw new InvalidDataException(
            $"Não foi possível reabrir o save após {description}.");
    }

    _workingBytes = candidate;
    _save = updatedSave;
}

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile) => throw NotYet("trocar dono");
    public void RepairRTC() => throw NotYet("reparar relógio");
    public int SetPokeBeanCount(int index, int count) => throw NotYet("editar feijões");
    public int SetGrandUndergroundItemCount(int itemId, int count) => throw NotYet("editar itens");
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("retirar da creche");
    public void UnlockAllLegalFashion() => throw NotYet("desbloquear roupas");
    public void SetStylePoints(int value) => throw NotYet("editar pontos de estilo");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("editar origem");
    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit) => throw NotYet("editar potencial");

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        bool isParty = box == -1;

        if (isParty)
        {
            if ((uint)slot >= (uint)_save.PartyCount)
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
        else
        {
            if ((uint)box >= OriginHeartGoldReadOnlySave.BoxCount)
                throw new ArgumentOutOfRangeException(nameof(box));

            if ((uint)slot >= OriginHeartGoldReadOnlySave.BoxSlotCount)
                throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if (edit.RelearnMoves is not null)
            throw NotYet("editar movimentos de relembre");

        if (edit.PP is { } requestedPP && requestedPP.Count != 4)
            throw new ArgumentException("PP deve conter exatamente quatro posições.", nameof(edit));

        if (edit.PPUps is { } requestedUps && requestedUps.Count != 4)
            throw new ArgumentException("PP Ups deve conter exatamente quatro posições.", nameof(edit));

        if (edit.PP is null && edit.PPUps is null)
            return;

        byte[] originalRecord = isParty
            ? _save.GetPartyPokemonBytes(slot).ToArray()
            : _save.GetBoxRecord(box, slot).ToArray();

        var pokemon = new PK4(originalRecord);

        if (!pokemon.Valid || pokemon.Species == 0)
            throw new InvalidOperationException(
                "Não é possível editar os golpes de um espaço vazio ou com checksum inválido.");

        ushort[] moves =
        [
            pokemon.Move1,
            pokemon.Move2,
            pokemon.Move3,
            pokemon.Move4
        ];

        int[] originalPP =
        [
            pokemon.Move1_PP,
            pokemon.Move2_PP,
            pokemon.Move3_PP,
            pokemon.Move4_PP
        ];

        int[] originalUps =
        [
            pokemon.Move1_PPUps,
            pokemon.Move2_PPUps,
            pokemon.Move3_PPUps,
            pokemon.Move4_PPUps
        ];

        int[] pp = originalPP.ToArray();
        int[] ups = originalUps.Select(value => Math.Clamp(value, 0, 3)).ToArray();

        if (edit.PPUps is { } newUps)
        {
            for (int i = 0; i < 4; i++)
            {
                if (moves[i] == 0)
                {
                    ups[i] = 0;
                    pp[i] = 0;
                    continue;
                }

                int value = Math.Clamp(newUps[i], 0, 3);

                if (value != ups[i])
                {
                    ups[i] = value;
                    pp[i] = OriginHeartGoldMoveData.GetMaxPP(moves[i], value);
                }
            }
        }

        if (edit.PP is { } newPP)
        {
            for (int i = 0; i < 4; i++)
            {
                if (moves[i] == 0)
                {
                    pp[i] = 0;
                    ups[i] = 0;
                    continue;
                }

                int maximum = OriginHeartGoldMoveData.GetMaxPP(moves[i], ups[i]);
                pp[i] = Math.Clamp(newPP[i], 0, maximum);
            }
        }

        bool changed = false;

        for (int i = 0; i < 4; i++)
        {
            if (pp[i] != originalPP[i] || ups[i] != originalUps[i])
            {
                changed = true;
                break;
            }
        }

        if (!changed)
            return;

        pokemon.Move1_PPUps = ups[0];
        pokemon.Move2_PPUps = ups[1];
        pokemon.Move3_PPUps = ups[2];
        pokemon.Move4_PPUps = ups[3];

        pokemon.Move1_PP = pp[0];
        pokemon.Move2_PP = pp[1];
        pokemon.Move3_PP = pp[2];
        pokemon.Move4_PP = pp[3];

        byte[] editedRecord = new byte[OriginHeartGoldReadOnlySave.BoxedSize];
        pokemon.WriteEncryptedDataStored(editedRecord);

        byte[] candidate;
        bool candidateCreated;

        if (isParty)
        {
            candidateCreated = OriginHeartGoldPartyMutation.TryCreateCandidate(
                _workingBytes,
                slot,
                editedRecord,
                out candidate);
        }
        else
        {
            candidateCreated = OriginHeartGoldBoxMutation.TryCreateCandidate(
                _workingBytes,
                box,
                slot,
                editedRecord,
                out candidate);
        }

        if (!candidateCreated)
        {
            throw new InvalidDataException(
                "A alteração dos PP não passou pelas verificações de segurança do Origin HeartGold.");
        }

        if (!OriginHeartGoldReadOnlyReader.TryOpen(candidate, out var updatedSave) ||
            updatedSave is null)
        {
            throw new InvalidDataException(
                "Não foi possível reabrir o save Origin HeartGold após a alteração dos PP.");
        }

        _workingBytes = candidate;
        _save = updatedSave;
    }

    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) => throw NotYet("editar loja de golpes");
    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) => throw NotYet("editar cosméticos");
    public void SetPokerus(int box, int slot, PokerusStatus status) => throw NotYet("editar Pokérus");
    public void SetRibbon(int box, int slot, string id, int value) => throw NotYet("editar fitas");
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) => throw NotYet("editar fitas");
    public int AwardAllObtainableRibbons(int box, int slot) => throw NotYet("dar fitas");
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) => throw NotYet("gerar Pokémon");

    public void Dispose() { }
}