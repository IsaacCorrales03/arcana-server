// Resolver (port de events/resolver.ts): cola de eventos pendientes. Cada evento pasa por las cartas en campo
// (iniciativa primero); una carta puede consumirlo y emitir otros. Lo que sobrevive se aplica y se registra.
namespace Arcana.Core;

/// <summary>Lo que produce un comando: hechos ya aplicados y eventos pendientes de resolver.</summary>
public sealed record Outcome(GameState State, IReadOnlyList<Ev> Facts, IReadOnlyList<Ev> Pending);

public static class Resolver
{
    const int MaxEvents = 1000; // corta bucles de efectos que se disparan entre sí

    /// <summary>Primero el jugador con iniciativa (Unidades y luego Hechizos, por espacio), después el rival.</summary>
    public static List<Self> SourcesInOrder(GameState s)
    {
        var first = s.Initiative ?? s.ActivePlayer ?? P.A;
        var list = new List<Self>();
        foreach (var owner in new[] { first, P.Other(first) })
            foreach (var c in s.Players[owner].FieldCards)
                if (c is not null) list.Add(new Self(c.Iid, c.CardId, owner));
        return list;
    }

    /// <summary>Efectos INVOCABLES: los crea la propia carta. Van antes que las reacciones de otras cartas.</summary>
    static IReadOnlyList<Ev> OwnEffects(GameState state, Ev e, GameContext ctx)
    {
        if (e.Type is "UNIT_SUMMONED" or "UNIT_REPLACED" or "SPELL_PLAYED")
            return ctx.Behavior(e.CardId!)?.Enter is not null
                ? [new Ev { Type = "ABILITY_ACTIVATED", Player = e.Player, SourceIid = e.Iid, Trigger = "enter", Targets = e.Targets, LinkId = e.LinkId }]
                : [];
        if (e.Type == "UNIT_PAIRED")
        {
            // «Al emparejar» de las dos cartas, sin importar cuál estaba primero en el campo
            var unit = state.Players[e.Player!].Unit(e.TargetIid!);
            if (unit?.Attached is not { } att) return [];
            return new[] { unit.CardId, att.CardId }
                .SelectMany(id => ctx.Behavior(id)?.Paired is { } run ? run(new InvokeCall(state, new Self(unit.Iid, id, e.Player!), ctx, null, e.LinkId)) : [])
                .ToList();
        }
        if (e.Type == "ATTACK_CLASH") return Combat.ResolveClash(state, e, ctx);
        if (e.Type == "LINK_NEGATED")
        {
            var cost = state.Chain?.Stack.FirstOrDefault(l => l.Id == e.LinkId)?.Cost ?? 0;
            var amount = cost / 2;
            return amount > 0 ? [new Ev { Type = "ENERGY_REFUNDED", Player = e.Player, Amount = amount }] : [];
        }
        if (e.Type != "ABILITY_ACTIVATED") return [];
        var self = SourcesInOrder(state).FirstOrDefault(x => x.Iid == e.SourceIid); // pudo salir del campo
        if (self is null) return [];
        var b = ctx.Behavior(self.CardId);
        var run = e.Trigger == "enter" ? b?.Enter : b?.Activate;
        return run is null ? [] : run(new InvokeCall(state, self, ctx, e.Targets, e.LinkId));
    }

    sealed record Item(Ev Event, bool Applied);

    static Ev EffectDamage(Ev e) => new() { Type = "HP_CHANGED", Player = e.Player, Delta = -e.Amount, Cause = "effect", SourcePlayer = e.SourcePlayer };

