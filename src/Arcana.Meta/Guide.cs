// Partida guiada (port de client/guia.ts): mano, robos y jugadas del rival escritos; cada paso explica algo y,
// si es de acción, solo deja hacer lo que pide (con red de seguridad para no bloquear nunca).
using Arcana.Content;
using Arcana.Core;

namespace Arcana.Meta;

public sealed record GuideInfo(string Arcana, IReadOnlyDictionary<string, string> Names, int ArcanaCost, string Rule, string DeckName, string Synergy, string HowToPlay,
    int Lesson = 0, string Family = "");
public sealed record CardOrder(IReadOnlyList<string> Hand, IReadOnlyList<string> Deck);
public sealed record GuideCtx(GameState S, string Me, bool Pending);

public sealed class GuideStep
{
    /// <summary>info | accion | libre</summary>
    public required string Kind { get; init; }
    public required Func<GuideCtx, string> Text { get; init; }
    public Func<Command, GuideCtx, bool>? Allows { get; init; }
    public Func<GuideCtx, bool>? Done { get; init; }
    public Func<GuideCtx, bool>? Skip { get; init; }
    public Func<GuideCtx, bool>? When { get; init; }
    public Func<GuideCtx, string>? Wait { get; init; }
    /// <summary>Al llegar a este paso se guarda el punto al que vuelve un paso Rewind.</summary>
    public bool Mark { get; init; }
    /// <summary>Paso informativo: al pulsar «Siguiente» el duelo vuelve al último Mark (ensayo sin castigo).</summary>
    public bool Rewind { get; init; }
}

public static partial class Guide
{
    const string Aprendiz = "M01", Segador = "D02", Circulo = "M05", Promesa = "L09", Guardian = "M03", Caballero = "D04";
    const string REscudero = "L01", RJuramentado = "L05", RBardo = "L03";
    public const int RivalHp = 12;
    public const int FreeRound = 4;

    public static readonly DeckDefinition GuideDeck = new()
    {
        Id = "guia", Name = "Aprendiz del Velo",
        Entries =
        [
            new() { Id = REscudero, Copies = 3 }, new() { Id = RJuramentado, Copies = 3 }, new() { Id = RBardo, Copies = 3 },
            new() { Id = "E01", Copies = 3 }, new() { Id = "E02", Copies = 3 }, new() { Id = "M02", Copies = 3 }, new() { Id = "D04", Copies = 2 },
        ],
    };

    public static (DeckDefinition mine, DeckDefinition rival, CardOrder myOrder, CardOrder rivalOrder, GuideInfo info) Prepare(string deckId, string arcana)
    {
        var mine = new DeckDefinition
        {
            Id = deckId, Name = Lore.DeckName(deckId),
            Entries =
            [
                new() { Id = Aprendiz, Copies = 3 }, new() { Id = Segador, Copies = 3 }, new() { Id = Guardian, Copies = 3 },
                new() { Id = Caballero, Copies = 3 }, new() { Id = Circulo, Copies = 3 }, new() { Id = Promesa, Copies = 2 }, new() { Id = arcana, Copies = 3 },
            ],
        };
        var text = Economy.Card(arcana)?.Text ?? "";
        var ids = mine.Entries.Concat(GuideDeck.Entries).Select(e => e.Id).Distinct();
        var lore = Lore.Decks.GetValueOrDefault(deckId);
        return (mine, GuideDeck,
            new CardOrder([Aprendiz, Segador, Circulo, Promesa, arcana],
                [Guardian, Caballero, Segador, Aprendiz, Caballero, Guardian, Segador, Aprendiz, Caballero, Guardian, arcana, arcana, Circulo, Promesa, Circulo]),
            new CardOrder([REscudero, RJuramentado, RBardo, "E01", "E02"],
                ["M02", "E01", "D04", RBardo, "E02", "M02", REscudero, RJuramentado, "D04", "E01", "E02", "M02", RBardo, REscudero, RJuramentado]),
            new GuideInfo(arcana, ids.ToDictionary(id => id, id => Economy.Card(id)?.Name ?? id), Economy.Card(arcana)?.Cost ?? 3,
                text.Contains("Regla rota:") ? text[text.IndexOf("Regla rota:")..] : "",
                lore?.Name ?? deckId, lore?.Synergy ?? "", lore?.HowToPlay ?? ""));
    }

