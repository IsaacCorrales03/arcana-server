// Preparación, mulligan, rondas, turnos, energía y victoria (port de state/setup.ts, rounds.ts, victory.ts).
using Arcana.Content;

namespace Arcana.Core;

public static class Rounds
{
    public const int MaxHp = 30, HandSize = 5, UnitSlots = 4, SpellSlots = 4, MaxEnergy = 7;

    /// <summary>Energía por ronda: 2, 3, … 7; tope 7 desde la ronda 6.</summary>
    public static int EnergyForRound(int round) => Math.Min(round + 1, MaxEnergy);

    /// <summary>Escenario del modo historia: pone en juego estas cartas (Unidades en ATQ, Hechizos con su duración) antes
    /// de empezar. Usa una copia del mazo o de la mano si la hay (la mano se rellena), si no crea una nueva.</summary>
    public static GameState Preset(GameState s, string player, IReadOnlyList<string> field, GameContext ctx)
    {
        var p = s.Players[player];
        var deck = p.Deck.ToList();
        var hand = p.Hand.ToList();
        var units = p.UnitSlots.ToArray();
        var spells = p.SpellSlots.ToArray();
        var n = 0;
        foreach (var entry in field)
        {
            var id = entry.Split('/')[0]; // «E04/def»: la Unidad entra en DEF
            if (ctx.Card(id) is not { } def) continue;
            CardInstance card;
            int i = deck.FindIndex(c => c.CardId == id), h = hand.FindIndex(c => c.CardId == id);
            if (i >= 0) { card = deck[i]; deck.RemoveAt(i); }
            else if (h >= 0)
            {
                card = hand[h];
                hand.RemoveAt(h);
                if (deck.Count > 0) { hand.Add(deck[0]); deck.RemoveAt(0); }
            }
            else card = new CardInstance { Iid = def.Token == true ? $"{player}:{id}:tk{n++}" : $"{player}:{id}:e{n++}", CardId = id };
            // «L06/pair»: se acopla sobre la última Unidad colocada (lecciones)
            if (entry.EndsWith("/pair") && Array.FindLastIndex(units, x => x is not null) is var host and >= 0)
            {
                var (pa, pd) = ArcanaRules.PairStats(ctx.Card(units[host]!.CardId)!, def);
                units[host] = units[host]! with { Attached = card, PairAtk = pa, PairDef = pd };
            }
            else if (def.Type == "unit" && Array.IndexOf(units, null) is var u and >= 0)
                units[u] = new UnitInPlay { Iid = card.Iid, CardId = id, Position = def.Token == true || entry.EndsWith("/def") ? "def" : "atk" };
            else if (def.Type == "spell" && def.Duration is { } d && Array.IndexOf(spells, null) is var sp and >= 0)
                spells[sp] = new SpellInPlay { Iid = card.Iid, CardId = id, RemainingDuration = d };
        }
        return s.Update(player, pl => pl with { Deck = deck.ToArray(), Hand = hand.ToArray(), UnitSlots = units, SpellSlots = spells });
    }

    /// <summary>Primer turno de la partida (A en la ronda 1): no roba ni puede ir a la Batalla.</summary>
    public static bool IsOpeningTurn(int round, string? player) => round == 1 && player == P.A;

    static Step Join(Step a, Step b) => new(b.State, [.. a.Events, .. b.Events]);
    static bool IsMain(string phase) => phase is "turnA" or "turnB";

    // ---------- Preparación ----------

    static CardInstance[] ExpandDeck(string owner, DeckDefinition deck) =>
        deck.WithArcano.SelectMany(e => Enumerable.Range(1, e.Copies).Select(n => new CardInstance { Iid = $"{owner}:{e.Id}:{n}", CardId = e.Id })).ToArray();