    /// <summary>`deferred` (solo al resolver una cadena): las habilidades «al entrar» esperan a una cadena nueva.</summary>
    public static Step Resolve(Outcome o, GameContext ctx, List<Ev>? deferred = null)
    {
        var state = o.State;
        var log = new List<Ev>();
        var queue = new LinkedList<Item>(o.Facts.Select(e => new Item(e, true)).Concat(o.Pending.Select(e => new Item(e, false))));
        void Unshift(IEnumerable<Item> items)
        {
            foreach (var it in items.Reverse()) queue.AddFirst(it);
        }
        for (var n = 0; queue.Count > 0; n++)
        {
            if (n >= MaxEvents) throw new InvalidOperationException($"Más de {MaxEvents} eventos encadenados: bucle de efectos");
            var (ev, applied) = queue.First!.Value;
            queue.RemoveFirst();
            if (deferred is not null && !applied && ev.Type == "ABILITY_ACTIVATED" && ev.Trigger == "enter")
            {
                deferred.Add(ev);
                continue;
            }
            if (!applied && ev.Type == "DAMAGE_AFTER_ATTACK")
            {
                if (deferred is not null) deferred.Add(ev);
                else queue.AddFirst(new Item(EffectDamage(ev), false));
                continue;
            }
            if (!applied && ev.Type == "AFTER_ALLY_DESTROYED")
            {
                deferred?.Add(ev); // fuera de una cadena no hay eslabón que esperar: falla
                continue;
            }
            var over = state.Phase == "gameOver";
            if (over && !applied) continue; // la partida terminó: lo pendiente ya no ocurre
            if (!applied && ev.Type == "CARD_DRAW_REQUESTED")
            {
                var drawn = Rounds.DrawCard(state, ev.Player!);
                state = drawn.State;
                // F02 v0.3: cuentan los robos por efecto en tu turno, no el robo del turno.
                var ownTurn = state.ActivePlayer == ev.Player && state.Phase is "turnA" or "turnB";
                if (ev.TurnDraw != true && ownTurn && drawn.Events.Any(x => x.Type == "CARD_DRAWN"))
                    state = state.Update(ev.Player!, p => p with { RoundCounters = p.RoundCounters.Set("drewOwnTurn", p.RoundCounters.Get("drewOwnTurn") + 1) });
                var next = ev.Remaining > 1 && state.Phase != "gameOver" ? [new Item(ev with { Remaining = ev.Remaining - 1 }, false)] : Array.Empty<Item>();
                Unshift([.. drawn.Events.Select(x => new Item(x, true)), .. next]);
                continue;
            }
            if (!applied && ev.Type == "CARD_DISCARD_REQUESTED")
            {
                var hand = state.Players[ev.Player!].Hand;
                var chosen = ev.Iid is not null ? null : ctx.Choices?.Discard(state, ev.Player!);
                var iid = ev.Iid ?? (chosen is not null && hand.Any(c => c.Iid == chosen) ? chosen : hand.FirstOrDefault()?.Iid);
                if (iid is null) continue;
                var discarded = new Ev { Type = "CARD_DISCARDED", Player = ev.Player, Iid = iid, AsAbilityCost = false };
                var nx = Reducers.Reduce(state, discarded, ctx);
                if (nx is null) continue;
                state = nx;
                queue.AddFirst(new Item(discarded, true));
                continue;
            }
            if (!applied && ev.Type == "HP_CHANGED_SIMULTANEOUSLY")
            {
                var nx = Reducers.Reduce(state, ev, ctx);
                if (nx is null) continue;
                state = nx;
                var victory = Rounds.ResolveVictory(new Step(state, []));
                state = victory.State;
                Unshift([.. ev.Changes!.Select(c => new Item(c, true)), .. victory.Events.Select(x => new Item(x, true))]);
                continue;
            }
            if (!applied && ev.Type == "CARD_DESTROYED" && ev.EffectLinkId is not null &&
                state.EffectPreventions.Any(p => p.EffectLinkId == ev.EffectLinkId && p.Iid == ev.Iid))
                continue;

            // Un hecho ya aplicado solo puede provocar reacciones; no se puede consumir.
            var emitted = new List<Ev>();
            var consumed = false;
            if (!over && ev.Type != "INITIAL_CARD_DRAWN")
            {
                foreach (var self in SourcesInOrder(state))
                {
                    if (consumed) break;
                    var listeners = ctx.Behavior(self.CardId)?.Listeners;
                    if (listeners is null) continue;
                    foreach (var l in listeners)
                    {
                        if (l.ListensTo != ev.Type) continue;
                        var call = new ListenerCall(state, ev, self, ctx);
                        if (l.When is not null && !l.When(call)) continue;
                        var r = l.Run(call);
                        if (r is null) continue;
                        if (r.Emit is not null) emitted.AddRange(r.Emit);
                        if (r.Consume && !applied)
                        {
                            consumed = true;
                            break;
                        }
                    }
                }
            }

            if (!consumed)
            {
                if (!applied)
                {
                    var nx = Reducers.Reduce(state, ev, ctx);
                    if (nx is null) continue; // sin objetivo legal: falla, sin reacciones
                    state = nx;
                }
                var eventIndex = log.Count;
                log.Add(ev);
                var v = Rounds.ResolveVictory(new Step(state, []));
                state = v.State;
                log.AddRange(v.Events);
                if (!applied)
                {
                    var own = OwnEffects(state, ev, ctx);
                    if (ev.Type == "ABILITY_ACTIVATED" && own.Count > 0)
                        log[eventIndex] = ev with { EffectExecuted = true };
                    emitted.InsertRange(0, own);
                }
            }
            if (!consumed && !applied && ev.Type == "ATTACK_ENDED" && deferred is not null)
            {
                var damage = deferred.Where(x => x.Type == "DAMAGE_AFTER_ATTACK" && x.AttackerIid == ev.AttackerIid).ToList();
                deferred.RemoveAll(x => x.Type == "DAMAGE_AFTER_ATTACK" && x.AttackerIid == ev.AttackerIid);
                emitted.AddRange(damage.Select(EffectDamage));
            }
            Unshift(emitted.Select(x => new Item(x, false)));
        }
        return new Step(state, log);
    }
}
