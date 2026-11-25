// Carga y validación de contenido (port de validators.ts + registry.ts). Rechaza datos inválidos antes de jugar.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Arcana.Content;

public sealed record ValidationIssue(string Path, string Message);

public sealed class ContentValidationError(IReadOnlyList<ValidationIssue> issues)
    : Exception($"Contenido inválido ({issues.Count} problema(s)):\n" + string.Join("\n", issues.Select(i => $"- {i.Path}: {i.Message}")))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

public sealed record GameContent(IReadOnlyList<CardDefinition> Cards, IReadOnlyList<DeckDefinition> Decks)
{
    /// <summary>Mazos iniciales («starter» en decks.json): se eligen al crear la cuenta y no son mazos base ni familias.</summary>
    public IReadOnlyList<DeckDefinition> Starters { get; init; } = [];
}

public static partial class ContentLoader
{
    public const int DeckSize = 40;
    public const int MinPlayableDeck = 20, MaxPlayableDeck = 50;
    public const int MaxCopies = 3;

    static readonly Dictionary<string, (int lo, int hi)> CostRange = new()
    {
        ["unit"] = (1, 5), ["spell"] = (1, 7), ["trick"] = (1, 3),
    };
    static readonly string[] Schools = ["wands", "cups", "swords", "pentacles"];
    static readonly string[] ArcanaKeys = ["fool", "magician", "priestess", "death", "devil", "lovers", "emperor", "empress", "fortune", "chariot"];
    static readonly HashSet<string> CardKeys = ["id", "name", "type", "school", "arcana", "cost", "rarity", "atk", "def", "duration", "text", "effects", "partner", "token"];
    static readonly HashSet<string> DeckKeys = ["id", "name", "entries", "starter"];

