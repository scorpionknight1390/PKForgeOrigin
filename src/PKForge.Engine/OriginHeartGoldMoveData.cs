
using System.Reflection;
using System.Text.Json;

namespace PKForge.Engine;

/// <summary>
/// Dados de um movimento extraídos da referência local do Origin HeartGold 4.0.3.
/// </summary>
internal sealed record OriginHeartGoldMove(
    int Id,
    string? Name,
    int BasePp,
    int Type,
    int Power,
    int Accuracy);

/// <summary>
/// Carrega e valida o catálogo local do Origin HeartGold.
/// O catálogo é opcional e não faz parte da distribuição do PKForge.
/// </summary>
internal static class OriginHeartGoldMoveData
{
    private const string ResourceName = "originheartgold.moves.json";
    private const string ExpectedGameVersion = "origin-heartgold-english-v4.0.3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<Catalog?> Data = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Data.Value is not null;

    public static IReadOnlyList<OriginHeartGoldMove> AvailableMoves =>
        Data.Value?.AvailableMoves ?? [];

    public static bool TryGet(int id, out OriginHeartGoldMove move)
    {
        var catalog = Data.Value;

        if (catalog is not null && (uint)id < (uint)catalog.Moves.Length)
        {
            move = catalog.Moves[id];
            return true;
        }

        move = null!;
        return false;
    }
    public static int GetMaxPP(int moveId, int ppUps)
    {
        if (ppUps is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(ppUps));

        if (!TryGet(moveId, out var move) ||
            moveId == 0 ||
            string.IsNullOrWhiteSpace(move.Name) ||
            move.BasePp <= 0)
        {
            throw new NotSupportedException(
                $"O movimento Origin {moveId} não possui PP base válido no catálogo.");
        }

        return move.BasePp + (move.BasePp * ppUps / 5);
    }
    /// <summary>
    /// Lê o recurso incorporado. Sem o catálogo local, retorna null.
    /// </summary>
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
                "Não foi possível interpretar o catálogo Origin HeartGold.", error);
        }

        if (document.SchemaVersion != 1 ||
            document.GameVersion != ExpectedGameVersion)
        {
            throw new InvalidDataException(
                "A versão do catálogo não corresponde ao Origin HeartGold 4.0.3.");
        }

        if (!ValidProvenance(document.Provenance))
            throw new InvalidDataException("A procedência do catálogo Origin é inválida.");

        if (document.Moves is not { Count: 921 } ||
            document.Readiness is null ||
            document.Readiness.Records != 921)
        {
            throw new InvalidDataException(
                "O catálogo Origin precisa conter exatamente 921 registros.");
        }

        var moves = new OriginHeartGoldMove[921];

        for (var i = 0; i < moves.Length; i++)
        {
            var entry = document.Moves[i];

            if (entry.Id != i ||
                !InByteRange(entry.BasePp) ||
                !InByteRange(entry.Type) ||
                !InByteRange(entry.Power) ||
                !InByteRange(entry.Accuracy) ||
                (entry.Name is not null && string.IsNullOrWhiteSpace(entry.Name)))
            {
                throw new InvalidDataException(
                    $"Registro de movimento Origin inválido: {i}.");
            }

            moves[i] = new OriginHeartGoldMove(
                entry.Id,
                entry.Name,
                entry.BasePp,
                entry.Type,
                entry.Power,
                entry.Accuracy);
        }

        var available = moves
            .Where(m => m.Id > 0 && !string.IsNullOrWhiteSpace(m.Name) && m.BasePp > 0)
            .ToArray();

        if (available.Length != 902 ||
            document.Readiness.Moves != available.Length)
        {
            throw new InvalidDataException(
                "A quantidade de movimentos disponíveis no catálogo está incorreta.");
        }

        return new Catalog(moves, available);
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

    private static bool InByteRange(int value) =>
        value is >= 0 and <= 255;

    private sealed record Catalog(
        OriginHeartGoldMove[] Moves,
        IReadOnlyList<OriginHeartGoldMove> AvailableMoves);

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; set; }
        public string? GameVersion { get; set; }
        public ProvenanceDocument? Provenance { get; set; }
        public ReadinessDocument? Readiness { get; set; }
        public List<MoveEntry>? Moves { get; set; }
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
        public int Moves { get; set; }
    }

    private sealed class MoveEntry
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int BasePp { get; set; }
        public int Type { get; set; }
        public int Power { get; set; }
        public int Accuracy { get; set; }
    }
}
