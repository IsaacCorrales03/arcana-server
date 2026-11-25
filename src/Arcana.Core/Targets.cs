// Variantes con objetivos de un comando (port de rules/targets.ts): simula cada secuencia de objetivos y
// conserva solo las que cambian el resultado.
using Arcana.Content;

namespace Arcana.Core;

public static class Targets
{
    const string OptionalAccept = "$accept";

    static bool IsTargetable(Command c) => c.Type is "SUMMON" or "REPLACE" or "PLAY_TRICK" or "ACTIVATE_ABILITY";

    sealed record TargetContext(Self Self, IReadOnlyList<Target> Attack, Ev? Clash);

    static TargetContext ContextOf(GameState state, Command command)
    {
        var p = state.Players[command.Player!];
        var cardId = command.Type == "ACTIVATE_ABILITY" ? p.Unit(command.SourceIid!)?.CardId : p.Hand.FirstOrDefault(c => c.Iid == command.Iid)?.CardId;
        if (cardId is null) throw new InvalidOperationException($"No se pudo resolver la carta de {command.Type}");
        var attackLink = (state.Chain?.Stack ?? []).LastOrDefault(l => l.Kind == "attack");
        var attack = attackLink?.Events.FirstOrDefault(e => e.Type == "ATTACK_CLASH");
        List<Target> participants = attack is null ? [] :
            [new(attack.AttackerIid!, attack.Player!), .. attack.TargetIid is null ? Array.Empty<Target>() : [new(attack.TargetIid, P.Other(attack.Player!))]];
        return new(new Self(command.Type == "ACTIVATE_ABILITY" ? command.SourceIid! : command.Iid!, cardId, command.Player!), participants, attack);
    }

    static List<EffectDefinition> EffectList(Command command, GameContext ctx, string cardId) =>
        (ctx.Card(cardId)?.Effects ?? []).Where(e => command.Type switch
        {
            "SUMMON" or "REPLACE" => e.Trigger.Kind == "onEnter",
            "ACTIVATE_ABILITY" => e.Trigger.Kind == "activated",
            _ => true,
        }).ToList();

    static List<string> DiscardDomain(EffectAction action, GameState state, Command command)
    {
        var player = action.Player == "opponent" ? P.Other(command.Player!) : action.Player == "active" ? state.ActivePlayer ?? command.Player! : command.Player!;
        if (player != command.Player) return [];
        return state.Players[player].Hand.Where(c => command.Iid is null || c.Iid != command.Iid).Select(c => c.Iid).ToList();
    }

    static List<List<string>> ActionDomains(IEnumerable<EffectAction> actions, GameState state, Command command, TargetContext tc, GameContext ctx)
    {
        var domains = new List<List<string>>();
        foreach (var a in actions)
        {
            if (a.Kind is "destroy" or "modifyStat" or "changePosition" or "preventDestruction" or "redirectAttack")
                domains.Add(Effects.LegalEffectTargets(a.Target!, state, tc.Self, ctx, tc.Attack));
            else if (a.Kind == "destroySpell")
                domains.Add(Effects.LegalEffectTargets(new TargetSpec { Kind = "spell", Owner = "any", CostAtMost = a.CostAtMost }, state, tc.Self, ctx, tc.Attack));
            else if (a.Kind == "discard")
                for (var n = 0; n < a.Count; n++) domains.Add(DiscardDomain(a, state, command));
        }
        return domains;
    }

    static List<string> TriggerDomain(EffectDefinition effect, GameState state, Command command, TargetContext tc, GameContext ctx)
    {
        List<string> FieldUnits(string player) => state.Players[player].Units.Select(u => u.Iid).ToList();
        var stack = state.Chain?.Stack ?? [];
        switch (effect.Trigger.Kind)
        {
            case "onAllyTargetedByEnemyEffect":
                return stack.Where(l => l.Player != command.Player && l.Kind != "attack").SelectMany(l => l.Targets)
                    .Where(iid => FieldUnits(command.Player!).Contains(iid)).ToList();
            case "onAllyDestroyed":
            {
                var ownEffect = effect.Trigger.Cause == "effect" && effect.Trigger.By == "self";
                return Effects.AllyDestructionThreats(state, command.Player!, stack, ctx, ownEffect).Select(t => t.iid).Distinct().ToList();
            }
            case "onAllyTargetedByAttack":
            {
                var target = tc.Clash?.TargetIid;
                List<string> attacked = target is not null && tc.Clash!.Player != command.Player && FieldUnits(command.Player!).Contains(target) ? [target] : [];
                // F05 también desvía efectos: cualquier Unidad apuntada por un eslabón de la pila
                var byEffect = P.Ids.SelectMany(FieldUnits).Where(iid => Effects.EffectLinkTargeting(state, stack, iid) is not null);
                return attacked.Concat(byEffect).Distinct().ToList();
            }
            case "onAllyAboutToBeDestroyed":
                return tc.Clash is null ? [] : Effects.CombatDestructions(state, tc.Clash, ctx).Where(u => u.Owner == command.Player).Select(u => u.Iid).ToList();
            default:
                return [];
        }
    }

