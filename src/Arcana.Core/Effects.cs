// Motor de efectos (port de effects/effects.ts): convierte EffectDefinition (datos) en CardBehavior.
using Arcana.Content;

namespace Arcana.Core;

public sealed record Target(string Iid, string Owner);

public static class Effects
{
    /// <summary>Contexto de una ejecución. `Announced` se comparte entre copias (como el arreglo en TS).</summary>
    sealed class Run
    {
        public required GameState State;
        public required Self Self;
        public required GameContext Ctx;
        /// <summary>Objetivos anunciados; se consumen en orden. null = nadie eligió.</summary>
        public List<string>? Announced;
        public IReadOnlyList<Target>? Attack;
        public string? AttackIid;
        public int? EffectLinkId;
        public bool DrawPending;
        /// <summary>Cursor del dado (compartido por las acciones de una misma activación).</summary>
        public Box? Rng;
        public int? Roll;
        /// <summary>Viene de un listener (TS: 'event' in r).</summary>
        public bool FromListener;
        /// <summary>Truco «cuando una Unidad aliada sea destruida»: la Unidad que cae (L10 mira si estaba emparejada).</summary>
        public string? Victim;
        public Run Copy() => (Run)MemberwiseClone();
    }

    sealed class Box(uint s) { public uint S = s; }

    const string OptionalAccept = "$accept";

    /// <summary>Diagnóstico (Arcana.Tools mide cobertura): se llama con el ID de la carta cuyo efecto produjo algo.</summary>
    public static Action<string>? OnEffect;

    static IEnumerable<CardInstance?> FieldCards(GameState s, string who) => s.Players[who].FieldCards;
    static int PrintedCost(Run r, string iid, string who) =>
        FieldCards(r.State, who).FirstOrDefault(c => c?.Iid == iid) is { } c && r.Ctx.Card(c.CardId) is { } d ? d.Cost : int.MaxValue;
    static int CostOf(GameContext? ctx, string cardId) => ctx?.Card(cardId)?.Cost ?? 0;
    static List<UnitInPlay> UnitsOf(GameState s, string who) => s.Players[who].Units.ToList();

    /// <summary>Líder: la Unidad de mayor coste impreso (empate: la de más a la izquierda).</summary>
    static string? LeaderOf(GameState s, string who, GameContext? ctx)
    {
        UnitInPlay? best = null;
        foreach (var u in UnitsOf(s, who)) if (best is null || CostOf(ctx, u.CardId) > CostOf(ctx, best.CardId)) best = u;
        return best?.Iid;
    }

    /// <summary>Jerarquía: 2+ Unidades y, de izquierda a derecha, el coste impreso nunca sube.</summary>
    static bool HierarchyOf(GameState s, string who, GameContext? ctx)
    {
        var cs = UnitsOf(s, who).Select(u => CostOf(ctx, u.CardId)).ToList();
        return cs.Count >= 2 && cs.Select((c, i) => i == 0 || c <= cs[i - 1]).All(x => x);
    }

    static Box Dice(GameState s, string iid) => new(s.RngState ^ Core.Rng.Salt(iid));

    /// <summary>Multiplicador de un bono: Hechizos (M03), descartes (F03), aliadas de menor coste (E02), otras aliadas (Z04), dado (R02).</summary>
    static int PerCount(string? per, GameState state, Self self, GameContext? ctx, int? roll)
    {
        var me = state.Players[self.Owner];
        return per switch
        {
            null => 1,
            "spellInPlay" => me.SpellSlots.Count(x => x is not null),
            "discardedThisRound" => me.RoundCounters.Get("discardedOwnTurn"),
            "lowerCostAlly" => UnitsOf(state, self.Owner).Count(u => u.Iid != self.Iid && CostOf(ctx, u.CardId) < CostOf(ctx, self.CardId)),
            "allyUnit" => UnitsOf(state, self.Owner).Count(u => u.Iid != self.Iid),
            "roll" => roll ?? 0,
            _ => throw new InvalidOperationException($"per «{per}» sin soporte"),
        };
    }

    static bool Holds(Condition c, GameState state, Self self, GameContext? ctx)
    {
        var me = state.Players[self.Owner];
        return c.Kind switch
        {
            "controlsSpell" => me.SpellSlots.Any(x => x is not null),
            "inPosition" => me.UnitSlots.Any(u => u?.Iid == self.Iid && u.Position == c.Position),
            "discardedThisTurn" => me.RoundCounters.Get("discardedOwnTurn") > 0,
            "firstTimeThis" => (c.Per == "turn" ? me.TurnCounters : me.RoundCounters).Get($"effect:{self.Iid}:firstTimeThis") == 0,
            "unitDestroyedThisRound" => me.RoundCounters.Get("unitDestroyed") > 0,
            "handHasOtherCard" => me.Hand.Length > 0,
            "drewThisTurn" => me.RoundCounters.Get("drewOwnTurn") > 0,
            "controlsCard" => FieldCards(state, self.Owner).Any(x => x?.CardId == c.CardId),
            "hpBelowOpponent" => me.Hp < state.Players[P.Other(self.Owner)].Hp,
            "hpAtMost" => me.Hp <= c.Value,
            "hpAbove" => me.Hp > c.Value,
            "roundFlag" => me.RoundCounters.Get($"flag:{self.Iid}:{c.Key}") > 0,
            "paired" => me.Unit(self.Iid) is { } unit && ArcanaRules.IsPaired(me, unit, ctx),
            "hierarchy" => HierarchyOf(state, self.Owner, ctx),
            "isLeader" => LeaderOf(state, self.Owner, ctx) == self.Iid,
            _ => throw new InvalidOperationException($"Condición «{c.Kind}» sin soporte"),
        };
    }

    static bool HoldsAll(EffectDefinition e, Run r) => (e.Conditions ?? []).All(c => Holds(c, r.State, r.Self, r.Ctx));

