// Único lugar donde un evento cambia el estado (port de events/reducers.ts). null = el evento falla y no ocurre.
namespace Arcana.Core;

public static class Reducers
{
    static UnitInPlay? UnitOf(GameState s, string who, string iid) => s.Players[who].Unit(iid);

    /// <summary>Mano o campo → descarte.</summary>
    static GameState? ToDiscard(GameState s, string who, string iid)
    {
        var p = s.Players[who];
        var card = p.Hand.Concat(p.FieldCards).FirstOrDefault(c => c?.Iid == iid);
        if (card is null) return null;
        return s.Update(who, pl => pl with
        {
            Hand = pl.Hand.Where(c => c.Iid != iid).ToArray(),
            UnitSlots = pl.UnitSlots.Select(u => u?.Iid == iid ? null : u).ToArray(),
            SpellSlots = pl.SpellSlots.Select(x => x?.Iid == iid ? null : x).ToArray(),
            // una Unidad emparejada lleva sus dos cartas al cementerio
            Discard = [.. pl.Discard, .. CardInstance.IsToken(iid) ? [] : new[] { new CardInstance { Iid = iid, CardId = card.CardId } },
                .. (card as UnitInPlay)?.Attached is { } att ? new[] { att.Plain() } : []],
        });
    }

    /// <summary>Destruir/sacrificar solo alcanza cartas EN CAMPO; los bonos «mientras esté en juego» se van con ella.</summary>
    static GameState? LeaveField(GameState s, string who, string iid, GameContext ctx)
    {
        if (!s.Players[who].FieldCards.Any(c => c?.Iid == iid)) return null;
        var d = ToDiscard(s, who, iid);
        if (d is null) return null;
        foreach (var w in P.Ids)
            d = d.Update(w, p => p with
            {
                UnitSlots = p.UnitSlots.Select(u => u is null ? null : u with { Modifiers = u.Modifiers.Where(m => !(m.Duration == "whileSourceInPlay" && m.SourceIid == iid)).ToArray() }).ToArray(),
            });
        return d;
    }

    static UnitInPlay Enter(GameState s, string position, CardInstance c) => new()
    {
        Iid = c.Iid, CardId = c.CardId, Position = position,
        HasAttackedThisCombat = false, PositionChangedThisTurn = false, SummonedInRound = s.Round,
        // el bono que tenía en la mano (La Emperatriz) pasa a ser permanente
        Modifiers = new[] { "atk", "def" }.Where(k => (c.Buff?.Get(k) ?? 0) != 0)
            .Select(k => new StatModifier { Id = $"hand#{k}", Stat = k, Amount = c.Buff!.Get(k), Duration = "permanent", SourceIid = null }).ToArray(),
    };

    static IReadOnlyDictionary<string, int> Inc(IReadOnlyDictionary<string, int> c, string k) => c.Set(k, c.Get(k) + 1);

    static GameState Counter(GameState s, Ev e, Func<IReadOnlyDictionary<string, int>, IReadOnlyDictionary<string, int>> f) =>
        s.Update(e.Player!, p => e.Per == "turn" ? p with { TurnCounters = f(p.TurnCounters) } : p with { RoundCounters = f(p.RoundCounters) });

    static int SlotOfNull<T>(T?[] slots) where T : class => Array.FindIndex(slots, x => x is null);

