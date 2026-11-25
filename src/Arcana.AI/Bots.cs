// Bots (port de core/bots.ts): decisiones legales agrupadas y las estrategias random, heuristic y strategic.
using Arcana.Content;
using Arcana.Core;

namespace Arcana.AI;

public static class Bots
{
    /// <summary>random | heuristic (Fácil) | strategic (Difícil)</summary>
    public static readonly string[] Strategies = ["random", "heuristic", "strategic"];

    /// <summary>Acciones legales agrupadas: las variantes con objetivos de un mismo comando van juntas.</summary>
    public static List<Decision> LegalDecisions(GameState state, string player, GameContext ctx)
    {
        var decisions = new List<Decision>();
        var order = new List<string>();
        var grouped = new Dictionary<string, List<Command>>();
        var actions = Rules.LegalActions(state, player, ctx, includeTargets: true);
        var hasLegalAttack = actions.Any(c => c.Type == "DECLARE_ATTACK");
        var hasLegalResponse = state.Chain is not null && actions.Any(c => c.Type != "PASS");
        foreach (var command in actions)
        {
            var key = (command with { Targets = null }).ToString();
            if (!grouped.TryGetValue(key, out var list))
            {
                grouped[key] = list = [];
                order.Add(key);
            }
            list.Add(command);
        }
        foreach (var key in order)
        {
            var group = grouped[key];
            var command = group.FirstOrDefault(c => c.Targets is null) ?? group[0];
            if (command.Type == "MULLIGAN")
            {
                var hand = state.Players[player].Hand;
                var subsets = new List<string[]> { Array.Empty<string>() };
                foreach (var card in hand) subsets = [.. subsets, .. subsets.Select(s => (string[])[.. s, card.Iid])];
                foreach (var iids in subsets)
                {
                    var mulligan = new Command { Type = "MULLIGAN", Player = player, Iids = iids };
                    if (Rules.Validate(state, mulligan, ctx) is null) decisions.Add(new Decision(mulligan, false));
                }
                continue;
            }
            var automatic = command.Type == "START_ROUND" ||
                (command.Type == "PASS" && state.Chain is not null && !hasLegalResponse) ||
                (command.Type == "END_COMBAT" && !hasLegalAttack);
            decisions.Add(group.Count > 1 ? new Decision(command, automatic, group) : new Decision(command, automatic));
        }
        return decisions;
    }

    static string? SourceIid(Command c) => c.Type == "ACTIVATE_ABILITY" ? c.SourceIid : c.Iid;

    static string? CardIdFor(GameState state, Command command)
    {
        var src = SourceIid(command);
        if (src is null || command.Player is null) return null;
        var p = state.Players[command.Player];
        return p.Hand.FirstOrDefault(c => c.Iid == src)?.CardId ?? p.Unit(src)?.CardId;
    }

    static IEnumerable<EffectDefinition> EffectsFor(CardDefinition def, Command command) => command.Type switch
    {
        "SUMMON" or "REPLACE" => (def.Effects ?? []).Where(e => e.Trigger.Kind == "onEnter"),
        "ATTACH" => (def.Effects ?? []).Where(e => e.Trigger.Kind == "onPaired"),
        "ACTIVATE_ABILITY" => (def.Effects ?? []).Where(e => e.Trigger.Kind == "activated"),
        _ => def.Effects ?? [],
    };

    static List<EffectAction> ActionsFor(IEnumerable<EffectDefinition> effects) =>
        effects.SelectMany(e => (e.Cost ?? []).Concat(e.Actions).Concat(e.ThenIfDone ?? [])).ToList();

    static string? OwnerFor(string? owner, string player, GameState state) => owner switch
    {
        "self" => player,
        "opponent" => P.Other(player),
        "active" => state.ActivePlayer ?? player,
        _ => null,
    };

    static string? PreferredOwner(TargetSpec t, string player, GameState state)
    {
        if (t.Kind == "self") return player;
        if (t.Kind is "unit" or "spell") return OwnerFor(t.Owner, player, state);
        if (t.Kind == "attackParticipant")
        {
            var attack = (state.Chain?.Stack ?? []).LastOrDefault(l => l.Kind == "attack");
            if (attack is null) return null;
            if (t.Role == "attacker") return attack.Player;
            if (t.Role == "defender") return P.Other(attack.Player);
        }
        return null;
    }