    /// <summary>Candidatos legales de un objetivo: solo cartas en CAMPO.</summary>
    static List<Target> Candidates(TargetSpec t, Run r)
    {
        if (t.Kind == "self") return [new(r.Self.Iid, r.Self.Owner)];
        if (t.Kind == "attackParticipant")
        {
            var list = (r.Attack ?? []).Where((_, i) => t.Role == "either" || (t.Role == "attacker") == (i == 0));
            bool Owner(Target c) => t.Owner is null or "any" || (t.Owner == "opponent") == (c.Owner != r.Self.Owner);
            bool Pair(Target c) => t.Paired != true || (r.State.Players[c.Owner].Unit(c.Iid) is { } u && ArcanaRules.IsPaired(r.State.Players[c.Owner], u, r.Ctx));
            return list.Where(c => Owner(c) && Pair(c) && (t.CostAtMost is null || PrintedCost(r, c.Iid, c.Owner) <= t.CostAtMost)).ToList();
        }
        if (t.Kind == "contextual") return (r.Attack ?? []).ToList();
        string[] owners = t.Owner == "any" ? [P.A, P.B] : [t.Owner == "opponent" ? P.Other(r.Self.Owner) : r.Self.Owner];
        var unit = t.Kind == "unit";
        var outList = new List<Target>();
        foreach (var owner in owners)
        {
            var player = r.State.Players[owner];
            IEnumerable<CardInstance?> slots = unit ? player.UnitSlots : player.SpellSlots;
            foreach (var c in slots)
            {
                if (c is null) continue;
                if (unit && t.ExcludeSource == true && c.Iid == r.Self.Iid) continue;
                if (unit && t.ExcludeAttackTarget == true && (r.Attack ?? []).Any(a => a.Iid == c.Iid)) continue;
                if (t.CostAtMost is not null && PrintedCost(r, c.Iid, owner) > t.CostAtMost) continue;
                if (t.Paired == true && c is UnitInPlay candidate && !ArcanaRules.IsPaired(player, candidate, r.Ctx)) continue;
                if (unit && t.Leader == true && LeaderOf(r.State, owner, r.Ctx) != c.Iid) continue;
                outList.Add(new(c.Iid, owner));
            }
        }
        return outList;
    }

    public static List<string> LegalEffectTargets(TargetSpec target, GameState state, Self self, GameContext ctx, IReadOnlyList<Target>? attack = null) =>
        Candidates(target, new Run { State = state, Self = self, Ctx = ctx, Attack = attack }).Select(c => c.Iid).ToList();

    static string? Shift(List<string>? list)
    {
        if (list is null || list.Count == 0) return null;
        var v = list[0];
        list.RemoveAt(0);
        return v;
    }

    /// <summary>Un objetivo: el anunciado (si no es legal, el efecto falla); sin anuncio lo elige el jugador o el primero legal.</summary>
    static Target? Pick(TargetSpec t, Run r)
    {
        var cs = Candidates(t, r);
        var want = Shift(r.Announced);
        // desviado por Cambio de Rumbo: vale cualquier Unidad en campo aunque la carta no pudiera elegirla
        if (want is not null && want.EndsWith(CardInstance.Redirected))
        {
            var iid = want[..^CardInstance.Redirected.Length];
            var owner = P.Ids.FirstOrDefault(p => r.State.Players[p].UnitSlots.Any(u => u?.Iid == iid));
            return owner is null ? null : new(iid, owner);
        }
        if (want is not null) return cs.FirstOrDefault(c => c.Iid == want);
        if (r.Announced is null && cs.Count > 1 && t.Kind != "self")
        {
            var chosen = r.Ctx.Choices?.Target(r.State, r.Self.Owner, r.Self.CardId, cs.Select(c => c.Iid).ToList());
            return cs.FirstOrDefault(c => c.Iid == chosen) ?? cs[0];
        }
        return cs.FirstOrDefault();
    }

    static readonly Dictionary<string, string> DurationMap = new()
    {
        ["untilEndOfRound"] = "endOfRound", ["thisCombat"] = "combat", ["whileInPlay"] = "whileSourceInPlay", ["permanent"] = "permanent",
    };

    static string PlayerOf(string? who, Run r) =>
        who == "opponent" ? P.Other(r.Self.Owner) : who == "active" ? r.State.ActivePlayer ?? r.Self.Owner : r.Self.Owner;

    static Ev Destroyed(Target t, Run r) => new()
    {
        Type = "CARD_DESTROYED", Player = t.Owner, Iid = t.Iid, Cause = "effect",
        SourcePlayer = r.Self.Owner, SourceIid = r.Self.Iid, EffectLinkId = r.EffectLinkId,
    };

