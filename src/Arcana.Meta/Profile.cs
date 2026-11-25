// Perfil del jugador y economía (port de client/perfil.ts). El JSON es el mismo que el de la web, así que un perfil
// web se puede importar. Las funciones son puras: devuelven un perfil nuevo y no tocan el recibido.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Arcana.Content;

namespace Arcana.Meta;

public sealed class Daily
{
    public string Dia { get; set; } = "";
    public List<string> Cartas { get; set; } = [];
    public List<bool> Vendidas { get; set; } = [];
}

public sealed class DailyMissions
{
    public string Dia { get; set; } = "";
    public string Mazo { get; set; } = "";
    public List<int> Prog { get; set; } = [];
}

public sealed class Profile
{
    public string Name { get; set; } = "";
    public Dictionary<string, int> Cards { get; set; } = [];
    public string? PlayerId { get; set; }
    public string? RankedKey { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
    public string? AvatarId { get; set; }
    public string FrameId { get; set; } = "laton";
    public string TitleId { get; set; } = "Duelista";
    public string CardBack { get; set; } = "clasico";
    public List<string> FavoriteCards { get; set; } = [];
    public List<DuelRecord> History { get; set; } = [];
    public List<DeckDefinition> DeckDrafts { get; set; } = [];
    public RankProgress Rank { get; set; } = new();
    public RankRules RankConfig { get; set; } = new();
    public List<string> ClaimedRewards { get; set; } = [];
    public string? ReadNotice { get; set; }
    public Dictionary<string, int> Alts { get; set; } = [];
    public List<string> UsaAlt { get; set; } = [];
    public List<DeckDefinition> Decks { get; set; } = [];
    public string Activo { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ActiveCopy { get; set; }
    public int Sobres { get; set; }
    public int SobresEpicos { get; set; }
    public Dictionary<string, int> SobresFam { get; set; } = [];
    public Dictionary<string, List<string>> FamilyBoxes { get; set; } = [];
    public HashSet<string> CompletedFamilyBoxes { get; set; } = [];
    public int FamilyBoxFormat { get; set; } = Economy.BoxFormat;
    public int Gemas { get; set; }
    public int Esencia { get; set; }
    public int Chispas { get; set; }
    public int Vales { get; set; }
    public List<string> Vistas { get; set; } = [];
    public List<string> AcquisitionOrder { get; set; } = [];
    public Daily? Diaria { get; set; }
    public DailyMissions? Misiones { get; set; }
    /// <summary>false = falta la partida guiada (perfil nuevo); null = perfil antiguo.</summary>
    public bool? Tutorial { get; set; }
    public int Partidas { get; set; }
    public bool Segundo { get; set; }
    public Dictionary<string, int> Retos { get; set; } = [];
    /// <summary>Modo historia: estrellas (1–2) de cada nivel superado.</summary>
    public Dictionary<string, int> Historia { get; set; } = [];
    /// <summary>Cuenta de desarrollo (ACherry): fuera de rankings, competitivo real y métricas.</summary>
    [JsonPropertyName("is_dev_account")] public bool IsDevAccount { get; set; }
    /// <summary>Perfil que corresponde a una cuenta del servidor (o de administración); sin esta marca es un perfil local antiguo.</summary>
    [JsonPropertyName("server-valid")] public bool ServerValid { get; set; }

    public Profile Clone() => JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);
    public int Has(string id) => Cards.TryGetValue(id, out var n) ? n : 0;
    [JsonIgnore] public DeckDefinition ActiveDeck => Decks.FirstOrDefault(d => d.Id == Activo && d.Copy == ActiveCopy)
        ?? Decks.FirstOrDefault(d => d.Id == Activo) ?? Decks[0];

    public void SelectDeck(DeckDefinition deck)
    {
        Activo = deck.Id;
        ActiveCopy = deck.Copy;
    }
}

/// <summary>Tipos de sobre: normal, arcana, epico o fam:&lt;familia&gt;.</summary>
public sealed record Opened(Profile Profile, List<string> Cards, List<bool> New, bool Arcana = false, int Arcanas = 0);
public sealed record Pull(string K, string? Id = null);
public sealed record Mission(string Text, int Goal, int Sparks, int Gems, Func<bool, string, bool> Counts);
public sealed record Progress(string Text, int Goal, int Before, int Now, int Sparks, int Gems);
public sealed record Challenge(string Id, string Text, int[] Goals, Func<GameSummary, int> Measure);

/// <summary>Lo que pasó en una partida para un jugador, contado desde los eventos del motor.</summary>
public sealed class GameSummary
{
    public bool Won;
    public int Direct, CombatDamage, EffectDamage, Healed, Sacrifices, Spells, Tokens, Dice, Hp;
    /// <summary>Modo historia: rondas jugadas, Unidades propias perdidas (sin fichas), Trucos jugados y cartas robadas.</summary>
    public int Rounds, Losses, Tricks, Draws;
    public bool Arcana;
}

public static partial class Economy
{
    public const int MinDeck = ContentLoader.MinPlayableDeck, MaxDeck = ContentLoader.MaxPlayableDeck,
        RecommendedDeck = ContentLoader.DeckSize, MaxCopies = ContentLoader.MaxCopies;
    public const int WinGems = 40, LossGems = 15, PackPrice = 60, FamilyPrice = PackPrice;
    public const int DailyCards = 3, CardsPerPack = 3, DailyPacks = 3;
    /// <summary>Cartas del día: se pagan con Esencia, a mitad del coste de crearlas.</summary>
    public static readonly Dictionary<string, int> CardPrice = new() { ["common"] = 50, ["rare"] = 150, ["epic"] = 400, ["legendary"] = 1200 };
    public static readonly (string k, double w)[] Weights = [("common", 0.68), ("rare", 0.25), ("epic", 0.07)];
    public static readonly string[] WithAlt = ["F00", "M00", "P00", "D00", "X00"];
    public static readonly Dictionary<string, int> DestroyEssence = new() { ["common"] = 20, ["rare"] = 60, ["epic"] = 200, ["legendary"] = 600 };
    public static readonly Dictionary<string, int> CraftCost = new() { ["common"] = 100, ["rare"] = 300, ["epic"] = 800, ["legendary"] = 2400, ["alt"] = 3000 };

