// Bot estratégico (port de strategy/strategic.ts): simula cada acción legal con el motor real y elige la que más
// mejora la evaluación del plan de su mazo. No mira información oculta ni el dado (simula con un PRNG imaginado).
using Arcana.Core;

namespace Arcana.AI;

public sealed record Decision(Command Command, bool Automatic, IReadOnlyList<Command>? FollowUp = null)
{
    public IReadOnlyList<Command> Options => FollowUp ?? [Command];
}

public sealed class Strategic
{
    const double Win = 1_000_000;
    const double ActMargin = 0.25, RespondMargin = 0.5;

    // TS usaba una variable de módulo; aquí es estado de la instancia (una por decisión)
    bool tiro;

    static double Pain(int hp) => (30 - hp) + 0.4 * Math.Pow(Math.Max(0, 10 - hp), 2);

    sealed record Stats(int Atk, int Def, int TempAtk, int TempDef);

    static Stats StatsOf(GameState s, UnitInPlay u, GameContext ctx, Dictionary<string, Stats> cache)
    {
        if (cache.TryGetValue(u.Iid, out var hit)) return hit;
        var def = ctx.Card(u.CardId)!;
        int Temp(string k) => u.Modifiers.Where(m => m.Stat == k && m.Duration != "whileSourceInPlay").Sum(m => m.Amount);
        var st = new Stats(
            ArcanaRules.EffectiveStat(u, def, "atk", Effects.StaticBonus(s, u.Iid, "atk", ctx)),
            ArcanaRules.EffectiveStat(u, def, "def", Effects.StaticBonus(s, u.Iid, "def", ctx)),
            Temp("atk"), Temp("def"));
        cache[u.Iid] = st;
        return st;
    }

    static List<UnitInPlay> UnitsOf(GameState s, string p) => s.Players[p].Units.ToList();

    static double UnitWorth(GameState s, UnitInPlay u, PlanWeights w, GameContext ctx, Dictionary<string, Stats> cache)
    {
        var def = ctx.Card(u.CardId)!;
        var st = StatsOf(s, u, ctx, cache);
        double atk = st.Atk - Math.Max(0, st.TempAtk);
        double dfn = st.Def - Math.Max(0, st.TempDef);
        var body = u.Position == "atk" ? atk * w.Atk + dfn * w.Def * 0.35 : dfn * w.Def + atk * w.Atk * 0.35;
        return 2 + def.Cost * 0.8 + body + (def.Arcana is not null ? 1.5 : 0);
    }

    /// <summary>Pronóstico codicioso del próximo combate de `attacker`.</summary>
    static (double killed, double hp) Forecast(GameState s, string attacker, GameContext ctx, Dictionary<string, Stats> cache, PlanWeights victimW)
    {
        var foe = P.Other(attacker);
        var attackers = UnitsOf(s, attacker).Where(u => u.Position == "atk" && !u.HasAttackedThisCombat)
            .Select(u => (u, atk: StatsOf(s, u, ctx, cache).Atk)).OrderByDescending(x => x.atk).ToList();
        var foeUnits = UnitsOf(s, foe);
        var alive = foeUnits.Select(u => u.Iid).ToHashSet();
        double killed = 0, hp = 0;
        foreach (var (_, atk) in attackers)
        {
            if (atk <= 0) continue;
            if (alive.Count == 0)
            {
                hp += atk;
                continue;
            }
            (string iid, double gain, double worth, double dmg)? best = null;
            foreach (var t in foeUnits)
            {
                if (!alive.Contains(t.Iid)) continue;
                var ts = StatsOf(s, t, ctx, cache);
                var vs = t.Position == "atk" ? ts.Atk : ts.Def;
                if (atk <= vs) continue; // no se suicida ni intercambia
                var worth = UnitWorth(s, t, victimW, ctx, cache);
                double dmg = t.Position == "atk" ? atk - vs : 0;
                if (best is null || worth + dmg > best.Value.gain) best = (t.Iid, worth + dmg, worth, dmg);
            }
            if (best is { } b)
            {
                alive.Remove(b.iid);
                killed += b.worth;
                hp += b.dmg;
            }
        }
        return (killed, hp);
    }

    static bool OnlyHelpsUnits(string cardId, GameContext ctx) =>
        (ctx.Card(cardId)?.Effects ?? []).All(e => e.Actions.All(a => a.Kind is "modifyStat" or "preventDestruction"));

    static double SpellWorth(GameState s, string p, GameContext ctx)
    {
        var hasUnits = s.Players[p].UnitSlots.Any(u => u is not null);
        double n = 0;
        foreach (var sp in s.Players[p].SpellSlots)
        {
            if (sp is null) continue;
            var cost = ctx.Card(sp.CardId)?.Cost ?? 0;
            var life = sp.RemainingDuration.IsIndefinite ? 1 : Math.Min(sp.RemainingDuration.Rounds!.Value, 3) / 3.0;
            var relevance = hasUnits || !OnlyHelpsUnits(sp.CardId, ctx) ? 1 : 0.25;
            n += (1 + cost * 0.9 * life) * relevance;
        }
        return n;
    }