    public static Result<GameState> Setup(long seed, DeckDefinition deck1, DeckDefinition deck2)
    {
        foreach (var d in new[] { deck1, deck2 })
        {
            var total = d.Entries.Sum(e => e.Copies);
            if (total is < ContentLoader.MinPlayableDeck or > ContentLoader.MaxPlayableDeck)
                return R.Err("INVALID_DECK", $"El mazo «{d.Id}» tiene {total} cartas (de {ContentLoader.MinPlayableDeck} a {ContentLoader.MaxPlayableDeck})");
        }
        var rng = Rng.SeedToState(seed);
        // Sorteo: la persona 1 es A si sale cara. El orden A→B no cambia en toda la partida.
        var (seat1IsA, afterCoin) = Rng.CoinFlip(rng);
        var deckA = seat1IsA ? deck1 : deck2;
        var deckB = seat1IsA ? deck2 : deck1;
        var (shA, r1) = Rng.Shuffle(ExpandDeck(P.A, deckA), afterCoin);
        var (shB, r2) = Rng.Shuffle(ExpandDeck(P.B, deckB), r1);
        PlayerState Build(string id, int seat, string deckId, CardInstance[] sh) => new()
        {
            Id = id, Seat = seat, DeckId = deckId, Hp = MaxHp,
            Deck = sh[HandSize..], Hand = sh[..HandSize],
            UnitSlots = new UnitInPlay?[UnitSlots], SpellSlots = new SpellInPlay?[SpellSlots],
        };
        return Result<GameState>.Success(new GameState
        {
            RngState = r2,
            Players = new Players { A = Build(P.A, seat1IsA ? 1 : 2, deckA.Family, shA), B = Build(P.B, seat1IsA ? 2 : 1, deckB.Family, shB) },
        });
    }

    /// <summary>Mulligan parcial: aparta 0–5 cartas, roba igual cantidad, devuelve las apartadas y baraja. Una vez por jugador.</summary>
    public static Result<GameState> ApplyMulligan(GameState state, string player, IReadOnlyList<string> iids)
    {
        if (state.Phase != "mulligan") return R.Err("WRONG_PHASE", "El mulligan solo se hace en la preparación");
        var p = state.Players[player];
        if (p.MulliganDone) return R.Err("MULLIGAN_ALREADY_DONE", $"El jugador {player} ya hizo su mulligan");
        if (iids.Distinct().Count() != iids.Count) return R.Err("DUPLICATE_CARD", "Carta repetida en el mulligan");
        var set = new List<CardInstance>();
        foreach (var iid in iids)
        {
            var card = p.Hand.FirstOrDefault(c => c.Iid == iid);
            if (card is null) return R.Err("CARD_NOT_IN_HAND", $"La carta {iid} no está en la mano");
            set.Add(card);
        }
        var n = set.Count;
        var keep = p.Hand.Where(c => !iids.Contains(c.Iid));
        var drawn = p.Deck.Take(n);
        CardInstance[] deck = [.. p.Deck.Skip(n), .. set];
        var rng = state.RngState;
        if (n > 0) (deck, rng) = Rng.Shuffle(deck, rng);
        var next = (state with { RngState = rng }).Update(player, pl => pl with { Hand = [.. keep, .. drawn], Deck = deck, MulliganDone = true });
        if (next.Players.A.MulliganDone && next.Players.B.MulliganDone) next = next with { Phase = "roundStart" };
        return Result<GameState>.Success(next);
    }

    // ---------- Victoria ----------

    /// <summary>Comprueba la derrota tras cada resolución individual.</summary>
    public static Step ResolveVictory(Step step)
    {
        var s = step.State;
        if (s.Phase == "gameOver") return step;
        var aDead = s.Players.A.Hp <= 0;
        var bDead = s.Players.B.Hp <= 0;
        if (!aDead && !bDead) return step;
        var winner = aDead && bDead ? "draw" : aDead ? P.B : P.A;
        return new(s with { Phase = "gameOver", ActivePlayer = null, Winner = winner }, [.. step.Events, new Ev { Type = "GAME_OVER", Winner = winner }]);
    }

    // ---------- Rondas y turnos ----------

    /// <summary>Roba 1 carta; con mazo vacío, daño creciente 1, 2, 3… (sin reinicio).</summary>
    public static Step DrawCard(GameState state, string player)
    {
        var p = state.Players[player];
        if (p.Deck.Length > 0)
        {
            var top = p.Deck[0];
            return ResolveVictory(new(state.Update(player, pl => pl with { Deck = pl.Deck[1..], Hand = [.. pl.Hand, top] }),
                [new Ev { Type = "CARD_DRAWN", Player = player, Iid = top.Iid, VisibleTo = player }]));
        }
        var failed = p.FailedDraws + 1;
        // La Sacerdotisa rompe una regla: mientras esté en campo, el daño de su robo fallido lo pierde el rival.
        var victim = ArcanaRules.Has(state, player, "priestess") ? P.Other(player) : player;
        var counted = state.Update(player, pl => pl with { FailedDraws = failed });
        return ResolveVictory(new(counted.Update(victim, pl => pl with { Hp = pl.Hp - failed }),
        [
            new Ev { Type = "DRAW_FAILED", Player = player, Damage = failed },
            new Ev { Type = "HP_CHANGED", Player = victim, Delta = -failed, Cause = "draw_failed" },
        ]));
    }