    [GeneratedRegex(@"^[A-Z]\d{2}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?:Duración\s*:?\s*(?:(?:de\s+)?\d+\s+(?:rondas?|turnos?(?:\s+propios?)?)|indefinida)|indefinid[oa])\.\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPrefix();

    static GameContent? cached;
    static readonly Lazy<JsonArray> BoxExpansion = new(() => JsonNode.Parse(ReadResource("weekly-box-cards.json"))!.AsArray());
    public static bool IsBoxExpansion(string id) => BoxExpansion.Value.Any(card => (string?)card?["id"] == id);
    static readonly Lazy<JsonArray> CampaignCards = new(() => JsonNode.Parse(ReadResource("campaign-cards.json"))!.AsArray());
    /// <summary>Cartas que solo da el modo historia: no van en cajas ni sobres.</summary>
    public static bool IsCampaignCard(string id) => CampaignCards.Value.Any(card => (string?)card?["id"] == id);

    /// <summary>Catálogo base y cartas de caja incluidos en el ensamblado.</summary>
    public static GameContent Load() => cached ??= LoadBuiltIn();

    static GameContent LoadBuiltIn()
    {
        var cards = JsonNode.Parse(ReadResource("cards.json"))!.AsArray();
        foreach (var card in BoxExpansion.Value.Concat(CampaignCards.Value)) cards.Add(card!.DeepClone());
        return Load(cards.ToJsonString(), ReadResource("decks.json"));
    }

    public static GameContent Load(string cardsJson, string decksJson)
    {
        var rawCards = JsonNode.Parse(cardsJson);
        var issues = ValidateCards(rawCards);
        if (issues.Count > 0) throw new ContentValidationError(issues);
        var cards = rawCards.Deserialize<List<CardDefinition>>(Json.Options)!;
        var rawDecks = JsonNode.Parse(decksJson);
        issues = ValidateDecks(rawDecks, cards);
        if (issues.Count > 0) throw new ContentValidationError(issues);
        foreach (var card in cards)
        {
            if (card.Duration is not { } duration) continue;
            var prefix = duration.Rounds switch
            {
                null => "Duración indefinida.",
                1 => "Duración de 1 turno propio.",
                int turns => $"Duración de {turns} turnos propios.",
            };
            var effect = DurationPrefix().Replace(card.Text.Trim(), "", 1);
            card.Text = effect.Length == 0 ? prefix : $"{prefix} {effect}";
        }
        var decks = rawDecks.Deserialize<List<DeckDefinition>>(Json.Options)!;
        return new GameContent(cards, decks.Where(d => !d.Starter).ToList()) { Starters = decks.Where(d => d.Starter).ToList() };
    }

    public static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Falta el recurso {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static bool IsInt(JsonNode? v, out int n)
    {
        n = 0;
        if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && jv.TryGetValue(out double d) && d == Math.Floor(d))
        {
            n = (int)d;
            return true;
        }
        return false;
    }

    static string? Str(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : null;
    static bool IsText(JsonNode? v) => !string.IsNullOrWhiteSpace(Str(v));

    public static List<ValidationIssue> ValidateCards(JsonNode? raw)
    {
        if (raw is not JsonArray arr) return [new("cards", "debe ser un arreglo")];
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>();
        for (var i = 0; i < arr.Count; i++)
        {
            var path = $"cards[{i}]";
            if (arr[i] is not JsonObject c)
            {
                issues.Add(new(path, "debe ser un objeto"));
                continue;
            }
            var id = Str(c["id"]);
            if (id is not null) path = $"cards[{id}]";
            void Bad(string m) => issues.Add(new(path, m));

            foreach (var k in c.Select(p => p.Key)) if (!CardKeys.Contains(k)) Bad($"campo desconocido «{k}»");
            if (id is null || !IdPattern().IsMatch(id)) Bad("id debe tener forma F00, M05…");
            else if (!seen.Add(id)) Bad("id duplicado");

            if (!IsText(c["name"])) Bad("name es obligatorio");
            if (c.ContainsKey("rarity") && Str(c["rarity"]) is not ("common" or "rare" or "epic" or "legendary")) Bad("rarity inválida");
            if (!IsText(c["text"])) Bad("text es obligatorio");

            var schoolNull = c.ContainsKey("school") && c["school"] is null;
            var school = Str(c["school"]);
            if (!schoolNull && (school is null || !Schools.Contains(school))) Bad("school debe ser null o bastos/copas/espadas/oros");

            var hasArcana = c.ContainsKey("arcana");
            if (hasArcana)
            {
                var a = c["arcana"] as JsonObject;
                if (a is null || Str(a["key"]) is not string key || !ArcanaKeys.Contains(key) || !IsInt(a["number"], out var num) || num < 0 || num > 21)
                    Bad("arcana debe ser {key, number 0–21}");
            }
            if (schoolNull != hasArcana) Bad("las cartas sin escuela deben ser Arcano Mayor (arcana) y viceversa");

            var type = Str(c["type"]);
            if (type is null || !CostRange.TryGetValue(type, out var range))
            {
                Bad("type debe ser unit, spell o trick");
                continue;
            }
            if (!IsInt(c["cost"], out var cost) || cost < range.lo || cost > range.hi) Bad($"coste de {type} debe ser entero {range.lo}–{range.hi}");

            if (type == "unit")
            {
                foreach (var k in new[] { "atk", "def" })
                    if (!IsInt(c[k], out var v) || v < 0) Bad($"{k} es obligatorio (entero ≥ 0) en Unidades");
            }
            else if (c.ContainsKey("atk") || c.ContainsKey("def")) Bad("ATQ/DEF solo en Unidades");

            if (type == "spell")
            {
                if (!(Str(c["duration"]) == "indefinite" || (IsInt(c["duration"], out var d) && d >= 1)))
                    Bad("duration en Hechizos debe ser entero ≥ 1 o «indefinite»");
            }
            else if (c.ContainsKey("duration")) Bad("duración solo en Hechizos");

            if (c.ContainsKey("effects"))
            {
                if (c["effects"] is not JsonArray fx || fx.Any(e => e is not JsonObject)) Bad("effects debe ser un arreglo de objetos");
                else
                    for (var j = 0; j < fx.Count; j++)
                        if (fx[j]!["trigger"] is not JsonObject || fx[j]!["actions"] is not JsonArray)
                            Bad($"effects[{j}] necesita trigger (objeto) y actions (arreglo)");
            }
            if (c.ContainsKey("token") && (c["token"]?.GetValueKind() != JsonValueKind.True || type != "unit" || hasArcana))
                Bad("token solo vale true en Unidades que no son Arcano");
            if (c.ContainsKey("partner") && (Str(c["partner"]) is not string partner || partner == id)) Bad("partner debe ser el ID de otra carta");
        }
        var ids = arr.OfType<JsonObject>().Select(c => Str(c["id"])).ToHashSet();
        foreach (var c in arr.OfType<JsonObject>())
            if (c.ContainsKey("partner") && !ids.Contains(Str(c["partner"])))
                issues.Add(new($"cards[{Str(c["id"])}]", $"pareja inexistente «{Str(c["partner"])}»"));
        return issues;
    }

    public static List<ValidationIssue> ValidateDecks(JsonNode? raw, IReadOnlyList<CardDefinition> cards)
    {
        if (raw is not JsonArray arr) return [new("decks", "debe ser un arreglo")];
        var known = cards.Where(c => c.Token != true).Select(c => c.Id).ToHashSet(); // las fichas no van en mazos
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>();
        for (var i = 0; i < arr.Count; i++)
        {
            var path = $"decks[{i}]";
            if (arr[i] is not JsonObject d)
            {
                issues.Add(new(path, "debe ser un objeto"));
                continue;
            }
            var id = Str(d["id"]);
            if (id is not null) path = $"decks[{id}]";
            void Bad(string m) => issues.Add(new(path, m));
            foreach (var k in d.Select(p => p.Key)) if (!DeckKeys.Contains(k)) Bad($"campo desconocido «{k}»");
            if (!IsText(d["id"])) Bad("id es obligatorio");
            else if (!seen.Add(id!)) Bad("id de mazo duplicado");
            if (!IsText(d["name"])) Bad("name es obligatorio");
            if (d["entries"] is not JsonArray entries)
            {
                Bad("entries debe ser un arreglo");
                continue;
            }
            var total = 0;
            var used = new HashSet<string>();
            for (var j = 0; j < entries.Count; j++)
            {
                if (entries[j] is not JsonObject e || Str(e["id"]) is not string eid || !IsInt(e["copies"], out var copies))
                {
                    Bad($"entries[{j}] debe ser {{id, copies}}");
                    continue;
                }
                if (!known.Contains(eid)) Bad($"carta inexistente «{eid}»");
                if (!used.Add(eid)) Bad($"carta repetida en entries «{eid}»");
                if (copies < 1 || copies > MaxCopies) Bad($"«{eid}»: copias debe estar entre 1 y {MaxCopies} (hay {copies})");
                total += copies;
            }
            if (total != DeckSize) Bad($"el mazo debe tener {DeckSize} cartas (tiene {total})");
        }
        return issues;
    }
}
