// Validación, acciones legales, prioridad y aplicación de comandos
// (port de rules/validate.ts, legal.ts, priority.ts y apply.ts).
using Arcana.Content;

namespace Arcana.Core;

public static class Rules
{
    static readonly GameError? OK = null;
    static bool IsMainTurn(GameState s) => s.Phase is "turnA" or "turnB";

    static GameError? OwnTurn(GameState s, string? player)
    {
        if (s.Phase == "gameOver") return R.Err("WRONG_PHASE", "La partida terminó");
        if (!IsMainTurn(s)) return R.Err("WRONG_PHASE", "Solo se puede en el turno principal");
        if (s.ActivePlayer != player) return R.Err("NOT_YOUR_TURN", $"No es el turno de {player}");
        return OK;
    }

    static GameError? HasPriority(GameState s, string? player) =>
        s.Chain?.Priority == player ? OK : R.Err("NO_PRIORITY", "No tienes la prioridad en la cadena");

    /// <summary>Hechizos: turno propio, ventanas de combate (ambos jugadores) o en respuesta con prioridad (§8).</summary>
    static GameError? SpellWindow(GameState s, string? player)
    {
        if (s.Chain is not null) return HasPriority(s, player);
        if (s.Phase == "combat") return OK;
        return OwnTurn(s, player);
    }

    /// <summary>En combate actúa primero la iniciativa; tras END_COMBAT, el rival (ventana final).</summary>
    public static string CombatHolder(GameState s) => s.ActivePlayer ?? s.Initiative ?? P.A;

    static GameError? HandCard(GameState s, GameContext ctx, string player, string? iid, string type, out CardInstance? inst, out CardDefinition? def)
    {
        inst = s.Players[player].Hand.FirstOrDefault(c => c.Iid == iid);
        def = null;
        if (inst is null || (s.Chain?.Stack.Any(l => l.HandIid == iid) ?? false)) return R.Err("CARD_NOT_IN_HAND", $"La carta {iid} no está en la mano");
        def = ctx.Card(inst.CardId);
        if (def is null) return R.Err("UNKNOWN_CARD", $"Carta desconocida {inst.CardId}");
        if (def.Type != type) return R.Err("WRONG_CARD_TYPE", $"{def.Name} no es de tipo {type}");
        return OK;
    }

    static GameError? CanPay(PlayerState p, CardDefinition def) =>
        p.Energy >= def.Cost ? OK : R.Err("INSUFFICIENT_ENERGY", $"Faltan EN: cuesta {def.Cost} y hay {p.Energy}");

    static bool IsMajor(GameContext ctx, string cardId) => ctx.Card(cardId)?.Arcana is not null;

    /// <summary>Máx. 1 Arcano Mayor en campo; solo la destrucción lo libera (no se reemplaza).</summary>
    static GameError? MajorLimit(PlayerState p, GameContext ctx, CardDefinition def, UnitInPlay? leaving = null)
    {
        if (leaving is not null && IsMajor(ctx, leaving.CardId)) return R.Err("MAJOR_ARCANA_LIMIT", "Un Arcano Mayor no puede reemplazarse");
        var another = p.Units.Any(u => u.Iid != leaving?.Iid && IsMajor(ctx, u.CardId));
        return def.Arcana is not null && another ? R.Err("MAJOR_ARCANA_LIMIT", "Ya controlas un Arcano Mayor") : OK;
    }

    /// <summary>Los Enamorados: cada Unidad emparejada de su dueño puede atacar dos veces por Batalla.</summary>
    static bool CanAttackAgain(GameState s, string player, UnitInPlay u, GameContext ctx)
    {
        var owner = s.Players[player];
        return ArcanaRules.Has(s, player, "lovers") && owner.TurnCounters.Get($"attack2:{u.Iid}") == 0 &&
            ArcanaRules.IsPaired(owner, u, ctx);
    }