    /// <summary>Sin reductor, el evento solo se anuncia.</summary>
    public static GameState? Reduce(GameState s, Ev e, GameContext ctx)
    {
        switch (e.Type)
        {
            case "ENERGY_SPENT":
                return s.Players[e.Player!].Energy < e.Amount ? null : s.Update(e.Player!, p => p with { Energy = p.Energy - e.Amount!.Value });

            // Acoplar: la carta sale de la mano y queda bajo su pareja; ATQ/DEF base pasan a ser los del par.
            case "UNIT_PAIRED":
            {
                var p = s.Players[e.Player!];
                var card = p.Hand.FirstOrDefault(c => c.Iid == e.Iid);
                var unit = p.Unit(e.TargetIid!);
                if (card is null || unit is null || unit.Attached is not null) return null;
                var (atk, def) = ArcanaRules.PairStats(ctx.Card(unit.CardId)!, ctx.Card(card.CardId)!);
                return s.Update(e.Player!, pl => pl with
                {
                    Hand = pl.Hand.Where(c => c.Iid != e.Iid).ToArray(),
                    UnitSlots = pl.UnitSlots.Select(u => u?.Iid == unit.Iid ? u with { Attached = card.Plain(), PairAtk = atk, PairDef = def } : u).ToArray(),
                });
            }

            case "UNIT_SUMMONED":
            {
                var card = s.Players[e.Player!].Hand.FirstOrDefault(c => c.Iid == e.Iid);
                var slot = SlotOfNull(s.Players[e.Player!].UnitSlots);
                if (card is null || slot < 0) return null;
                return s.Update(e.Player!, p => p with
                {
                    Hand = p.Hand.Where(c => c.Iid != e.Iid).ToArray(),
                    UnitSlots = p.UnitSlots.Select((u, i) => i == slot ? Enter(s, e.Position!, card) : u).ToArray(),
                });
            }

            // Reemplazo: la anterior va al descarte sin ser «destruida» (no hay CARD_DESTROYED).
            case "UNIT_REPLACED":
            {
                var card = s.Players[e.Player!].Hand.FirstOrDefault(c => c.Iid == e.Iid);
                var old = UnitOf(s, e.Player!, e.OldIid!);
                if (card is null || old is null) return null;
                return s.Update(e.Player!, p => p with
                {
                    Hand = p.Hand.Where(c => c.Iid != e.Iid).ToArray(),
                    Discard = CardInstance.IsToken(old.Iid) ? p.Discard : [.. p.Discard, new CardInstance { Iid = old.Iid, CardId = old.CardId }],
                    UnitSlots = p.UnitSlots.Select(u => u?.Iid == old.Iid ? Enter(s, e.Position!, card) : u).ToArray(),
                });
            }

            case "SPELL_PLAYED":
            {
                var card = s.Players[e.Player!].Hand.FirstOrDefault(c => c.Iid == e.Iid);
                var duration = ctx.Card(e.CardId!)?.Duration;
                var slot = SlotOfNull(s.Players[e.Player!].SpellSlots);
                if (card is null || slot < 0 || duration is null) return null;
                return s.Update(e.Player!, p => p with
                {
                    Hand = p.Hand.Where(c => c.Iid != e.Iid).ToArray(),
                    SpellSlots = p.SpellSlots.Select((x, i) => i == slot
                        ? new SpellInPlay { Iid = card.Iid, CardId = card.CardId, Buff = card.Buff, RemainingDuration = duration.Value, UsesThisTurn = 0, UsesThisRound = 0 }
                        : x).ToArray(),
                });
            }

            case "TRICK_PLAYED":
                return s.Players[e.Player!].Hand.Any(c => c.Iid == e.Iid) ? ToDiscard(s, e.Player!, e.Iid!) : s;
            case "CARD_NEGATED":
                return ToDiscard(s, e.Player!, e.Iid!);
            case "CARD_DISCARDED":
            {
                var card = s.Players[e.Player!].Hand.FirstOrDefault(c => c.Iid == e.Iid);
                if (card is null) return null;
                // F02: el descarte en el turno propio dura toda la ronda.
                var ownTurn = s.ActivePlayer == e.Player && s.Phase is "turnA" or "turnB";
                return s.Update(e.Player!, p => p with
                {
                    Hand = p.Hand.Where(c => c.Iid != e.Iid).ToArray(),
                    Discard = [.. p.Discard, card],
                    TurnCounters = Inc(p.TurnCounters, "discarded"),
                    RoundCounters = ownTurn ? Inc(p.RoundCounters, "discardedOwnTurn") : p.RoundCounters,
                });
            }
            case "COUNTER_INCREMENTED":
                return Counter(s, e, c => Inc(c, e.Key!));
            case "COUNTER_SET":
                return Counter(s, e, c => c.Set(e.Key!, e.Value!.Value));

            case "HP_PAID_AS_ABILITY_COST":
                return s.Players[e.Player!].Hp < e.Amount ? null : s.Update(e.Player!, p => p with { Hp = p.Hp - e.Amount!.Value });

            case "LINK_NEGATED":
                return s.Chain is not null && s.Chain.Stack.Any(l => l.Id == e.LinkId && !l.Negated)
                    ? s with { Chain = s.Chain with { Stack = s.Chain.Stack.Select(l => l.Id == e.LinkId ? l with { Negated = true } : l).ToArray() } }
                    : null;

            // El reembolso nunca supera el máximo de energía de la ronda; el exceso se pierde.
            case "ENERGY_REFUNDED":
                return s.Update(e.Player!, p => p with { Energy = Math.Min(p.MaxEnergy, p.Energy + e.Amount!.Value) });

            case "POSITION_CHANGED":
                return UnitOf(s, e.Player!, e.Iid!) is null ? null : s.Update(e.Player!, p => p with
                {
                    UnitSlots = p.UnitSlots.Select(u => u?.Iid == e.Iid ? u! with { Position = e.Position!, PositionChangedThisTurn = true } : u).ToArray(),
                });

            case "HP_CHANGED":
                return s.Update(e.Player!, p => p with { Hp = p.Hp + e.Delta!.Value });
            case "HP_CHANGED_SIMULTANEOUSLY":
                return e.Changes!.Aggregate(s, (st, ch) => st.Update(ch.Player!, p => p with { Hp = p.Hp + ch.Delta!.Value }));

            case "CARD_DESTROYED":
            {
                var wasUnit = s.Players[e.Player!].UnitSlots.Any(u => u?.Iid == e.Iid);
                var left = LeaveField(s, e.Player!, e.Iid!, ctx);
                if (left is null) return null;
                if (!wasUnit) return left; // un Hechizo destruido no cuenta para «una Unidad fue destruida» (D04)
                foreach (var pl in P.Ids) left = left.Update(pl, p => p with { RoundCounters = Inc(p.RoundCounters, "unitDestroyed") });
                return left;
            }
            case "CARD_SACRIFICED":
                return LeaveField(s, e.Player!, e.Iid!, ctx);
            case "COMBAT_DESTRUCTION_PREVENTED":
                return s.CombatPreventions.Any(p => p.AttackerIid == e.AttackerIid && p.Iid == e.Iid)
                    ? s
                    : s with { CombatPreventions = [.. s.CombatPreventions, new CombatPrevention(e.AttackerIid!, e.Iid!, e.Player!)] };
            case "EFFECT_DESTRUCTION_PREVENTED":
                return s.EffectPreventions.Any(p => p.EffectLinkId == e.EffectLinkId && p.Iid == e.Iid)
                    ? s
                    : s with { EffectPreventions = [.. s.EffectPreventions, new EffectPrevention(e.EffectLinkId!.Value, e.Iid!, e.Player!)] };

            case "ATTACK_DECLARED":
            {
                var u = UnitOf(s, e.Player!, e.AttackerIid!);
                if (u is null) return null;
                return s.Update(e.Player!, p => p with
                {
                    // segundo ataque (Los Enamorados): se marca para que no haya un tercero
                    TurnCounters = u.HasAttackedThisCombat ? p.TurnCounters.Set($"attack2:{e.AttackerIid}", 1) : p.TurnCounters,
                    UnitSlots = p.UnitSlots.Select(x => x?.Iid == e.AttackerIid ? x! with { HasAttackedThisCombat = true } : x).ToArray(),
                });
            }

            // F05: el eslabón del ataque cambia de objetivo; el choque se calcula al resolver con el nuevo.
            case "ATTACK_REDIRECTED":
            {
                var link = s.Chain?.Stack.FirstOrDefault(l => l.Id == e.LinkId && l.Kind == "attack");
                if (s.Chain is null || link is null || UnitOf(s, P.Other(link.Player), e.ToIid!) is null) return null;
                ChainLink Retarget(ChainLink l) => l with
                {
                    Targets = l.Targets.Select(t => t == e.FromIid ? e.ToIid! : t).ToArray(),
                    Events = l.Events.Select(ev => ev.Type == "ATTACK_CLASH" && ev.TargetIid == e.FromIid ? ev with { TargetIid = e.ToIid } : ev).ToArray(),
                };
                return s with { Chain = s.Chain with { Stack = s.Chain.Stack.Select(l => l.Id == e.LinkId ? Retarget(l) : l).ToArray() } };
            }

            // F05 sobre un efecto: cambia el objetivo en el eslabón y en los eventos que lo llevan.
            case "EFFECT_REDIRECTED":
            {
                if (s.Chain is null || !s.Chain.Stack.Any(l => l.Id == e.LinkId)) return null;
                string[] Sw(string[] ts, string to) => ts.Select(t => t == e.FromIid ? to : t).ToArray();
                ChainLink Retarget(ChainLink l) => l with
                {
                    Targets = Sw(l.Targets, e.ToIid!),
                    Events = l.Events.Select(ev => ev.Targets is not null ? ev with { Targets = Sw(ev.Targets, e.ToIid + CardInstance.Redirected) } : ev).ToArray(),
                };
                return s with { Chain = s.Chain with { Stack = s.Chain.Stack.Select(l => l.Id == e.LinkId ? Retarget(l) : l).ToArray() } };
            }

            // Los bonos «en este enfrentamiento» expiran al terminarlo (§6).
            case "ATTACK_ENDED":
            {
                var cleared = s;
                foreach (var who in P.Ids)
                    cleared = cleared.Update(who, p => p with
                    {
                        UnitSlots = p.UnitSlots.Select(u => u is null ? null : u with { Modifiers = u.Modifiers.Where(m => m.Duration != "combat").ToArray() }).ToArray(),
                    });
                return cleared with { CombatPreventions = cleared.CombatPreventions.Where(p => p.AttackerIid != e.AttackerIid).ToArray() };
            }

            case "STAT_MODIFIED":
            {
                var u = UnitOf(s, e.Player!, e.Iid!);
                if (u is null) return null;
                var mod = new StatModifier { Id = $"{e.SourceIid ?? "fx"}#{u.Modifiers.Length}", Stat = e.Stat!, Amount = e.Amount!.Value, Duration = e.Duration!, SourceIid = e.SourceIid };
                return s.Update(e.Player!, p => p with
                {
                    UnitSlots = p.UnitSlots.Select(x => x?.Iid == e.Iid ? x! with { Modifiers = [.. x.Modifiers, mod] } : x).ToArray(),
                });
            }

            case "DICE_ROLLED":
                return s with { RngState = Rng.NextFloat(s.RngState).next };

            case "TOKEN_CREATED":
            {
                var slot = SlotOfNull(s.Players[e.Player!].UnitSlots);
                if (slot < 0) return null; // sin espacio: la ficha no se crea
                return s.Update(e.Player!, p => p with
                {
                    UnitSlots = p.UnitSlots.Select((u, i) => i == slot ? Enter(s, e.Position!, new CardInstance { Iid = e.Iid!, CardId = e.CardId! }) : u).ToArray(),
                    RoundCounters = Inc(p.RoundCounters, "tokens"),
                });
            }

            case "HAND_BUFFED":
            {
                if (e.Iids is not { Length: > 0 }) return null;
                return s.Update(e.Player!, p => p with
                {
                    Hand = p.Hand.Select(c =>
                    {
                        if (!e.Iids.Contains(c.Iid)) return c;
                        var b = c.Buff ?? new Buff(0, 0);
                        return c with { Buff = e.Stat == "atk" ? b with { Atk = b.Atk + e.Amount!.Value } : b with { Def = b.Def + e.Amount!.Value } };
                    }).ToArray(),
                });
            }

            case "UNITS_REORDERED":
            {
                var now = s.Players[e.Player!].UnitSlots;
                var byIid = now.OfType<UnitInPlay>().ToDictionary(u => u.Iid);
                var order = e.Order!;
                if (order.Length != now.Length || order.Count(x => x is not null) != byIid.Count || !order.All(x => x is null || byIid.ContainsKey(x)))
                    return null;
                return s.Update(e.Player!, p => p with { UnitSlots = order.Select(x => x is null ? null : byIid[x]).ToArray() });
            }

            case "OMEN_ACTIVATED":
            {
                var deck = s.Players[e.Player!].Deck;
                var n = e.N!.Value;
                var top = deck.Take(n).ToArray();
                var cards = e.Cards!;
                if (n <= 0 || e.VisibleTo != e.Player || cards.Length != top.Length ||
                    cards.Select(c => c.Iid).Distinct().Count() != top.Length ||
                    !cards.All(c => top.Any(t => t.Iid == c.Iid && t.CardId == c.CardId)))
                    return null;
                return s.Update(e.Player!, p => p with { Deck = [.. cards, .. p.Deck.Skip(top.Length)] });
            }

            default:
                return s;
        }
    }
}