    /// <summary>Coloca la mano pedida y, arriba del mazo, los robos pedidos (el resto queda debajo).</summary>
    public static PlayerState Arrange(PlayerState p, CardOrder o)
    {
        var rest = p.Hand.Concat(p.Deck).ToList();
        List<CardInstance> Take(string id)
        {
            var i = rest.FindIndex(c => c.CardId == id);
            if (i < 0) return [];
            var c = rest[i];
            rest.RemoveAt(i);
            return [c];
        }
        var hand = o.Hand.SelectMany(Take).ToList();
        var top = o.Deck.SelectMany(Take).ToList();
        while (hand.Count < p.Hand.Length && rest.Count > 0) { hand.Add(rest[0]); rest.RemoveAt(0); }
        return p with { Hand = hand.ToArray(), Deck = [.. top, .. rest] };
    }

    static string Rival(string me) => P.Other(me);
    static List<UnitInPlay> Units(GameState s, string p) => s.Players[p].Units.ToList();
    static UnitInPlay? OnField(GuideCtx c, string cardId) => Units(c.S, c.Me).FirstOrDefault(u => u.CardId == cardId);
    static bool InHand(GuideCtx c, string cardId) => c.S.Players[c.Me].Hand.Any(h => h.CardId == cardId);
    static string? CardOf(GuideCtx c, string? iid) => c.S.Players[c.Me].Hand.FirstOrDefault(h => h.Iid == iid)?.CardId ?? Units(c.S, c.Me).FirstOrDefault(u => u.Iid == iid)?.CardId;
    static bool MyTurn(GuideCtx c) => c.S.Phase is "turnA" or "turnB" && c.S.ActivePlayer == c.Me && c.S.Chain is null;
    static bool MyBattle(GuideCtx c) => c.S.Phase == "combat" && c.S.Initiative == c.Me;
    static bool Still(GuideCtx c) => !c.Pending && c.S.Chain is null;
    static bool OnlyPass(Command cmd, GuideCtx c) => cmd.Type == "PASS" || (cmd.Type == "END_COMBAT" && c.S.Initiative != c.Me);
    static bool Attacked(GuideCtx c) => c.S.Chain?.Priority == c.Me && c.S.Chain.Stack.LastOrDefault() is { Kind: "attack" } l && l.Player != c.Me;