    static Step BeginTurn(GameState state, string player, bool deferDraw)
    {
        var reset = state;
        // «Una vez por turno» se cuenta por turno de jugador (§7): ambos contadores empiezan de cero.
        foreach (var id in P.Ids)
            reset = reset.Update(id, pl => pl with
            {
                TurnCounters = Counters.Empty,
                UnitSlots = pl.UnitSlots.Select(u => u is null ? null : u with { PositionChangedThisTurn = false }).ToArray(),
                SpellSlots = pl.SpellSlots.Select(s => s is null ? null : s with { UsesThisTurn = 0 }).ToArray(),
            });
        var started = new Step(reset with { Phase = player == P.A ? "turnA" : "turnB", ActivePlayer = player, Initiative = null },
            [new Ev { Type = "TURN_STARTED", Player = player }]);
        return deferDraw || IsOpeningTurn(state.Round, player) ? started : Join(started, DrawCard(started.State, player));
    }

    /// <summary>Inicio de ronda: recarga energía, limpia marcadores «esta ronda» y abre el turno de A.</summary>
    public static Result<Step> StartRound(GameState state, bool deferDraw = false)
    {
        if (state.Phase != "roundStart") return R.Err("WRONG_PHASE", "La ronda solo comienza desde «roundStart»");
        var energy = EnergyForRound(state.Round);
        var s = state;
        foreach (var id in P.Ids)
            s = s.Update(id, pl => pl with
            {
                Energy = energy, MaxEnergy = energy, RoundCounters = Counters.Empty,
                SpellSlots = pl.SpellSlots.Select(sp => sp is null ? null : sp with { UsesThisRound = 0 }).ToArray(),
            });
        s = s with { Initiative = null };
        var start = new Step(s, [new Ev { Type = "ROUND_STARTED", Round = state.Round, Energy = energy, Initiative = null }]);
        return Result<Step>.Success(Join(start, BeginTurn(start.State, P.A, deferDraw)));
    }

    static Step AfterTurn(GameState state, string player, bool deferDraw)
    {
        var events = new List<Ev> { new() { Type = "TURN_ENDED", Player = player } };
        var s = state with { Initiative = null };
        if (!ArcanaRules.Has(s, player, "magician"))
        {
            s = s.Update(player, pl =>
            {
                var expired = new List<CardInstance>();
                var spells = pl.SpellSlots.Select(sp =>
                {
                    if (sp is null || sp.RemainingDuration.Rounds is not int duration) return sp;
                    var left = duration - 1;
                    if (left > 0)
                    {
                        events.Add(new Ev { Type = "SPELL_DURATION_CHANGED", Player = player, Iid = sp.Iid, Remaining = left });
                        return sp with { RemainingDuration = new Duration(left) };
                    }
                    expired.Add(new CardInstance { Iid = sp.Iid, CardId = sp.CardId });
                    events.Add(new Ev { Type = "SPELL_EXPIRED", Player = player, Iid = sp.Iid });
                    return null;
                }).ToArray();
                var units = pl.UnitSlots;
                if (expired.Count > 0)
                {
                    var gone = expired.Select(c => c.Iid).ToHashSet();
                    // Los bonos con límite de ronda o enfrentamiento cumplen su propio plazo.
                    units = units.Select(u => u is null ? null : u with
                    {
                        Modifiers = u.Modifiers.Where(m => m.Duration is "endOfRound" or "combat"
                            || m.SourceIid is null || !gone.Contains(m.SourceIid)).ToArray(),
                    }).ToArray();
                }
                return pl with { UnitSlots = units, SpellSlots = spells, Discard = [.. pl.Discard, .. expired] };
            });
        }
        return Join(new Step(s, events), player == P.A ? BeginTurn(s, P.B, deferDraw) : CloseRound(s));
    }

    /// <summary>Fin del turno sin Batalla: A→turno de B, B→cierre de la ronda.</summary>
    public static Result<Step> EndTurn(GameState state, bool deferDraw = false)
    {
        if (!IsMain(state.Phase)) return R.Err("WRONG_PHASE", "Solo se puede terminar un turno principal");
        return Result<Step>.Success(AfterTurn(state, state.ActivePlayer!, deferDraw));
    }

    static PlayerState ClearAttacks(PlayerState p) =>
        p with { UnitSlots = p.UnitSlots.Select(u => u is null ? null : u with { HasAttackedThisCombat = false }).ToArray() };

