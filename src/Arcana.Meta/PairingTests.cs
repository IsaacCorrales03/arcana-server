// Pruebas del sistema Emparejar (acoplar) de Los Enamorados y de la sinergia de sus magias y trucos.
// Las ejecuta tools/Arcana.Tools («parejas») y el cliente con --selftest (Arcana.Tools está bloqueado en algunos equipos).
using Arcana.AI;
using Arcana.Content;
using Arcana.Core;

namespace Arcana.Meta;

public static class PairingTests
{
    sealed class Table(GameContext ctx)
    {
        public GameState S = null!;
        public readonly List<Ev> Events = [];

        public void Apply(Command cmd, bool settle = true)
        {
            Events.Clear();
            while (true)
            {
                var r = Rules.Apply(S, cmd, ctx);
                if (!r.Ok) throw new InvalidOperationException($"{cmd.Type}: {r.Error!.Message}");
                S = r.Value!.State;
                Events.AddRange(r.Value.Events);
                if (!settle || S.Chain is not { } chain) return;
                cmd = Command.Pass(chain.Priority);
            }
        }
        public string? Why(Command cmd) => Rules.Validate(S, cmd, ctx)?.Message;
        public UnitInPlay U(string iid) => S.Players.A.Unit(iid)!;
        public (int, int) Stats(string iid)
        {
            var u = U(iid);
            var d = ctx.Card(u.CardId)!;
            return (ArcanaRules.EffectiveStat(u, d, "atk", Effects.StaticBonus(S, iid, "atk", ctx)),
                    ArcanaRules.EffectiveStat(u, d, "def", Effects.StaticBonus(S, iid, "def", ctx)));
        }
    }

    static UnitInPlay Unit(string iid, string card, string pos = "atk") => new() { Iid = iid, CardId = card, Position = pos };
    static CardInstance Card(string iid, string card) => new() { Iid = iid, CardId = card };
    static SpellInPlay Spell(string iid, string card) => new() { Iid = iid, CardId = card, RemainingDuration = new Duration(3) };

    static GameState Board(UnitInPlay?[] units, CardInstance[] hand, SpellInPlay?[]? spells = null, string phase = "turnA") => new()
    {
        Phase = phase, Round = 3, ActivePlayer = P.A,
        Players = new Players
        {
            A = new PlayerState
            {
                Id = P.A, Seat = 1, DeckId = "lovers", Hp = 20, Energy = 7, MaxEnergy = 7, MulliganDone = true,
                UnitSlots = [.. units, .. Enumerable.Repeat<UnitInPlay?>(null, 4 - units.Length)], Hand = hand,
                SpellSlots = [.. spells ?? [], .. Enumerable.Repeat<SpellInPlay?>(null, 4 - (spells?.Length ?? 0))],
                Deck = Enumerable.Range(0, 12).Select(i => Card($"A:E01:d{i}", "E01")).ToArray(),
            },
            B = new PlayerState
            {
                Id = P.B, Seat = 2, DeckId = "fool", Hp = 30, Energy = 7, MaxEnergy = 7, MulliganDone = true,
                UnitSlots = new UnitInPlay?[4], SpellSlots = new SpellInPlay?[4],
                Deck = Enumerable.Range(0, 12).Select(i => Card($"B:E01:d{i}", "E01")).ToArray(),
            },
        },
    };

    static Command Attach(string hand, string unit) => new() { Type = "ATTACH", Player = P.A, Iid = hand, UnitIid = unit };

