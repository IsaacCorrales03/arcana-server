// Tutorial de cuenta nueva en 3 fases: 1 «El primer paso», 2 «La emboscada», 3 «El eco del Arcano» (según el Arcano elegido).
// Manos y robos fijos y rival guionado, para que cada lección ocurra en su turno exacto. Un mensaje por paso.
using Arcana.Content;
using Arcana.Core;

namespace Arcana.Meta;

/// <summary>Una fase del tutorial lista para Match.Start.</summary>
public sealed record Lesson(DeckDefinition Mine, DeckDefinition Rival, CardOrder MyOrder, CardOrder RivalOrder, int RivalHp,
    IReadOnlyList<string>? RivalField, IReadOnlyList<string>? MyField, string RivalName, GuideInfo Info);

public static partial class Guide
{
    public const int LessonCount = 3;

    static DeckDefinition Deck(string id, string name, params (string id, int copies)[] entries) =>
        new() { Id = id, Name = name, Entries = entries.Select(e => new DeckEntry { Id = e.id, Copies = e.copies }).ToList() };

    // Fases 1 y 2: el mismo mazo prestado para todos.
    static readonly DeckDefinition Apprentice = Deck("aprendiz", "Mazo del Aprendiz",
        ("L02", 3), ("D02", 3), ("E07", 3), ("M05", 3), ("L09", 3), ("M08", 3), ("P02", 2));
    static readonly DeckDefinition Shadow = Deck("sombra", "Sombra de práctica", ("F01", 7), ("E01", 7), ("E03", 6));
    static readonly DeckDefinition Hunter = Deck("cazador", "Cazador", ("D02", 7), ("F01", 7), ("M05", 6));
    static readonly DeckDefinition Echo = Deck("eco", "Eco del Arcano", ("E01", 7), ("E03", 7), ("E04", 6));

    /// <summary>Fase n del tutorial; la 3 depende de la familia del Arcano elegido (fool, death o lovers).</summary>
    public static Lesson LessonFor(int n, string family)
    {
        family = family is "fool" or "death" or "lovers" ? family : "lovers";
        GuideInfo Info(string arcana) => new(arcana, Economy.Cards.ToDictionary(c => c.Id, c => c.Name.Split(" · ")[^1]), 0, "", "", "", "", n, family);
        CardOrder Order(params string[] hand) => new(hand, []);
        switch (n)
        {
            case 1:
                return new(Apprentice, Shadow, Order("L02", "D02", "E07", "P02", "M05"), Order("F01", "E01", "E03"), 5, null, null,
                    "Sombra de práctica", Info(""));
            case 2:
                return new(Apprentice, Hunter, Order("M05", "L02", "L09", "D02", "M08"), Order("D02", "F01", "M05"), 8, null, null,
                    "Cazador", Info(""));
        }
        var starter = Economy.Starters.First(d => d.Entries.Any(e => Economy.MajorKey(e.Id) == family));
        return family switch
        {
            "fool" => new(starter, Echo, Order("F00", "F01", "F04", "F09", "F02"), Order(), 4, ["E03/def", "E04/def", "P02/def"], null,
                "Muralla del Eco", Info("F00")),
            "death" => new(starter, Echo, Order("D02", "D03", "D05", "D07", "D09"), Order(), 6, ["C04", "E04/def"], ["D00", "D01"],
                "Coloso del Eco", Info("D00")),
            _ => new(starter, Echo, Order("L01", "L02", "L03", "L04", "L09"), Order(), 7, ["E01/def"], ["L00", "L05", "L06/pair"],
                "Guardián del Eco", Info("L00")),
        };
    }

    // ---------- Pasos ----------