    static (string owner, UnitInPlay unit)? UnitFor(GameState state, string iid)
    {
        foreach (var owner in P.Ids)
            if (state.Players[owner].Unit(iid) is { } u) return (owner, u);
        return null;
    }

    static int Stat(GameState s, UnitInPlay u, CardDefinition def, string k, GameContext ctx) =>
        ArcanaRules.EffectiveStat(u, def, k, Effects.StaticBonus(s, u.Iid, k, ctx));

    static double UnitValue(GameState state, UnitInPlay unit, GameContext ctx)
    {
        var def = ctx.Card(unit.CardId) ?? throw new InvalidOperationException($"Carta desconocida en el campo: {unit.CardId}");
        return def.Cost * 4 + Stat(state, unit, def, "atk", ctx) + Stat(state, unit, def, "def", ctx);
    }

    static double TargetScore(GameState state, string player, Command command, IReadOnlyList<string> targets, CardDefinition? def, GameContext ctx)
    {
        if (def is null) return 0;
        var actions = ActionsFor(EffectsFor(def, command));
        var src = SourceIid(command);
        double score = 0;
        foreach (var iid in targets)
        {
            if (iid == "$accept")
            {
                score += 1;
                continue;
            }
            var handCard = state.Players[player].Hand.FirstOrDefault(c => c.Iid == iid);
            if (handCard is not null)
            {
                if (iid == src)
                {
                    score += 20;
                    continue;
                }
                var cardValue = ctx.Card(handCard.CardId)?.Cost ?? 0;
                score += actions.Any(a => a.Kind == "discard") ? 100 - cardValue * 4 : -20;
                continue;
            }
            if (UnitFor(state, iid) is { } fu)
            {
                var value = UnitValue(state, fu.unit, ctx);
                double cand = 0;
                foreach (var a in actions)
                {
                    if (a.Kind == "destroy")
                    {
                        var pref = PreferredOwner(a.Target!, player, state);
                        if (pref is not null && pref != fu.owner) continue;
                        cand += fu.owner == player ? 100 - value : 100 + value;
                    }
                    else if (a.Kind == "preventDestruction")
                    {
                        var pref = PreferredOwner(a.Target!, player, state) ?? player;
                        if (pref == fu.owner) cand += fu.owner == player ? 100 + value : -100 - value;
                    }
                    else if (a.Kind == "modifyStat")
                    {
                        var pref = a.Amount > 0 ? player : P.Other(player);
                        cand += fu.owner == pref ? 100 + value + Math.Abs(a.Amount) : -100 - value;
                    }
                    else if (a.Kind == "changePosition")
                    {
                        var d = ctx.Card(fu.unit.CardId);
                        int atk = d?.Atk ?? 0, dfn = d?.Def ?? 0;
                        cand += fu.owner != player ? -100 - value
                            : (fu.unit.Position == "def" && atk > dfn) || (fu.unit.Position == "atk" && dfn > atk) ? 100 : -100;
                    }
                }
                score += cand != 0 ? cand : fu.owner == player ? -20 - value : 20 + value;
                continue;
            }
            foreach (var owner in P.Ids)
            {
                var spell = state.Players[owner].SpellSlots.FirstOrDefault(c => c?.Iid == iid);
                if (spell is null) continue;
                var sv = (ctx.Card(spell.CardId)?.Cost ?? 0) * 4;
                score += actions.Any(a => a.Kind == "destroySpell")
                    ? owner == player ? -100 - sv : 100 + sv
                    : owner == player ? -20 - sv : 20 + sv;
                break;
            }
        }
        return score;
    }

    static IReadOnlyList<string> TargetsOf(Command c) => c.Targets ?? [];