    public static readonly (string K, string Name, double W)[] Gacha =
    [
        ("esencia", "1000 de Esencia", 0.25), ("carta", "Carta del catálogo", 0.17), ("epico", "Sobre de cartas épicas", 0.15),
        ("gemas", "300 Gemas arcanas", 0.12), ("esencias", "2000 de Esencia", 0.1), ("caja", "5 sobres de tu Arcano activo", 0.08),
        ("sobres", "3 sobres de cartas", 0.08), ("vale", "Vale de carta a elegir", 0.05),
    ];

    static GameContent C => ContentLoader.Load();
    /// <summary>Cartas coleccionables (sin fichas).</summary>
    public static IReadOnlyList<CardDefinition> Cards => cards ??= C.Cards.Where(c => c.Token != true).ToList();
    static List<CardDefinition>? cards;
    public static IReadOnlyList<DeckDefinition> Decks => C.Decks;
    /// <summary>Mazos iniciales entre los que elige una cuenta nueva (El Viajero, La Cosecha, El Juramento).</summary>
    public static IReadOnlyList<DeckDefinition> Starters => C.Starters;
    public static CardDefinition? Card(string id) => C.Cards.FirstOrDefault(c => c.Id == id);
    public static IReadOnlyList<string> Majors => majors ??= Cards.Where(c => c.Arcana is not null).Select(c => c.Id).ToList();
    static List<string>? majors;
    /// <summary>Familias: letra de sus IDs → clave del Arcano.</summary>
    public static IReadOnlyList<(char Letter, string Key)> Families => Decks.Select(d => (d.Entries[0].Id[0], d.Id)).ToList();
    public static string? FamilyOf(string id) => Families.FirstOrDefault(f => f.Letter == id[0]).Key;
    /// <summary>Clave del mazo de un Arcano Mayor (null si no lo es).</summary>
    public static string? MajorKey(string id) => Card(id)?.Arcana?.Key;
    public static bool IsMajor(string id) => MajorKey(id) is not null;
    public static string Rarity(string id) => Card(id)?.Rarity ?? "common";

    static Profile WithCards(Profile p, IEnumerable<string> ids)
    {
        var q = p.Clone();
        var seen = new HashSet<string>(q.Vistas);
        foreach (var id in ids)
        {
            if (q.Has(id) == 0)
            {
                seen.Remove(id);
                q.AcquisitionOrder.Remove(id);
                q.AcquisitionOrder.Add(id);
            }
            q.Cards[id] = Math.Min(MaxCopies, q.Has(id) + 1);
        }
        q.Vistas = q.Vistas.Where(seen.Contains).Concat(seen.Except(q.Vistas)).ToList();
        return q;
    }

    static IEnumerable<string> Expand(DeckDefinition d) => d.Entries.SelectMany(e => Enumerable.Repeat(e.Id, e.Copies));
    static DeckDefinition CopyDeck(DeckDefinition d) => new() { Id = d.Id, Name = d.Name, Entries = d.Entries.Select(e => new DeckEntry { Id = e.Id, Copies = e.Copies }).ToList() };

    static Profile WithDeck(Profile p, string deckId)
    {
        // un mazo inicial («starter-fool») se guarda con el id de la familia de su Arcano, como un mazo base
        var d = Decks.FirstOrDefault(m => m.Id == deckId)
            ?? (Starters.FirstOrDefault(m => m.Id == deckId) is { } s ? new DeckDefinition { Id = MajorKey(s.Entries.First(e => IsMajor(e.Id)).Id)!, Name = s.Name, Entries = s.Entries } : null)
            ?? throw new InvalidOperationException($"Mazo desconocido: {deckId}");
        deckId = d.Id;
        var starter = DeckWorkshop.Preset(d);
        var q = WithCards(p, Expand(d));
        q = WithCards(q, DeckWorkshop.Missing(q, starter).SelectMany(e => Enumerable.Repeat(e.Id, e.Copies)));
        if (!q.Decks.Any(m => m.Id == deckId)) q.Decks.Add(starter);
        return q;
    }

