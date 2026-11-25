// Cadenas de reacción (§8) y enfrentamiento (§6): port de chains/chain.ts y combat/combat.ts.
namespace Arcana.Core;

public sealed record NewLink(string Kind, string Player, string CardId, string? HandIid, int Cost, Ev[] Events, IReadOnlyList<string>? Targets = null);

public static class Chain
{
    /// <summary>Añade un eslabón (abre la ventana si no había) y da la prioridad al otro jugador.</summary>
    public static GameState PushLink(GameState s, NewLink l)
    {
        var id = s.Chain?.NextId ?? 0;
        var link = new ChainLink
        {
            Kind = l.Kind, Player = l.Player, CardId = l.CardId, HandIid = l.HandIid, Cost = l.Cost,
            Events = l.Events, Targets = l.Targets?.ToArray() ?? [], Id = id, Negated = false,
        };
        return s with { Chain = new ChainState { Stack = [.. s.Chain?.Stack ?? [], link], Priority = P.Other(l.Player), Passes = 0, NextId = id + 1 } };
    }

    static List<Ev> DiscardIfCard(ChainLink l) =>
        l.HandIid is not null ? [new Ev { Type = "CARD_NEGATED", Player = l.Player, Iid = l.HandIid }] : [];

    static List<Ev> LinkEvents(ChainLink l, GameState s, GameContext ctx)
    {
        if (l.Kind != "trick")
            return l.Events.Select(e => e.Type is "UNIT_SUMMONED" or "UNIT_REPLACED" or "SPELL_PLAYED" or "ABILITY_ACTIVATED" ? e with { LinkId = l.Id } : e).ToList();
        var self = new Self(l.HandIid!, l.CardId, l.Player);
        var fx = ctx.Behavior(l.CardId)?.Trick?.Run(new TrickCall(s, self, ctx, s.Chain!.Stack, l.Targets)) ?? [];
        return [new Ev { Type = "TRICK_PLAYED", Player = l.Player, Iid = l.HandIid!, CardId = l.CardId }, .. fx];
    }

    /// <summary>PASS: a los 2 pases consecutivos se resuelve toda la cadena.</summary>
    public static Step PassPriority(GameState s, string player, GameContext ctx)
    {
        var c = s.Chain!;
        if (c.Passes + 1 < 2) return new(s with { Chain = c with { Passes = c.Passes + 1, Priority = P.Other(player) } }, []);
        return ResolveChain(s, ctx);
    }

    /// <param name="linkEnds">Opcional (solo presentación): recibe cuántos eventos lleva el registro al terminar cada
    /// eslabón, para que el cliente anime la resolución eslabón a eslabón. No cambia estado ni eventos.</param>
    public static Step ResolveChain(GameState start, GameContext ctx, List<int>? linkEnds = null)
    {
        var s = start;
        var log = new List<Ev>();
        var deferred = new List<Ev>();
        var resolvedLinks = 0;
        while (s.Chain is { Stack.Length: > 0 } && s.Phase != "gameOver")
        {
            var link = s.Chain.Stack[^1];
            s = s with { Chain = s.Chain with { Stack = s.Chain.Stack[..^1] } };
            var pending = link.Negated ? DiscardIfCard(link) : LinkEvents(link, s, ctx);
            // Las habilidades de una cadena de automáticas no se vuelven a diferir.
            var r = Resolver.Resolve(new Outcome(s, [], pending), ctx, link.Kind == "ability" ? null : deferred);
            s = r.State;
            log.AddRange(r.Events);
            var delayed = new List<Ev>();
            for (var i = 0; i < deferred.Count; i++)
            {
                var ev = deferred[i];
                if (ev.Type != "AFTER_ALLY_DESTROYED" || ev.LinkId != link.Id) continue;
                deferred.RemoveAt(i--);
                if (r.Events.Any(x => x.Type == "CARD_DESTROYED" && x.Iid == ev.TargetIid &&
                        (ev.OwnEffect != true || (x.Cause == "effect" && x.EffectLinkId == link.Id && x.SourcePlayer == ev.Player))))
                    delayed.AddRange(ev.Events!);
            }
            if (delayed.Count > 0)
            {
                var damage = Resolver.Resolve(new Outcome(s, [], delayed), ctx);
                s = damage.State;
                log.AddRange(damage.Events);
            }
            resolvedLinks++;
            linkEnds?.Add(log.Count);
        }
        var remainingLinks = s.Chain?.Stack.Length ?? 0;
        s = s with { Chain = null };
        log.Add(new Ev { Type = "CHAIN_RESOLVED", ResolvedLinks = resolvedLinks, RemainingLinks = remainingLinks });
        return new(s.Phase == "gameOver" ? s : OpenAutomatic(s, deferred), log);
    }