    /// <summary>Evaluación del estado desde el punto de vista de `me` según su plan. Mayor = mejor.</summary>
    public static double Evaluate(GameState s, string me, GameContext ctx, DeckPlan? plan = null)
    {
        plan ??= Plans.For(s.Players[me].DeckId);
        var foe = P.Other(me);
        if (s.Phase == "gameOver") return s.Winner == me ? Win : s.Winner == "draw" ? -Win / 5 : -Win;
        var w = plan.Weights;
        var mine = s.Players[me];
        var theirs = s.Players[foe];
        var cache = new Dictionary<string, Stats>();
        var mainTurn = s.Phase is "turnA" or "turnB";

        var v = w.OppHp * Pain(theirs.Hp) - w.SelfHp * (Pain(mine.Hp) + 1.5 * Math.Max(0, plan.HpFloor - mine.Hp));
        // sumas en orden (como reduce en TS): el orden de suma de doubles cambia el resultado
        v += w.Board * (UnitsOf(s, me).Aggregate(0.0, (n, u) => n + UnitWorth(s, u, w, ctx, cache)) - UnitsOf(s, foe).Aggregate(0.0, (n, u) => n + UnitWorth(s, u, w, ctx, cache)));
        v += w.Spell * SpellWorth(s, me, ctx) - 0.8 * SpellWorth(s, foe, ctx);
        v += w.Hand * mine.Hand.Length - 0.5 * theirs.Hand.Length;
        if (mine.Deck.Length < 4) v -= (4 - mine.Deck.Length) * 1.5;
        if (theirs.Deck.Length < 4) v += (4 - theirs.Deck.Length) * 0.8;

        // Marca de ronda armada (P00 activada): vale por las curaciones que aún puede pagar este turno.
        var armed = mine.RoundCounters.Count(kv => kv.Key.StartsWith("flag:") && kv.Value > 0);
        if (armed > 0)
        {
            var healers = mine.Hand.Count(c => ctx.Card(c.CardId) is { } d && d.Cost <= mine.Energy && (d.Effects ?? []).Any(e => e.Actions.Any(a => a.Kind == "heal")));
            v += armed * Math.Min(healers, 2) * 1.2;
        }

        // EN guardada solo vale si hay Trucos en mano que pagar como reacción.
        var trickCosts = mine.Hand.Select(c => ctx.Card(c.CardId)).Where(d => d?.Type == "trick").Select(d => d!.Cost).ToList();
        if (trickCosts.Count > 0) v += w.Reserve * Math.Min(mine.Energy, trickCosts.Max());

        double Value(string attacker, (double killed, double hp) f)
        {
            var victim = P.Other(attacker);
            var victimHp = s.Players[victim].Hp;
            var hpValue = PainD(victimHp - f.hp) - Pain(victimHp);
            return attacker == me
                ? w.Attack * (f.killed * w.Board + hpValue * w.OppHp)
                : -w.Threat * (f.killed * w.Board + hpValue * w.SelfHp);
        }
        var plannedW = Plans.For(theirs.DeckId).Weights;
        var current = mainTurn ? s.ActivePlayer : s.Phase == "combat" ? s.Initiative : null;
        if (mainTurn && current is not null && !Rounds.IsOpeningTurn(s.Round, current))
            v += Value(current, Forecast(s, current, ctx, cache, current == me ? plannedW : w));
        var next = current is null ? null : P.Other(current);
        if (next is not null) v += (mainTurn ? 0.5 : 0.6) * Value(next, Forecast(s, next, ctx, new Dictionary<string, Stats>(), next == me ? plannedW : w));
        return v;
    }

    static double PainD(double hp) => (30 - hp) + 0.4 * Math.Pow(Math.Max(0, 10 - hp), 2);

    // ---------- simulación ----------

    sealed record Sim(GameState State, List<Ev> Events);

    void Marca(IReadOnlyList<Ev> events) { if (events.Any(e => e.Type == "DICE_ROLLED")) tiro = true; }

    static GameState Imaginado(GameState s, int k)
    {
        unchecked
        {
            var mix = s.RngState ^ (0x9e3779b9u + (uint)k * 0x632be5abu);
            return s with { RngState = mix * 0x85ebca6bu };
        }
    }

    double Esperado(GameState state, Func<GameState, double> f)
    {
        var antes = tiro;
        tiro = false;
        var v = f(Imaginado(state, 0));
        if (tiro)
        {
            for (var k = 1; k < 4; k++) v += f(Imaginado(state, k));
            v /= 4;
        }
        tiro = antes || tiro;
        return v;
    }