    public static GameError? Validate(GameState state, Command cmd, GameContext ctx)
    {
        if (state.Phase == "gameOver") return R.Err("WRONG_PHASE", "La partida terminó");
        if (state.Chain is not null && cmd.Type is not ("PASS" or "PLAY_TRICK" or "PLAY_SPELL"))
            return R.Err("CHAIN_OPEN", "Hay una cadena abierta: responde o pasa");
        var player = cmd.Player!;
        switch (cmd.Type)
        {
            case "MULLIGAN":
                return Rounds.ApplyMulligan(state, player, cmd.Iids ?? []).Error;
            case "START_ROUND":
                return state.Phase == "roundStart" ? OK : R.Err("WRONG_PHASE", "No es el inicio de una ronda");
            case "END_TURN":
                return OwnTurn(state, player);
            case "BATTLE":
                return OwnTurn(state, player) ?? (Rounds.IsOpeningTurn(state.Round, player) ? R.Err("INVALID_ATTACK", "En el primer turno de la partida no se puede atacar") : OK);
            case "END_COMBAT":
                if (state.Phase != "combat") return R.Err("WRONG_PHASE", "No hay combate en curso");
                return CombatHolder(state) == player ? OK : R.Err("NO_PRIORITY", "No tienes la prioridad de combate");
            case "SUMMON":
            {
                if (OwnTurn(state, player) is { } t) return t;
                if (HandCard(state, ctx, player, cmd.Iid, "unit", out _, out var def) is { } c) return c;
                var p = state.Players[player];
                if (CanPay(p, def!) is { } pay) return pay;
                if (!p.UnitSlots.Any(u => u is null)) return R.Err("NO_FREE_SLOT", "No hay espacio libre de Unidad");
                return MajorLimit(p, ctx, def!);
            }
            // Acoplar (Los Enamorados): sin hueco ni posición; se paga el coste de la carta acoplada.
            case "ATTACH":
            {
                if (OwnTurn(state, player) is { } t) return t;
                if (HandCard(state, ctx, player, cmd.Iid, "unit", out _, out var def) is { } c) return c;
                var p = state.Players[player];
                if (CanPay(p, def!) is { } pay) return pay;
                return ArcanaRules.AttachBlock(def!, p.Unit(cmd.UnitIid ?? ""), ctx) is { } why ? R.Err("INVALID_ATTACH", why) : OK;
            }
            case "REPLACE":
            {
                if (OwnTurn(state, player) is { } t) return t;
                if (HandCard(state, ctx, player, cmd.Iid, "unit", out _, out var def) is { } c) return c;
                var p = state.Players[player];
                var old = p.Unit(cmd.UnitIid!);
                if (old is null) return R.Err("UNIT_NOT_FOUND", "La Unidad a reemplazar no es tuya o no está en campo");
                return MajorLimit(p, ctx, def!, old) ?? CanPay(p, def!); // se paga TODO el coste de la nueva
            }
            case "CHANGE_POSITION":
            {
                if (OwnTurn(state, player) is { } t) return t;
                var u = state.Players[player].Unit(cmd.UnitIid!);
                if (u is null) return R.Err("UNIT_NOT_FOUND", "Esa Unidad no es tuya o no está en campo");
                return u.PositionChangedThisTurn ? R.Err("POSITION_ALREADY_CHANGED", "Esa Unidad ya cambió de posición este turno") : OK;
            }
            case "PLAY_SPELL":
            {
                if (SpellWindow(state, player) is { } w) return w;
                if (HandCard(state, ctx, player, cmd.Iid, "spell", out _, out var def) is { } c) return c;
                var p = state.Players[player];
                var cost = GameContext.SpellCost(state, player, def!);
                if (p.Energy < cost) return R.Err("INSUFFICIENT_ENERGY", $"Faltan EN: cuesta {cost} y hay {p.Energy}");
                var pendingSpells = state.Chain?.Stack.Count(l => l.Kind == "spell" && l.Player == player) ?? 0;
                return p.SpellSlots.Count(x => x is null) > pendingSpells ? OK : R.Err("NO_FREE_SLOT", "No hay espacio libre de Hechizo");
            }
            case "PLAY_TRICK":
            {
                if (HandCard(state, ctx, player, cmd.Iid, "trick", out var inst, out var def) is { } c) return c;
                if (CanPay(state.Players[player], def!) is { } pay) return pay;
                if (state.Chain is null) return R.Err("NO_REACTION_WINDOW", "No hay una ventana de reacción abierta");
                if (HasPriority(state, player) is { } prio) return prio;
                var self = new Self(inst!.Iid, inst.CardId, player);
                var when = ctx.Behavior(self.CardId)?.Trick?.When;
                return when is not null && !when(new TrickCall(state, self, ctx, state.Chain.Stack, cmd.Targets ?? []))
                    ? R.Err("NO_REACTION_WINDOW", "No se cumple la condición del Truco")
                    : OK;
            }
            case "ACTIVATE_ABILITY":
            {
                if (OwnTurn(state, player) is { } t) return t;
                var u = state.Players[player].Unit(cmd.SourceIid!);
                if (u is null) return R.Err("UNIT_NOT_FOUND", "Esa Unidad no es tuya o no está en campo");
                var effect = ctx.Card(u.CardId)?.Effects?.FirstOrDefault(e => e.Trigger.Kind == "activated");
                if (effect is null) return R.Err("ABILITY_UNAVAILABLE", "Esa carta no tiene habilidad activa");
                if (state.Players[player].TurnCounters.Get($"ability:{u.Iid}") > 0) return R.Err("ABILITY_UNAVAILABLE", "Habilidad ya usada este turno");
                int requiredDiscards = 0, requiredHp = 0;
                foreach (var cost in effect.Cost ?? [])
                {
                    if (cost.Kind == "discard")
                    {
                        if (cost.Count < 0) return R.Err("NOT_IMPLEMENTED", $"Coste de descarte inválido: {cost.Count}");
                        requiredDiscards += cost.Count;
                    }
                    else if (cost.Kind == "payHp")
                    {
                        if (cost.Amount < 0) return R.Err("NOT_IMPLEMENTED", $"Coste de PV inválido: {cost.Amount}");
                        requiredHp += cost.Amount;
                    }
                    else return R.Err("NOT_IMPLEMENTED", $"Coste «{cost.Kind}» sin soporte");
                }
                var me = state.Players[player];
                if (me.Hand.Length < requiredDiscards) return R.Err("INSUFFICIENT_COST", "No hay suficientes cartas en la mano para pagar el coste");
                if (me.Hp <= requiredHp) return R.Err("INSUFFICIENT_COST", "No hay suficientes PV para pagar el coste");
                var chosen = cmd.Targets?.Take(requiredDiscards).ToList() ?? [];
                if (chosen.Distinct().Count() != chosen.Count || chosen.Any(iid => !me.Hand.Any(c => c.Iid == iid)))
                    return R.Err("CARD_NOT_IN_HAND", "Una carta elegida para descartar no está en la mano");
                return OK;
            }
            case "DECLARE_ATTACK":
            {
                if (state.Phase != "combat") return R.Err("WRONG_PHASE", "Solo se ataca en la Batalla");
                if (state.Initiative != player) return R.Err("NO_PRIORITY", "Solo ataca el jugador de la Batalla");
                if (CombatHolder(state) != player) return R.Err("NO_PRIORITY", "Ya cerraste tus ataques");
                var attacker = state.Players[player].Unit(cmd.AttackerIid!);
                if (attacker is null) return R.Err("UNIT_NOT_FOUND", "El atacante no es tuyo o no está en campo");
                if (attacker.Position != "atk") return R.Err("INVALID_ATTACK", "Solo las Unidades en ATQ atacan");
                if (attacker.HasAttackedThisCombat && !CanAttackAgain(state, player, attacker, ctx))
                    return R.Err("INVALID_ATTACK", "Esa Unidad ya atacó en esta Batalla");
                var enemyUnits = state.Players[P.Other(player)].Units.ToList();
                // El Emperador no puede ser objetivo de ataque mientras su dueño controle otra Unidad.
                bool Shielded(UnitInPlay u) => u.CardId == ArcanaRules.Id["emperor"] && enemyUnits.Count > 1;
                // El Loco puede atacar directamente aunque el rival tenga Unidades.
                if (cmd.TargetIid is null && attacker.CardId == ArcanaRules.Id["fool"]) return OK;
                if (enemyUnits.Count > 0)
                {
                    var target = enemyUnits.FirstOrDefault(u => u.Iid == cmd.TargetIid);
                    if (target is null) return R.Err("INVALID_ATTACK", "Si el rival tiene Unidades, debes atacar a una de ellas");
                    return Shielded(target) ? R.Err("INVALID_ATTACK", "El Emperador no puede ser atacado mientras su dueño tenga otra Unidad") : OK;
                }
                return cmd.TargetIid is null ? OK : R.Err("INVALID_ATTACK", "Sin Unidades rivales el ataque es directo");
            }
            case "PASS":
                return state.Chain is not null ? HasPriority(state, player) : R.Err("NO_REACTION_WINDOW", "No hay ventana de prioridad abierta");
            default:
                return R.Err("NOT_IMPLEMENTED", $"Comando desconocido {cmd.Type}");
        }
    }