    static GuideStep Info(string text, bool rewind = false) => new() { Kind = "info", Text = _ => text, Rewind = rewind };
    static GuideStep Keep => new()
    {
        Kind = "accion", Text = _ => "Antes del duelo puedes cambiar cartas de tu mano inicial una vez. Para esta práctica ya tienes las cartas que necesitas.",
        Allows = (cmd, _) => cmd.Type == "MULLIGAN" && cmd.Iids!.Length == 0, Done = c => c.S.Players[c.Me].MulliganDone,
    };
    static GuideStep Summon(string id, string pos, string text) => new()
    {
        Kind = "accion", Text = _ => text,
        Allows = (cmd, c) => (cmd.Type == "SUMMON" && cmd.Position == pos && CardOf(c, cmd.Iid) == id) || cmd.Type == "PASS",
        Done = c => OnField(c, id) is not null && Still(c),
    };
    static GuideStep Pos(string id, string pos, string text) => new()
    {
        Kind = "accion", Text = _ => text,
        Allows = (cmd, c) => cmd.Type == "CHANGE_POSITION" && CardOf(c, cmd.UnitIid) == id,
        Done = c => OnField(c, id)?.Position == pos,
    };
    static GuideStep Spell(string id, string text) => new()
    {
        Kind = "accion", Text = _ => text,
        Allows = (cmd, c) => (cmd.Type == "PLAY_SPELL" && CardOf(c, cmd.Iid) == id) || cmd.Type == "PASS",
        Done = c => Still(c) && !InHand(c, id),
    };
    static GuideStep Battle(string text, bool mark = false) => new()
    {
        Kind = "accion", Text = _ => text, Allows = (cmd, _) => cmd.Type == "BATTLE", Done = MyBattle, Mark = mark,
    };
    /// <summary>Ataque de «id»; target: id de la Unidad rival, o null para atacar directo a los PV.</summary>
    static GuideStep Strike(string id, string? target, string text, Func<GuideCtx, bool>? done = null) => new()
    {
        Kind = "accion", Text = _ => text,
        Allows = (cmd, c) => (cmd.Type == "DECLARE_ATTACK" && CardOf(c, cmd.AttackerIid) == id &&
            (target is null ? cmd.TargetIid is null : Units(c.S, Rival(c.Me)).FirstOrDefault(u => u.Iid == cmd.TargetIid)?.CardId == target)) || cmd.Type == "PASS",
        Done = done ?? (c => Still(c) && (OnField(c, id)?.HasAttackedThisCombat ?? true)),
    };
    static GuideStep EndBattle(string text) => new()
    {
        Kind = "accion", Text = _ => text, Allows = (cmd, _) => cmd.Type is "END_COMBAT" or "PASS",
        Done = c => !MyBattle(c) || c.S.ActivePlayer != c.Me,
    };
    static GuideStep EndTurn(string text) => new()
    {
        Kind = "accion", Text = _ => text, Allows = (cmd, _) => cmd.Type == "END_TURN", Done = c => c.S.Phase is "turnA" or "turnB" or "combat" && c.S.ActivePlayer != c.Me,
    };
    static GuideStep Wait(int round, Func<GuideCtx, string> text, Func<Command, GuideCtx, bool>? allows = null) => new()
    {
        Kind = "accion", Text = text, Allows = allows ?? OnlyPass, Done = c => MyTurn(c) && c.S.Round >= round,
    };
    static GuideStep Finish(string text) => new() { Kind = "libre", Text = _ => text };
    static bool Over(GuideCtx c) => c.S.Phase == "gameOver";
    static bool RivalSpell(GuideCtx c) => c.S.Chain?.Priority == c.Me && c.S.Chain.Stack.LastOrDefault() is { Kind: "spell" } l && l.Player != c.Me;