    /// <summary>Una copia de un Arcano: habilita su mazo (vacío salvo el Arcano).</summary>
    static void UnlockFamily(Profile p, string id)
    {
        var key = MajorKey(id)!;
        if (p.Decks.Any(d => d.Id == key)) return;
        p.Decks.Add(new DeckDefinition { Id = key, Name = Decks.FirstOrDefault(m => m.Id == key)?.Name ?? key, Entries = [new DeckEntry { Id = id, Copies = 1 }] });
    }

    public static Profile NewProfile(string name, string deckId)
    {
        var p = WithDeck(new Profile
        {
            Name = name, Activo = deckId, Sobres = 1, Vistas = Cards.Select(c => c.Id).ToList(), Tutorial = false,
            RegisteredAt = DateTimeOffset.UtcNow,
        }, deckId);
        p.SelectDeck(p.Decks[0]);
        return p;
    }

    public static bool CanPickSecond(Profile p) => !p.Segundo && p.Tutorial != false && p.Partidas >= 1
        && p.Decks.Select(d => d.Id).Distinct().Count() < Decks.Count;

    public static Profile PickSecond(Profile p, string deckId)
    {
        if (!CanPickSecond(p)) throw new InvalidOperationException("Ya elegiste tu mazo secundario");
        var full = Decks.FirstOrDefault(m => m.Id == deckId);
        if (full is null || p.Decks.Any(d => d.Id == deckId && d.Entries.Sum(e => e.Copies) >= MinDeck)) throw new InvalidOperationException("Ese mazo ya lo tienes");
        var q = p.Clone();
        q.Segundo = true;
        q.SobresFam[deckId] = q.SobresFam.GetValueOrDefault(deckId) + 3;
        return q;
    }

    public static Profile FinishTutorial(Profile p)
    {
        var q = p.Clone();
        if (q.Tutorial == false) q.Sobres++; // sobre de bienvenida, solo la primera vez
        q.Tutorial = true;
        return q;
    }

    // ---------- Maestría ----------

    public static GameSummary Summarize(IEnumerable<Arcana.Core.Ev> events, string me, bool won, int finalHp)
    {
        var r = new GameSummary { Won = won, Hp = finalHp };
        foreach (var e in events)
        {
            if (e.Type == "ATTACK_DECLARED" && e.Player == me && e.TargetIid is null) r.Direct++;
            else if (e.Type == "HP_CHANGED" && e.Player != me && e.Delta < 0) { if (e.Cause == "combat") r.CombatDamage -= e.Delta!.Value; else r.EffectDamage -= e.Delta!.Value; }
            else if (e.Type == "HP_CHANGED" && e.Player == me && e.Delta > 0) r.Healed += e.Delta!.Value;
            else if (e.Type == "CARD_DESTROYED" && e.Player == me && e.Cause == "effect" && e.SourcePlayer == me) r.Sacrifices++;
            else if (e.Type == "SPELL_PLAYED" && e.Player == me) r.Spells++;
            else if (e.Type == "TOKEN_CREATED" && e.Player == me) r.Tokens++;
            else if (e.Type == "DICE_ROLLED" && e.Player == me) r.Dice++;
            else if (e.Type is "UNIT_SUMMONED" or "UNIT_REPLACED" && e.Player == me && IsMajor(e.CardId!)) r.Arcana = true;
            if (e.Type == "ROUND_STARTED") r.Rounds = Math.Max(r.Rounds, e.Round ?? 0);
            if (e.Player != me) continue;
            if (e.Type == "CARD_DESTROYED" && e.Iid?.Contains(":tk") == false) r.Losses++;
            else if (e.Type == "TRICK_PLAYED") r.Tricks++;
            else if (e.Type == "CARD_DRAWN") r.Draws++;
        }
        return r;
    }

    static readonly Dictionary<string, Challenge> Own = new()
    {
        ["fool"] = new("directos", "Ataques directos", [5, 20, 60], r => r.Direct),
        ["magician"] = new("hechizos", "Hechizos jugados", [5, 20, 60], r => r.Spells),
        ["priestess"] = new("curado", "PV recuperados", [10, 40, 120], r => r.Healed),
        ["death"] = new("sacrificios", "Sacrificios", [3, 15, 40], r => r.Sacrifices),
        ["devil"] = new("efecto", "PV quitados con efectos", [10, 40, 120], r => r.EffectDamage),
        ["lovers"] = new("combate", "PV quitados en combate", [20, 80, 200], r => r.CombatDamage),
        ["emperor"] = new("intacto", "Victorias con 20 PV o más", [1, 5, 15], r => r.Won && r.Hp >= 20 ? 1 : 0),
        ["empress"] = new("fichas", "Retoños invocados", [10, 40, 120], r => r.Tokens),
        ["fortune"] = new("dados", "Dados y monedas lanzados", [10, 40, 120], r => r.Dice),
        ["chariot"] = new("carga", "PV quitados en combate", [20, 80, 200], r => r.CombatDamage),
    };