    /// <summary>Una acción → eventos. [] = falló (sin objetivo legal), así «si lo haces» no se cumple.</summary>
    static List<Ev> Act(EffectAction a, Run r)
    {
        switch (a.Kind)
        {
            case "destroy":
                return Pick(a.Target!, r) is { } dt ? [Destroyed(dt, r)] : [];
            case "destroySpell":
                return Pick(new TargetSpec { Kind = "spell", Owner = "any", CostAtMost = a.CostAtMost }, r) is { } ts ? [Destroyed(ts, r)] : [];
            case "modifyStat":
            {
                if (a.Duration!.Kind == "whileCondition") throw new InvalidOperationException("Duración «whileCondition» no va en acción puntual");
                var duration = DurationMap[a.Duration.Kind];
                var targets = a.Target!.Kind == "unit" && a.Target.All == true
                    ? Candidates(a.Target, r)
                    : Pick(a.Target, r) is { } one ? [one] : [];
                var amount = a.Amount * PerCount(a.Per, r.State, r.Self, r.Ctx, r.Roll);
                if (amount == 0) return [];
                return targets.Select(t2 => new Ev { Type = "STAT_MODIFIED", Player = t2.Owner, Iid = t2.Iid, Stat = a.Stat, Amount = amount, Duration = duration, SourceIid = r.Self.Iid }).ToList();
            }
            case "redirectAttack":
            {
                var from = r.Attack?.FirstOrDefault()?.Iid;
                var t = Pick(a.Target!, r);
                return t is not null && from is not null && r.EffectLinkId is not null
                    ? [new Ev { Type = "ATTACK_REDIRECTED", Player = r.Self.Owner, LinkId = r.EffectLinkId, FromIid = from, ToIid = t.Iid }]
                    : [];
            }
            case "setRoundFlag":
                return [new Ev { Type = "COUNTER_SET", Player = r.Self.Owner, Key = $"flag:{r.Self.Iid}:{a.Key}", Value = 1, Per = "round" }];
            case "changePosition":
            {
                var t = Pick(a.Target!, r);
                var u = t is null ? null : r.State.Players[t.Owner].Unit(t.Iid);
                return t is not null && u is not null ? [new Ev { Type = "POSITION_CHANGED", Player = t.Owner, Iid = t.Iid, Position = u.Position == "atk" ? "def" : "atk" }] : [];
            }
            case "draw":
            {
                if (a.Count < 0) throw new InvalidOperationException($"Robo inválido: {a.Count}");
                var player = PlayerOf(a.Player, r);
                // per «victimPaired»: solo si la Unidad que cae estaba emparejada (L10)
                var count = a.Per != "victimPaired" ? a.Count
                    : r.Victim is { } v && r.State.Players[r.Self.Owner].Unit(v) is { } fallen && ArcanaRules.IsPaired(r.State.Players[r.Self.Owner], fallen, r.Ctx) ? a.Count : 0;
                return count > 0 ? [new Ev { Type = "CARD_DRAW_REQUESTED", Player = player, Remaining = count }] : [];
            }
            case "discard":
            {
                if (a.Count < 0) throw new InvalidOperationException($"Descarte inválido: {a.Count}");
                var player = PlayerOf(a.Player, r);
                var hand = r.State.Players[player].Hand;
                var events = new List<Ev>();
                for (var i = 0; i < a.Count; i++)
                {
                    var selected = Shift(r.Announced);
                    if (selected is not null && !hand.Any(c => c.Iid == selected)) return [];
                    if (selected is null && i >= hand.Length && !r.DrawPending) return [];
                    // Sin elección anunciada, la carta se elige al resolver (ctx.choices.discard).
                    events.Add(selected is not null
                        ? new Ev { Type = "CARD_DISCARD_REQUESTED", Player = player, Iid = selected }
                        : new Ev { Type = "CARD_DISCARD_REQUESTED", Player = player });
                }
                return events;
            }
            case "roll":
            {
                if (a.Sides < 2) throw new InvalidOperationException($"Dado inválido: {a.Sides}");
                var box = r.Rng ?? Dice(r.State, r.Self.Iid);
                int Throw()
                {
                    var (f, next) = Core.Rng.NextFloat(box.S);
                    box.S = next;
                    return 1 + (int)Math.Floor(f * a.Sides);
                }
                var value = Throw();
                // La Rueda rompe una regla: mientras esté en campo, un 1 de su dueño se vuelve a lanzar (una vez).
                if (value == 1 && ArcanaRules.Has(r.State, r.Self.Owner, "fortune")) value = Throw();
                var outcome = (a.Outcomes ?? []).OrderByDescending(o => o.Min).FirstOrDefault(o => o.Min <= value);
                var sub = r.Copy();
                sub.Rng = box;
                sub.Roll = value;
                return [
                    new Ev { Type = "DICE_ROLLED", Player = r.Self.Owner, SourceIid = r.Self.Iid, Sides = a.Sides, Value = value },
                    .. (outcome?.Actions ?? []).SelectMany(x => Act(x, sub)),
                ];
            }
            case "summonToken":
            {
                if (a.Count < 1 || r.Ctx.Card(a.CardId!)?.Token != true) throw new InvalidOperationException($"Ficha inválida: {a.CardId}");
                var bas = r.State.Players[r.Self.Owner].RoundCounters.Get("tokens");
                var tag = Core.Rng.ToBase36(Core.Rng.Salt(r.Self.Iid));
                return Enumerable.Range(0, a.Count).Select(k => new Ev
                {
                    Type = "TOKEN_CREATED", Player = r.Self.Owner, Iid = $"{r.Self.Owner}:{a.CardId}:tk{r.State.Round}.{bas + k}.{tag}",
                    CardId = a.CardId, Position = a.Position ?? "def", SourceIid = r.Self.Iid, // el enjambre entra protegiendo
                }).ToList();
            }
            case "buffHand":
            {
                var iids = r.State.Players[r.Self.Owner].Hand
                    .Where(c => r.Ctx.Card(c.CardId) is { Type: "unit" } d && d.Cost <= a.CostAtMost).Select(c => c.Iid).ToArray();
                return iids.Length > 0 ? [new Ev { Type = "HAND_BUFFED", Player = r.Self.Owner, Iids = iids, Stat = a.Stat, Amount = a.Amount }] : [];
            }
            case "sortUnits":
            {
                var slots = r.State.Players[r.Self.Owner].UnitSlots;
                // OrderBy es estable: a igual coste se conserva el orden (x.i - y.i en TS)
                var sorted = UnitsOf(r.State, r.Self.Owner).OrderByDescending(u => CostOf(r.Ctx, u.CardId)).Select(u => u.Iid).ToList();
                var order = slots.Select((_, i) => i < sorted.Count ? sorted[i] : null).ToArray();
                return order.Select((x, i) => x == slots[i]?.Iid).All(b => b) ? [] : [new Ev { Type = "UNITS_REORDERED", Player = r.Self.Owner, Order = order }];
            }
            case "heal":
            case "damagePlayer":
            {
                var who = (a.Player ?? "self") == "opponent" ? P.Other(r.Self.Owner) : r.Self.Owner;
                var amount = a.Amount * PerCount(a.Per, r.State, r.Self, r.Ctx, r.Roll);
                if (amount == 0) return [];
                if (a.Kind == "damagePlayer" && a.Timing == "afterCombat")
                {
                    if (r.AttackIid is null) throw new InvalidOperationException("damagePlayer afterCombat requiere un ataque asociado");
                    return [new Ev { Type = "DAMAGE_AFTER_ATTACK", Player = who, Amount = amount, AttackerIid = r.AttackIid, SourcePlayer = r.Self.Owner }];
                }
                return [new Ev { Type = "HP_CHANGED", Player = who, Delta = a.Kind == "heal" ? amount : -amount, Cause = "effect", SourcePlayer = r.Self.Owner }];
            }
            case "preventDestruction":
            {
                var t = Pick(a.Target!, r);
                if (t is null) return [];
                if (a.Scope == "combat" && r.AttackIid is not null)
                    return [new Ev { Type = "COMBAT_DESTRUCTION_PREVENTED", Player = t.Owner, Iid = t.Iid, AttackerIid = r.AttackIid }];
                if (a.Scope == "thatEffect" && r.EffectLinkId is not null)
                    return [new Ev { Type = "EFFECT_DESTRUCTION_PREVENTED", Player = t.Owner, Iid = t.Iid, EffectLinkId = r.EffectLinkId }];
                throw new InvalidOperationException($"preventDestruction «{a.Scope}» requiere su efecto asociado");
            }
            case "negate":
            {
                var link = r.EffectLinkId is null ? null : r.State.Chain?.Stack.FirstOrDefault(c => c.Id == r.EffectLinkId);
                return link is not null ? [new Ev { Type = "LINK_NEGATED", Player = link.Player, LinkId = link.Id, ByPlayer = r.Self.Owner }] : [];
            }
            case "reduceNextSpellCost":
                if (a.Amount < 0 || a.Min < 1) throw new InvalidOperationException("Descuento de Hechizo inválido");
                return [new Ev { Type = "COUNTER_SET", Player = r.Self.Owner, Key = "nextSpellDiscount", Value = a.Amount, Per = "round" }];
            case "payHp":
                if (a.Amount < 0) throw new InvalidOperationException($"Pago de PV inválido: {a.Amount}");
                return [new Ev { Type = "HP_PAID_AS_ABILITY_COST", Player = r.Self.Owner, Amount = a.Amount, SourceIid = r.Self.Iid }];
            case "omen":
            {
                if (a.Count < 0) throw new InvalidOperationException($"Presagio inválido: {a.Count}");
                if (a.Count == 0) return [];
                var cards = r.State.Players[r.Self.Owner].Deck.Take(a.Count).ToArray();
                var selected = r.Announced?.Take(cards.Length).ToList() ?? [];
                var validSelection = selected.Count == cards.Length && selected.Distinct().Count() == cards.Length && cards.All(c => selected.Contains(c.Iid));
                bool IsOrder(IReadOnlyList<string>? o) => o is not null && o.Count == cards.Length && cards.All(c => o.Contains(c.Iid));
                var chosen = validSelection ? null : r.Ctx.Choices?.OmenOrder(r.State, r.Self.Owner, cards);
                IReadOnlyList<string> order = validSelection ? selected : IsOrder(chosen) ? chosen! : cards.Select(c => c.Iid).ToList();
                if (validSelection) r.Announced?.RemoveRange(0, cards.Length); // mazo vacío: selección vacía «válida» y sin anuncio
                var byIid = cards.ToDictionary(c => c.Iid);
                return [new Ev { Type = "OMEN_ACTIVATED", Player = r.Self.Owner, N = a.Count, Cards = order.Select(i => byIid[i]).ToArray(), VisibleTo = r.Self.Owner }];
            }
            case "peekOpponentDeck":
            {
                if (a.Count < 0) throw new InvalidOperationException($"Mirada de mazo inválida: {a.Count}");
                if (a.Count == 0) return [];
                return [new Ev
                {
                    Type = "OPPONENT_DECK_PEEKED", Player = r.Self.Owner, N = a.Count,
                    Cards = r.State.Players[P.Other(r.Self.Owner)].Deck.Take(a.Count).ToArray(), VisibleTo = r.Self.Owner,
                }];
            }
            default:
                throw new InvalidOperationException($"Acción declarativa no soportada: {a.Kind}");
        }
    }