    static List<GuideStep> LessonSteps(GuideInfo g) => g.Lesson switch
    {
        1 =>
        [
            Info("Deja a la Sombra de práctica en 0 puntos de vida (PV). El marco dorado señala qué tocar en cada paso."),
            Keep,
            Summon("L02", "atk", "Tienes 2 de energía (EN). Invocar a Doncella del Alba cuesta 1 EN. En Ataque, la carta queda vertical."),
            Pos("L02", "def", "En Defensa, la carta se gira y usa su DEF. La Doncella tiene 3 DEF y puede resistir un ataque de 2 ATQ."),
            EndTurn("Ya preparaste tu defensa. Cede el turno y fíjate en cómo responde la Sombra."),
            Wait(2, _ => "Observa el 2 ATQ del atacante contra los 3 DEF de tu Doncella. El atacante cae y la Sombra pierde 1 PV."),
            Summon("D02", "atk", "Segador Novicio tiene 3 ATQ. Invócalo en Ataque para poder usarlo en la Batalla."),
            Battle("La Batalla es la parte del turno en la que tus Unidades en Ataque pueden golpear."),
            Strike("D02", "E01", "Selecciona al Segador, elige «Atacar» y toca al Heraldo Imperial. Con 3 ATQ superas sus 2 DEF."),
            EndBattle("El Heraldo cayó. Como estaba en Defensa, la Sombra no pierde PV por ese golpe."),
            Wait(3, _ => "Fíjate en el Centurión que invoca la Sombra. Se coloca en Defensa con 4 DEF."),
            Battle("Vamos a probar qué ocurre cuando el defensor supera el ATQ de tu Unidad.", mark: true),
            Strike("D02", "E03", "Ataca al Centurión aunque tenga 4 DEF frente a tus 3 ATQ. Podrás repetir el turno con otra estrategia.",
                c => Still(c) && OnField(c, "D02") is null),
            Info("El Segador fue destruido y perdiste 1 PV, la diferencia entre 4 DEF y 3 ATQ. Pulsa «Siguiente» para repetir desde antes del ataque.", rewind: true),
            Spell("E07", "Orden de Batalla cuesta 1 EN y le da +2 ATQ al Segador esta ronda. Úsala antes de atacar."),
            Battle("Comprueba el ATQ del Segador. Ahora tiene 5 y supera los 4 DEF del Centurión."),
            Strike("D02", "E03", "Repite el ataque contra el Centurión con el ATQ mejorado."),
            EndBattle("El Centurión cayó. Cierra la Batalla y deja que la Sombra haga su jugada."),
            Wait(4, _ => "La Sombra se queda sin defensas."),
            Pos("L02", "atk", "La Sombra ya no tiene Unidades. Coloca a la Doncella en Ataque para poder golpear con tus dos cartas."),
            Battle("Cuando el campo rival está vacío, puedes atacar directamente sus PV."),
            Strike("D02", null, "Selecciona al Segador, elige «Atacar» y confirma con «Ataque directo»."),
            Strike("L02", null, "La Doncella puede atacar aunque el Segador ya haya atacado. Úsala para llevar a la Sombra a 0 PV.", Over),
            Finish("¡Victoria! Has completado El primer paso."),
        ],
        2 =>
        [
            Info("Aprende a responder durante el turno rival. Guarda energía para proteger a tus Unidades y lleva al Cazador a 0 PV."),
            Keep,
            Spell("M05", "Círculo de Potencia cuesta 2 EN. Mientras esté activo, tus Unidades ganan +1 ATQ. El contador muestra cuánto le queda."),
            EndTurn("Fíjate en el contador del Hechizo al terminar el turno."),
            Wait(2, _ => "El Cazador invoca a su Segador y ataca directo: tu campo está vacío."),
            Summon("L02", "atk", "Turno 2 · Invoca a Doncella del Alba (1 EN) en Ataque. Guarda tu EN: la que no gastes sigue disponible en el turno del rival."),
            EndTurn("Pulsa «Fin del turno» con EN de sobra."),
            Wait(3, c => Attacked(c) && InHand(c, "L09")
                    ? "El Cazador ataca a tu Doncella. Responde con Promesa de Protección para darle +2 ATQ y +2 DEF durante este enfrentamiento."
                    : "Turno del Cazador: prepara su ataque.",
                (cmd, c) => Attacked(c) && InHand(c, "L09") ? cmd.Type == "PLAY_TRICK" && CardOf(c, cmd.Iid) == "L09" : OnlyPass(cmd, c)),
            Info("Su ataque falló: su Unidad cayó y el Cazador perdió la diferencia en PV."),
            Summon("D02", "atk", "Turno 3 · Invoca a tu Segador Novicio en Ataque."),
            Battle("Pulsa «Batalla»: el Cazador no tiene Unidades."),
            Strike("D02", null, "Ataca directo con el Segador."),
            Strike("L02", null, "Y con la Doncella."),
            EndBattle("Engaño: termina con EN y cartas en la mano sin usar. Pulsa «Fin de la batalla»."),
            Wait(4, _ => "El Cazador ve tu EN guardada y tus cartas: duda, se cubre en Defensa y no ataca."),
            EndTurn("Turno 4 · Estrategia: no gastes nada. Pulsa «Fin del turno» y espera su jugada."),
            Wait(5, c => RivalSpell(c) && InHand(c, "M08")
                    ? "El Cazador acaba de jugar un Hechizo. Contraconjuro cuesta 3 EN y lo niega antes de que su efecto se aplique."
                    : "Turno del Cazador.",
                (cmd, c) => RivalSpell(c) && InHand(c, "M08") ? cmd.Type == "PLAY_TRICK" && CardOf(c, cmd.Iid) == "M08" : OnlyPass(cmd, c)),
            Battle("Turno 5 · Remata. Pulsa «Batalla»."),
            Strike("D02", "F01", "Destruye a su Viajero en Defensa (DEF 1)."),
            Strike("L02", null, "Ataca directo con la Doncella y gana.", Over),
            Finish("¡Victoria! Has superado La emboscada."),
        ],
        _ => g.Family switch
        {
            "fool" =>
            [
                Info("Fase 3 · El eco de El Loco. Una muralla de defensores protege al Eco. Tu Arcano rompe una regla para cruzarla."),
                Keep,
                Summon("F00", "atk", "Invoca a El Loco (2 EN) en Ataque."),
                EndTurn("Pulsa «Fin del turno»."),
                Wait(2, _ => "La muralla no se mueve."),
                Summon("F01", "atk", "Invoca a Viajero del Umbral (1 EN) en Ataque."),
                Battle("Pulsa «Batalla».", mark: true),
                Strike("F01", "E04", "Ataca la muralla con el Viajero y mira lo que cuesta.", c => Still(c) && OnField(c, "F01") is null),
                Info("Atacar la muralla destruye tus Unidades y te cuesta PV. Rebobinamos: pulsa «Siguiente».", rewind: true),
                Battle("Pulsa «Batalla»."),
                Strike("F00", null, "Regla rota: El Loco ataca directo aunque haya Unidades. Ataca a los PV del Eco y termina el combate.", Over),
                Finish("El Loco te ha elegido. Su carta ya está en tu colección."),
            ],
            "death" =>
            [
                Info("Fase 3 · El eco de La Muerte. Tu Arcano ya está en juego, junto a un Acólito del Ocaso."),
                Keep,
                EndTurn("Pulsa «Fin del turno»."),
                Wait(2, _ => "El Coloso (ATQ 6) ataca a La Muerte (ATQ 6). Con el mismo ATQ caerían ambos, pero La Muerte no se destruye por empate."),
                new()
                {
                    Kind = "accion", Text = _ => "Usa la habilidad de La Muerte: sacrifica a tu Acólito y destruye al Pretor rival.",
                    Allows = (cmd, c) => cmd.Type == "ACTIVATE_ABILITY" && CardOf(c, cmd.SourceIid) == "D00" && cmd.Targets is { Length: > 0 } || cmd.Type == "PASS",
                    Done = c => Still(c) && !Units(c.S, Rival(c.Me)).Any(u => u.CardId == "E04"),
                },
                Battle("Pulsa «Batalla»."),
                Strike("D00", null, "El Eco no tiene defensas: ataca directo con La Muerte.", Over),
                Finish("La Muerte te ha elegido. Su carta ya está en tu colección."),
            ],
            _ =>
            [
                Info("Fase 3 · El eco de Los Enamorados. Caballero Juramentado y Dama del Lago están emparejados, y Los Enamorados en juego."),
                Keep,
                EndTurn("Pulsa «Fin del turno»."),
                Wait(2, _ => "El Eco se esconde tras un solo defensor."),
                Battle("Pulsa «Batalla»."),
                Strike("L05", "E01", "Regla rota: cada Unidad emparejada ataca dos veces. Primer golpe: destruye al defensor."),
                Strike("L05", null, "Segundo golpe: directo a los PV del Eco.", Over),
                Finish("Los Enamorados te han elegido. Su carta ya está en tu colección."),
            ],
        },
    };