    /// <summary>Quién debe actuar ahora.</summary>
    public static string PlayerFor(GameState state)
    {
        if (state.Chain is not null) return state.Chain.Priority;
        return state.Phase switch
        {
            "mulligan" => state.Players.A.MulliganDone ? P.B : P.A,
            "roundStart" => P.A,
            "turnA" or "turnB" => state.ActivePlayer ?? throw new InvalidOperationException($"Falta activePlayer durante {state.Phase}"),
            "combat" => CombatHolder(state),
            "gameOver" => throw new InvalidOperationException("No hay acciones para una partida terminada"),
            _ => throw new InvalidOperationException($"Fase desconocida: {state.Phase}"),
        };
    }

    /// <summary>Acciones legales. `includeTargets` añade comandos con objetivos cuando elegirlos cambia el resultado.</summary>
    public static List<Command> LegalActions(GameState state, string player, GameContext ctx, bool includeTargets = false)
    {
        var output = new List<Command>();
        void Push(Command c)
        {
            if (!includeTargets)
            {
                if (Validate(state, c, ctx) is null) output.Add(c);
                return;
            }
            output.AddRange(Targets.LegalTargetVariants(state, c, ctx).Where(x => Validate(state, x, ctx) is null));
        }
        var me = state.Players[player];
        var units = me.Units.ToList();
        Push(new Command { Type = "MULLIGAN", Player = player, Iids = [] });
        Push(new Command { Type = "START_ROUND" });
        foreach (var card in me.Hand)
        {
            var def = ctx.Card(card.CardId);
            if (def is null) continue;
            if (def.Type == "unit")
            {
                foreach (var position in new[] { "atk", "def" })
                {
                    Push(new Command { Type = "SUMMON", Player = player, Iid = card.Iid, Position = position });
                    foreach (var u in units) Push(new Command { Type = "REPLACE", Player = player, Iid = card.Iid, UnitIid = u.Iid, Position = position });
                }
                if (def.Partner is not null)
                    foreach (var u in units) Push(new Command { Type = "ATTACH", Player = player, Iid = card.Iid, UnitIid = u.Iid });
            }
            else if (def.Type == "spell") Push(new Command { Type = "PLAY_SPELL", Player = player, Iid = card.Iid });
            else Push(new Command { Type = "PLAY_TRICK", Player = player, Iid = card.Iid });
        }
        foreach (var u in units)
        {
            Push(new Command { Type = "CHANGE_POSITION", Player = player, UnitIid = u.Iid });
            Push(new Command { Type = "ACTIVATE_ABILITY", Player = player, SourceIid = u.Iid });
            var enemies = state.Players[P.Other(player)].Units.ToList();
            if (enemies.Count == 0 || u.CardId == ArcanaRules.Id["fool"])
                Push(new Command { Type = "DECLARE_ATTACK", Player = player, AttackerIid = u.Iid, TargetIid = null });
            foreach (var e in enemies) Push(new Command { Type = "DECLARE_ATTACK", Player = player, AttackerIid = u.Iid, TargetIid = e.Iid });
        }
        Push(Command.Pass(player));
        Push(new Command { Type = "BATTLE", Player = player });
        Push(new Command { Type = "END_TURN", Player = player });
        Push(new Command { Type = "END_COMBAT", Player = player });
        return output;
    }