    static List<List<string>> MergeFirst(List<List<string>> first, List<string> trigger)
    {
        if (first.Count == 0) return [trigger.ToList()];
        return [first[0].Where(trigger.Contains).ToList(), .. first.Skip(1)];
    }

    static readonly HashSet<string> NeedsTrigger = ["onAllyTargetedByEnemyEffect", "onAllyDestroyed", "onAllyTargetedByAttack", "onAllyAboutToBeDestroyed"];

    static List<List<string>> EffectDomains(EffectDefinition effect, GameState state, Command command, TargetContext tc, GameContext ctx)
    {
        var actionDomains = ActionDomains(effect.Actions.Concat(effect.ThenIfDone ?? []), state, command, tc, ctx);
        var trig = TriggerDomain(effect, state, command, tc, ctx);
        var kind = effect.Trigger.Kind;
        if (trig.Count == 0) return NeedsTrigger.Contains(kind) ? [] : actionDomains;
        if (kind == "onAllyTargetedByEnemyEffect")
        {
            var hasSacrifice = effect.Actions.Any(a => a.Kind == "destroy" && a.Target!.Kind == "unit" && a.Target.Owner == "self");
            return hasSacrifice ? [trig, .. actionDomains] : MergeFirst(actionDomains, trig);
        }
        if (kind == "onAllyAboutToBeDestroyed") return [trig];
        // F05: solo se elige el nuevo objetivo (el origen se deduce); el Truco (when) filtra los destinos válidos
        if (kind == "onAllyTargetedByAttack") return [P.Ids.SelectMany(p => state.Players[p].Units.Select(u => u.Iid)).ToList()];
        return MergeFirst(actionDomains, trig);
    }

    static List<List<string>> Sequences(List<List<string>> domains, bool acceptsOptional)
    {
        if (domains.Any(d => d.Count == 0)) return [];
        var seqs = new List<List<string>>();
        void Build(int index, List<string> prefix)
        {
            if (index == domains.Count)
            {
                if (!acceptsOptional)
                {
                    seqs.Add(prefix);
                    return;
                }
                for (var pos = 0; pos <= prefix.Count; pos++) seqs.Add([.. prefix.Take(pos), OptionalAccept, .. prefix.Skip(pos)]);
                return;
            }
            foreach (var c in domains[index]) Build(index + 1, [.. prefix, c]);
        }
        if (domains.Count == 0)
        {
            if (acceptsOptional) seqs.Add([OptionalAccept]);
        }
        else Build(0, []);
        return seqs;
    }

    static GameState Resolved(GameState state, Command command, GameContext ctx)
    {
        var r = Rules.ApplyStep(state, command, ctx);
        if (!r.Ok) throw new InvalidOperationException($"Decisión legal rechazada por applyCommand: {r.Error!.Code}");
        var current = r.Value!.State;
        while (current.Chain is not null && current.Phase != "gameOver")
        {
            r = Rules.ApplyStep(current, Command.Pass(current.Chain.Priority), ctx);
            if (!r.Ok) throw new InvalidOperationException($"No se pudo resolver {command.Type} en {current.Phase}: {r.Error!.Code}");
            current = r.Value!.State;
        }
        return current;
    }

    static string Outcome(GameState s) => Signature.Of(s.Phase == "gameOver" ? s with { Chain = null } : s);

    public static List<Command> LegalTargetVariants(GameState state, Command command, GameContext ctx)
    {
        if (!IsTargetable(command)) return [command];
        var tc = ContextOf(state, command);
        var effects = EffectList(command, ctx, tc.Self.CardId);
        var domains = new List<List<string>>();
        if (command.Type == "ACTIVATE_ABILITY")
        {
            var activation = effects.FirstOrDefault(e => e.Trigger.Kind == "activated");
            foreach (var a in activation?.Cost ?? [])
                if (a.Kind == "discard")
                {
                    var d = DiscardDomain(a, state, command);
                    for (var n = 0; n < a.Count; n++) domains.Add(d);
                }
        }
        foreach (var e in effects) domains.AddRange(EffectDomains(e, state, command, tc, ctx));
        var seqs = Sequences(domains, effects.Any(e => e.Optional == true));
        var baseIsLegal = Rules.Validate(state, command, ctx) is null;
        if (seqs.Count == 0) return baseIsLegal ? [command] : [];
        // F05 exige elegir el destino: sin variante «Ninguna» y con todos los destinos
        var eligeDestino = effects.Any(e => e.Trigger.Kind == "onAllyTargetedByAttack");
        var baseline = baseIsLegal && !eligeDestino ? Outcome(Resolved(state, command, ctx)) : null;
        var variants = new List<Command>();
        var keys = new HashSet<string>();
        var outcomes = new HashSet<string>();
        foreach (var targets in seqs)
        {
            var targeted = command with { Targets = targets.ToArray() };
            if (Rules.Validate(state, targeted, ctx) is not null) continue;
            var outcome = Outcome(Resolved(state, targeted, ctx));
            if (baseline is not null && outcome == baseline) continue;
            if (!outcomes.Add(outcome)) continue;
            if (keys.Add(targeted.ToString())) variants.Add(targeted);
        }
        if (eligeDestino && variants.Count > 0) return variants;
        return baseIsLegal ? [command, .. variants] : variants;
    }
}
