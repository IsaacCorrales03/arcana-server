// Esquema de contenido (port de src/content/schema.ts). Todo es DATOS: ningún campo contiene código.
// Las uniones discriminadas de TypeScript se representan como una clase con `Kind` y campos opcionales.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcana.Content;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Duración de un Hechizo: rondas o «indefinite» (Rounds = null).</summary>
[JsonConverter(typeof(DurationConverter))]
public readonly record struct Duration(int? Rounds)
{
    public static readonly Duration Indefinite = new(null);
    public bool IsIndefinite => Rounds is null;
}

public sealed class DurationConverter : JsonConverter<Duration>
{
    public override Duration Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        r.TokenType == JsonTokenType.String ? Duration.Indefinite : new Duration(r.GetInt32());

    public override void Write(Utf8JsonWriter w, Duration v, JsonSerializerOptions o)
    {
        if (v.Rounds is int n) w.WriteNumberValue(n);
        else w.WriteStringValue("indefinite");
    }
}

public sealed class ArcanaInfo
{
    public string Key { get; set; } = "";
    public int Number { get; set; }
}

public sealed class CardDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>unit | spell | trick</summary>
    public string Type { get; set; } = "";
    /// <summary>wands | cups | swords | pentacles; null en Arcanos Mayores.</summary>
    public string? School { get; set; }
    public ArcanaInfo? Arcana { get; set; }
    public int Cost { get; set; }
    public string? Rarity { get; set; }
    public int? Atk { get; set; }
    public int? Def { get; set; }
    public Duration? Duration { get; set; }
    public string Text { get; set; } = "";
    public List<EffectDefinition>? Effects { get; set; }
    public string? Partner { get; set; }
    public bool? Token { get; set; }

    public int? Stat(string stat) => stat == "atk" ? Atk : Def;
}

public sealed class DeckEntry
{
    public string Id { get; set; } = "";
    public int Copies { get; set; }
}

public sealed class DeckDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<DeckEntry> Entries { get; set; } = [];
    public string? Arcano { get; set; }
    public bool SeparateArcano { get; set; }
    public string? Description { get; set; }
    public string? CoverCardId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Starter { get; set; }
    public string? CardBack { get; set; }
    public List<string>? Order { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Copy { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Key => Copy == 0 ? Id : $"{Id}~{Copy}";
    [System.Text.Json.Serialization.JsonIgnore] public string Family => !SeparateArcano ? Id :
        ContentLoader.Load().Cards.FirstOrDefault(c => c.Id == Arcano)?.Arcana?.Key ?? "";
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<DeckEntry> WithArcano =>
        SeparateArcano && Arcano is { } id ? Entries.Append(new DeckEntry { Id = id, Copies = 1 }) : Entries;
}

public sealed class Limit
{
    public string Per { get; set; } = "turn";
    public int Times { get; set; }
}

/// <summary>Cuándo se activa un efecto (unión Trigger de schema.ts).</summary>
public sealed class Trigger
{
    public string Kind { get; set; } = "";
    public string? Cause { get; set; }
    public string? By { get; set; }
    public bool? Other { get; set; }
    public int? CostAtMost { get; set; }
    public int? AtLeast { get; set; }
    public string? Whose { get; set; }
    public bool? BeforeDraw { get; set; }
    public Limit? Limit { get; set; }
    public string? Position { get; set; }
    public bool? AsAbilityCost { get; set; }
}

public sealed class Condition
{
    public string Kind { get; set; } = "";
    public string? Position { get; set; }
    public string? Per { get; set; }
    public int Value { get; set; }
    public bool? Printed { get; set; }
    public string? CardId { get; set; }
    public string? Key { get; set; }
}

public sealed class EffectDuration
{
    /// <summary>untilEndOfRound | thisCombat | whileCondition | whileInPlay | permanent</summary>
    public string Kind { get; set; } = "";
}

public sealed class TargetSpec
{
    /// <summary>self | unit | spell | attackParticipant | contextual</summary>
    public string Kind { get; set; } = "";
    public string? Owner { get; set; }
    public bool? ExcludeSource { get; set; }
    public int? CostAtMost { get; set; }
    public bool? All { get; set; }
    public bool? ExcludeAttackTarget { get; set; }
    public bool? Paired { get; set; }
    public bool? Leader { get; set; }
    public string? Role { get; set; }
}

public sealed class RollOutcome
{
    public int Min { get; set; }
    public List<EffectAction> Actions { get; set; } = [];
}

/// <summary>Acción atómica (unión Action de schema.ts).</summary>
public sealed class EffectAction
{
    public string Kind { get; set; } = "";
    public TargetSpec? Target { get; set; }
    public int Count { get; set; }
    public string? Player { get; set; }
    public string? Stat { get; set; }
    public int Amount { get; set; }
    public EffectDuration? Duration { get; set; }
    public int? Max { get; set; }
    public string? Per { get; set; }
    public string? Key { get; set; }
    public string? What { get; set; }
    public int Sides { get; set; }
    public List<RollOutcome>? Outcomes { get; set; }
    public string? CardId { get; set; }
    public string? Position { get; set; }
    public int CostAtMost { get; set; }
    public string? Scope { get; set; }
    public string? OncePer { get; set; }
    public int Min { get; set; }
    public string? Timing { get; set; }

    public EffectAction With(int amount)
    {
        var c = (EffectAction)MemberwiseClone();
        c.Amount = amount;
        return c;
    }
}

public sealed class Cap
{
    public string Per { get; set; } = "round";
    public int Amount { get; set; }
}

public sealed class EffectDefinition
{
    public Trigger Trigger { get; set; } = new();
    public List<Condition>? Conditions { get; set; }
    public List<EffectAction>? Cost { get; set; }
    public List<EffectAction> Actions { get; set; } = [];
    public bool? Optional { get; set; }
    public List<EffectAction>? ThenIfDone { get; set; }
    public Cap? Cap { get; set; }

    public EffectDefinition WithThenIfDone(List<EffectAction>? then)
    {
        var c = (EffectDefinition)MemberwiseClone();
        c.ThenIfDone = then;
        return c;
    }
}
