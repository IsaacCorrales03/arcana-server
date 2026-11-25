// Modo historia «Los Hilos del Destino»: capítulos y niveles de campaign.json, condiciones, estrellas y recompensas.
// Lógica pura: la pantalla (StoryScreen) y el duelo (DuelSetup.Campaign) solo la llaman.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Arcana.Content;

namespace Arcana.Meta;

public sealed class CampaignChapter
{
    public string Id { get; set; } = "";
    public string Numero { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Lugar { get; set; } = "";
    public string Color { get; set; } = "#e2bd70";
    public string Acento { get; set; } = "#ffffff";
    public string Jefe { get; set; } = "";
    public string Leccion { get; set; } = "";
    public string Relato { get; set; } = "";
    public string Titulo { get; set; } = "";
    public List<string> Cartas { get; set; } = [];
    public List<CampaignLevel> Niveles { get; set; } = [];
    [JsonIgnore] public int MaxStars => Niveles.Sum(l => l.Estrella is null ? 1 : 2);
}

public sealed class CampaignLevel
{
    public string Id { get; set; } = "";
    public int N { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>duelo | condicion | especial | minijefe | jefe</summary>
    public string Tipo { get; set; } = "duelo";
    public string Npc { get; set; } = "";
    public string Mazo { get; set; } = "";
    public string Ia { get; set; } = "heuristic";
    public int Pv { get; set; } = 30;
    public string? Prestado { get; set; }
    public string? Condicion { get; set; }
    public string? Estrella { get; set; }
    public string Intro { get; set; } = "";
    public string? Texto { get; set; }
    public List<string>? Mano { get; set; }
    public List<string>? Robos { get; set; }
    public List<string>? Campo { get; set; }
    public int? MiPV { get; set; }
    [JsonIgnore] public string Chapter => Id[..Id.LastIndexOf('-')];
}

/// <summary>Resultado de un duelo de campaña. Failed explica por qué no se superó.</summary>
public sealed record CampaignResult(Profile Profile, int Stars, bool FirstClear, List<string> Rewards, List<string> Cards, string? Failed);

public static partial class Economy
{
    static List<CampaignChapter>? campaign;
    public static IReadOnlyList<CampaignChapter> Campaign =>
        campaign ??= JsonSerializer.Deserialize<List<CampaignChapter>>(ContentLoader.ReadResource("campaign.json"), Json.Options)!;

    public static CampaignChapter ChapterOf(CampaignLevel level) => Campaign.First(c => c.Id == level.Chapter);
    public static int Stars(Profile p, CampaignLevel level) => p.Historia.GetValueOrDefault(level.Id);
    public static int Stars(Profile p, CampaignChapter chapter) => chapter.Niveles.Sum(l => Stars(p, l));
    public static bool Cleared(Profile p, CampaignChapter chapter) => Stars(p, chapter.Niveles[^1]) > 0;

    public static bool IsOpen(Profile p, CampaignChapter chapter)
    {
        if (p.IsDevAccount) return true;
        int i = Campaign.ToList().IndexOf(chapter);
        return i <= 0 || Cleared(p, Campaign[i - 1]);
    }

    public static bool IsOpen(Profile p, CampaignLevel level) => p.IsDevAccount ||
        IsOpen(p, ChapterOf(level)) && (level.N == 1 || Stars(p, ChapterOf(level).Niveles[level.N - 2]) > 0);

    /// <summary>Títulos de perfil ganados al vencer a cada jefe.</summary>
    public static IEnumerable<string> CampaignTitles(Profile p) => Campaign.Where(c => p.IsDevAccount || Cleared(p, c)).Select(c => c.Titulo);

    /// <summary>Cuenta de desarrollo con todo desbloqueado, generada leyendo el contenido (las cartas nuevas entran solas).
    /// Idempotente: la misma entrada da siempre el mismo perfil.</summary>
    public static Profile DevProfile(string name)
    {
        var p = new Profile
        {
            Name = name, IsDevAccount = true, ServerValid = true, Tutorial = true, RegisteredAt = DateTimeOffset.UnixEpoch,
            Cards = Cards.ToDictionary(c => c.Id, _ => MaxCopies),
            Alts = WithAlt.Where(id => Card(id) is not null).ToDictionary(id => id, _ => MaxCopies),
            Gemas = 999_999, Esencia = 999_999, Chispas = 9_999, Sobres = 999, SobresEpicos = 999,
            Partidas = 2_000, // nivel de cuenta (Ranked.Level) muy por encima de 15
            Vistas = Cards.Select(c => c.Id).ToList(),
            Historia = Campaign.SelectMany(c => c.Niveles).ToDictionary(l => l.Id, l => l.Estrella is null ? 1 : 2),
            TitleId = "Arcano Mayor", FrameId = "Amatista", CardBack = "celeste",
        };
        p.CompletedFamilyBoxes.UnionWith(Families.Select(f => f.Key));
        foreach (var d in Decks.Concat(Starters))
        {
            var preset = DeckWorkshop.Preset(d.Starter ? new DeckDefinition { Id = MajorKey(d.Entries.First(e => IsMajor(e.Id)).Id)!, Name = d.Name, Entries = d.Entries } : d);
            if (d.Starter) preset.Copy = 1;
            p.Decks.Add(preset);
        }
        p.SelectDeck(p.Decks[0]);
        return p;
    }

