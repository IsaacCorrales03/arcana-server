using System.Text.Json;
using Arcana.Content;

namespace Arcana.Meta;

public sealed record DeckImport(IReadOnlyList<DeckEntry> Cards, IReadOnlyList<string> Unknown, IReadOnlyList<string> Warnings)
{
    public int Count => Cards.Where(e => !Economy.IsMajor(e.Id)).Sum(e => e.Copies);
}

public static class DeckWorkshop
{
    public const int DeckSize = ContentLoader.DeckSize;
    public const int MaxCopies = ContentLoader.MaxCopies;
    public const int ArcanoSlots = 1;
    public const string Example = "F01:8;F01:2; M02:2; F00:1; Z99:1;";
    static readonly HashSet<string> CardIds = Economy.Cards.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
    static readonly string[] NumericIds = Economy.Cards.Select(c => c.Id).ToArray();

    public static DeckImport Parse(string input)
    {
        var cards = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        if (input.Length > 12000) return new([], [], ["El código es demasiado largo."]);
        input = input.Trim();
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return new([], [], ["El enlace no es válido."]);
            var code = uri.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith("d=", StringComparison.Ordinal));
            if (code is null) return new([], [], ["El enlace no contiene un código de mazo."]);
            input = Uri.UnescapeDataString(code[2..]);
        }
        int malformed = 0;
        var clipped = new HashSet<string>();
        foreach (var raw in input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length is < 1 or > 60 || parts[1].Length == 0 ||
                !parts[1].All(char.IsAsciiDigit) || parts[1].All(c => c == '0'))
            {
                malformed++;
                continue;
            }
            var id = parts[0].ToUpperInvariant();
            if (int.TryParse(id, out var index) && index >= 1 && index <= NumericIds.Length) id = NumericIds[index - 1];
            if (!CardIds.Contains(id)) { unknown.Add(id); continue; }
            var max = Economy.IsMajor(id) ? ArcanoSlots : MaxCopies;
            var countText = parts[1].TrimStart('0');
            int count = countText.Length == 1 ? countText[0] - '0' : max + 1;
            int combined = cards.GetValueOrDefault(id) + count;
            if (combined > max) clipped.Add(id);
            cards[id] = Math.Min(combined, max);
        }
        if (malformed > 0) warnings.Add($"{malformed} entradas con formato incorrecto. Usa ID:cantidad;");
        if (clipped.Count > 0) warnings.Add("Copias recortadas al máximo permitido: " + string.Join(", ", clipped));
        if (cards.Keys.Count(Economy.IsMajor) > ArcanoSlots) warnings.Add("Hay varios Arcanos. Se conserva el primero.");
        if (cards.Where(x => !Economy.IsMajor(x.Key)).Sum(x => x.Value) != DeckSize)
            warnings.Add($"Un mazo competitivo necesita {DeckSize} cartas principales.");
        if (cards.Count == 0) warnings.Add("Pega un código con al menos una carta reconocida.");
        return new(cards.Select(x => new DeckEntry { Id = x.Key, Copies = x.Value }).ToList(), unknown.Order().ToArray(), warnings);
    }

    public static string Export(DeckDefinition deck) => string.Concat(deck.WithArcano.OrderBy(e => e.Id, StringComparer.Ordinal)
        .Select(e => $"{e.Id}:{(Economy.IsMajor(e.Id) ? 1 : e.Copies)};"));
    public static DeckDefinition Copy(DeckDefinition source) =>
        JsonSerializer.Deserialize<DeckDefinition>(JsonSerializer.Serialize(source, Json.Options), Json.Options)!;

    public static object CasualWireDeck(DeckDefinition source) => new
    {
        Id = source.Family, source.Name, Entries = source.WithArcano, source.Copy, source.CardBack
    };

    public static DeckDefinition Convert(DeckDefinition source)
    {
        var deck = Copy(source);
        if (deck.SeparateArcano) return deck;
        deck.Arcano = deck.Entries.FirstOrDefault(e => Economy.IsMajor(e.Id))?.Id;
        deck.Entries.RemoveAll(e => Economy.IsMajor(e.Id));
        deck.SeparateArcano = true;
        return deck;
    }

    public static DeckDefinition Create(string name, IEnumerable<DeckEntry> cards)
    {
        var deck = new DeckDefinition { Id = "deck-" + Guid.NewGuid().ToString("N"), Name = name, Entries = cards.ToList(), UpdatedAt = DateTimeOffset.UtcNow };
        return Convert(deck);
    }

    public static string Cover(Profile p, DeckDefinition deck)
    {
        var id = deck.CoverCardId is { } cover && p.Has(cover) > 0 ? cover :
            deck.Arcano is { } arcano && p.Has(arcano) > 0 ? arcano :
            deck.Entries.FirstOrDefault(e => p.Has(e.Id) > 0)?.Id ??
            p.Cards.FirstOrDefault(pair => pair.Value > 0).Key ?? "F00";
        return p.UsaAlt.Contains(id) ? id + "@alt" : id;
    }

    public static List<DeckEntry> Missing(Profile p, DeckDefinition deck) =>
        deck.WithArcano.Where(e => p.Has(e.Id) < e.Copies).Select(e => new DeckEntry
        { Id = e.Id, Copies = e.Copies - p.Has(e.Id) }).ToList();

    public static long MissingCost(IEnumerable<DeckEntry> entries) => entries.Where(e => !Economy.IsMajor(e.Id))
        .Sum(e => (long)e.Copies * Economy.CraftCostOf(e.Id));

    public static Profile CraftMissing(Profile p, DeckDefinition deck)
    {
        var needed = Missing(p, deck);
        foreach (var entry in needed)
            if (Economy.CraftRestriction(p, entry.Id) is { } reason) throw new InvalidOperationException(reason);
        if (MissingCost(needed) > p.Esencia) throw new InvalidOperationException("Te falta Esencia para crear las copias.");
        return needed.Aggregate(p, (next, e) => Enumerable.Range(0, e.Copies).Aggregate(next, (q, _) => Economy.Craft(q, e.Id)));
    }

    public static DeckDefinition OwnedOnly(Profile p, DeckDefinition deck)
    {
        var copy = Copy(deck);
        foreach (var e in copy.Entries) e.Copies = Math.Min(e.Copies, p.Has(e.Id));
        copy.Entries.RemoveAll(e => e.Copies == 0);
        if (copy.Arcano is { } major && p.Has(major) == 0) copy.Arcano = null;
        return copy;
    }

    public static DeckDefinition Preset(DeckDefinition source)
    {
        var model = Convert(source);
        var family = source.Id;
        return Suggest(model, Economy.Cards.Where(c => c.Token != true && c.Arcana is null && !ContentLoader.IsCampaignCard(c.Id)).ToDictionary(c => c.Id, _ => MaxCopies), family);
    }

    public static DeckDefinition Suggest(DeckDefinition current, IReadOnlyDictionary<string, int> supply, string? preference = null)
    {
        var deck = Copy(current);
        var family = preference ?? (deck.Arcano is { } a ? Economy.FamilyOf(a) : deck.Entries.GroupBy(e => Economy.FamilyOf(e.Id)).OrderByDescending(g => g.Sum(e => e.Copies)).FirstOrDefault()?.Key);
        var total = deck.Entries.Sum(e => e.Copies);
        var options = Economy.Cards.Where(c => c.Arcana is null && supply.GetValueOrDefault(c.Id) > 0)
            .OrderByDescending(c => Economy.FamilyOf(c.Id) == family).ThenBy(c => c.Cost).ThenBy(c => c.Id);
        foreach (var c in options)
        {
            if (total >= DeckSize) break;
            var existing = deck.Entries.FirstOrDefault(e => e.Id == c.Id);
            int need = Math.Min(DeckSize - total, Math.Min(MaxCopies, supply.GetValueOrDefault(c.Id)) - (existing?.Copies ?? 0));
            if (need <= 0) continue;
            if (existing is null) deck.Entries.Add(new DeckEntry { Id = c.Id, Copies = need });
            else existing.Copies += need;
            total += need;
        }
        if (deck.Arcano is null) deck.Arcano = Economy.Majors.FirstOrDefault(id => Economy.FamilyOf(id) == family && supply.GetValueOrDefault(id) > 0) ??
            Economy.Majors.FirstOrDefault(id => supply.GetValueOrDefault(id) > 0);
        return deck;
    }

    public static string Status(Profile p, DeckDefinition deck)
    {
        var missing = Missing(p, deck);
        if (missing.Count > 0) return "Con cartas pendientes";
        var total = Convert(deck).Entries.Sum(e => e.Copies);
        if (total < DeckSize) return $"Incompleto · faltan {DeckSize - total}";
        return ValidateRanked(deck, p.Cards).Count == 0 ? "Apto para Competitivo" :
            Economy.ValidateDeck(deck, p.Cards).Count == 0 ? "Válido" : "Revisar mazo";
    }

    public static List<string> ValidateRanked(DeckDefinition deck, IReadOnlyDictionary<string, int>? collection = null)
    {
        var converted = Convert(deck);
        var issues = Economy.ValidateDeck(converted, collection, draft: true);
        if (converted.Arcano is null) issues.Add("Falta tu Arcano Mayor.");
        if (converted.Entries.Sum(e => e.Copies) != DeckSize) issues.Add($"Tu mazo necesita {DeckSize} cartas principales.");
        if (collection is not null)
            issues.AddRange(Missing(new Profile { Cards = new Dictionary<string, int>(collection) }, converted)
                .Select(e => collection.GetValueOrDefault(e.Id) > 0
                    ? $"Faltan {e.Copies} de {Economy.Card(e.Id)?.Name ?? e.Id}."
                    : Economy.IsMajor(e.Id) ? "Hay un Arcano pendiente de obtener." : "Hay cartas pendientes de obtener."));
        return issues.Distinct().ToList();
    }
}
