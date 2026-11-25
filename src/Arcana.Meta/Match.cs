// Partida en vivo (port de client/live.ts): aplica órdenes de la persona, de la IA o del rival en línea.
// Si al resolver surge una decisión de una persona (Presagio, descarte, «puedes», objetivo) se lanza NeedChoice y se
// repite la orden con la respuesta añadida: el motor es puro, repetir es seguro.
using Arcana.AI;
using Arcana.Content;
using Arcana.Core;

namespace Arcana.Meta;

public sealed class NeedChoice(string kind, string player, IReadOnlyList<string> cards) : Exception("Falta una decisión del jugador")
{
    /// <summary>omen | discard | accept | target</summary>
    public string Kind { get; } = kind;
    public string Player { get; } = player;
    /// <summary>omen: iids a ordenar · discard: la mano · accept: [cardId] · target: candidatas.</summary>
    public IReadOnlyList<string> Cards { get; } = cards;
}

/// <summary>Respuesta a una decisión: orden (lista), iid (texto) o sí/no.</summary>
public sealed record Answer(IReadOnlyList<string>? Order = null, string? Iid = null, bool? Yes = null);

/// <summary>Una orden aplicada: lo que la partida guarda para repetirla igual (orden + respuestas).</summary>
public sealed record Played(Command Command, IReadOnlyList<Answer> Answers, GameState Before, IReadOnlyList<Ev> Events, GameState After);

public sealed class Match
{
    public GameState State { get; private set; }
    public GameState Initial { get; }
    public GameContext Ctx { get; }
    public GameContext BaseCtx { get; }
    public string Me { get; }
    public uint Rng { get; private set; }
    public bool Online { get; }
    public string Bot { get; }
    public long Seed { get; }
    public DeckDefinition[] Decks { get; }
    public List<Played> Log { get; } = [];

    public Match(GameState initial, GameContext baseCtx, string me, long seed, bool online, string bot, DeckDefinition[] decks)
    {
        State = Initial = initial;
        BaseCtx = baseCtx;
        // decisiones al resolver: las del plan de la IA (también en sus simulaciones); las de una persona se piden en Send()
        Ctx = online ? baseCtx : baseCtx with { Choices = new PlanChoices(baseCtx) };
        Me = me;
        Seed = seed;
        Rng = unchecked((uint)(seed ^ 0xa5a5a5a5));
        Online = online;
        Bot = bot;
        Decks = decks;
    }

    public sealed record StartOptions(string Bot = "strategic", int? RivalHp = null, bool First = false, CardOrder? MyOrder = null, CardOrder? RivalOrder = null,
        IReadOnlyList<string>? RivalField = null, int? MyHp = null, IReadOnlyList<string>? MyField = null);

    /// <summary>decks = [persona 1, persona 2]; seat = cuál soy. En línea ambos clientes usan la misma semilla.</summary>
    public static Match Start(DeckDefinition deck1, DeckDefinition deck2, GameContext baseCtx, int seat = 1, long? seed = null, bool online = false, StartOptions? opts = null)
    {
        opts ??= new StartOptions();
        var s = seed ?? Random.Shared.Next(int.MaxValue);
        var setup = Rounds.Setup(s, deck1, deck2);
        // la moneda la decide la semilla: para jugar primero se prueba la siguiente hasta que toque
        for (var k = 1; opts.First && setup.Ok && setup.Value!.Players.A.Seat != seat && k < 64; k++) setup = Rounds.Setup(++s, deck1, deck2);
        if (!setup.Ok) throw new InvalidOperationException(setup.Error!.Message);
        var state = setup.Value!;
        var me = state.Players.A.Seat == seat ? P.A : P.B;
        var rival = P.Other(me);
        if (opts.MyOrder is not null) state = state.Update(me, p => Guide.Arrange(p, opts.MyOrder));
        if (opts.RivalOrder is not null) state = state.Update(rival, p => Guide.Arrange(p, opts.RivalOrder));
        if (opts.RivalHp is int hp) state = state.Update(rival, p => p with { Hp = hp });
        if (opts.MyHp is int mine) state = state.Update(me, p => p with { Hp = mine });
        if (opts.RivalField is { Count: > 0 } field) state = Rounds.Preset(state, rival, field, baseCtx);
        if (opts.MyField is { Count: > 0 } own) state = Rounds.Preset(state, me, own, baseCtx);
        return new Match(state, baseCtx, me, s, online, opts.Bot, [deck1, deck2]);
    }

    /// <summary>Tutorial: vuelve al estado anterior a la orden número «count» del registro (ensayo sin castigo).</summary>
    public void Rewind(int count)
    {
        if (count < 0 || count >= Log.Count) return;
        State = Log[count].Before;
        Log.RemoveRange(count, Log.Count - count);
    }

    /// <summary>Quién decide ahora; null si la partida terminó.</summary>
    public string? TurnOf => State.Phase == "gameOver" ? null : Rules.PlayerFor(State);

    /// <summary>Órdenes legales de la persona (con variantes de objetivos).</summary>
    public List<Command> LegalOptions() => Rules.LegalActions(State, Me, Ctx, includeTargets: true);