    static List<Ev> GroupSimultaneousHpChanges(List<Ev> events)
    {
        var changes = events.Where(e => e.Type == "HP_CHANGED").ToArray();
        if (!changes.Any(e => e.Player == P.A) || !changes.Any(e => e.Player == P.B)) return events;
        var first = events.FindIndex(e => e.Type == "HP_CHANGED");
        var output = new List<Ev>();
        for (var i = 0; i < events.Count; i++)
        {
            if (i == first) output.Add(new Ev { Type = "HP_CHANGED_SIMULTANEOUSLY", Changes = changes });
            else if (events[i].Type != "HP_CHANGED") output.Add(events[i]);
        }
        return output;
    }

    /// <summary>Acciones en orden; «si lo haces»: thenIfDone solo si la primera parte produjo algo.</summary>
    static List<Ev> RunEffect(EffectDefinition e, Run r)
    {
        if (e.Optional == true)
        {
            // Una automática con «puedes» (P05) no tiene comando que anuncie la elección: decide ctx.choices.accept.
            if (r.FromListener && r.Announced is null)
            {
                if (!HoldsAll(e, r)) return [];
                if (!(r.Ctx.Choices?.Accept(r.State, r.Self.Owner, r.Self.CardId) ?? true)) return [];
            }
            else
            {
                var accept = r.Announced?.IndexOf(OptionalAccept) ?? -1;
                if (accept >= 0) r.Announced!.RemoveAt(accept);
                else if (r.Announced is not { Count: > 0 }) return [];
            }
        }
        if (!HoldsAll(e, r)) return [];
        var capKey = $"effect:{r.Self.Iid}:cap";
        var capRemaining = e.Cap is null ? int.MaxValue : e.Cap.Amount - r.State.Players[r.Self.Owner].RoundCounters.Get(capKey);
        if (capRemaining <= 0) return [];
        var first = new List<Ev>();
        var drawPending = false;
        r = r.Copy();
        r.Rng ??= Dice(r.State, r.Self.Iid); // un cursor de dado por activación
        foreach (var action in e.Actions)
        {
            var limited = e.Cap is not null && action.Kind == "modifyStat" ? action.With(Math.Min(action.Amount, capRemaining)) : action;
            var rr = r.Copy();
            rr.DrawPending = drawPending;
            first.AddRange(Act(limited, rr));
            if (action.Kind == "draw" && action.Count > 0) drawPending = true;
        }
        if (first.Count == 0) return [];
        var rest = new List<Ev>();
        foreach (var a in e.ThenIfDone ?? [])
        {
            var rr = r.Copy();
            rr.DrawPending = drawPending;
            rest.AddRange(Act(a, rr));
        }
        var marks = (e.Conditions ?? []).Where(c => c.Kind == "firstTimeThis").Select(c => c.Per).Distinct()
            .Select(per => new Ev { Type = "COUNTER_INCREMENTED", Player = r.Self.Owner, Key = $"effect:{r.Self.Iid}:firstTimeThis", Per = per });
        List<Ev> capMark = e.Cap is not null ? [new Ev { Type = "COUNTER_INCREMENTED", Player = r.Self.Owner, Key = capKey, Per = "round" }] : [];
        OnEffect?.Invoke(r.Self.CardId);
        return GroupSimultaneousHpChanges([.. first, .. rest, .. marks, .. capMark]);
    }