    Sim Settle(Sim sim, GameContext ctx)
    {
        var state = sim.State;
        var events = sim.Events.ToList();
        for (var guard = 0; state.Chain is not null && state.Phase != "gameOver" && guard < 64; guard++)
        {
            var r = Rules.ApplyStep(state, Command.Pass(state.Chain.Priority), ctx);
            if (!r.Ok) break;
            state = r.Value!.State;
            events.AddRange(r.Value.Events);
            Marca(r.Value.Events);
        }
        return new Sim(state, events);
    }

    Sim? StepSim(GameState state, Command cmd, GameContext ctx)
    {
        var r = Rules.ApplyStep(state, cmd, ctx);
        if (r.Ok) Marca(r.Value!.Events);
        return r.Ok ? new Sim(r.Value!.State, r.Value.Events.ToList()) : null;
    }

    static double Score(Sim sim, string me, GameContext ctx, DeckPlan plan)
    {
        var seen = sim.Events.Sum(e => e.Type == "OMEN_ACTIVATED" && e.Player == me ? e.Cards!.Length : 0);
        return Evaluate(sim.State, me, ctx, plan) + plan.Weights.Omen * seen;
    }

    static List<Command> OptionsOf(IEnumerable<Decision> decisions) => decisions.SelectMany(d => d.Options).ToList();

    (Command command, double value) BestResponse(GameState state, string me, GameContext ctx, DeckPlan plan, Func<GameState, string, GameContext, List<Decision>> legal)
    {
        var pass = Command.Pass(me);
        double Valor(Command cmd) => Esperado(state, s =>
        {
            var sim = StepSim(s, cmd, ctx);
            return sim is not null ? Score(Settle(sim, ctx), me, ctx, plan) : double.NegativeInfinity;
        });
        var best = (command: pass, value: Valor(pass));
        var passValue = best.value;
        foreach (var cmd in OptionsOf(legal(state, me, ctx)))
        {
            if (cmd.Type is not ("PLAY_TRICK" or "PLAY_SPELL")) continue;
            var value = Valor(cmd);
            if (value > best.value) best = (cmd, value);
        }
        return best.command.Type != "PASS" && best.value < passValue + RespondMargin ? (pass, passValue) : best;
    }

    double AttackValue(GameState state, Command cmd, string me, GameContext ctx, DeckPlan plan, Func<GameState, string, GameContext, List<Decision>> legal)
    {
        var sim = StepSim(state, cmd, ctx);
        if (sim is null) return double.NegativeInfinity;
        if (sim.State.Chain is not null && sim.State.Chain.Priority != me)
        {
            var passed = StepSim(sim.State, Command.Pass(sim.State.Chain.Priority), ctx);
            if (passed is not null) sim = new Sim(passed.State, [.. sim.Events, .. passed.Events]);
        }
        if (sim.State.Chain is not null && sim.State.Chain.Priority == me && sim.State.Phase != "gameOver")
            return BestResponse(sim.State, me, ctx, plan, legal).value;
        return Score(Settle(sim, ctx), me, ctx, plan);
    }

    /// <summary>Decide una acción del bot estratégico entre las decisiones legales dadas.</summary>
    public Command Choose(GameState state, string me, GameContext ctx, List<Decision> decisions, Func<GameState, string, GameContext, List<Decision>> legal)
    {
        var plan = Plans.For(state.Players[me].DeckId);
        var all = OptionsOf(decisions);

        if (state.Phase == "mulligan")
        {
            var wanted = Plans.MulliganChoice(state, me, ctx).ToHashSet();
            return all.FirstOrDefault(c => c.Type == "MULLIGAN" && c.Iids!.Length == wanted.Count && c.Iids.All(wanted.Contains))
                ?? all.FirstOrDefault(c => c.Type == "MULLIGAN" && c.Iids!.Length == 0) ?? all[0];
        }

        var real = decisions.Where(d => !d.Automatic).ToList();
        if (real.Count == 0) return decisions[0].Command;

        if (state.Chain is not null) return BestResponse(state, me, ctx, plan, legal).command;

        // la referencia es «no hacer nada»: ir a la Batalla (si se puede) o terminar
        var idle = all.FirstOrDefault(c => c.Type == "BATTLE") ?? all.FirstOrDefault(c => c.Type is "END_COMBAT" or "END_TURN");
        var best = (command: idle, value: Score(new Sim(state, []), me, ctx, plan) + ActMargin);
        foreach (var cmd in all)
        {
            if (cmd.Type is "END_COMBAT" or "END_TURN" or "BATTLE" or "PASS" or "START_ROUND") continue;
            var value = cmd.Type == "DECLARE_ATTACK"
                ? Esperado(state, s => AttackValue(s, cmd, me, ctx, plan, legal))
                : Esperado(state, s =>
                {
                    var sim = StepSim(s, cmd, ctx);
                    return sim is not null ? Score(Settle(sim, ctx), me, ctx, plan) : double.NegativeInfinity;
                });
            if (value > best.value) best = (cmd, value);
        }
        return best.command ?? decisions.FirstOrDefault(d => d.Automatic)?.Command ?? all[0];
    }
}
