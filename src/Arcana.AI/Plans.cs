// Planes de juego por mazo (port de strategy/plans.ts): pesos de evaluación y preferencias de cartas.
using Arcana.Core;

namespace Arcana.AI;

public sealed record PlanWeights(
    double OppHp = 1, double SelfHp = 1, double Board = 1, double Atk = 1, double Def = 0.8, double Spell = 0.8,
    double Hand = 1.4, double Reserve = 0.6, double Attack = 0.7, double Threat = 0.9, double Omen = 0.25);

public sealed record DeckPlan(string DeckId, string WinCondition, PlanWeights Weights, IReadOnlyDictionary<string, double> KeyCards, int HpFloor);

public static class Plans
{
    static readonly PlanWeights Base = new();

    public static readonly IReadOnlyDictionary<string, DeckPlan> All = new Dictionary<string, DeckPlan>
    {
        ["fool"] = new("fool", "Tempo agresivo",
            Base with { OppHp = 1.35, SelfHp = 0.85, Atk = 1.25, Def = 0.6, Attack = 1.1, Reserve = 0.8 },
            new Dictionary<string, double> { ["F00"] = 2.5, ["F02"] = 2, ["F04"] = 2, ["F08"] = 1.5, ["F09"] = 1.5, ["F03"] = 1, ["F01"] = 1, ["F07"] = 1 }, 8),
        ["magician"] = new("magician", "Conjuración",
            Base with { Spell = 1.6, Def = 0.9, Reserve = 0.9, Hand = 1.5 },
            new Dictionary<string, double> { ["M05"] = 2.5, ["M03"] = 2.5, ["M04"] = 1.5, ["M08"] = 1.5, ["M06"] = 1, ["M00"] = 1 }, 10),
        ["priestess"] = new("priestess", "Muro y desgaste",
            Base with { SelfHp = 1.3, OppHp = 0.9, Def = 1.4, Atk = 0.7, Threat = 1.3, Attack = 0.5, Hand = 1.6, Omen = 0.4 },
            new Dictionary<string, double> { ["P04"] = 2.5, ["P02"] = 2, ["P00"] = 2, ["P10"] = 1.5, ["P09"] = 1, ["P06"] = 1, ["P08"] = 1 }, 12),
        ["death"] = new("death", "Control por sacrificio",
            Base with { Board = 1.25, Atk = 1.1, Hand = 1.5, Threat = 1 },
            new Dictionary<string, double> { ["D00"] = 3, ["D04"] = 2, ["D03"] = 1.5, ["D01"] = 1, ["D07"] = 1, ["D02"] = 1 }, 10),
        ["devil"] = new("devil", "Carrera de daño",
            Base with { OppHp = 1.6, SelfHp = 0.7, Atk = 1.3, Def = 0.5, Attack = 1.2, Threat = 0.8 },
            new Dictionary<string, double> { ["X00"] = 2.5, ["X03"] = 2, ["X04"] = 2, ["X01"] = 1.5, ["X09"] = 1.5, ["X06"] = 1 }, 9),
        ["lovers"] = new("lovers", "Enlace",
            Base with { Board = 1.3, Def = 1, Hand = 1.3 },
            new Dictionary<string, double> { ["L00"] = 2.5, ["L05"] = 2, ["L06"] = 2, ["L04"] = 1.5, ["L03"] = 1.5, ["L01"] = 1, ["L02"] = 1, ["L08"] = 1 }, 10),
        ["emperor"] = new("emperor", "Jerarquía",
            Base with { Board = 1.3, Def = 1.1, Threat = 1.1 },
            new Dictionary<string, double> { ["E00"] = 3, ["E05"] = 2, ["E04"] = 1.5, ["E03"] = 1.5, ["E02"] = 1.5, ["E10"] = 1, ["E06"] = 1 }, 10),
        ["empress"] = new("empress", "Enjambre",
            Base with { Board = 1.4, Atk = 1.2, Def = 0.6, Attack = 1.1 },
            new Dictionary<string, double> { ["Z00"] = 3, ["Z03"] = 2, ["Z06"] = 2, ["Z05"] = 1.5, ["Z04"] = 1.5, ["Z07"] = 1, ["Z01"] = 1 }, 9),
        ["fortune"] = new("fortune", "Azar",
            Base with { OppHp = 1.3, Attack = 1, Atk = 1.1 },
            new Dictionary<string, double> { ["R00"] = 2.5, ["R05"] = 2, ["R07"] = 2, ["R10"] = 1.5, ["R02"] = 1.5, ["R04"] = 1, ["R01"] = 1 }, 9),
        ["chariot"] = new("chariot", "Carga conjunta",
            Base with { OppHp = 1.3, Board = 1.25, Atk = 1.3, Def = 0.7, Attack = 1.2 },
            new Dictionary<string, double> { ["C00"] = 3, ["C04"] = 2, ["C03"] = 2, ["C08"] = 1.5, ["C05"] = 1.5, ["C01"] = 1, ["C07"] = 1 }, 9),
    };