    sealed class Asking(Match m, IReadOnlyList<Answer> answers, List<Answer>? recorded = null) : IPlayerChoices
    {
        int n;
        bool Person(string p) => m.Online || p == m.Me;
        Answer Ask(string kind, string p, IReadOnlyList<string> cards, Func<Answer> choose)
        {
            if (recorded is null)
                return n < answers.Count ? answers[n++] : throw new NeedChoice(kind, p, cards);
            var answer = choose();
            recorded.Add(answer);
            return answer;
        }

        public IReadOnlyList<string>? OmenOrder(GameState s, string p, IReadOnlyList<CardInstance> cards)
        {
            if (!Person(p)) return Plans.OmenOrder(s, p, cards, m.BaseCtx);
            var iids = cards.Select(c => c.Iid).ToList();
            if (iids.Count < 2) return iids;
            return Ask("omen", p, iids, () => new Answer(Order: Plans.OmenOrder(s, p, cards, m.BaseCtx))).Order ?? iids;
        }
        public string? Discard(GameState s, string p)
        {
            if (!Person(p)) return Plans.DiscardChoice(s, p, m.BaseCtx);
            var hand = s.Players[p].Hand.Select(c => c.Iid).ToList();
            if (hand.Count < 2) return hand.FirstOrDefault();
            return Ask("discard", p, hand, () => new Answer(Iid: Plans.DiscardChoice(s, p, m.BaseCtx))).Iid;
        }
        public bool? Accept(GameState s, string p, string cardId) => Person(p)
            ? Ask("accept", p, [cardId], () => new Answer(Yes: Plans.AcceptOptional(s, p))).Yes != false
            : Plans.AcceptOptional(s, p);
        public string? Target(GameState s, string p, string cardId, IReadOnlyList<string> candidates) =>
            Person(p) ? Ask("target", p, candidates, () => new Answer(Iid: candidates.FirstOrDefault())).Iid : null;
    }

    /// <summary>Aplica una orden. Lanza NeedChoice si falta una decisión de persona, o InvalidOperationException si el motor la rechaza.</summary>
    public Played Send(Command cmd, IReadOnlyList<Answer>? answers = null, uint? rng = null)
    {
        answers ??= [];
        return Apply(cmd, new Asking(this, answers), answers, rng);
    }

    /// <summary>Aplica una orden en un duelo entre dos IA y graba sus decisiones para la repetición.</summary>
    public Played SendSimulated(Command cmd, uint rng)
    {
        var answers = new List<Answer>();
        return Apply(cmd, new Asking(this, [], answers), answers, rng);
    }

    Played Apply(Command cmd, IPlayerChoices choices, IReadOnlyList<Answer> answers, uint? rng)
    {
        var before = State;
        var r = Rules.Apply(before, cmd, Ctx with { Choices = choices });
        if (!r.Ok) throw new InvalidOperationException($"{r.Error!.Code}: {r.Error.Message}");
        State = r.Value!.State;
        if (rng is uint x) Rng = x;
        var played = new Played(cmd, answers, before, r.Value.Events, State);
        Log.Add(played);
        return played;
    }

    /// <summary>Orden de la IA para quien tenga la decisión (si solo puede pasar, responde al instante).</summary>
    public (Command cmd, uint rng) BotMove() => Bots.Move(Bot, State, Ctx, Rng);

    /// <summary>Simula un ataque ahora mismo (lo calcula el motor, no la interfaz).</summary>
    public List<Ev> PreviewAttack(Command cmd) => cmd.Type != "DECLARE_ATTACK" ? []
        : Combat.ResolveClash(State, new Ev { Type = "ATTACK_CLASH", Player = cmd.Player, AttackerIid = cmd.AttackerIid, TargetIid = cmd.TargetIid }, Ctx);

    /// <summary>ATQ/DEF efectivos de una Unidad (base + modificadores + estáticos).</summary>
    public (int atk, int def) StatsOf(UnitInPlay u)
    {
        var def = Ctx.Card(u.CardId)!;
        return (ArcanaRules.EffectiveStat(u, def, "atk", Effects.StaticBonus(State, u.Iid, "atk", Ctx)),
                ArcanaRules.EffectiveStat(u, def, "def", Effects.StaticBonus(State, u.Iid, "def", Ctx)));
    }

    /// <summary>Registro para guardar y volver a ver: semilla, mazos y órdenes con sus respuestas.</summary>
    public ReplayFile ToReplay() => new()
    {
        Seed = Seed, Me = Me, Decks = Decks, Bot = Bot, Online = Online,
        Initial = Initial,
        Steps = Log.Select(l => new ReplayStep { Command = l.Command, Answers = l.Answers.ToList() }).ToList(),
    };
}

public sealed class ReplayStep
{
    public Command Command { get; set; } = new() { Type = "PASS" };
    public List<Answer> Answers { get; set; } = [];
}

/// <summary>Repetición propia del cliente Godot: el estado inicial y las órdenes bastan para reconstruirla exacta.</summary>
public sealed class ReplayFile
{
    public int Version { get; set; } = 1;
    public long Seed { get; set; }
    public string Me { get; set; } = P.A;
    public DeckDefinition[] Decks { get; set; } = [];
    public string Bot { get; set; } = "strategic";
    public bool Online { get; set; }
    public GameState? Initial { get; set; }
    public List<ReplayStep> Steps { get; set; } = [];
}
