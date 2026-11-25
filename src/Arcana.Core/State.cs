// Estado del juego (port de core/state/types.ts). Registros inmutables: toda actualización usa `with`
// y crea arreglos/diccionarios nuevos; nunca se muta un estado ya publicado.
using System.Text.Json.Serialization;
using Arcana.Content;

namespace Arcana.Core;

public static class P
{
    public const string A = "A", B = "B";
    public static readonly string[] Ids = [A, B];
    public static string Other(string p) => p == A ? B : A;
}

public sealed record Buff(int Atk, int Def)
{
    public int Get(string stat) => stat == "atk" ? Atk : Def;
}

/// <summary>Copia concreta de una carta. Iid es único en toda la partida.</summary>
public record CardInstance
{
    public required string Iid { get; init; }
    public required string CardId { get; init; }
    /// <summary>Solo en la mano: bono acumulado (La Emperatriz) que la Unidad conserva al entrar.</summary>
    public Buff? Buff { get; init; }

    public CardInstance Plain() => new() { Iid = Iid, CardId = CardId };
    /// <summary>Las fichas (Retoño) llevan «:tk» en su iid: desaparecen al salir del campo.</summary>
    public static bool IsToken(string iid) => iid.Contains(":tk");
    /// <summary>Marca de un objetivo desviado por Cambio de Rumbo.</summary>
    public const string Redirected = "#r";
}

public sealed record StatModifier
{
    public required string Id { get; init; }
    public required string Stat { get; init; }
    public required int Amount { get; init; }
    /// <summary>endOfRound | combat | whileSourceInPlay | permanent</summary>
    public required string Duration { get; init; }
    public string? SourceIid { get; init; }
}

public sealed record UnitInPlay : CardInstance
{
    public required string Position { get; init; }
    public bool HasAttackedThisCombat { get; init; }
    public bool PositionChangedThisTurn { get; init; }
    public int SummonedInRound { get; init; }
    public StatModifier[] Modifiers { get; init; } = [];
    /// <summary>Los Enamorados: carta acoplada bajo esta Unidad (una sola Unidad emparejada con dos cartas).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CardInstance? Attached { get; init; }
    /// <summary>ATQ y DEF base de la Unidad emparejada (ArcanaRules.PairStats), en lugar de los de su carta.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PairAtk { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PairDef { get; init; }
}

public sealed record SpellInPlay : CardInstance
{
    public required Duration RemainingDuration { get; init; }
    public int UsesThisTurn { get; init; }
    public int UsesThisRound { get; init; }
}

public sealed record PlayerState
{
    public required string Id { get; init; }
    public int Seat { get; init; }
    public required string DeckId { get; init; }
    public int Hp { get; init; }
    /// <summary>El índice 0 es la carta superior.</summary>
    public CardInstance[] Deck { get; init; } = [];
    public CardInstance[] Hand { get; init; } = [];
    public CardInstance[] Discard { get; init; } = [];
    public UnitInPlay?[] UnitSlots { get; init; } = [];
    public SpellInPlay?[] SpellSlots { get; init; } = [];
    public int Energy { get; init; }
    public int MaxEnergy { get; init; }
    public int FailedDraws { get; init; }
    public bool MulliganDone { get; init; }
    public IReadOnlyDictionary<string, int> TurnCounters { get; init; } = Counters.Empty;
    public IReadOnlyDictionary<string, int> RoundCounters { get; init; } = Counters.Empty;

    [JsonIgnore] public IEnumerable<UnitInPlay> Units => UnitSlots.OfType<UnitInPlay>();
    /// <summary>Unidades y Hechizos en campo, por espacio (los null incluidos, como fieldCards en TS).</summary>
    [JsonIgnore] public IEnumerable<CardInstance?> FieldCards => UnitSlots.Cast<CardInstance?>().Concat(SpellSlots);
    public UnitInPlay? Unit(string iid) => UnitSlots.FirstOrDefault(u => u?.Iid == iid);
}

public static class Counters
{
    public static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>();
    public static int Get(this IReadOnlyDictionary<string, int> c, string k) => c.TryGetValue(k, out var v) ? v : 0;
    public static IReadOnlyDictionary<string, int> Set(this IReadOnlyDictionary<string, int> c, string k, int v) =>
        new Dictionary<string, int>(c) { [k] = v };
}