    public static List<GuideStep> Steps(GuideInfo g)
    {
        if (g.Lesson > 0) return LessonSteps(g);
        string N(string id) => g.Names.GetValueOrDefault(id, id);
        GuideStep Summon(string id, Func<GuideCtx, string> text, string? pos = null) => new()
        {
            Kind = "accion", Text = text,
            Allows = (cmd, c) => (cmd.Type == "SUMMON" && (pos is null || cmd.Position == pos) && CardOf(c, cmd.Iid) == id) || cmd.Type == "PASS",
            Done = c => OnField(c, id) is not null && Still(c),
            Skip = c => Still(c) && ((!InHand(c, id) && OnField(c, id) is null) || !MyTurn(c)),
        };
        GuideStep Position(string id, string pos, string text) => new()
        {
            Kind = "accion", Text = _ => text,
            Allows = (cmd, c) => cmd.Type == "CHANGE_POSITION" && CardOf(c, cmd.UnitIid) == id,
            Done = c => OnField(c, id)?.Position == pos,
            Skip = c => Still(c) && (OnField(c, id) is null || !MyTurn(c) || OnField(c, id)!.PositionChangedThisTurn),
        };
        GuideStep Battle(string text) => new()
        {
            Kind = "accion", Text = _ => text, Allows = (cmd, _) => cmd.Type == "BATTLE", Done = MyBattle,
            Skip = c => Still(c) && !MyTurn(c) && !MyBattle(c),
        };
        GuideStep Attack(string id, string text) => new()
        {
            Kind = "accion", Text = _ => text,
            Allows = (cmd, c) => (cmd.Type == "DECLARE_ATTACK" && CardOf(c, cmd.AttackerIid) == id) || cmd.Type == "PASS",
            Done = c => Still(c) && (OnField(c, id)?.HasAttackedThisCombat ?? false),
            Skip = c => Still(c) && (!MyBattle(c) || OnField(c, id)?.Position != "atk"),
        };
        GuideStep EndBattle(string text) => new()
        {
            Kind = "accion", Text = _ => text, Allows = (cmd, _) => cmd.Type is "END_COMBAT" or "PASS",
            Done = c => !MyBattle(c) || c.S.ActivePlayer != c.Me,
        };
        GuideStep RivalTurn(int round, Func<GuideCtx, string> text, Func<Command, GuideCtx, bool>? allows = null) => new()
        {
            Kind = "accion", Text = text, Allows = allows ?? OnlyPass, Done = c => MyTurn(c) && c.S.Round >= round,
        };

        var arcanaStep = Summon(g.Arcana, _ =>
            $"¡Invoca a tu Arcano Mayor, {N(g.Arcana)} ({g.ArcanaCost} EN)! Es la carta más poderosa de tu mazo: llevas 3 copias, pero solo puede haber uno en tu campo.");
        return
        [
            new() { Kind = "info", Text = _ =>
                "¡Bienvenido a ARCANA! Esta es una partida guiada: sigue las instrucciones de este panel; solo podrás hacer lo que se te pida. " +
                $"Ganas cuando el rival llega a 0 PV (aquí empieza con {RivalHp}; en una partida normal, con 30)." },
            new() { Kind = "accion", Text = _ =>
                "Mulligan: antes de empezar puedes devolver al mazo las cartas que no quieras y robar otras tantas, una sola vez por partida. " +
                "Toca «Ver» bajo una carta para leerla en grande. Hoy tu mano ya está preparada: pulsa «Conservar mano».",
                Allows = (cmd, _) => cmd.Type == "MULLIGAN" && cmd.Iids!.Length == 0, Done = c => c.S.Players[c.Me].MulliganDone },
            new() { Kind = "info", When = MyTurn, Wait = _ => "Empieza la ronda 1…", Text = _ =>
                "Ronda 1. Al empezar cada ronda los dos recargáis la energía (EN, junto a tus PV): 2 en la primera ronda y 1 más cada ronda, hasta 7. " +
                "La que no gastes se pierde. Juegas primero: en el primer turno de la partida no se roba carta ni se puede atacar." },
            Summon(Aprendiz, c => c.Pending
                ? "Presagio 2: miras las 2 cartas de arriba de tu mazo y eliges en qué orden las robarás. Toca primero la que quieras robar antes y confirma."
                : $"Invoca a {N(Aprendiz)} (cuesta 1 EN) en ATAQUE: toca la carta en tu mano y elige «Invocar en ataque». Al entrar activa su efecto: Presagio.", "atk"),
            new() { Kind = "info", Text = _ =>
                "Posiciones. En Ataque (carta vertical) la Unidad usa su ATQ y puede atacar. En Defensa (girada) usa su DEF y no ataca, pero aguanta mejor. " +
                "Cada Unidad puede cambiar de posición gratis una vez por turno." },
            Position(Aprendiz, "def", $"Cambia a {N(Aprendiz)} a DEFENSA: tócalo en el campo y elige «Cambiar posición». Su DEF 2 protegerá tus PV en el turno del rival."),
            new() { Kind = "accion", Text = _ => "Te queda 1 EN, pero no hay nada más que hacer. En el primer turno no se ataca: pulsa «Fin del turno».",
                Allows = (cmd, _) => cmd.Type == "END_TURN", Done = c => c.S.ActivePlayer != c.Me || c.S.Round > 1 },
            RivalTurn(2, _ => $"Turno del rival: roba una carta e invoca a {N(REscudero)} (ATQ 2) en Ataque. Cuando termine empieza la ronda 2."),
            new() { Kind = "info", Text = c =>
                $"Ronda 2: tienes {c.S.Players[c.Me].Energy} EN. Al empezar tu turno robas 1 carta: es una de las que viste con el Presagio. " +
                "Tu turno tiene dos partes: primero juegas cartas; después, si quieres, vas a la Batalla para atacar." },
            Summon(Segador, _ => $"Invoca a {N(Segador)} (2 EN, ATQ 3) en ATAQUE: será tu atacante.", "atk"),
            Battle("Ya no vas a jugar más cartas este turno: pulsa «Batalla» para pasar a la fase de ataque."),
            Attack(Segador, $"Ataca con {N(Segador)} a {N(REscudero)}: toca tu Unidad y elige «Atacar», o arrástrala hasta su objetivo (la flecha te " +
                "anuncia el resultado). Tu ATQ 3 supera su ATQ 2: lo destruirás y el rival perderá la diferencia, 1 PV."),
            new() { Kind = "info", Text = _ =>
                "Así se resuelve un ataque contra una Unidad en Ataque: gana el ATQ mayor, la Unidad perdedora es destruida y su dueño pierde la diferencia en PV. " +
                "Si tu ATQ es menor, pierdes tú. Con ATQ iguales se destruyen las dos. Tu Aprendiz está en Defensa: no puede atacar." },
            EndBattle("Pulsa «Fin de la batalla»: con ella termina tu turno. Tu Segador se queda en Ataque durante el turno del rival."),
            RivalTurn(3, c => Attacked(c) && InHand(c, Promesa)
                    ? $"¡{N(RJuramentado)} (ATQ 4) ataca a tu {N(Segador)} (ATQ 3)! Lo perderías junto con 1 PV. Responde con tu Truco: toca {N(Promesa)} " +
                      "y pulsa «Activar». Le da +2 ATQ y +2 DEF en este enfrentamiento."
                    : InHand(c, Promesa)
                        ? $"Turno del rival: invoca a {N(RJuramentado)} (ATQ 4) y va a su Batalla. Guardaste 1 EN: te servirá para responder."
                        : "¡Bien jugado! Con 5 de ATQ tu Segador gana: el atacante es destruido y el rival pierde la diferencia. " +
                          "Los Trucos se juegan desde la mano en respuesta a lo que hace el rival; por eso conviene guardar algo de energía.",
                (cmd, c) => Attacked(c) && InHand(c, Promesa) ? cmd.Type == "PLAY_TRICK" && CardOf(c, cmd.Iid) == Promesa : OnlyPass(cmd, c)),
            new() { Kind = "info", Text = _ =>
                "Cada vez que alguien juega una carta o ataca, el otro puede responder: el juego te preguntará «¿Responder?» solo cuando tengas con qué. " +
                "Ronda 3: el rival se quedó sin Unidades. Es el momento de golpear." },
            new() { Kind = "accion", Text = _ =>
                $"Juega el Hechizo {N(Circulo)} (2 EN): tócalo en tu mano y pulsa «Activar». Los Hechizos se quedan en tu Grimorio durante varias rondas; " +
                "este da +1 ATQ a todas tus Unidades.",
                Allows = (cmd, c) => (cmd.Type == "PLAY_SPELL" && CardOf(c, cmd.Iid) == Circulo) || cmd.Type == "PASS",
                Done = c => Still(c) && c.S.Players[c.Me].SpellSlots.Any(x => x?.CardId == Circulo),
                Skip = c => Still(c) && (!InHand(c, Circulo) || !MyTurn(c)) && !c.S.Players[c.Me].SpellSlots.Any(x => x?.CardId == Circulo) },
            Position(Aprendiz, "atk", $"Pon a {N(Aprendiz)} en ATAQUE (tócalo y elige «Cambiar posición»): sin Unidades rivales, también puede golpear."),
            Battle("Pulsa «Batalla»."),
            Attack(Segador, $"El rival no tiene Unidades: tu ataque va directo a sus PV. Ataca con {N(Segador)} (ATQ 3 + 1 del Hechizo)."),
            Attack(Aprendiz, $"Ahora ataca con {N(Aprendiz)}: cada Unidad en Ataque puede atacar una vez por Batalla."),
            EndBattle("Pulsa «Fin de la batalla»."),
            RivalTurn(FreeRound, _ => $"Turno del rival: se protege con {N(RBardo)} en Defensa (DEF 3). Mientras tenga una Unidad no puedes atacar directo."),
            new()
            {
                Kind = arcanaStep.Kind, Text = arcanaStep.Text, Allows = arcanaStep.Allows, Done = arcanaStep.Done, Skip = arcanaStep.Skip,
                // ya en el campo (o entrando) deja de esperar
                When = c => OnField(c, g.Arcana) is not null || c.S.Chain is not null || c.Pending || (MyTurn(c) && c.S.Players[c.Me].Energy >= g.ArcanaCost),
                Wait = c => $"Tu Arcano Mayor, {N(g.Arcana)}, cuesta {g.ArcanaCost} EN y ahora tienes {c.S.Players[c.Me].Energy}. Juega como quieras: lo invocarás en cuanto tengas energía.",
            },
            new() { Kind = "info", Text = _ => $"Cada Arcano Mayor rompe una regla mientras está en tu campo. {g.Rule} Las cartas con ✦ tienen un efecto que puedes activar tocándolas." },
            new() { Kind = "info", Text = _ => $"Fuera de esta práctica jugarás con tu mazo, {g.DeckName}. {g.Synergy} {g.HowToPlay}" },
            new() { Kind = "libre", Text = _ =>
                $"¡Ahora tú! El rival se defiende con {N(RBardo)} (DEF 3). Contra una Unidad en Defensa necesitas más ATQ que su DEF: la destruyes, pero el rival " +
                "no pierde PV (y con ATQ igual, no pasa nada). Ve a la Batalla, destrúyela con tu Segador (ATQ 4) y remata con ataques directos." },
        ];
    }

