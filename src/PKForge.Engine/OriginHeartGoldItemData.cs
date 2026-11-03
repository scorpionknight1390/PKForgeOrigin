using System.Reflection;
using System.Text.Json;

namespace PKForge.Engine;

/// <summary>
/// Item extraído da referência local do Origin HeartGold 4.0.3.
/// </summary>
internal sealed record OriginHeartGoldItem(
    int Id,
    string Name,
    string Pocket);

/// <summary>
/// Carrega e valida o catálogo local de itens do Origin HeartGold.
/// O catálogo não faz parte da distribuição do PKForge.
/// </summary>
internal static class OriginHeartGoldItemData
{
    private const string ResourceName = "originheartgold.items.json";
    private const string ExpectedGameVersion = "origin-heartgold-english-v4.0.3";
    private const int ExpectedItemCount = 790;

    private static readonly HashSet<string> ValidPockets = new(StringComparer.Ordinal)
    {
        "items", "keyItems", "tmHm", "mail",
        "medicine", "berries", "balls", "battle",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<Catalog?> Data = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Data.Value is not null;

    public static IReadOnlyList<OriginHeartGoldItem> AvailableItems =>
        Data.Value?.Items ?? [];

    public static bool TryGet(int id, out OriginHeartGoldItem item)
    {
        var catalog = Data.Value;

        if (catalog is not null && catalog.ItemsById.TryGetValue(id, out var found))
        {
            item = found;
            return true;
        }

        item = null!;
        return false;
    }

    public static IReadOnlyList<OriginHeartGoldItem> GetPocketItems(string pocket)
    {
        var catalog = Data.Value;

        if (catalog is null || !ValidPockets.Contains(pocket))
            return [];

        return catalog.ItemsByPocket.TryGetValue(pocket, out var items)
            ? items
            : [];
    }

    private static Catalog? Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName);

        if (stream is null)
            return null;

        CatalogDocument document;

        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(
                stream, JsonOptions)
                ?? throw new InvalidDataException("Catálogo Origin vazio.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "Não foi possível interpretar o catálogo de itens Origin.", error);
        }

        if (document.SchemaVersion != 1 ||
            document.GameVersion != ExpectedGameVersion)
        {
            throw new InvalidDataException(
                "A versão do catálogo não corresponde ao Origin HeartGold 4.0.3.");
        }

        if (!ValidProvenance(document.Provenance))
            throw new InvalidDataException("A procedência do catálogo de itens é inválida.");

        if (document.Items is not { Count: ExpectedItemCount } ||
            document.Readiness is null ||
            document.Readiness.Records != ExpectedItemCount ||
            document.Readiness.Items != ExpectedItemCount)
        {
            throw new InvalidDataException(
                "O catálogo Origin precisa conter exatamente 790 itens identificados.");
        }

        var items = new OriginHeartGoldItem[ExpectedItemCount];
        var byId = new Dictionary<int, OriginHeartGoldItem>(ExpectedItemCount);
        var byPocket = new Dictionary<string, List<OriginHeartGoldItem>>(
            StringComparer.Ordinal);

        for (var i = 0; i < ExpectedItemCount; i++)
        {
            var entry = document.Items[i];

            if (entry is null ||
    entry.Id != i + 1 ||
    string.IsNullOrWhiteSpace(entry.Name) ||
    string.IsNullOrWhiteSpace(entry.Pocket) ||
    !ValidPockets.Contains(entry.Pocket))
            {
                throw new InvalidDataException(
                    "Registro de item Origin inválido: " + (i + 1) + ".");
            }

            var item = new OriginHeartGoldItem(
                entry.Id,
                entry.Name,
                entry.Pocket);

            items[i] = item;
            byId.Add(item.Id, item);

            if (!byPocket.TryGetValue(item.Pocket, out var pocketItems))
            {
                pocketItems = [];
                byPocket.Add(item.Pocket, pocketItems);
            }

            pocketItems.Add(item);
        }

        return new Catalog(
            items,
            byId,
            byPocket.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<OriginHeartGoldItem>)pair.Value.ToArray(),
                StringComparer.Ordinal));
    }

    private static bool ValidProvenance(ProvenanceDocument? provenance)
    {
        if (provenance is null ||
            !IsSha256(provenance.RomSha256) ||
            !IsSha256(provenance.PayloadSha256) ||
            provenance.Sources is not { Count: 8 })
            return false;

        return provenance.Sources.Values.All(IsSha256);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c =>
            (c >= '0' && c <= '9') ||
            (c >= 'a' && c <= 'f'));

    private sealed record Catalog(
        OriginHeartGoldItem[] Items,
        IReadOnlyDictionary<int, OriginHeartGoldItem> ItemsById,
        IReadOnlyDictionary<string, IReadOnlyList<OriginHeartGoldItem>> ItemsByPocket);

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; set; }
        public string? GameVersion { get; set; }
        public ProvenanceDocument? Provenance { get; set; }
        public ReadinessDocument? Readiness { get; set; }
        public List<ItemEntry?>? Items { get; set; }
    }

    private sealed class ProvenanceDocument
    {
        public string? RomSha256 { get; set; }
        public string? PayloadSha256 { get; set; }
        public Dictionary<string, string>? Sources { get; set; }
    }

    private sealed class ReadinessDocument
    {
        public int Records { get; set; }
        public int Items { get; set; }
    }

    private sealed class ItemEntry
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Pocket { get; set; }
    }
}