    public static List<Challenge> Challenges(string deck) =>
    [
        new("victorias", "Victorias", [1, 5, 15], r => r.Won ? 1 : 0),
        new("arcano", "Partidas invocando a tu Arcano", [1, 5, 15], r => r.Arcana ? 1 : 0),
        .. Own.TryGetValue(deck, out var o) ? [o] : Array.Empty<Challenge>(),
    ];

    public static int ChallengeProgress(Profile p, string deck, string id) => p.Retos.TryGetValue($"{deck}:{id}", out var n) ? n : 0;
    public static int MasteryLevel(Profile p, string deck) =>
        1 + Challenges(deck).Sum(r => r.Goals.Count(m => ChallengeProgress(p, deck, r.Id) >= m));

    static (Profile p, string? text) AddMastery(Profile p, string deck, GameSummary res)
    {
        var before = MasteryLevel(p, deck);
        var q = p.Clone();
        foreach (var r in Challenges(deck)) q.Retos[$"{deck}:{r.Id}"] = ChallengeProgress(p, deck, r.Id) + r.Measure(res);
        var now = MasteryLevel(q, deck);
        var name = Decks.FirstOrDefault(m => m.Id == deck)?.Name.Split(" — ")[0] ?? deck;
        return (q, now > before ? $"¡Maestría de {name}: nivel {now}!" : null);
    }

    // ---------- Sobres ----------

    static string RandomCard(Func<double> rnd, string? fam = null, string? fixedRarity = null)
    {
        var r = rnd();
        var rarity = fixedRarity ?? Weights[0].k;
        if (fixedRarity is null)
            foreach (var (k, w) in Weights)
            {
                rarity = k;
                if ((r -= w) < 0) break;
            }
        var pool = Cards.Where(c => c.Arcana is null && c.Rarity == rarity && (fam is null || FamilyOf(c.Id) == fam) && !ContentLoader.IsCampaignCard(c.Id)).ToList();
        if (pool.Count == 0) pool = Cards.Where(c => c.Arcana is null && (fam is null || FamilyOf(c.Id) == fam) && !ContentLoader.IsCampaignCard(c.Id)).ToList();
        return pool[(int)Math.Floor(rnd() * pool.Count)].Id;
    }

    static List<bool> NewOnes(Profile p, IEnumerable<string> ids)
    {
        var seen = p.Cards.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet();
        seen.UnionWith(p.Alts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}@alt"));
        return ids.Select(id => seen.Add(id)).ToList();
    }

    public static Opened OpenPack(Profile p, Func<double> rnd, string? fam = null, BoxRotation? rotation = null)
    {
        if (fam is not null) return OpenFamily(p, fam, rnd, rotation);
        if (p.Sobres < 1) throw new InvalidOperationException("No tienes sobres");
        var cards = Enumerable.Range(0, CardsPerPack).Select(_ => RandomCard(rnd)).ToList();
        var q = p.Clone();
        q.Sobres--;
        return new Opened(WithCards(q, cards), cards, NewOnes(p, cards));
    }

    public static List<(string Type, int N)> Inventory(Profile p) =>
        new List<(string, int)> { ("epico", p.SobresEpicos), ("normal", p.Sobres) }
            .Concat(p.SobresFam.Select(kv => ($"fam:{kv.Key}", kv.Value))).Where(x => x.Item2 > 0).ToList();

    static Opened OpenOne(Profile p, string type, Func<double> rnd, BoxRotation? rotation) =>
        type == "epico" ? OpenEpic(p, rnd) : OpenPack(p, rnd, type == "normal" ? null : type[4..], rotation);

    /// <summary>Abre n sobres del mismo tipo. Todo o nada.</summary>
    public static Opened OpenMany(Profile p, string type, int n, Func<double> rnd, BoxRotation? rotation = null)
    {
        if (n < 1 || Inventory(p).FirstOrDefault(x => x.Type == type).N < n) throw new InvalidOperationException("No tienes tantos sobres");
        if (type.StartsWith("fam:", StringComparison.Ordinal)) RequireFamilyPack(p, type[4..], n, rotation);
        var q = p;
        var arcanas = 0;
        var cards = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var r = OpenOne(q, type, rnd, rotation);
            q = r.Profile;
            cards.AddRange(r.Cards);
            arcanas += r.Arcanas;
        }
        return new Opened(q, cards, NewOnes(p, cards), arcanas > 0, arcanas);
    }

    public static Profile BuyMany(Profile p, string type, int n, BoxRotation? rotation = null)
    {
        if (n < 1 || n > int.MaxValue / FamilyPrice) throw new InvalidOperationException("Cantidad inválida");
        if (type != "normal" && (!type.StartsWith("fam:", StringComparison.Ordinal) || !HasFamilyBox(type[4..])))
            throw new InvalidOperationException("Este sobre no se vende");
        if (type != "normal") RequireFamilyPack(p, type[4..], n, rotation);
        var q = Pay(p, (type == "normal" ? PackPrice : FamilyPrice) * n);
        if (type == "normal") q.Sobres = checked(q.Sobres + n);
        else q.SobresFam[type[4..]] = checked(q.SobresFam.GetValueOrDefault(type[4..]) + n);
        return q;
    }