    /// <summary>Bono continuo (trigger static) sobre una Unidad: se calcula al consultar, nunca se guarda.</summary>
    public static int StaticBonus(GameState state, string unitIid, string stat, GameContext ctx)
    {
        var sum = 0;
        foreach (var owner in P.Ids)
            foreach (var (srcIid, srcCard) in FieldCards(state, owner).OfType<CardInstance>()
                .SelectMany(c => (c as UnitInPlay)?.Attached is { } att ? new[] { (c.Iid, c.CardId), (c.Iid, att.CardId) } : [(c.Iid, c.CardId)]))
            {
                var src = new CardInstance { Iid = srcIid, CardId = srcCard }; // la carta acoplada aporta sus pasivos a la Unidad
                foreach (var e in ctx.Card(src.CardId)?.Effects ?? [])
                {
                    if (e.Trigger.Kind != "static") continue;
                    var self = new Self(src.Iid, src.CardId, owner);
                    if (!(e.Conditions ?? []).All(c => Holds(c, state, self, ctx))) continue;
                    foreach (var a in e.Actions)
                    {
                        if (a.Kind != "modifyStat" || a.Stat != stat) continue;
                        var t = a.Target!;
                        var hit = t.Kind == "self" ? src.Iid == unitIid
                            : t.Kind == "unit" && Candidates(t, new Run { State = state, Self = self, Ctx = ctx }).Any(c => c.Iid == unitIid);
                        if (!hit) continue;
                        var add = a.Amount * PerCount(a.Per, state, self, ctx, null);
                        if (add != 0) OnEffect?.Invoke(src.CardId);
                        sum += add;
                    }
                }
            }
        return sum;
    }

    static bool MatchesBy(string? by, string owner, string player) => by is null or "any" || (by == "self") == (owner == player);

    static Run FromCall(ListenerCall c) => new() { State = c.State, Self = c.Self, Ctx = c.Ctx, FromListener = true };

    static Listener? Listen(EffectDefinition e)
    {
        var tr = e.Trigger;
        Reaction Emit(Run r) => new(RunEffect(e, r));
        switch (tr.Kind)
        {
            // P00: «cuando recuperes PV por el efecto de una carta».
            case "onHealed":
                return new("HP_CHANGED", c => Emit(FromCall(c)), c => c.Event.Player == c.Self.Owner && c.Event.Delta > 0 && c.Event.Cause == "effect");
            // F02: «cada vez que robes una carta».
            case "onDraw":
                return new("CARD_DRAWN", c => Emit(FromCall(c)), c => c.Event.Player == c.Self.Owner);
            case "onDestroyed":
                return new("CARD_DESTROYED", c => { var r = FromCall(c); r.EffectLinkId = c.Event.EffectLinkId; return Emit(r); }, c =>
                    c.Event.Iid == c.Self.Iid &&
                    (tr.Cause is null or "any" || c.Event.Cause == tr.Cause) &&
                    MatchesBy(tr.By, c.Self.Owner, c.Event.SourcePlayer ?? c.Event.Player!));
            // «Cada vez que saques N o más en un dado» (R10).
            case "onRoll":
                return new("DICE_ROLLED", c => Emit(FromCall(c)), c => c.Event.Player == c.Self.Owner && c.Event.Value >= (tr.AtLeast ?? 1));
            case "onAllyDestroyed":
                return new("CARD_DESTROYED", c => { var r = FromCall(c); r.EffectLinkId = c.Event.EffectLinkId; return Emit(r); }, c =>
                    c.Event.Player == c.Self.Owner &&
                    // la destruida sigue en campo mientras se escucha el evento: se lee su coste impreso (Z00)
                    (tr.CostAtMost is null || CostOf(c.Ctx, c.State.Players[c.Event.Player!].Unit(c.Event.Iid!)?.CardId ?? "") <= tr.CostAtMost) &&
                    !(tr.Other == true && c.Event.Iid == c.Self.Iid) &&
                    (tr.Cause is null or "any" || c.Event.Cause == tr.Cause) &&
                    MatchesBy(tr.By, c.Self.Owner, c.Event.SourcePlayer ?? c.Event.Player!));
            case "onAllyPaired":
                return new("UNIT_PAIRED", c => Emit(FromCall(c)), c => c.Event.Player == c.Self.Owner);
            case "onAttackDeclared":
                return new("ATTACK_DECLARED", c =>
                {
                    var foe = P.Other(c.Event.Player!);
                    var r = FromCall(c);
                    r.Attack = [new(c.Event.AttackerIid!, c.Event.Player!), .. c.Event.TargetIid is null ? Array.Empty<Target>() : [new(c.Event.TargetIid, foe)]];
                    return Emit(r);
                }, c =>
                {
                    var by = tr.By ?? "any";
                    bool mine = c.Event.Player == c.Self.Owner;
                    // «otherAlly»: una Unidad aliada distinta de la propia carta (el Escudero de Carga no se bufa a sí mismo)
                    return by == "any" || (by == "otherAlly" ? mine && c.Event.AttackerIid != c.Self.Iid : (by == "ally") == mine);
                });
            case "onSpellPlayed":
                return new("SPELL_PLAYED", c => { var r = FromCall(c); r.EffectLinkId = c.Event.LinkId; return Emit(r); }, c =>
                    tr.By == "self" ? c.Event.Player == c.Self.Owner : tr.By == "opponent" ? c.Event.Player != c.Self.Owner : true);
            case "onTurnStart":
                return new("TURN_STARTED", c => Emit(FromCall(c)), c =>
                    tr.Whose == "self" ? c.Event.Player == c.Self.Owner
                    : tr.Whose == "opponent" ? c.Event.Player != c.Self.Owner
                    : tr.Whose == "active" && c.Event.Player == c.State.ActivePlayer);
            case "onActiveAbilityActivated":
                return new("ABILITY_ACTIVATED", c => { var r = FromCall(c); r.EffectLinkId = c.Event.LinkId; return Emit(r); }, c =>
                    c.Event.Trigger == "activate" &&
                    (tr.By == "active" ? c.Event.Player == c.State.ActivePlayer : MatchesBy(tr.By, c.Self.Owner, c.Event.Player!)));
            case "onDiscard":
                return new("CARD_DISCARDED", c => Emit(FromCall(c)), c =>
                    c.Event.Player == c.Self.Owner && (tr.AsAbilityCost is null || tr.AsAbilityCost == c.Event.AsAbilityCost));
            case "onHpPaidAsAbilityCost":
                return new("HP_PAID_AS_ABILITY_COST", c => Emit(FromCall(c)), c => MatchesBy(tr.By, c.Self.Owner, c.Event.Player!));
            // «Evitar destrucción»: consume el CARD_DESTROYED pendiente; la carta sigue en campo.
            case "onAllyAboutToBeDestroyed":
                return new("CARD_DESTROYED", c =>
                {
                    if (!(e.Conditions ?? []).All(k => Holds(k, c.State, c.Self, c.Ctx))) return null;
                    var prevention = e.Actions.FirstOrDefault(a => a.Kind == "preventDestruction");
                    if (prevention?.OncePer == "round")
                    {
                        var key = $"effect:{c.Self.Iid}:prevented";
                        if (c.State.Players[c.Self.Owner].RoundCounters.Get(key) > 0) return null;
                        OnEffect?.Invoke(c.Self.CardId);
                        return new Reaction([new Ev { Type = "COUNTER_INCREMENTED", Player = c.Self.Owner, Key = key, Per = "round" }], true);
                    }
                    OnEffect?.Invoke(c.Self.CardId);
                    return new Reaction(null, true);
                }, c =>
                {
                    if (c.Event.Player != c.Self.Owner || (tr.Cause != "any" && tr.Cause != c.Event.Cause)) return false;
                    if (tr.Position is not null)
                    {
                        var unit = c.State.Players[c.Event.Player!].Unit(c.Event.Iid!);
                        if (unit is null || unit.Position != tr.Position) return false;
                    }
                    return true;
                });
            default:
                return null;
        }
    }