    /// <summary>El jugador del turno pasa a su Batalla: solo él declara ataques en ella.</summary>
    public static Result<Step> StartBattle(GameState state)
    {
        if (!IsMain(state.Phase)) return R.Err("WRONG_PHASE", "Solo se va a la Batalla desde el turno principal");
        var player = state.ActivePlayer!;
        if (IsOpeningTurn(state.Round, player)) return R.Err("INVALID_ATTACK", "En el primer turno de la partida no hay Batalla");
        var s = state with
        {
            Phase = "combat", ActivePlayer = player, Initiative = player,
            Players = new Players { A = ClearAttacks(state.Players.A), B = ClearAttacks(state.Players.B) },
        };
        return Result<Step>.Success(new(s, [new Ev { Type = "COMBAT_STARTED", Initiative = player }]));
    }

    /// <summary>Cierre de ronda (§4.5): retira los modificadores que terminan con la ronda.</summary>
    public static Step CloseRound(GameState state)
    {
        var s = state;
        foreach (var id in P.Ids)
            s = s.Update(id, pl => pl with
            {
                UnitSlots = pl.UnitSlots.Select(u => u is null ? null : u with
                {
                    HasAttackedThisCombat = false,
                    Modifiers = u.Modifiers.Where(m => m.Duration is "whileSourceInPlay" or "permanent").ToArray(),
                }).ToArray(),
            });
        return new(s with { Phase = "roundStart", ActivePlayer = null, Initiative = null, Round = state.Round + 1 },
            [new Ev { Type = "ROUND_ENDED", Round = state.Round }]);
    }

    /// <summary>Fin de la Batalla: termina el turno de su atacante (A→turno de B; B→cierre de la ronda).</summary>
    public static Result<Step> EndCombat(GameState state, bool deferDraw = false)
    {
        if (state.Phase != "combat") return R.Err("WRONG_PHASE", "No hay combate en curso");
        var attacker = state.Initiative ?? P.A;
        var s = state with { Players = new Players { A = ClearAttacks(state.Players.A), B = ClearAttacks(state.Players.B) } };
        return Result<Step>.Success(AfterTurn(s, attacker, deferDraw));
    }
}

/// <summary>«Cada Arcano rompe una regla» (§5.1): se mira por ID.</summary>
public static class ArcanaRules
{
    public static readonly Dictionary<string, string> Id = new()
    {
        ["fool"] = "F00", ["magician"] = "M00", ["priestess"] = "P00", ["death"] = "D00", ["devil"] = "X00",
        ["lovers"] = "L00", ["emperor"] = "E00", ["empress"] = "Z00", ["fortune"] = "R00", ["chariot"] = "C00",
    };

    public static bool Has(GameState s, string p, string key) => s.Players[p].UnitSlots.Any(u => u?.CardId == Id[key]);

    /// <summary>Emparejada = tiene una carta acoplada (Los Enamorados cuentan siempre como emparejados).</summary>
    public static bool IsPaired(PlayerState owner, UnitInPlay unit, GameContext? ctx) => unit.CardId == Id["lovers"] || unit.Attached is not null;

    /// <summary>Estadísticas base de una Unidad emparejada. Único sitio de la regla: hoy, el mayor ATQ y la mayor DEF del par.</summary>
    public static (int Atk, int Def) PairStats(CardDefinition a, CardDefinition b) =>
        (Math.Max(a.Atk ?? 0, b.Atk ?? 0), Math.Max(a.Def ?? 0, b.Def ?? 0));

    /// <summary>¿Puede «card» acoplarse sobre «unit»? null = sí; si no, el motivo.</summary>
    public static string? AttachBlock(CardDefinition card, UnitInPlay? unit, GameContext ctx)
    {
        if (card.Partner is null) return "Esta carta no tiene pareja";
        if (unit is null || unit.CardId != card.Partner || ctx.Card(unit.CardId)?.Partner != card.Id) return "Tu pareja no está en el campo";
        if (unit.Attached is not null) return "Esa Unidad ya está emparejada";
        return null;
    }

    /// <summary>ATQ/DEF efectivos: base + modificadores, nunca por debajo de 0 (§5).</summary>
    public static int EffectiveStat(UnitInPlay u, CardDefinition def, string stat, int bonus = 0) =>
        Math.Max(0, ((stat == "atk" ? u.PairAtk : u.PairDef) ?? def.Stat(stat) ?? 0) + bonus + u.Modifiers.Where(m => m.Stat == stat).Sum(m => m.Amount));
}