    public static Opened OpenEpic(Profile p, Func<double> rnd)
    {
        if (p.SobresEpicos < 1) throw new InvalidOperationException("No tienes sobres épicos");
        var cards = Enumerable.Range(0, CardsPerPack).Select(_ => RandomCard(rnd, null, "epic")).ToList();
        var q = p.Clone();
        q.SobresEpicos--;
        return new Opened(WithCards(q, cards), cards, NewOnes(p, cards));
    }

    static uint DayHash(string day)
    {
        uint h = 0;
        foreach (var ch in day) h = unchecked(h * 31 + ch);
        return h;
    }

    /// <summary>Familias de los sobres de la tienda hoy (rotan cada día).</summary>
    public static List<string> DailyFamilies(string day)
    {
        var h = DayHash(day);
        var keys = Families.Select(f => f.Key).ToList();
        var output = new List<string>();
        while (output.Count < DailyPacks)
        {
            h = unchecked(h * 1103515245 + 12345);
            var k = keys[(int)(h % (uint)keys.Count)];
            if (!output.Contains(k)) output.Add(k);
        }
        return output;
    }

    // ---------- Gacha ----------

    public static (Profile p, Pull t) Pull1(Profile p, Func<double> rnd)
    {
        if (p.Chispas < 1) throw new InvalidOperationException("No tienes chispas");
        var q = p.Clone();
        q.Chispas--;
        var r = rnd();
        var k = Gacha[0].K;
        foreach (var g in Gacha)
        {
            k = g.K;
            if ((r -= g.W) < 0) break;
        }
        string One(IReadOnlyList<string> of) => of[(int)Math.Floor(rnd() * of.Count)];
        switch (k)
        {
            case "esencia": q.Esencia += 1000; return (q, new Pull(k));
            case "epico": q.SobresEpicos++; return (q, new Pull(k));
            case "gemas": q.Gemas += 300; return (q, new Pull(k));
            case "sobres": q.Sobres += 3; return (q, new Pull(k));
            case "vale": q.Vales++; return (q, new Pull(k));
            case "caja":
                q.SobresFam[q.Activo] = q.SobresFam.GetValueOrDefault(q.Activo) + 5;
                return (q, new Pull(k, q.Activo));
            case "esencias": q.Esencia += 2000; return (q, new Pull(k));
            case "carta":
            {
                var pool = Cards.Where(c => c.Arcana is null && !ContentLoader.IsCampaignCard(c.Id)).Select(c => c.Id).ToList();
                var free = pool.Where(id => q.Has(id) < MaxCopies).ToList();
                var id = One(free.Count > 0 ? free : pool);
                return (WithCards(q, [id]), new Pull(k, id));
            }
            default: throw new InvalidOperationException("Premio desconocido");
        }
    }

    public static (Profile p, List<Pull> t) PullMany(Profile p, int n, Func<double> rnd)
    {
        if (p.Chispas < n) throw new InvalidOperationException($"Necesitas {n} chispas");
        var q = p;
        var t = new List<Pull>();
        for (var i = 0; i < n; i++)
        {
            var r = Pull1(q, rnd);
            q = r.p;
            t.Add(r.t);
        }
        return (q, t);
    }

    public static Profile RedeemVoucher(Profile p, string id)
    {
        if (p.Vales < 1) throw new InvalidOperationException("No tienes vales de carta");
        var c = Card(id);
        if (c is null || c.Token == true) throw new InvalidOperationException("Carta desconocida");
        if (c.Arcana is not null) throw new InvalidOperationException("Este Arcano se obtiene en su caja");
        if (p.Has(id) >= MaxCopies) throw new InvalidOperationException($"Ya tienes {MaxCopies} copias");
        var q = p.Clone();
        q.Vales--;
        return WithCards(q, [id]);
    }

    // ---------- Misiones ----------

    public static List<Mission> Missions(string deck)
    {
        var name = Decks.FirstOrDefault(m => m.Id == deck)?.Name.Split(" — ")[0] ?? deck;
        return
        [
            new("Juega 3 partidas", 3, 1, 0, (_, _) => true),
            new("Gana 2 partidas", 2, 2, 0, (g, _) => g),
            new($"Gana con {name}", 1, 1, 50, (g, d) => g && d == deck),
        ];
    }