    // ---------- Aplicación ----------

    static Result<Outcome> Legacy(Result<Step> r) =>
        r.Ok ? Result<Outcome>.Success(new Outcome(r.Value!.State, r.Value.Events, [])) : r.Error!;

    /// <summary>Valida, convierte el comando en eventos, los resuelve y pasa por quien no puede responder.</summary>
    public static Result<Step> Apply(GameState state, Command cmd, GameContext ctx)
    {
        var r = ApplyStep(state, cmd, ctx);
        return r.Ok ? Result<Step>.Success(PassWithoutResponses(r.Value!, ctx)) : r;
    }

    /// <summary>Un solo comando (sin pases automáticos), para el análisis interno de objetivos.</summary>
    public static Result<Step> ApplyStep(GameState state, Command cmd, GameContext ctx)
    {
        if (Validate(state, cmd, ctx) is { } v) return v;
        if (cmd.Type == "PASS") return Result<Step>.Success(Chain.PassPriority(state, cmd.Player!, ctx));
        var r = Execute(state, cmd, ctx);
        return r.Ok ? Result<Step>.Success(Resolver.Resolve(r.Value!, ctx)) : r.Error!;
    }

    static Step PassWithoutResponses(Step step, GameContext ctx)
    {
        var state = step.State;
        var events = step.Events.ToList();
        var seen = new HashSet<string>();
        for (var loops = 0; state.Chain is not null && state.Phase != "gameOver"; loops++)
        {
            if (loops >= 64 && !seen.Add(Signature.Of(state)))
                throw new InvalidOperationException("BUG CORE: ciclo al pasar automáticamente sin respuestas legales");
            var player = state.Chain.Priority;
            if (HasResponse(state, player, ctx)) break;
            var passed = Chain.PassPriority(state, player, ctx);
            state = passed.State;
            events.AddRange(passed.Events);
        }
        return new Step(state, events);
    }