    static (ChainLink link, Ev clash)? AttackOf(TrickCall call)
    {
        var link = call.Below.LastOrDefault(l => l.Kind == "attack");
        var clash = link?.Events.FirstOrDefault(e => e.Type == "ATTACK_CLASH");
        return clash is null ? null : (link!, clash);
    }

    static List<Target> Participants(Ev clash) =>
        [new(clash.AttackerIid!, clash.Player!), .. clash.TargetIid is null ? Array.Empty<Target>() : [new(clash.TargetIid, P.Other(clash.Player!))]];

    public static List<Target> CombatDestructions(GameState state, Ev clash, GameContext ctx)
    {
        var attacker = state.Players[clash.Player!].Unit(clash.AttackerIid!);
        if (attacker is null || clash.TargetIid is null) return [];
        var opponent = P.Other(clash.Player!);
        var target = state.Players[opponent].Unit(clash.TargetIid);
        if (target is null) return [];
        int Stat(UnitInPlay u, string k) => ctx.Card(u.CardId) is { } d ? ArcanaRules.EffectiveStat(u, d, k, StaticBonus(state, u.Iid, k, ctx)) : 0;
        var attack = Stat(attacker, "atk");
        var defense = Stat(target, target.Position == "atk" ? "atk" : "def");
        if (attack > defense) return [new(target.Iid, opponent)];
        if (attack == defense && target.Position == "atk") return [new(target.Iid, opponent), new(attacker.Iid, clash.Player!)];
        return [new(attacker.Iid, clash.Player!)];
    }

    /// <summary>Unidades de `owner` que un eslabón de la pila destruiría. `ownEffect`: solo efectos propios (X09).</summary>
    public static List<(int linkId, string iid)> AllyDestructionThreats(GameState state, string owner, IReadOnlyList<ChainLink> stack, GameContext ctx, bool ownEffect)
    {
        bool OnField(string iid) => state.Players[owner].UnitSlots.Any(u => u?.Iid == iid);
        var list = new List<(int, string)>();
        foreach (var link in stack.Reverse())
        {
            if (link.Kind == "attack")
            {
                if (ownEffect) continue;
                var clash = link.Events.FirstOrDefault(e => e.Type == "ATTACK_CLASH");
                if (clash is not null)
                    list.AddRange(CombatDestructions(state, clash, ctx).Where(u => u.Owner == owner).Select(u => (link.Id, u.Iid)));
                continue;
            }
            if (ownEffect && link.Player != owner) continue;
            list.AddRange(link.Targets.Where(OnField).Select(iid => (link.Id, iid)));
        }
        return list;
    }

    static bool OnFieldAny(GameState state, string iid) => P.Ids.Any(p => state.Players[p].UnitSlots.Any(u => u?.Iid == iid));

    /// <summary>Eslabón (no ataque) más reciente cuyo efecto apunta a la Unidad iid.</summary>
    public static ChainLink? EffectLinkTargeting(GameState state, IReadOnlyList<ChainLink> stack, string iid) =>
        !OnFieldAny(state, iid) ? null : stack.LastOrDefault(l => l.Kind != "attack" && l.Targets.Contains(iid));

    /// <summary>F05: un efecto se puede desviar a cualquier otra Unidad en campo, salvo la que ya apunta.</summary>
    public static List<string> RedirectCandidates(GameState state, string from) =>
        P.Ids.SelectMany(p => state.Players[p].Units.Where(u => u.Iid != from).Select(u => u.Iid)).ToList();

