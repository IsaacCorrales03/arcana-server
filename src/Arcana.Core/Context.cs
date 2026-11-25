// Comandos, contexto y comportamientos (port de rules/commands.ts y events/listener.ts).
using Arcana.Content;

namespace Arcana.Core;

/// <summary>Comando del jugador (unión Command de commands.ts). Type decide qué campos se usan.</summary>
public sealed record Command
{
    public required string Type { get; init; }
    public string? Player { get; init; }
    public string[]? Iids { get; init; }
    public string? Iid { get; init; }
    public string? Position { get; init; }
    public string[]? Targets { get; init; }
    public string? UnitIid { get; init; }
    public string? SourceIid { get; init; }
    public string? AttackerIid { get; init; }
    /// <summary>iid de la Unidad objetivo, o null para ataque directo.</summary>
    public string? TargetIid { get; init; }

    public static Command Pass(string p) => new() { Type = "PASS", Player = p };
    public override string ToString() => System.Text.Json.JsonSerializer.Serialize(this, Json.Options);
}

/// <summary>Decisiones que surgen al resolver y no se anunciaron con el comando. null = comportamiento por defecto.</summary>
public interface IPlayerChoices
{
    /// <summary>Nuevo orden (iids, el primero queda arriba) de las cartas que `player` mira con Presagio.</summary>
    IReadOnlyList<string>? OmenOrder(GameState s, string player, IReadOnlyList<CardInstance> cards) => null;
    /// <summary>iid de la mano de `player` que descarta.</summary>
    string? Discard(GameState s, string player) => null;
    /// <summary>¿`player` usa el «puedes» de una automática de `cardId`? null = sí.</summary>
    bool? Accept(GameState s, string player, string cardId) => null;
    /// <summary>Objetivo que `player` elige para una automática con varias candidatas. null = la primera.</summary>
    string? Target(GameState s, string player, string cardId, IReadOnlyList<string> candidates) => null;
}

public sealed record Self(string Iid, string CardId, string Owner);

public sealed record ListenerCall(GameState State, Ev Event, Self Self, GameContext Ctx);

/// <summary>consume: el evento no se aplica; emit: eventos nuevos, se resuelven antes.</summary>
public sealed record Reaction(IReadOnlyList<Ev>? Emit = null, bool Consume = false);

public sealed class Listener(string listensTo, Func<ListenerCall, Reaction?> run, Func<ListenerCall, bool>? when = null)
{
    public string ListensTo { get; } = listensTo;
    public Func<ListenerCall, bool>? When { get; } = when;
    public Func<ListenerCall, Reaction?> Run { get; } = run;
}

public sealed record InvokeCall(GameState State, Self Self, GameContext Ctx, IReadOnlyList<string>? Targets, int? LinkId);

public sealed record TrickCall(GameState State, Self Self, GameContext Ctx, IReadOnlyList<ChainLink> Below, IReadOnlyList<string> Targets);

public sealed class TrickBehavior
{
    public Func<TrickCall, bool>? When { get; init; }
    public required Func<TrickCall, IReadOnlyList<Ev>> Run { get; init; }
}

public sealed class CardBehavior
{
    public TrickBehavior? Trick { get; set; }
    public Func<InvokeCall, IReadOnlyList<Ev>>? Enter { get; set; }
    public Func<InvokeCall, IReadOnlyList<Ev>>? Activate { get; set; }
    /// <summary>«Al emparejar»: se dispara una vez al acoplar, para las dos cartas de la Unidad.</summary>
    public Func<InvokeCall, IReadOnlyList<Ev>>? Paired { get; set; }
    public List<Listener> Listeners { get; } = [];
}

/// <summary>Datos de solo lectura que el motor necesita además del estado.</summary>
public sealed record GameContext(
    IReadOnlyDictionary<string, CardDefinition> Cards,
    IReadOnlyDictionary<string, CardBehavior> Behaviors,
    IPlayerChoices? Choices = null)
{
    public CardDefinition? Card(string id) => Cards.TryGetValue(id, out var c) ? c : null;
    public CardBehavior? Behavior(string id) => Behaviors.TryGetValue(id, out var b) ? b : null;
    public GameContext WithoutChoices() => this with { Choices = null };

    public static GameContext Create(IEnumerable<CardDefinition> cards, IPlayerChoices? choices = null)
    {
        var list = cards.ToList();
        // los efectos declarativos se compilan aquí (una sola vez por contexto)
        var behaviors = list.Where(c => c.Effects is { Count: > 0 }).ToDictionary(c => c.Id, Effects.Compile);
        return new GameContext(list.ToDictionary(c => c.Id), behaviors, choices);
    }

    public static int SpellCost(GameState s, string player, CardDefinition def)
    {
        var discount = s.Players[player].RoundCounters.Get("nextSpellDiscount");
        return Math.Max(1, def.Cost - discount);
    }
}