    /// <summary>¿Tiene `player` alguna acción legal además de pasar? Barato primero; la lista con objetivos sin decisiones.</summary>
    static bool HasResponse(GameState state, string player, GameContext ctx) =>
        LegalActions(state, player, ctx).Any(a => a.Type != "PASS") ||
        LegalActions(state, player, ctx.WithoutChoices(), includeTargets: true).Any(a => a.Type != "PASS");

    static Result<Outcome> Execute(GameState state, Command cmd, GameContext ctx)
    {
        var player = cmd.Player!;
        Result<Outcome> Declare(string kind, string cardId, string handIid, int cost, Ev[] events, IReadOnlyList<string>? targets = null, IEnumerable<Ev>? extra = null) =>
            Result<Outcome>.Success(new Outcome(
                Chain.PushLink(state, new NewLink(kind, player, cardId, handIid, cost, events, targets)), [],
                [new Ev { Type = "ENERGY_SPENT", Player = player, Amount = cost }, .. extra ?? []]));

        switch (cmd.Type)
        {
            case "MULLIGAN":
            {
                var r = Rounds.ApplyMulligan(state, player, cmd.Iids ?? []);
                if (!r.Ok) return r.Error!;
                return Result<Outcome>.Success(new Outcome(r.Value!, [new Ev { Type = "MULLIGAN_DONE", Player = player, Count = cmd.Iids?.Length ?? 0 }], []));
            }
            case "BATTLE":
                return Legacy(Rounds.StartBattle(state));
            case "START_ROUND":
            case "END_TURN":
            case "END_COMBAT":
            {
                if (cmd.Type == "END_COMBAT")
                {
                    // El atacante cierra sus ataques y cede la ventana final al rival (§4, §7); el rival cierra la Batalla.
                    var first = state.Initiative ?? P.A;
                    if (CombatHolder(state) == first)
                        return Result<Outcome>.Success(new Outcome(state with { ActivePlayer = P.Other(first) }, [new Ev { Type = "COMBAT_PRIORITY_PASSED", Player = first }], []));
                }
                var r = cmd.Type == "START_ROUND" ? Rounds.StartRound(state, true) : cmd.Type == "END_TURN" ? Rounds.EndTurn(state, true) : Rounds.EndCombat(state, true);
                if (!r.Ok) return r.Error!;
                var s = r.Value!.State;
                // El robo del turno va DESPUÉS de TURN_STARTED, así «antes de robar» ocurre antes (P05).
                List<Ev> draw = IsMainTurn(s) && !Rounds.IsOpeningTurn(s.Round, s.ActivePlayer)
                    ? [new Ev { Type = "CARD_DRAW_REQUESTED", Player = s.ActivePlayer, Remaining = 1, TurnDraw = true }]
                    : [];
                return Result<Outcome>.Success(new Outcome(s, r.Value.Events, draw));
            }
            case "SUMMON":
            case "REPLACE":
            {
                var card = state.Players[player].Hand.First(c => c.Iid == cmd.Iid);
                var amount = ctx.Card(card.CardId)!.Cost; // el coste se paga al declarar, aunque luego se niegue
                var ev = cmd.Type == "SUMMON"
                    ? new Ev { Type = "UNIT_SUMMONED", Player = player, Iid = cmd.Iid, CardId = card.CardId, Position = cmd.Position, Targets = cmd.Targets }
                    : new Ev { Type = "UNIT_REPLACED", Player = player, OldIid = cmd.UnitIid, Iid = cmd.Iid, CardId = card.CardId, Position = cmd.Position, Targets = cmd.Targets };
                return Declare(cmd.Type == "SUMMON" ? "summon" : "replace", card.CardId, cmd.Iid!, amount, [ev], cmd.Targets);
            }
            case "ATTACH":
            {
                var card = state.Players[player].Hand.First(c => c.Iid == cmd.Iid);
                var ev = new Ev { Type = "UNIT_PAIRED", Player = player, Iid = cmd.Iid, TargetIid = cmd.UnitIid, CardId = card.CardId };
                return Declare("attach", card.CardId, cmd.Iid!, ctx.Card(card.CardId)!.Cost, [ev]);
            }
            case "CHANGE_POSITION":
            {
                var u = state.Players[player].Unit(cmd.UnitIid!)!;
                return Result<Outcome>.Success(new Outcome(state, [], [new Ev { Type = "POSITION_CHANGED", Player = player, Iid = u.Iid, Position = u.Position == "atk" ? "def" : "atk" }]));
            }
            case "PLAY_SPELL":
            {
                var card = state.Players[player].Hand.First(c => c.Iid == cmd.Iid);
                var discount = state.Players[player].RoundCounters.Get("nextSpellDiscount");
                var cost = GameContext.SpellCost(state, player, ctx.Card(card.CardId)!);
                var ev = new Ev { Type = "SPELL_PLAYED", Player = player, Iid = cmd.Iid, CardId = card.CardId };
                List<Ev> consumed = discount > 0 ? [new Ev { Type = "COUNTER_SET", Player = player, Key = "nextSpellDiscount", Value = 0, Per = "round" }] : [];
                return Declare("spell", card.CardId, cmd.Iid!, cost, [ev], null, consumed);
            }
            case "PLAY_TRICK":
            {
                var card = state.Players[player].Hand.First(c => c.Iid == cmd.Iid);
                return Declare("trick", card.CardId, cmd.Iid!, ctx.Card(card.CardId)!.Cost, [], cmd.Targets);
            }
            case "DECLARE_ATTACK":
            {
                var attacker = state.Players[player].Unit(cmd.AttackerIid!)!;
                // El choque se calcula al resolver la cadena, con los valores finales tras las respuestas.
                var link = new NewLink("attack", player, attacker.CardId, null, 0,
                    [
                        new Ev { Type = "ATTACK_CLASH", Player = player, AttackerIid = cmd.AttackerIid, TargetIid = cmd.TargetIid },
                        new Ev { Type = "ATTACK_ENDED", Player = player, AttackerIid = cmd.AttackerIid },
                    ],
                    cmd.TargetIid is not null ? [cmd.AttackerIid!, cmd.TargetIid] : [cmd.AttackerIid!]);
                return Result<Outcome>.Success(new Outcome(Chain.PushLink(state, link), [],
                    [new Ev { Type = "ATTACK_DECLARED", Player = player, AttackerIid = cmd.AttackerIid, TargetIid = cmd.TargetIid }]));
            }
            case "ACTIVATE_ABILITY":
            {
                var u = state.Players[player].Unit(cmd.SourceIid!)!;
                var effect = ctx.Card(u.CardId)?.Effects?.FirstOrDefault(x => x.Trigger.Kind == "activated");
                var paid = state;
                var facts = new List<Ev>();
                var skip = 0;
                foreach (var cost in effect?.Cost ?? [])
                {
                    if (cost.Kind == "discard")
                        for (var i = 0; i < cost.Count; i++)
                        {
                            var iid = cmd.Targets is { } ts && skip < ts.Length ? ts[skip] : paid.Players[player].Hand.FirstOrDefault()?.Iid;
                            if (iid is null) return R.Err("INSUFFICIENT_COST", "No hay una carta en la mano para descartar");
                            var discarded = new Ev { Type = "CARD_DISCARDED", Player = player, Iid = iid, AsAbilityCost = true };
                            var next = Reducers.Reduce(paid, discarded, ctx);
                            if (next is null) return R.Err("CARD_NOT_IN_HAND", $"La carta {iid} no está en la mano");
                            paid = next;
                            facts.Add(discarded);
                            skip++;
                        }
                    else if (cost.Kind == "payHp")
                    {
                        var hp = new Ev { Type = "HP_PAID_AS_ABILITY_COST", Player = player, Amount = cost.Amount, SourceIid = u.Iid };
                        var next = Reducers.Reduce(paid, hp, ctx);
                        if (next is null) return R.Err("INSUFFICIENT_COST", "No hay suficientes PV para pagar el coste");
                        paid = next;
                        facts.Add(hp);
                    }
                    else return R.Err("NOT_IMPLEMENTED", $"Coste «{cost.Kind}» sin soporte");
                }
                var targets = cmd.Targets?.Skip(skip).ToArray();
                var ev = new Ev { Type = "ABILITY_ACTIVATED", Player = player, SourceIid = u.Iid, Trigger = "activate", Targets = targets, LinkId = state.Chain?.NextId ?? 0 };
                var s = Chain.PushLink(paid, new NewLink("ability", player, u.CardId, null, 0, [ev], targets));
                // 1/turno: el contador lo lee validate (ability:<iid>) y rounds lo limpia al cambiar de turno
                return Result<Outcome>.Success(new Outcome(s.Update(player, p => p with { TurnCounters = p.TurnCounters.Set($"ability:{u.Iid}", 1) }), facts, []));
            }
            default:
                return R.Err("NOT_IMPLEMENTED", $"El comando {cmd.Type} aún no tiene ejecución");
        }
    }
}

/// <summary>Firma del estado para detectar bucles y deduplicar variantes (JSON.stringify en TS).</summary>
public static class Signature
{
    public static string Of(GameState s) => System.Text.Json.JsonSerializer.Serialize(s, Json.Options);
}