    public static Profile MissionsToday(Profile p, string day)
    {
        if (p.Misiones?.Dia == day) return p;
        var h = DayHash(day);
        var choices = p.Decks.Select(d => d.Family).Where(x => !string.IsNullOrEmpty(x)).Distinct().DefaultIfEmpty("fool").ToList();
        var deck = choices[(int)(h % (uint)choices.Count)];
        var q = p.Clone();
        q.Misiones = new DailyMissions { Dia = day, Mazo = deck, Prog = Missions(deck).Select(_ => 0).ToList() };
        return q;
    }

    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>Premio por terminar una partida: gemas, misiones y maestría.</summary>
    public static (Profile p, string text, List<Progress> progress, string? mastery) Reward(Profile p, bool won, string? deckId = null, string? day = null, GameSummary? res = null)
    {
        deckId ??= p.ActiveDeck.Family;
        day ??= Today();
        var g = won ? WinGems : LossGems;
        var (mp, mt) = res is not null ? AddMastery(p, deckId, res) : (p, null);
        var q0 = mp.Clone();
        q0.Gemas = p.Gemas + g;
        q0.Partidas = p.Partidas + 1;
        var q = MissionsToday(q0, day).Clone();
        var texts = new List<string> { $"+{g} gemas arcanas" };
        var list = Missions(q.Misiones!.Mazo);
        var prog = new List<int>();
        for (var i = 0; i < list.Count; i++)
        {
            var m = list[i];
            var n = i < q.Misiones.Prog.Count ? q.Misiones.Prog[i] : 0;
            if (n >= m.Goal || !m.Counts(won, deckId)) { prog.Add(n); continue; }
            if (n + 1 == m.Goal)
            {
                q.Chispas += m.Sparks;
                q.Gemas += m.Gems;
                texts.Add($"Misión «{m.Text}»: +{m.Sparks} chispa{(m.Sparks > 1 ? "s" : "")}{(m.Gems > 0 ? $" y {m.Gems} gemas" : "")}");
            }
            prog.Add(n + 1);
        }
        var progress = list.Select((m, i) => new Progress(m.Text, m.Goal, Math.Min(m.Goal, i < q.Misiones.Prog.Count ? q.Misiones.Prog[i] : 0), prog[i], m.Sparks, m.Gems)).ToList();
        q.Misiones.Prog = prog;
        return (q, string.Join(" · ", texts), progress, mt);
    }

    // ---------- Repetidas, Esencia y fabricar ----------

    public static int Spare(Profile p, string id)
    {
        var used = Math.Max(1, p.Decks.Select(d => d.WithArcano.FirstOrDefault(e => e.Id == id)?.Copies ?? 0).DefaultIfEmpty(0).Max());
        return Math.Max(0, p.Has(id) - used);
    }

    public static List<(string Id, int N)> Duplicates(Profile p) => Cards.Select(c => (c.Id, Spare(p, c.Id))).Where(x => x.Item2 > 0).ToList();
    public static int EssenceOf(string id) => DestroyEssence.GetValueOrDefault(Card(id)?.Rarity ?? "common", DestroyEssence["common"]);

    public static Profile Destroy(Profile p, string id, int n = 1)
    {
        if (n < 1 || n > Spare(p, id)) throw new InvalidOperationException("No tienes tantas copias repetidas");
        var q = p.Clone();
        q.Cards[id] -= n;
        q.Esencia += EssenceOf(id) * n;
        return q;
    }

    public static Profile DestroyAllDuplicates(Profile p) => Duplicates(p).Aggregate(p, (q, x) => Destroy(q, x.Id, x.N));

    public static int CraftCostOf(string id) => CraftCost.GetValueOrDefault(id.EndsWith("@alt") ? "alt" : Card(id)?.Rarity ?? "epic", CraftCost["epic"]);

    public static string? FamilyMajor(string cardId)
    {
        var family = FamilyOf(cardId);
        return family is null ? null : Cards.FirstOrDefault(card => card.Arcana?.Key == family)?.Id;
    }

    public static string? CraftRestriction(Profile p, string id)
    {
        bool alternative = id.EndsWith("@alt", StringComparison.Ordinal);
        string baseId = alternative ? id[..^4] : id;
        var card = Card(baseId);
        if (card is null || card.Token == true || alternative && !WithAlt.Contains(baseId))
            return "Carta desconocida";
        if (card.Arcana is not null || alternative) return "Esta edición se obtiene en su caja";
        if (ContentLoader.IsCampaignCard(baseId) && p.Has(baseId) == 0) return "Esta carta se gana en el modo historia";
        if (FamilyMajor(baseId) is { } major && p.Has(major) == 0)
            return $"Necesitas a {Lore.DeckName(MajorKey(major)!)} para fabricar cartas de este capítulo";
        return null;
    }

    public static Profile Craft(Profile p, string id)
    {
        if (CraftRestriction(p, id) is { } restriction) throw new InvalidOperationException(restriction);
        if (p.Has(id) >= MaxCopies) throw new InvalidOperationException($"Ya tienes {MaxCopies} copias");
        var cost = CraftCostOf(id);
        if (p.Esencia < cost) throw new InvalidOperationException("No tienes suficiente Esencia");
        var q = p.Clone();
        q.Esencia -= cost;
        return WithCards(q, [id]);
    }

    // ---------- Tienda ----------

    public static Profile Pay(Profile p, int price)
    {
        if (p.Gemas < price) throw new InvalidOperationException("No tienes suficientes gemas arcanas");
        var q = p.Clone();
        q.Gemas -= price;
        return q;
    }

    public static Profile DailyOffer(Profile p, string day, Func<double> rnd)
    {
        if (p.Diaria?.Dia == day) return p;
        var set = new List<string>();
        while (set.Count < DailyCards)
        {
            var id = RandomCard(rnd);
            if (!set.Contains(id)) set.Add(id);
        }
        var q = p.Clone();
        q.Diaria = new Daily { Dia = day, Cartas = set, Vendidas = set.Select(_ => false).ToList() };
        return q;
    }

