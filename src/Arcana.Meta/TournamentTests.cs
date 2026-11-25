// Pruebas de la llave de eliminación directa del Torneo (4, 8, 16, 32) y del relleno con bots.
// Las ejecuta tools/Arcana.Tools («parejas») y el cliente con --selftest.
namespace Arcana.Meta;

public static class TournamentTests
{
    public static int Run(Action<bool, string> report)
    {
        var fails = 0;
        void Check(bool ok, string what) { report(ok, what); if (!ok) fails++; }
        var decks = Enumerable.Range(0, 9).Select(i => ($"d{i}", $"Mazo {i}")).ToList();

        foreach (var size in Tournaments.BracketSizes)
        {
            foreach (var humansN in new[] { 0, 1, Math.Min(3, size), size })
            {
                var rnd = new Random(size * 31 + humansN);
                double R() => rnd.NextDouble();
                var humans = Enumerable.Range(0, humansN).Select(i => new Human($"h{i}", $"J{i}", "d0")).ToList();
                var t = Tournaments.Create(size, humans, decks, "heuristic", R, bracket: true);
                var bots = t.Duelistas.Count(d => !d.Humano);
                Check(t.Duelistas.Count == size && bots == size - humansN && t.Duelos.Count == size / 2,
                    $"llave de {size} con {humansN} humanos: se rellena con {size - humansN} bots y abre con {size / 2} duelos");
                var rounds = 0;
                var ok = true;
                var expectDuels = size / 2;
                while (t.Campeon is null && rounds < 10)
                {
                    ok &= t.Duelos.Count == expectDuels && t.Descansa is null && t.Active.Count == expectDuels * 2;
                    for (var k = 0; k < t.Duelos.Count; k++)
                        t = Tournaments.Record(t, k, k % 7 == 3 ? "empate" : R() < 0.5 ? t.Duelos[k].A : t.Duelos[k].B, 10, 5);
                    var before = t.Active.Count;
                    t = Tournaments.CloseRound(t, R);
                    ok &= t.Active.Count == before / 2;
                    expectDuels /= 2;
                    rounds++;
                }
                var expected = (int)Math.Log2(size);
                Check(ok && t.Campeon is not null && t.Active.Count == 1 && t.Active[0].Id == t.Campeon && rounds == expected
                      && t.Pasadas.Count == expected && t.Pasadas.Select(r => r.Count).SequenceEqual(Enumerable.Range(0, expected).Select(r => size >> (r + 1))),
                    $"llave de {size} con {humansN} humanos: {expected} rondas hasta un único campeón (rondas {rounds})");
            }
        }

        // el ganador de cada duelo es quien juega la ronda siguiente, y el cuadro mantiene su sitio
        var r2 = new Random(5);
        var t8 = Tournaments.Create(8, [new Human("h", "J", "d0")], decks, "heuristic", r2.NextDouble, bracket: true);
        var winners = t8.Duelos.Select(d => d.A).ToList();
        for (var k = 0; k < 4; k++) t8 = Tournaments.Record(t8, k, winners[k], 10, 0);
        t8 = Tournaments.CloseRound(t8, r2.NextDouble);
        Check(t8.Duelos.Select((d, k) => (d.A, d.B)).SelectMany(p => new[] { p.A, p.B }).SequenceEqual(winners), "llave: los ganadores pasan emparejados en el orden del cuadro");

        // un humano que se va sigue como bot y no rompe la llave
        var left = Tournaments.ToAI(t8, t8.Duelistas.First(d => d.Humano).Id);
        Check(left.Duelistas.All(d => !d.Humano) && left.Duelos.All(d => d.Sala is null), "llave: el humano que se va sigue como bot");

        // tamaños no válidos
        bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
        Check(Throws(() => Tournaments.Create(6, [], decks, "heuristic", new Random(1).NextDouble, bracket: true))
              && Throws(() => Tournaments.Create(4, Enumerable.Range(0, 5).Select(i => new Human($"h{i}", "J", "d0")).ToList(), decks, "heuristic", new Random(1).NextDouble, bracket: true)),
            "llave: solo 4, 8, 16 o 32 y sin más humanos que cupos");

        // la liguilla de siempre no cambia
        var rr = Tournaments.Create(5, [new Human("h", "J", "d0")], decks, "heuristic", new Random(2).NextDouble);
        Check(!rr.Llave && rr.Duelistas.Count == 5, "liguilla: sigue siendo todos contra todos de 3 a 9");
        return fails;
    }
}