    public static bool Waiting(GuideStep? step, GuideCtx c) => step?.When is not null && !step.When(c);

    public static bool Allows(GuideStep? step, GuideCtx c, Command cmd) =>
        step is null || step.Kind == "libre" || Waiting(step, c) ? true : step.Kind == "info" ? false : step.Allows?.Invoke(cmd, c) ?? true;

    /// <summary>Órdenes que el paso deja hacer. open: ninguna encajaba y se permiten todas para no bloquear.</summary>
    public static (List<Command> cmds, bool open) Allowed(GuideStep? step, GuideCtx c, IReadOnlyList<Command> legal)
    {
        if (step is null || step.Kind == "libre" || Waiting(step, c)) return (legal.ToList(), false);
        if (step.Kind == "info") return ([], false);
        var cmds = legal.Where(cmd => Allows(step, c, cmd)).ToList();
        return cmds.Count > 0 || legal.Count == 0 ? (cmds, false) : (legal.ToList(), true);
    }

    public static int Advance(IReadOnlyList<GuideStep> steps, int i, GuideCtx c)
    {
        while (i < steps.Count - 1)
        {
            var p = steps[i];
            if (p.Kind == "libre") break;
            var met = p.Kind == "accion" && !Waiting(p, c) && (p.Done?.Invoke(c) ?? false);
            if (!met && !(p.Skip?.Invoke(c) ?? false)) break;
            i++;
        }
        return i;
    }