    // ---------- Rival guionado ----------

    static Command? LessonRival(GameState s, string me, IReadOnlyList<Command> legal, GuideInfo g)
    {
        var yo = Rival(me);
        var mio = s.Players[yo];
        Command? Legal(Func<Command, bool> f) => legal.FirstOrDefault(f);
        if (s.Phase == "mulligan") return Legal(c => c.Type == "MULLIGAN");
        if (s.Chain is not null) return Legal(c => c.Type == "PASS");
        Command? End() => Legal(c => c.Type == "END_COMBAT") ?? Legal(c => c.Type == "END_TURN") ?? Legal(c => c.Type == "PASS");
        string? Iid(string cardId) => mio.Hand.FirstOrDefault(h => h.CardId == cardId)?.Iid;
        Command? Summon(string cardId, string position) => Units(s, yo).Any(u => u.CardId == cardId) ? null
            : Legal(c => c.Type == "SUMMON" && c.Iid == Iid(cardId) && c.Position == position && (c.Targets?.Length ?? 0) == 0);
        Command? Attack(string attacker, string? target)
        {
            var a = Units(s, yo).FirstOrDefault(u => u.CardId == attacker && !u.HasAttackedThisCombat && u.Position == "atk");
            var t = target is null ? null : Units(s, me).FirstOrDefault(u => u.CardId == target);
            return a is null || (target is not null && t is null) ? null
                : Legal(c => c.Type == "DECLARE_ATTACK" && c.AttackerIid == a.Iid && c.TargetIid == t?.Iid);
        }
        Command? Fight(string attacker, string? target) => s.Phase == "combat"
            ? (s.Initiative == yo ? Attack(attacker, target) : null) ?? End()
            : Legal(c => c.Type == "BATTLE") ?? End();
        if (s.Phase == "combat" && s.Initiative != yo) return End();
        switch (g.Lesson, s.Round)
        {
            case (1, 1): return Summon("F01", "atk") ?? Summon("E01", "def") ?? Fight("F01", "L02");
            case (1, 2): return Summon("E03", "def") ?? End();
            case (2, 1): return Summon("D02", "atk") ?? Fight("D02", null);
            case (2, 2): return Fight("D02", "L02");
            case (2, 3): return Summon("F01", "def") ?? End();
            case (2, 4):
                var spell = mio.Hand.FirstOrDefault(h => h.CardId == "M05");
                return (spell is null ? null : Legal(c => c.Type == "PLAY_SPELL" && c.Iid == spell.Iid)) ?? End();
            case (3, 1) when g.Family == "death": return Fight("C04", "D00");
        }
        return End();
    }
}