    /// <summary>Ejecuta todas las pruebas; report(ok, texto) por cada una. Devuelve el número de fallos.</summary>
    public static int Run(Action<bool, string> report)
    {
        var baseCtx = GameContext.Create(ContentLoader.Load().Cards);
        var ctx = baseCtx with { Choices = new PlanChoices(baseCtx) };
        int fails = 0;
        void Check(bool ok, string label) { if (!ok) fails++; report(ok, label); }
        void Safe(string label, Action test)
        {
            try { test(); }
            catch (Exception e) { Check(false, $"{label}: {e.Message}"); }
        }
        var t = new Table(ctx);

        // ---------- Fase 1: estadísticas y «al emparejar», en los dos órdenes, sin y con El Arcano ----------
        (string a, string b, int atk, int def, string extra)[] pairs =
        [
            ("L01", "L02", 3, 3, "heal2"), ("L03", "L04", 4, 4, "draw1"), ("L05", "L06", 6, 7, "omen"), ("L12", "L13", 5, 3, "heal2draw1"),
        ];
        foreach (var (a, b, atk, def, extra) in pairs)
            foreach (var (first, second) in new[] { (a, b), (b, a) })
                foreach (var major in new[] { false, true })
                    Safe($"{first}+{second}", () =>
                    {
                        UnitInPlay?[] field = major ? [Unit("u1", first), Unit("arc", "L00")] : [Unit("u1", first)];
                        t.S = Board(field, [Card("h1", second)]);
                        t.Apply(Attach("h1", "u1"));
                        int bonus = major ? 1 : 0;
                        var hand = t.S.Players.A.Hand.Length;
                        bool effect = extra switch
                        {
                            "heal2" => t.S.Players.A.Hp == 22,
                            "draw1" => hand == 1,
                            "omen" => t.Events.Any(e => e.Type == "OMEN_ACTIVATED"),
                            _ => t.S.Players.A.Hp == 22 && hand == 1,
                        };
                        Check(t.Stats("u1") == (atk + bonus, def + bonus) && effect && t.U("u1").Attached?.CardId == second,
                            $"{first} en campo + {second} acoplada{(major ? " con Los Enamorados" : "")}: {t.Stats("u1").Item1}/{t.Stats("u1").Item2} (esperado {atk + bonus}/{def + bonus}), al emparejar {extra}");
                    });

        Safe("reglas", () =>
        {
            t.S = Board([Unit("u1", "L01")], [Card("h1", "L03")]);
            Check(t.Why(Attach("h1", "u1")) == "Tu pareja no está en el campo", "Una carta que no es la pareja no puede acoplarse");
            t.S = Board([Unit("u1", "L01")], [Card("h1", "L02"), Card("h2", "L02")]);
            t.Apply(Attach("h1", "u1"));
            Check(t.Why(Attach("h2", "u1")) == "Esa Unidad ya está emparejada", "Una Unidad ya emparejada no puede acoplar otra");
            t.S = Board([Unit("u1", "L03")], [Card("h1", "L02")]);
            Check(!Rules.LegalActions(t.S, P.A, ctx).Any(c => c.Type == "ATTACH"), "«Acoplar» no aparece si la pareja no está en el campo");
            t.S = Board([Unit("u1", "L05", "def") with { HasAttackedThisCombat = true }, Unit("x2", "E01"), Unit("x3", "E01"), Unit("x4", "E01")], [Card("h1", "L06")]);
            t.Apply(Attach("h1", "u1"));
            Check(t.U("u1").Attached is not null, "Acoplar funciona con los 4 espacios de Unidad ocupados");
            Check(t.U("u1").Position == "def" && t.U("u1").HasAttackedThisCombat, "La Unidad conserva su posición y su estado de ataque");
            t.S = Reducers.Reduce(t.S, new Ev { Type = "CARD_DESTROYED", Player = P.A, Iid = "u1" }, ctx)!;
            var grave = t.S.Players.A.Discard.Select(c => c.CardId).ToList();
            Check(grave.Contains("L05") && grave.Contains("L06"), "Al destruirse la Unidad emparejada, las dos cartas llegan al cementerio");
            t.S = Board([Unit("u1", "L01"), Unit("u2", "L02")], []);
            Check(!ArcanaRules.IsPaired(t.S.Players.A, t.U("u1"), ctx), "Dos parejas sueltas en el campo ya no están emparejadas");
        });

        // ---------- Fase 2: sinergia de magias y trucos ----------
        Safe("L07", () =>
        {
            t.S = Board([Unit("u1", "L03")], [Card("h1", "L04")], [Spell("s1", "L07")]);
            t.Apply(Attach("h1", "u1"));
            Check(t.S.Players.A.Hp == 21 && t.Stats("u1") == (4, 5), $"L07: recupera 1 PV al acoplar y +1 DEF una sola vez ({t.Stats("u1").Item1}/{t.Stats("u1").Item2})");
        });
        Safe("L08", () =>
        {
            t.S = Board([], [Card("h0", "L08")]);
            t.Apply(new Command { Type = "PLAY_SPELL", Player = P.A, Iid = "h0" });
            Check(t.S.Players.A.Hand.Length == 1, "L08: roba 1 carta al jugarlo");
            t.S = Board([Unit("u1", "L01"), Unit("u2", "L05")], [Card("h1", "L02"), Card("h2", "L06")], [Spell("s1", "L08")]);
            t.Apply(Attach("h1", "u1"));
            var afterFirst = t.S.Players.A.Hand.Length;
            t.Apply(Attach("h2", "u2"));
            Check(afterFirst == 2 && t.S.Players.A.Hand.Length == 1, "L08: roba la primera vez por ronda que acoplas y no la segunda");
        });
        (int atk, int def) DefenseWithPromise(bool paired)
        {
            UnitInPlay mine = paired ? Unit("u1", "L03") with { Attached = Card("a1", "L04"), PairAtk = 3, PairDef = 3 } : Unit("u1", "L03");
            t.S = Board([mine], [Card("h1", "L09"), Card("h2", "L09")], phase: "combat") with { Initiative = P.B, ActivePlayer = P.B };
            // el rival guarda un Truco de respuesta: así la cadena no se resuelve sola y se puede mirar entre eslabones
            t.S = t.S.Update(P.B, p => p with { UnitSlots = [Unit("r1", "C04"), null, null, null], Hand = [Card("b1", "M09")] });
            t.Apply(new Command { Type = "DECLARE_ATTACK", Player = P.B, AttackerIid = "r1", TargetIid = "u1" }, settle: false);
            t.Apply(new Command { Type = "PLAY_TRICK", Player = P.A, Iid = "h1" });
            var mods = t.Events.Where(e => e.Type == "STAT_MODIFIED" && e.Iid == "u1").ToList();
            return (mods.Where(m => m.Stat == "atk").Sum(m => m.Amount ?? 0), mods.Where(m => m.Stat == "def").Sum(m => m.Amount ?? 0));
        }
        Safe("L09", () =>
        {
            var loose = DefenseWithPromise(false);
            Check(loose == (2, 2), $"L09: +2/+2 a una Unidad suelta (medido +{loose.atk}/+{loose.def})");
            var paired = DefenseWithPromise(true);
            Check(paired == (3, 3), $"L09: +3/+3 a una Unidad emparejada (medido +{paired.atk}/+{paired.def})");
        });
        int DrawsWithBrokenHeart(bool paired)
        {
            UnitInPlay mine = paired ? Unit("u1", "L01") with { Attached = Card("a1", "L02"), PairAtk = 2, PairDef = 3 } : Unit("u1", "L01");
            t.S = Board([mine], [Card("h1", "L10")], phase: "combat") with { Initiative = P.B, ActivePlayer = P.B };
            t.S = t.S.Update(P.B, p => p with { UnitSlots = [Unit("r1", "C04"), null, null, null] });
            t.Apply(new Command { Type = "DECLARE_ATTACK", Player = P.B, AttackerIid = "r1", TargetIid = "u1" }, settle: false);
            t.Apply(new Command { Type = "PLAY_TRICK", Player = P.A, Iid = "h1", Targets = ["u1"] });
            return t.S.Players.A.Hand.Length;
        }
        Safe("L10", () =>
        {
            Check(DrawsWithBrokenHeart(false) == 1 && t.S.Players.B.Hp == 28, "L10: roba 1 si cae una Unidad suelta (y el rival pierde 2 PV)");
            Check(DrawsWithBrokenHeart(true) == 2, "L10: roba 2 si cae una Unidad emparejada");
        });
        Safe("combinado", () =>
        {
            t.S = Board([Unit("u1", "L05"), Unit("arc", "L00")], [Card("h1", "L06")], [Spell("s1", "L07"), Spell("s2", "L08")]);
            t.Apply(Attach("h1", "u1"));
            var twice = t.S with { Phase = "combat", Initiative = P.A };
            twice = twice.Update(P.A, p => p with { UnitSlots = p.UnitSlots.Select(u => u?.Iid == "u1" ? u with { HasAttackedThisCombat = true } : u).ToArray() });
            var again = Rules.Validate(twice, new Command { Type = "DECLARE_ATTACK", Player = P.A, AttackerIid = "u1" }, ctx);
            Check(t.Stats("u1") == (8, 9) && again is null,
                $"Caballero + Dama con Los Enamorados, L07 y L08: {t.Stats("u1").Item1}/{t.Stats("u1").Item2} (esperado 8/9) y puede atacar dos veces");
        });
        // ---------- Escudero de Carga: bufa a las otras aliadas que atacan, no a sí mismo ----------
        Safe("C01", () =>
        {
            foreach (var (attacker, expected) in new[] { ("u1", 0), ("u2", 1) })
            {
                t.S = Board([Unit("u1", "C01"), Unit("u2", "E01")], [], null, "combat") with { Initiative = P.A };
                t.Apply(new Command { Type = "DECLARE_ATTACK", Player = P.A, AttackerIid = attacker, TargetIid = null });
                int buffs = t.Events.Count(e => e.Type == "STAT_MODIFIED" && e.SourceIid == "u1" && e.Iid == attacker);
                Check(buffs == expected, $"Escudero de Carga: cuando ataca {(attacker == "u1" ? "él mismo" : "otra aliada")} da {buffs} bufo(s) (esperado {expected})");
            }
        });

        return fails;
    }
}