    /// <summary>Jugada del rival según el guion; null = ya no hay guion (juega la IA).</summary>
    public static Command? RivalMove(GameState s, string me, IReadOnlyList<Command> legal, GuideInfo? g = null)
    {
        if (g is { Lesson: > 0 }) return LessonRival(s, me, legal, g);
        var yo = Rival(me);
        var mio = s.Players[yo];
        Command? Legal(Func<Command, bool> f) => legal.FirstOrDefault(f);
        if (s.Phase == "mulligan") return Legal(c => c.Type == "MULLIGAN");
        if (s.Round >= FreeRound) return null;
        if (s.Chain is not null) return Legal(c => c.Type == "PASS");
        Command? End() => Legal(c => c.Type == "END_COMBAT") ?? Legal(c => c.Type == "END_TURN") ?? Legal(c => c.Type == "PASS");
        string? Iid(string cardId) => mio.Hand.FirstOrDefault(h => h.CardId == cardId)?.Iid;
        Command? Summon(string cardId, string position) =>
            Units(s, yo).Any(u => u.CardId == cardId) ? null : Legal(c => c.Type == "SUMMON" && c.Iid == Iid(cardId) && c.Position == position && (c.Targets?.Length ?? 0) == 0);
        if (s.Phase == "combat")
        {
            if (s.Initiative != yo) return End();
            var atq = Units(s, yo).FirstOrDefault(u => u.CardId == RJuramentado && !u.HasAttackedThisCombat);
            var target = Units(s, me).FirstOrDefault(u => u.CardId == Segador && u.Position == "atk");
            return (atq is not null && target is not null ? Legal(c => c.Type == "DECLARE_ATTACK" && c.AttackerIid == atq.Iid && c.TargetIid == target.Iid) : null) ?? End();
        }
        if (s.Round == 1) return Summon(REscudero, "atk") ?? End();
        if (s.Round == 2)
        {
            var enter = Summon(RJuramentado, "atk");
            if (enter is not null) return enter;
            var canAttack = Units(s, yo).Any(u => u.CardId == RJuramentado) && Units(s, me).Any(u => u.CardId == Segador && u.Position == "atk");
            return (canAttack ? Legal(c => c.Type == "BATTLE") : null) ?? End();
        }
        return Summon(RBardo, "def") ?? End();
    }
}