    public static int PriceOf(string id) => CardPrice.GetValueOrDefault(Card(id)?.Rarity ?? "epic", CardPrice["epic"]);

    public static Profile BuyCard(Profile p, int i)
    {
        var id = p.Diaria is { } d && i < d.Cartas.Count ? d.Cartas[i] : null;
        if (p.Diaria is null || id is null || p.Diaria.Vendidas[i]) throw new InvalidOperationException("Esa carta ya no está a la venta");
        if (p.Has(id) >= MaxCopies) throw new InvalidOperationException($"Ya tienes {MaxCopies} copias");
        int price = PriceOf(id);
        if (p.Esencia < price) throw new InvalidOperationException("No tienes suficiente Esencia");
        var paid = p.Clone();
        paid.Esencia -= price;
        var q = WithCards(paid, [id]);
        q.Diaria!.Vendidas[i] = true;
        return q;
    }

    public static Profile MarkSeen(Profile p, string id)
    {
        if (p.Vistas.Contains(id)) return p;
        var q = p.Clone();
        q.Vistas.Add(id);
        return q;
    }

    /// <summary>Problemas de un mazo (vacío = jugable). cards = colección (null = no se comprueba la posesión).</summary>
    public static List<string> ValidateDeck(DeckDefinition? d, IReadOnlyDictionary<string, int>? cards = null, bool draft = false)
    {
        if (d is null || string.IsNullOrEmpty(d.Id) || d.Name is null || d.Entries is null) return ["Mazo con formato inválido"];
        var output = new List<string>();
        var seen = new HashSet<string>();
        var majors = new HashSet<string>();
        long total = 0;
        foreach (var e in d.Entries)
        {
            var c = e?.Id is null ? null : Card(e.Id);
            if (c is null || c.Token == true || e!.Copies < 1 || !seen.Add(e.Id))
            {
                output.Add("Entrada inválida en el mazo");
                continue;
            }
            total += e.Copies;
            if (e.Copies > MaxCopies) output.Add($"{c.Name}: máximo {MaxCopies} copias");
            if (cards is not null && e.Copies > cards.GetValueOrDefault(e.Id) && !(d.SeparateArcano && draft))
                output.Add($"{c.Name}: solo tienes {cards.GetValueOrDefault(e.Id)}");
            if (c.Arcana is not null) majors.Add(c.Arcana.Key);
        }
        if (!draft && (total < MinDeck || total > MaxDeck)) output.Add($"El mazo debe tener de {MinDeck} a {MaxDeck} cartas (tiene {total})");
        if (d.SeparateArcano)
        {
            if (majors.Count > 0 || (d.Arcano is not null && !IsMajor(d.Arcano)) || (!draft && d.Arcano is null))
                output.Add("El Arcano Mayor debe ocupar su espacio separado");
            if (d.Arcano is { } id && cards is not null && cards.GetValueOrDefault(id) == 0 && !draft)
                output.Add("Todavía no tienes ese Arcano Mayor");
            if (total > MaxDeck) output.Add($"Máximo {MaxDeck} cartas principales");
        }
        else if (majors.Count != 1 || !majors.Contains(d.Id)) output.Add("Debe incluir su Arcano Mayor y ningún otro");
        return output;
    }

    // ---------- Persistencia: sanear lo que llega de un archivo (o de la web) ----------