    static TrickBehavior CompileTrick(EffectDefinition effect)
    {
        var trigger = effect.Trigger;
        switch (trigger.Kind)
        {
            case "onAttackDeclared":
                return new TrickBehavior
                {
                    When = call =>
                    {
                        if (AttackOf(call) is not { } at) return false;
                        var clash = at.clash;
                        var by = trigger.By ?? "any";
                        var run = new Run { State = call.State, Self = call.Self, Ctx = call.Ctx, Attack = Participants(clash) };
                        // un Truco que no haría nada no se puede jugar (M09 o D07 ante un ataque directo)
                        var inutil = effect.Actions.All(x => x.Target is not null && Candidates(x.Target, run).Count == 0);
                        // un objetivo anunciado ilegal (D07 sobre la Unidad propia) no se acepta
                        var first = effect.Actions.FirstOrDefault();
                        var ilegal = call.Targets.Count > 0 && first?.Target is not null && !Candidates(first.Target, run).Any(c => c.Iid == call.Targets[0]);
                        // condiciones impresas (E08 «si hay Jerarquía»)
                        var cumple = HoldsAll(effect, run);
                        return !inutil && !ilegal && cumple &&
                            (by == "any" || (by == "ally") == (clash.Player == call.Self.Owner)) &&
                            call.State.Players[clash.Player!].UnitSlots.Any(u => u?.Iid == clash.AttackerIid);
                    },
                    Run = call =>
                    {
                        if (AttackOf(call) is not { } at) return [];
                        var (link, clash) = at;
                        return RunEffect(effect, new Run
                        {
                            State = call.State, Self = call.Self, Ctx = call.Ctx, Announced = [.. call.Targets],
                            Attack = Participants(clash), AttackIid = clash.AttackerIid, EffectLinkId = link.Id,
                        });
                    },
                };
            case "onAllyTargetedByAttack":
            {
                // targets = [nuevo objetivo]: el origen se deduce del eslabón más reciente que pueda desviarse ahí.
                bool Own(TrickCall call, string iid) => call.State.Players[call.Self.Owner].UnitSlots.Any(u => u?.Iid == iid);
                (ChainLink link, string from, bool attack)? Redirect(TrickCall call)
                {
                    var to = call.Targets.Count > 0 ? call.Targets[0] : null;
                    foreach (var link in call.Below.Reverse())
                    {
                        if (link.Kind == "attack")
                        {
                            var clash = link.Events.FirstOrDefault(e => e.Type == "ATTACK_CLASH");
                            if (clash is null) continue;
                            var from = clash.TargetIid;
                            if (from is not null && clash.Player != call.Self.Owner && Own(call, from) &&
                                (to is null ? call.State.Players[call.Self.Owner].UnitSlots.Any(u => u is not null && u.Iid != from) : to != from && Own(call, to)))
                                return (link, from, true);
                            continue;
                        }
                        // con varios objetivos (La Muerte: su sacrificio y tu Unidad) se desvía primero el que es tuyo
                        var froms = link.Targets.Where(t => EffectLinkTargeting(call.State, [link], t) is not null)
                            .OrderBy(x => Own(call, x) ? 0 : 1).ToList();
                        foreach (var from in froms)
                        {
                            var cs = RedirectCandidates(call.State, from).Where(u => !link.Targets.Contains(u)).ToList();
                            if (to is null ? cs.Count > 0 : cs.Contains(to)) return (link, from, false);
                        }
                    }
                    return null;
                }
                return new TrickBehavior
                {
                    When = call => Redirect(call) is not null,
                    Run = call =>
                    {
                        if (Redirect(call) is not { } rd) return [];
                        var (link, from, attack) = rd;
                        if (!attack)
                        {
                            var to = call.Targets.Count > 0 ? call.Targets[0] : RedirectCandidates(call.State, from).First(u => !link.Targets.Contains(u));
                            OnEffect?.Invoke(call.Self.CardId);
                            return [new Ev { Type = "EFFECT_REDIRECTED", Player = call.Self.Owner, LinkId = link.Id, FromIid = from, ToIid = to }];
                        }
                        return RunEffect(effect, new Run
                        {
                            State = call.State, Self = call.Self, Ctx = call.Ctx, Announced = [.. call.Targets],
                            Attack = [new(from, call.Self.Owner)], EffectLinkId = link.Id,
                        });
                    },
                };
            }
            case "onAllyAboutToBeDestroyed":
                return new TrickBehavior
                {
                    When = call =>
                    {
                        if (AttackOf(call) is not { } at || trigger.Cause == "effect") return false;
                        var clash = at.clash;
                        var threatened = CombatDestructions(call.State, clash, call.Ctx).Where(u => u.Owner == call.Self.Owner);
                        return threatened.Any(u => call.Targets.Count == 0 || call.Targets[0] == u.Iid);
                    },
                    Run = call =>
                    {
                        if (AttackOf(call) is not { } at) return [];
                        var (link, clash) = at;
                        var threatened = CombatDestructions(call.State, clash, call.Ctx).Where(u => u.Owner == call.Self.Owner).ToList();
                        var selected = call.Targets.Count > 0 ? call.Targets[0] : null;
                        var target = selected is not null ? threatened.FirstOrDefault(u => u.Iid == selected) : threatened.FirstOrDefault();
                        return target is null ? [] : RunEffect(effect, new Run
                        {
                            State = call.State, Self = call.Self, Ctx = call.Ctx, Announced = selected is not null ? [selected] : [],
                            AttackIid = clash.AttackerIid, Attack = [target], EffectLinkId = link.Id,
                        });
                    },
                };
            case "onSpellPlayed":
            {
                bool Matches(TrickCall call, ChainLink link)
                {
                    var by = trigger.By ?? "active";
                    return link.Kind == "spell" && link.Events.Any(e => e.Type == "SPELL_PLAYED") &&
                        (by == "self" ? link.Player == call.Self.Owner
                        : by == "opponent" ? link.Player != call.Self.Owner
                        : by != "active" || link.Player == call.State.ActivePlayer);
                }
                return new TrickBehavior
                {
                    When = call => call.Below.Any(l => Matches(call, l)),
                    Run = call => call.Below.LastOrDefault(l => Matches(call, l)) is { } link
                        ? RunEffect(effect, new Run { State = call.State, Self = call.Self, Ctx = call.Ctx, Announced = [.. call.Targets], EffectLinkId = link.Id })
                        : [],
                };
            }
            case "onActiveAbilityActivated":
            {
                bool Matches(TrickCall call, ChainLink link)
                {
                    var activation = link.Events.FirstOrDefault(e => e.Type == "ABILITY_ACTIVATED");
                    var by = trigger.By ?? "any";
                    return link.Kind == "ability" && activation is not null && activation.Trigger == "activate" &&
                        (by == "active" ? link.Player == call.State.ActivePlayer : MatchesBy(by, call.Self.Owner, link.Player));
                }
                return new TrickBehavior
                {
                    When = call => call.Below.Any(l => Matches(call, l)),
                    Run = call => call.Below.LastOrDefault(l => Matches(call, l)) is { } link
                        ? RunEffect(effect, new Run { State = call.State, Self = call.Self, Ctx = call.Ctx, Announced = [.. call.Targets], EffectLinkId = link.Id })
                        : [],
                };
            }
            case "onAllyTargetedByEnemyEffect":
            {
                ChainLink? TargetedLink(TrickCall call)
                {
                    var targetIid = call.Targets.Count > 0 ? call.Targets[0] : null;
                    if (targetIid is null || !call.State.Players[call.Self.Owner].UnitSlots.Any(u => u?.Iid == targetIid)) return null;
                    return call.Below.LastOrDefault(l => l.Kind != "attack" && l.Player != call.Self.Owner && l.Targets.Contains(targetIid));
                }
                return new TrickBehavior
                {
                    When = call => TargetedLink(call) is not null,
                    Run = call =>
                    {
                        var link = TargetedLink(call);
                        if (link is null) return [];
                        var sacrificesForNegation = effect.Actions.Any(a => a.Kind == "destroy" && a.Target!.Kind == "unit" && a.Target.Owner == "self");
                        var resolved = link.Targets.Length == 1 ? effect
                            : effect.WithThenIfDone(effect.ThenIfDone?.Where(a => a.Kind != "negate" || a.What != "effect").ToList());
                        return RunEffect(resolved, new Run
                        {
                            State = call.State, Self = call.Self, Ctx = call.Ctx,
                            Announced = sacrificesForNegation ? call.Targets.Skip(1).ToList() : [.. call.Targets], EffectLinkId = link.Id,
                        });
                    },
                };
            }
            case "onAllyDestroyed":
            {
                // Se juega en respuesta al eslabón que la destruiría; su efecto espera a que ese eslabón resuelva.
                var ownEffect = trigger.Cause == "effect" && trigger.By == "self";
                (int linkId, string iid)? PickThreat(TrickCall call)
                {
                    var threats = AllyDestructionThreats(call.State, call.Self.Owner, call.Below, call.Ctx, ownEffect);
                    if (call.Targets.Count > 0) return threats.Any(t => t.iid == call.Targets[0]) ? threats.First(t => t.iid == call.Targets[0]) : null;
                    return threats.Count > 0 ? threats[0] : null;
                }
                return new TrickBehavior
                {
                    When = call => PickThreat(call) is not null,
                    Run = call =>
                    {
                        if (PickThreat(call) is not { } pt) return [];
                        var (linkId, iid) = pt;
                        var events = RunEffect(effect, new Run
                        {
                            State = call.State, Self = call.Self, Ctx = call.Ctx, Announced = call.Targets.Skip(1).ToList(), EffectLinkId = linkId, Victim = iid,
                        });
                        return events.Count > 0
                            ? [new Ev { Type = "AFTER_ALLY_DESTROYED", Player = call.Self.Owner, LinkId = linkId, TargetIid = iid, OwnEffect = ownEffect, Events = events.ToArray() }]
                            : [];
                    },
                };
            }
            default:
                throw new InvalidOperationException($"Disparador «{trigger.Kind}» sin soporte para Truco");
        }
    }