    static double BestTargetScore(GameState state, string player, Decision decision, GameContext ctx)
    {
        var options = decision.Options;
        var command = decision.Command;
        var cardId = CardIdFor(state, command);
        var def = cardId is null ? null : ctx.Card(cardId);
        var targeted = options.Where(o => TargetsOf(o).Count > 0).ToList();
        var defaultOption = options.FirstOrDefault(o => TargetsOf(o).Count == 0);
        var defaultTargeted = targeted.FirstOrDefault(o => !TargetsOf(o).Contains("$accept"));
        var defaultTargets = defaultTargeted is null ? [] : TargetsOf(defaultTargeted);
        var best = defaultOption is not null ? TargetScore(state, player, command, defaultTargets, def, ctx) : double.NegativeInfinity;
        foreach (var o in targeted)
        {
            var sc = TargetScore(state, player, o, TargetsOf(o), def, ctx);
            if (sc > best) best = sc;
        }
        return best;
    }

    static Command ChooseOption(GameState state, string player, Decision decision, GameContext ctx)
    {
        var options = decision.Options;
        var selected = options.FirstOrDefault();
        var bestScore = double.NegativeInfinity;
        var cardId = CardIdFor(state, decision.Command);
        var def = cardId is null ? null : ctx.Card(cardId);
        var defaultTargeted = options.FirstOrDefault(o => TargetsOf(o).Count > 0 && !TargetsOf(o).Contains("$accept"));
        var defaultTargets = defaultTargeted is null ? [] : TargetsOf(defaultTargeted);
        foreach (var o in options)
        {
            // en TS: option === decision.command (misma referencia, no igualdad de valores)
            var targets = ReferenceEquals(o, decision.Command) && TargetsOf(o).Count == 0 ? defaultTargets : TargetsOf(o);
            var sc = TargetScore(state, player, o, targets, def, ctx);
            if (sc > bestScore)
            {
                selected = o;
                bestScore = sc;
            }
        }
        return selected ?? throw new InvalidOperationException("Decisión sin comandos disponibles");
    }

    static bool IsNegationTrick(GameState state, Decision d, GameContext ctx) =>
        d.Command.Type == "PLAY_TRICK" && CardIdFor(state, d.Command) is { } id && ctx.Card(id) is { } def && ActionsFor(def.Effects ?? []).Any(a => a.Kind == "negate");

    static double TrickUtility(GameState state, string player, Decision d, GameContext ctx)
    {
        if (d.Command.Type != "PLAY_TRICK") return double.NegativeInfinity;
        var cardId = CardIdFor(state, d.Command);
        var def = cardId is null ? null : ctx.Card(cardId);
        if (def is null) return double.NegativeInfinity;
        double u = 0;
        foreach (var a in ActionsFor(def.Effects ?? []))
            u += a.Kind switch
            {
                "negate" => 1_000,
                "preventDestruction" => 40,
                "destroy" or "destroySpell" => 25,
                "draw" => a.Count * 8,
                "omen" => a.Count * 4,
                "heal" => a.Amount * (state.Players[player].Hp < 30 ? 6 : 2),
                "damagePlayer" => a.Player == "opponent" ? a.Amount * 10 : -a.Amount * 12,
                "modifyStat" => Math.Abs(a.Amount) * (a.Amount > 0 ? 10 : 8),
                "changePosition" => 15,
                "reduceNextSpellCost" => a.Amount * 3,
                "payHp" => -a.Amount * 12,
                "discard" => -a.Count * 3,
                _ => 0,
            };
        return u + BestTargetScore(state, player, d, ctx) - def.Cost * 2;
    }

    static string PreferredPosition(GameState state, string player, CardDefinition def, GameContext ctx)
    {
        var attack = def.Atk ?? 0;
        var defense = def.Def ?? 0;
        var enemies = state.Players[P.Other(player)].Units.ToList();
        var canWin = enemies.Any(u =>
        {
            var ed = ctx.Card(u.CardId) ?? throw new InvalidOperationException($"Carta desconocida en el campo: {u.CardId}");
            var k = u.Position == "atk" ? "atk" : "def";
            return attack > Stat(state, u, ed, k, ctx);
        });
        return attack > 0 && (enemies.Count == 0 || canWin || attack >= defense) ? "atk" : "def";
    }

    static int CommandCardCost(GameState state, Decision d, GameContext ctx) =>
        CardIdFor(state, d.Command) is { } id ? ctx.Card(id)?.Cost ?? -1 : -1;