    /// <summary>Automáticas disparadas durante la resolución: nueva cadena, jugador activo primero (queda abajo).</summary>
    static GameState OpenAutomatic(GameState s, List<Ev> events)
    {
        var active = s.ActivePlayer ?? s.Initiative ?? P.A;
        var abilities = events.Where(e => e.Type == "ABILITY_ACTIVATED").ToList();
        var sorted = abilities.Where(e => e.Player == active).Concat(abilities.Where(e => e.Player != active));
        var output = s;
        foreach (var e in sorted)
        {
            var src = s.Players[e.Player!].FieldCards.FirstOrDefault(u => u?.Iid == e.SourceIid);
            if (src is not null) output = PushLink(output, new NewLink("ability", e.Player!, src.CardId, null, 0, [e], e.Targets));
        }
        return output;
    }
}

public static class Combat
{
    /// <summary>Enfrentamiento: estado + ataque → eventos de destrucción y daño.</summary>
    public static List<Ev> ResolveClash(GameState s, Ev e, GameContext ctx)
    {
        UnitInPlay? Unit(string who, string iid) => s.Players[who].Unit(iid);
        var atker = Unit(e.Player!, e.AttackerIid!);
        if (atker is null) return []; // R-018: si alguien salió del campo, no hay daño ni nuevo objetivo
        int Stat(UnitInPlay u, string k) => ArcanaRules.EffectiveStat(u, ctx.Card(u.CardId)!, k, Effects.StaticBonus(s, u.Iid, k, ctx));
        var atk = Stat(atker, "atk");
        var foe = P.Other(e.Player!);
        List<Ev> Hit(string player, int amount, string src) =>
            amount > 0 ? [new Ev { Type = "HP_CHANGED", Player = player, Delta = -amount, Cause = "combat", SourcePlayer = src }] : [];
        List<Ev> Kill(string player, string iid, string src) =>
            s.CombatPreventions.Any(p => p.AttackerIid == e.AttackerIid && p.Iid == iid)
                ? []
                : [new Ev { Type = "CARD_DESTROYED", Player = player, Iid = iid, Cause = "combat", AttackIid = e.AttackerIid, SourcePlayer = src }];

        if (e.TargetIid is null) return Hit(foe, atk, e.Player!); // ataque directo = ATQ [MVP]
        var target = Unit(foe, e.TargetIid);
        if (target is null) return [];

        // La Emperatriz rompe una regla: su dueño no pierde PV en enfrentamientos entre Unidades.
        List<Ev> Clash(string player, int amount, string src) => ArcanaRules.Has(s, player, "empress") ? [] : Hit(player, amount, src);
        var vs = Stat(target, target.Position == "atk" ? "atk" : "def");
        // El Carro rompe una regla: Arrollar, el exceso contra una Unidad en DEF también llega al rival.
        bool tramples = target.Position == "atk" || ArcanaRules.Has(s, e.Player!, "chariot");
        if (atk > vs) return [.. Kill(foe, target.Iid, e.Player!), .. Clash(foe, tramples ? atk - vs : 0, e.Player!)];
        if (atk == vs)
        {
            if (target.Position == "def") return []; // empate contra DEF: nadie es destruido
            // La Muerte rompe una regla: no se destruye por empate.
            return [
                .. target.CardId == ArcanaRules.Id["death"] ? [] : Kill(foe, target.Iid, e.Player!),
                .. atker.CardId == ArcanaRules.Id["death"] ? [] : Kill(e.Player!, atker.Iid, foe),
            ];
        }
        // Ataque fallido: el atacante es destruido y su dueño pierde la diferencia (El Diablo: la pierde el rival).
        var payer = ArcanaRules.Has(s, e.Player!, "devil") ? foe : e.Player!;
        return [.. Kill(e.Player!, atker.Iid, foe), .. Clash(payer, vs - atk, foe)];
    }
}