    static readonly Regex Rule = new(@"^([a-z]+)(>=|<=)(\d+)$", RegexOptions.CultureInvariant);

    static int Measure(string key, GameSummary r) => key switch
    {
        "pv" => r.Hp, "directos" => r.Direct, "hechizos" => r.Spells, "curar" => r.Healed, "sacrificios" => r.Sacrifices,
        "fichas" => r.Tokens, "dados" => r.Dice, "efecto" => r.EffectDamage, "combate" => r.CombatDamage,
        "rondas" => r.Rounds, "bajas" => r.Losses, "trucos" => r.Tricks, "robos" => r.Draws,
        _ => throw new InvalidOperationException($"Condición desconocida: {key}"),
    };

    public static bool Meets(string? condition, GameSummary r)
    {
        if (condition is null) return true;
        var m = Rule.Match(condition);
        if (!m.Success) throw new InvalidOperationException($"Condición mal escrita: {condition}");
        int value = Measure(m.Groups[1].Value, r), goal = int.Parse(m.Groups[3].Value);
        return m.Groups[2].Value == ">=" ? value >= goal : value <= goal;
    }

    public static string ConditionText(string? condition)
    {
        if (condition is null) return "Gana el duelo";
        if (condition == "hechizos<=0") return "Gana sin jugar Hechizos";
        var m = Rule.Match(condition);
        if (!m.Success) return condition;
        var n = m.Groups[3].Value;
        return m.Groups[1].Value switch
        {
            "pv" => $"Gana con {n} PV o más", "directos" => $"Haz {n} ataques directos", "hechizos" => $"Juega {n} Hechizos",
            "curar" => $"Recupera {n} PV", "sacrificios" => $"Sacrifica {n} Unidades", "fichas" => $"Invoca {n} Retoños",
            "dados" => $"Lanza {n} dados o monedas", "efecto" => $"Quita {n} PV con efectos", "combate" => $"Quita {n} PV en combate",
            "rondas" => $"Gana en {n} rondas o menos", "bajas" => $"Pierde {n} Unidades como máximo", "trucos" => $"Juega {n} Trucos",
            "robos" => $"Roba {n} cartas", _ => condition,
        };
    }

    public static DeckDefinition CampaignRival(CampaignLevel level) => Decks.First(d => d.Id == level.Mazo);
    public static DeckDefinition? CampaignLoan(CampaignLevel level) => level.Prestado is { } id ? Decks.First(d => d.Id == id) : null;

    /// <summary>Prepara el duelo del nivel: mazo propio o prestado, rival, IA, PV, mano guionada y cartas ya en juego.</summary>
    public static Match StartCampaign(DeckDefinition mine, CampaignLevel level, Arcana.Core.GameContext ctx, long? seed = null)
    {
        var order = level.Mano is null && level.Robos is null ? null : new CardOrder(level.Mano ?? [], level.Robos ?? []);
        var opts = new Match.StartOptions(level.Ia, level.Pv, level.Tipo == "especial", order, null, level.Campo, level.MiPV);
        return Match.Start(CampaignLoan(level) ?? mine, CampaignRival(level), ctx, 1, seed, false, opts);
    }

    /// <summary>Aplica el resultado: estrellas y, la primera vez, las recompensas del nivel y del capítulo completo.</summary>
    public static CampaignResult FinishCampaign(Profile p, CampaignLevel level, GameSummary r)
    {
        if (!r.Won) return new(p, 0, false, [], [], "Has perdido el duelo.");
        if (!Meets(level.Condicion, r)) return new(p, 0, false, [], [], $"Ganaste, pero no cumpliste la condición: {ConditionText(level.Condicion)}.");
        var chapter = ChapterOf(level);
        int stars = level.Estrella is not null && Meets(level.Estrella, r) ? 2 : 1;
        int before = Stars(p, level), chapterBefore = Stars(p, chapter);
        var q = p.Clone();
        q.Historia[level.Id] = Math.Max(before, stars);
        var rewards = new List<string>();
        var cards = new List<string>();
        if (before == 0)
            switch (level.Tipo)
            {
                case "condicion" or "especial":
                    q.Chispas++;
                    rewards.Add("+1 chispa");
                    break;
                case "minijefe":
                    q.Sobres++;
                    rewards.Add("+1 sobre");
                    cards.AddRange([chapter.Cartas[0], chapter.Cartas[0]]);
                    break;
                case "jefe":
                    q.Sobres += 2;
                    rewards.Add("+2 sobres");
                    rewards.Add($"Título «{chapter.Titulo}»");
                    cards.AddRange([chapter.Cartas[1], chapter.Cartas[1]]);
                    if (q.Has(chapter.Jefe) == 0) cards.Add(chapter.Jefe);
                    break;
            }
        if (chapterBefore < chapter.MaxStars && Stars(q, chapter) == chapter.MaxStars)
        {
            q.SobresEpicos++;
            rewards.Add("Capítulo perfecto: +1 sobre épico");
            cards.AddRange(chapter.Cartas);
        }
        if (cards.Count > 0)
        {
            q = WithCards(q, cards);
            if (cards.Contains(chapter.Jefe)) UnlockFamily(q, chapter.Jefe);
        }
        return new(q, stars, before == 0, rewards, cards, null);
    }
}