    static Decision? ChooseBest(IEnumerable<Decision> candidates, Func<Decision, double> score)
    {
        Decision? selected = null;
        var best = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var s = score(c);
            if (s > best)
            {
                selected = c;
                best = s;
            }
        }
        return selected;
    }

    static double? AttackScore(GameState state, string player, Command command, GameContext ctx)
    {
        if (command.Type != "DECLARE_ATTACK") return null;
        var attacker = UnitFor(state, command.AttackerIid!) ?? throw new InvalidOperationException($"Atacante desaparecido: {command.AttackerIid}");
        var def = ctx.Card(attacker.unit.CardId)!;
        var attack = Stat(state, attacker.unit, def, "atk", ctx);
        if (command.TargetIid is null) return attack > 0 ? 100 + attack : null;
        var target = UnitFor(state, command.TargetIid);
        if (target is null || target.Value.owner == player) throw new InvalidOperationException($"Objetivo de ataque inválido: {command.TargetIid}");
        var td = ctx.Card(target.Value.unit.CardId)!;
        var defense = Stat(state, target.Value.unit, td, target.Value.unit.Position == "atk" ? "atk" : "def", ctx);
        if (attack <= defense) return null;
        return 50 + UnitValue(state, target.Value.unit, ctx) + attack - defense;
    }

    static bool AbilityIsSafe(GameState state, string player, Decision d, GameContext ctx)
    {
        if (d.Command.Type != "ACTIVATE_ABILITY") return false;
        var ability = ctx.Card(CardIdFor(state, d.Command) ?? "")?.Effects?.FirstOrDefault(e => e.Trigger.Kind == "activated");
        if (ability is null) return false;
        var hpCost = (ability.Cost ?? []).Sum(c => c.Kind == "payHp" ? c.Amount : 0);
        return state.Players[player].Hp - hpCost >= 5;
    }

    static Command ChooseHeuristic(GameState state, string player, GameContext ctx, List<Decision> decisions)
    {
        if (state.Phase == "mulligan")
        {
            var keepAll = decisions.FirstOrDefault(d => d.Command.Type == "MULLIGAN" && d.Command.Iids!.Length == 0);
            if (keepAll is not null) return keepAll.Command;
        }
        if (state.Chain is not null)
        {
            var negation = ChooseBest(decisions.Where(d => IsNegationTrick(state, d, ctx)),
                d => -CommandCardCost(state, d, ctx) * 100 + BestTargetScore(state, player, d, ctx));
            if (negation is not null) return ChooseOption(state, player, negation, ctx);
            var response = ChooseBest(decisions.Where(d => d.Command.Type == "PLAY_TRICK"), d => TrickUtility(state, player, d, ctx));
            if (response is not null && TrickUtility(state, player, response, ctx) > 0) return ChooseOption(state, player, response, ctx);
            var pass = decisions.FirstOrDefault(d => d.Command.Type == "PASS");
            if (pass is not null) return pass.Command;
        }
        if (state.Phase == "combat")
        {
            var attacks = decisions.Where(d => d.Command.Type == "DECLARE_ATTACK")
                .Select(d => (d, score: AttackScore(state, player, d.Command, ctx))).Where(x => x.score is not null).ToList();
            var attack = ChooseBest(attacks.Select(x => x.d), d => attacks.First(x => ReferenceEquals(x.d, d)).score!.Value);
            if (attack is not null) return attack.Command;
            var endCombat = decisions.FirstOrDefault(d => d.Command.Type == "END_COMBAT");
            if (endCombat is not null) return endCombat.Command;
        }
        if (state.Phase is "turnA" or "turnB")
        {
            var summons = decisions.Where(d => d.Command.Type == "SUMMON").ToList();
            if (summons.Count > 0)
            {
                var summon = ChooseBest(summons, d =>
                {
                    var cost = CommandCardCost(state, d, ctx);
                    var def = CardIdFor(state, d.Command) is { } id ? ctx.Card(id) : null;
                    var preferred = def is not null ? PreferredPosition(state, player, def, ctx) : "def";
                    var bonus = d.Command.Position == preferred ? 1 : 0;
                    return cost * 10_000 + bonus * 100 + BestTargetScore(state, player, d, ctx);
                });
                if (summon is not null) return ChooseOption(state, player, summon, ctx);
            }
            var replacements = decisions.Where(d => d.Command.Type == "REPLACE").ToList();
            if (replacements.Count > 0)
            {
                var replace = ChooseBest(replacements, d =>
                {
                    var cost = CommandCardCost(state, d, ctx);
                    var old = UnitFor(state, d.Command.UnitIid!);
                    var oldValue = old is { } o ? UnitValue(state, o.unit, ctx) : 0;
                    var def = CardIdFor(state, d.Command) is { } id ? ctx.Card(id) : null;
                    var preferred = def is not null ? PreferredPosition(state, player, def, ctx) : "def";
                    var bonus = d.Command.Position == preferred ? 1 : 0;
                    return cost * 10_000 - oldValue * 100 + bonus * 10 + BestTargetScore(state, player, d, ctx);
                });
                if (replace is not null) return ChooseOption(state, player, replace, ctx);
            }
            var ability = ChooseBest(decisions.Where(d => AbilityIsSafe(state, player, d, ctx)), d => BestTargetScore(state, player, d, ctx));
            if (ability is not null) return ChooseOption(state, player, ability, ctx);

            var change = decisions.FirstOrDefault(d =>
            {
                if (d.Command.Type != "CHANGE_POSITION") return false;
                var unit = UnitFor(state, d.Command.UnitIid!);
                if (unit is not { } u || u.unit.Position != "def") return false;
                var def = ctx.Card(u.unit.CardId)!;
                return PreferredPosition(state, player, def, ctx) == "atk" && Stat(state, u.unit, def, "atk", ctx) > Stat(state, u.unit, def, "def", ctx);
            });
            if (change is not null) return change.Command;

            var spell = ChooseBest(decisions.Where(d => d.Command.Type == "PLAY_SPELL"), d => CommandCardCost(state, d, ctx));
            if (spell is not null) return spell.Command;

            var battle = decisions.FirstOrDefault(d => d.Command.Type == "BATTLE");
            if (battle is not null && state.Players[player].UnitSlots.Any(u => u?.Position == "atk")) return battle.Command;
            var endTurn = decisions.FirstOrDefault(d => d.Command.Type == "END_TURN");
            if (endTurn is not null) return endTurn.Command;
        }
        return decisions.FirstOrDefault(d => d.Automatic)?.Command ?? throw new InvalidOperationException($"El bot heurístico no encontró una acción en {state.Phase}");
    }

    static (int index, uint next) RandomIndex(int length, uint rng)
    {
        var (r, next) = Rng.NextFloat(rng);
        return ((int)Math.Floor(r * length), next);
    }

    /// <summary>Elige el comando del bot. `ctx` debe llevar las decisiones del plan (PlanChoices) para que simule igual que TS.</summary>
    public static (Command command, uint rng) Choose(string strategy, GameState state, string player, GameContext ctx, uint rng)
    {
        var decisions = LegalDecisions(state, player, ctx);
        if (decisions.Count == 0) throw new InvalidOperationException($"Sin decisiones legales para {player} en {state.Phase}");
        Command command;
        if (strategy == "random")
        {
            var choices = decisions.Where(d => !d.Automatic).ToList();
            if (choices.Count == 0) command = decisions.First(d => d.Automatic).Command;
            else
            {
                int i;
                (i, rng) = RandomIndex(choices.Count, rng);
                var selected = choices[i];
                if (selected.FollowUp is { } fu)
                {
                    (i, rng) = RandomIndex(fu.Count, rng);
                    command = fu[i];
                }
                else command = selected.Command;
            }
        }
        else if (strategy == "strategic") command = new Strategic().Choose(state, player, ctx, decisions, LegalDecisions);
        else command = ChooseHeuristic(state, player, ctx, decisions);

        if (Rules.Validate(state, command, ctx) is { } err)
            throw new InvalidOperationException($"BUG BOT: decisión legal rechazada ({err.Code}: {err.Message})");
        return (command, rng);
    }

    /// <summary>Igual que Choose, pero si solo puede pasar responde al instante (live.ts botMove).</summary>
    public static (Command command, uint rng) Move(string strategy, GameState state, GameContext ctx, uint rng)
    {
        var player = Rules.PlayerFor(state);
        var only = Rules.LegalActions(state, player, ctx);
        if (only.Count == 1 && only[0].Type == "PASS") return (only[0], rng);
        return Choose(strategy, state, player, ctx, rng);
    }
}