    /// <summary>Falla pronto (al construir el contexto) si una carta usa algo que el motor aún no ejecuta.</summary>
    public static CardBehavior Compile(CardDefinition card)
    {
        var b = new CardBehavior();
        var enterEffects = new List<EffectDefinition>();
        var activateEffects = new List<EffectDefinition>();
        var pairedEffects = new List<EffectDefinition>();
        foreach (var e in card.Effects ?? [])
        {
            var k = e.Trigger.Kind;
            if (card.Type == "trick")
            {
                if (b.Trick is not null) throw new InvalidOperationException($"{card.Id}: cada Truco debe declarar un único efecto");
                b.Trick = CompileTrick(e);
            }
            else if (k == "onEnter") enterEffects.Add(e);
            else if (k == "activated") activateEffects.Add(e);
            else if (k == "onPaired") pairedEffects.Add(e);
            else if (k == "static")
            {
                if (e.Actions.Any(a => a.Kind != "modifyStat")) throw new InvalidOperationException($"{card.Id}: static solo admite modifyStat");
            }
            else if (Listen(e) is { } l)
            {
                if (e.Actions.Any(a => a.Kind == "preventDestruction") != (k == "onAllyAboutToBeDestroyed"))
                    throw new InvalidOperationException($"{card.Id}: preventDestruction solo con onAllyAboutToBeDestroyed");
                b.Listeners.Add(l);
            }
            else throw new InvalidOperationException($"{card.Id}: disparador «{k}» sin soporte");
        }
        Func<InvokeCall, IReadOnlyList<Ev>> Invoke(List<EffectDefinition> effects) => call =>
        {
            var run = new Run
            {
                State = call.State, Self = call.Self, Ctx = call.Ctx,
                Announced = call.Targets?.ToList(), EffectLinkId = call.LinkId,
                Rng = Dice(call.State, call.Self.Iid), // los efectos de una misma carta comparten el dado
            };
            return effects.SelectMany(effect => RunEffect(effect, run)).ToList();
        };
        if (enterEffects.Count > 0) b.Enter = Invoke(enterEffects);
        if (activateEffects.Count > 0) b.Activate = Invoke(activateEffects);
        if (pairedEffects.Count > 0) b.Paired = Invoke(pairedEffects);
        return b;
    }
}