    public static Profile? Sanitize(string json, string? name = null)
    {
        JsonNode? raw;
        try { raw = JsonNode.Parse(json); } catch { return null; }
        if (raw is not JsonObject o || o["cards"] is not JsonObject rc || o["decks"] is not JsonArray rd) return null;
        name ??= (string?)o["name"] ?? "";
        int N(JsonNode? v)
        {
            try { return v is null ? 0 : Math.Max(0, (int)Math.Floor(v.GetValue<double>())); } catch { return 0; }
        }
        var cards = new Dictionary<string, int>();
        foreach (var c in Cards) { var n = N(rc[c.Id]); if (n > 0) cards[c.Id] = Math.Min(MaxCopies, n); }
        var alts = new Dictionary<string, int>();
        foreach (var id in WithAlt) { var n = N(o["alts"]?[id]); if (n > 0) alts[id] = Math.Min(MaxCopies, n); }
        var decks = new List<DeckDefinition>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dn in rd)
        {
            DeckDefinition? d;
            try { d = dn.Deserialize<DeckDefinition>(Json.Options); } catch { d = null; }
            if (d is not null && d.Copy >= 0 && ValidateDeck(d, cards, true).Count == 0 && keys.Add(d.Key)) decks.Add(DeckWorkshop.Convert(d));
        }
        if (decks.Count == 0) return null;
        var p = new Profile { Name = name, Cards = cards, Alts = alts, Decks = decks };
        try
        {
            var extras = o.Deserialize<Profile>(Json.Options)!;
            p.PlayerId = extras.PlayerId;
            p.RankedKey = extras.RankedKey;
            p.RegisteredAt = extras.RegisteredAt;
            p.AvatarId = extras.AvatarId;
            p.FrameId = extras.FrameId ?? "laton";
            p.TitleId = extras.TitleId ?? "Duelista";
            p.CardBack = extras.CardBack ?? "clasico";
            p.FavoriteCards = (extras.FavoriteCards ?? []).Where(id => cards.GetValueOrDefault(id) > 0).Distinct().Take(3).ToList();
            p.History = (extras.History ?? []).Where(row => row is not null).Take(1000).ToList();
            p.DeckDrafts = (extras.DeckDrafts ?? []).Where(d => d is not null && d.SeparateArcano && ValidateDeck(d, draft: true).Count == 0)
                .DistinctBy(d => d.Key).Take(100).ToList();
            p.Rank = extras.Rank ?? new();
            p.RankConfig = extras.RankConfig is { PointsPerDivision: > 0 } ? extras.RankConfig : new();
            p.ClaimedRewards = extras.ClaimedRewards ?? [];
            p.ReadNotice = extras.ReadNotice;
            p.IsDevAccount = extras.IsDevAccount;
            p.ServerValid = extras.ServerValid;
            p.Historia = (extras.Historia ?? []).Where(kv => kv.Value is 1 or 2).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (JsonException) { }
        var activo = (string?)o["activo"];
        int copy = N(o["activeCopy"]);
        p.SelectDeck(decks.FirstOrDefault(d => d.Id == activo && d.Copy == copy)
            ?? decks.FirstOrDefault(d => d.Id == activo) ?? decks[0]);
        p.UsaAlt = (o["usaAlt"] as JsonArray)?.Select(x => (string?)x).Where(x => x is not null && alts.ContainsKey(x)).Select(x => x!).ToList() ?? [];
        p.Sobres = N(o["sobres"]); p.SobresEpicos = N(o["sobresEpicos"]);
        p.Gemas = (int)Math.Min(int.MaxValue, N(o["gemas"]) + 400L * N(o["sobresArcana"]));
        foreach (var f in Families) { var n = N(o["sobresFam"]?[f.Key]); if (n > 0) p.SobresFam[f.Key] = n; }
        if (o["completedFamilyBoxes"] is JsonArray completed)
            foreach (var item in completed)
                if (item is JsonValue value && value.TryGetValue<string>(out var family) && HasFamilyBox(family))
                    p.CompletedFamilyBoxes.Add(family);
        bool legacyBoxes = N(o["familyBoxFormat"]) < BoxFormat;
        if (o["familyBoxes"] is JsonObject boxes)
            foreach (var (family, rawBox) in boxes)
                if (HasFamilyBox(family) && !p.CompletedFamilyBoxes.Contains(family) && rawBox is JsonArray remaining)
                {
                    var ids = remaining.Select(card => card is JsonValue value && value.TryGetValue<string>(out var id) ? id : "").ToList();
                    if (ReadBox(family, ids, legacyBoxes) is { } box) p.FamilyBoxes[family] = box;
                }
        p.Esencia = N(o["esencia"]); p.Chispas = N(o["chispas"]); p.Vales = N(o["vales"]);
        p.Vistas = o["vistas"] is JsonArray vs ? vs.Select(x => (string?)x).Where(x => x is not null).Select(x => x!).ToList()
            : [.. Cards.Select(c => c.Id), .. WithAlt.Select(id => $"{id}@alt")];
        if (o["acquisitionOrder"] is JsonArray acquisition)
            p.AcquisitionOrder = acquisition.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var id) ? id : "")
                .Where(id => id is { Length: > 0 } && (id.EndsWith("@alt", StringComparison.Ordinal)
                    ? p.Alts.GetValueOrDefault(id[..^4]) > 0 : p.Has(id) > 0))
                .Reverse().Distinct().Reverse().ToList();
        if (o["diaria"] is JsonObject dd && (string?)dd["dia"] is string dia && dd["cartas"] is JsonArray dc && dc.Count == DailyCards &&
            dc.All(x => (string?)x is string id && Card(id) is not null && !IsMajor(id)) && dd["vendidas"] is JsonArray dv)
            p.Diaria = new Daily { Dia = dia, Cartas = dc.Select(x => (string)x!).ToList(), Vendidas = dc.Select((_, i) => i < dv.Count && dv[i]?.GetValueKind() == JsonValueKind.True).ToList() };
        if (o["tutorial"]?.GetValueKind() == JsonValueKind.False) p.Tutorial = false;
        p.Partidas = N(o["partidas"]);
        p.Segundo = o["segundo"]?.GetValueKind() == JsonValueKind.True;
        if (o["retos"] is JsonObject rr)
            foreach (var kv in rr)
                if (System.Text.RegularExpressions.Regex.IsMatch(kv.Key, "^[a-z]+:[a-z]+$")) p.Retos[kv.Key] = N(kv.Value);
        if (o["misiones"] is JsonObject mm && (string?)mm["dia"] is string md && (string?)mm["mazo"] is string mz && Decks.Any(d => d.Id == mz) && mm["prog"] is JsonArray mp)
            p.Misiones = new DailyMissions { Dia = md, Mazo = mz, Prog = Missions(mz).Select((_, i) => i < mp.Count ? N(mp[i]) : 0).ToList() };
        return p;
    }
}