    static readonly DeckPlan Generic = new("generic", "Plan genérico", Base, new Dictionary<string, double>(), 10);

    public static DeckPlan For(string deckId) => All.TryGetValue(deckId, out var p) ? p : Generic;

    /// <summary>Cuánto quiere `me` tener esta carta pronto. Solo usa información propia.</summary>
    public static double Desirability(CardInstance card, GameState state, string me, GameContext ctx)
    {
        var def = ctx.Card(card.CardId);
        if (def is null) return 0;
        var plan = For(state.Players[me].DeckId);
        var p = state.Players[me];
        var nextEnergy = Rounds.EnergyForRound(state.Round + 1);
        var units = p.Units.ToList();
        var spells = p.SpellSlots.Count(s => s is not null);
        double score = plan.KeyCards.TryGetValue(def.Id, out var k) ? k : 0;
        var affordable = def.Cost <= nextEnergy;
        if (def.Type == "unit")
        {
            score += 5 + (affordable ? 2 + def.Cost * 0.4 : -1.5 * (def.Cost - nextEnergy));
            if (units.Count >= 4) score -= 3;
            if (def.Arcana is not null && units.Any(u => ctx.Card(u.CardId)?.Arcana is not null)) score -= 5;
            if (units.Count <= 1) score += 2;
        }
        else if (def.Type == "spell")
        {
            score += 3 + (affordable ? 1 : -1);
            if (spells >= 4) score -= 6;
        }
        else score += 3 + (def.Cost <= 2 ? 0.5 : 0);
        return score;
    }

    /// <summary>Orden de Presagio: lo más deseado arriba.</summary>
    public static List<string> OmenOrder(GameState state, string me, IReadOnlyList<CardInstance> cards, GameContext ctx) =>
        cards.Select((c, i) => (c, i, d: Desirability(c, state, me, ctx))).OrderByDescending(x => x.d).ThenBy(x => x.i).Select(x => x.c.Iid).ToList();

    /// <summary>«Puedes» de una automática con coste en PV: solo por encima del umbral del plan.</summary>
    public static bool AcceptOptional(GameState state, string me) => state.Players[me].Hp - 1 > For(state.Players[me].DeckId).HpFloor;

    /// <summary>Descarte: la carta menos deseada de la mano.</summary>
    public static string? DiscardChoice(GameState state, string me, GameContext ctx)
    {
        (string iid, double d)? worst = null;
        foreach (var c in state.Players[me].Hand)
        {
            var d = Desirability(c, state, me, ctx);
            if (worst is null || d < worst.Value.d) worst = (c.Iid, d);
        }
        return worst?.iid;
    }

    /// <summary>Mulligan: devuelve las cartas que no ayudan a jugar en las rondas 1–3 (curva 2–4 EN).</summary>
    public static List<string> MulliganChoice(GameState state, string me, GameContext ctx)
    {
        var hand = state.Players[me].Hand;
        var cheapUnits = hand.Count(c => ctx.Card(c.CardId) is { Type: "unit", Cost: <= 3 });
        if (cheapUnits >= 2) return hand.Where(c => (ctx.Card(c.CardId)?.Cost ?? 0) >= 5).Select(c => c.Iid).ToList();
        var keptNonUnits = 0;
        return hand.Where(c =>
        {
            var def = ctx.Card(c.CardId);
            if (def is null) return false;
            if (def.Type == "unit") return def.Cost >= 4;
            // Conserva como mucho un Truco/Hechizo barato; el resto se cambia buscando Unidades.
            if (def.Cost <= 2 && keptNonUnits == 0)
            {
                keptNonUnits++;
                return false;
            }
            return true;
        }).Select(c => c.Iid).ToList();
    }
}

/// <summary>Las decisiones al resolver que toma la IA (Presagio, descarte, «puedes») según su plan.</summary>
public sealed class PlanChoices(GameContext baseCtx) : IPlayerChoices
{
    public IReadOnlyList<string>? OmenOrder(GameState s, string player, IReadOnlyList<CardInstance> cards) => Plans.OmenOrder(s, player, cards, baseCtx);
    public string? Discard(GameState s, string player) => Plans.DiscardChoice(s, player, baseCtx);
    public bool? Accept(GameState s, string player, string cardId) => Plans.AcceptOptional(s, player);
}