/// <summary>Un eslabón de la cadena (§8). El coste ya se pagó al declararlo.</summary>
public sealed record ChainLink
{
    public int Id { get; init; }
    /// <summary>summon | replace | spell | trick | attack | ability</summary>
    public required string Kind { get; init; }
    public required string Player { get; init; }
    public required string CardId { get; init; }
    public string? HandIid { get; init; }
    public int Cost { get; init; }
    public Ev[] Events { get; init; } = [];
    public string[] Targets { get; init; } = [];
    public bool Negated { get; init; }
}

public sealed record ChainState
{
    public ChainLink[] Stack { get; init; } = [];
    public required string Priority { get; init; }
    public int Passes { get; init; }
    public int NextId { get; init; }
}

public sealed record CombatPrevention(string AttackerIid, string Iid, string Player);
public sealed record EffectPrevention(int EffectLinkId, string Iid, string Player);

public sealed record Players
{
    [JsonPropertyName("A")] public required PlayerState A { get; init; }
    [JsonPropertyName("B")] public required PlayerState B { get; init; }
    public PlayerState this[string id] => id == P.A ? A : B;
    public Players With(string id, PlayerState p) => id == P.A ? this with { A = p } : this with { B = p };
}

public sealed record GameState
{
    public int Version { get; init; } = 1;
    public uint RngState { get; init; }
    public int Round { get; init; } = 1;
    /// <summary>mulligan | roundStart | turnA | turnB | combat | roundEnd | gameOver</summary>
    public string Phase { get; init; } = "mulligan";
    public string? ActivePlayer { get; init; }
    public string? Initiative { get; init; }
    public required Players Players { get; init; }
    /// <summary>A | B | draw | null</summary>
    public string? Winner { get; init; }
    public ChainState? Chain { get; init; }
    public CombatPrevention[] CombatPreventions { get; init; } = [];
    public EffectPrevention[] EffectPreventions { get; init; } = [];

    public GameState Update(string id, Func<PlayerState, PlayerState> fn) =>
        this with { Players = Players.With(id, fn(Players[id])) };
}

/// <summary>Evento de dominio (unión DomainEvent de state/events.ts): un tipo y sus campos opcionales.</summary>
public sealed record Ev
{
    public required string Type { get; init; }
    public string? Player { get; init; }
    public int? Count { get; init; }
    public int? Round { get; init; }
    public int? Energy { get; init; }
    public string? Initiative { get; init; }
    public int? Remaining { get; init; }
    public bool? TurnDraw { get; init; }
    public string? Iid { get; init; }
    public bool? AsAbilityCost { get; init; }
    public string? Key { get; init; }
    public string? Per { get; init; }
    public int? Value { get; init; }
    public int? Amount { get; init; }
    public string? SourceIid { get; init; }
    public string? VisibleTo { get; init; }
    public int? Damage { get; init; }
    public string? CardId { get; init; }
    public string? Position { get; init; }
    public string[]? Targets { get; init; }
    public int? LinkId { get; init; }
    public string? OldIid { get; init; }
    public int? Delta { get; init; }
    public string? Cause { get; init; }
    public string? SourcePlayer { get; init; }
    public Ev[]? Changes { get; init; }
    public string? Trigger { get; init; }
    // Recorded on resolved ability events for the client. The command and replay formats do not need it.
    [JsonIgnore] public bool EffectExecuted { get; init; }
    public string? AttackIid { get; init; }
    public int? EffectLinkId { get; init; }
    public string? AttackerIid { get; init; }
    public string? TargetIid { get; init; }
    public bool? OwnEffect { get; init; }
    public Ev[]? Events { get; init; }
    public string? Stat { get; init; }
    public string? Duration { get; init; }
    public int? N { get; init; }
    public CardInstance[]? Cards { get; init; }
    public string? FromIid { get; init; }
    public string? ToIid { get; init; }
    public int? ResolvedLinks { get; init; }
    public int? RemainingLinks { get; init; }
    public string? ByPlayer { get; init; }
    public int? Sides { get; init; }
    public string[]? Iids { get; init; }
    public string?[]? Order { get; init; }
    public string? Winner { get; init; }
}

/// <summary>Resultado de una transición: estado nuevo + eventos producidos.</summary>
public sealed record Step(GameState State, IReadOnlyList<Ev> Events);

public sealed record GameError(string Code, string Message);

public readonly struct Result<T>
{
    public readonly T? Value;
    public readonly GameError? Error;
    Result(T? v, GameError? e) { Value = v; Error = e; }
    public bool Ok => Error is null;
    public static Result<T> Success(T v) => new(v, null);
    public static Result<T> Fail(string code, string message) => new(default, new GameError(code, message));
    public static implicit operator Result<T>(GameError e) => new(default, e);
}

public static class R
{
    public static GameError Err(string code, string message) => new(code, message);
}
