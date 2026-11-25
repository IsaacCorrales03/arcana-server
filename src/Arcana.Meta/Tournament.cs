// Torneo de los Arcanos (port de client/torneo.ts): todos contra todos por rondas con eliminación, o llave de eliminación directa (4, 8, 16, 32). Lógica pura.
namespace Arcana.Meta;

public sealed record Duelist(string Id, string Nombre, string Deck, bool Humano, int Puntos = 0, int Dif = 0, bool Fuera = false);
public sealed record Duel(string A, string B, int Seed, string? Sala = null, string? Ganador = null);
public sealed record Tournament(int Ronda, string Bot, List<Duelist> Duelistas, List<Duel> Duelos, string? Descansa = null, string? Eliminado = null, string? Campeon = null, bool Llave = false, List<List<Duel>>? Cuadro = null)
{
    /// <summary>Llave: rondas ya jugadas (el cuadro), sin la actual.</summary>
    public List<List<Duel>> Pasadas => Cuadro ?? [];
    public Duelist Get(string id) => Duelistas.First(d => d.Id == id);
    public List<Duelist> Active => Duelistas.Where(d => !d.Fuera).ToList();
    public bool RoundOver => Duelos.All(d => d.Ganador is not null);
    /// <summary>Primero los que siguen, luego por puntos y diferencia de PV.</summary>
    public List<Duelist> Standings => Duelistas.OrderBy(x => x.Fuera ? 1 : 0).ThenByDescending(x => x.Puntos).ThenByDescending(x => x.Dif).ToList();
}

public sealed record Human(string Id, string Nombre, string Deck);

public static class Tournaments
{
    public static readonly int[] Sizes = [3, 4, 5, 6, 7, 8, 9];
    public static readonly int[] BracketSizes = [4, 8, 16, 32];
    const int Win = 3, Draw = 1, Rest = 1;

    static List<T> Shuffle<T>(IEnumerable<T> xs, Func<double> rnd)
    {
        var a = xs.ToList();
        for (var i = a.Count - 1; i > 0; i--)
        {
            var j = (int)Math.Floor(rnd() * (i + 1));
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }

    public static string Code(Func<double> rnd) => new(Enumerable.Range(0, 5).Select(_ => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"[(int)Math.Floor(rnd() * 32)]).ToArray());

    public static Tournament Create(int size, IReadOnlyList<Human> humans, IReadOnlyList<(string Id, string Name)> decks, string bot, Func<double> rnd, bool bracket = false)
    {
        if (!(bracket ? BracketSizes : Sizes).Contains(size) || humans.Count > size)
            throw new InvalidOperationException(bracket ? "La llave es de 4, 8, 16 o 32 duelistas" : "El torneo es de 3 a 9 duelistas");
        var free = Shuffle(decks.Where(m => !humans.Any(h => h.Deck == m.Id)), rnd);
        var ias = Enumerable.Range(0, size - humans.Count).Select(i => i < free.Count ? free[i] : decks[(int)Math.Floor(rnd() * decks.Count)]).ToList();
        var duelists = humans.Select(h => new Duelist(h.Id, h.Nombre, h.Deck, true))
            .Concat(ias.Select((m, i) => new Duelist($"ia{i}", $"Discípulo de {m.Name}", m.Id, false))).ToList();
        // Llave: el sorteo fija el cuadro (humanos y bots mezclados); Pair lo respeta ronda a ronda.
        return Pair(new Tournament(1, bot, bracket ? Shuffle(duelists, rnd) : duelists, [], Llave: bracket), rnd);
    }

    static Tournament Pair(Tournament t, Func<double> rnd)
    {
        var free = t.Llave ? t.Active.Select(d => d.Id).ToList() : Shuffle(t.Active.Select(d => d.Id), rnd);
        string? rest = null;
        if (free.Count % 2 == 1)
        {
            rest = free.FirstOrDefault(id => id != t.Descansa) ?? free[0];
            free.Remove(rest);
        }
        var duels = new List<Duel>();
        for (var i = 0; i < free.Count; i += 2)
        {
            var (a, b) = (free[i], free[i + 1]);
            var people = t.Get(a).Humano && t.Get(b).Humano;
            var seed = (int)Math.Floor(rnd() * 2147483648.0);
            duels.Add(new Duel(a, b, seed, people ? Code(rnd) : null));
        }
        return t with { Duelos = duels, Descansa = rest };
    }

    public static Tournament Record(Tournament t, int i, string winner, int hpA, int hpB)
    {
        if (i < 0 || i >= t.Duelos.Count) return t;
        var d = t.Duelos[i];
        if (d.Ganador is not null || (winner != "empate" && winner != d.A && winner != d.B)) return t;
        var pa = winner == "empate" ? Draw : winner == d.A ? Win : 0;
        var pb = winner == "empate" ? Draw : winner == d.B ? Win : 0;
        var duelists = t.Duelistas.Select(x =>
            x.Id == d.A ? x with { Puntos = x.Puntos + pa, Dif = x.Dif + hpA - hpB }
            : x.Id == d.B ? x with { Puntos = x.Puntos + pb, Dif = x.Dif + hpB - hpA } : x).ToList();
        return t with { Duelistas = duelists, Duelos = t.Duelos.Select((x, k) => k == i ? x with { Ganador = winner } : x).ToList() };
    }

    /// <summary>Una persona se fue: sigue como IA.</summary>
    public static Tournament ToAI(Tournament t, string id) => t with
    {
        Duelistas = t.Duelistas.Select(x => x.Id == id ? x with { Humano = false, Nombre = $"{x.Nombre} (IA)" } : x).ToList(),
        Duelos = t.Duelos.Select(d => d.A == id || d.B == id ? d with { Sala = null } : d).ToList(),
    };

    public static Tournament CloseRound(Tournament t, Func<double> rnd)
    {
        if (!t.RoundOver || t.Campeon is not null) throw new InvalidOperationException("La ronda aún no terminó");
        if (t.Llave) return CloseBracketRound(t, rnd);
        var duelists = t.Duelistas.Select(x => x.Id == t.Descansa ? x with { Puntos = x.Puntos + Rest } : x).ToList();
        var alive = duelists.Where(x => !x.Fuera).ToList();
        if (alive.Count == 2 && t.Duelos.Count == 1)
        {
            var f = t.Duelos[0];
            var order = alive.OrderByDescending(x => x.Puntos).ThenByDescending(x => x.Dif).ToList();
            var champ = f.Ganador != "empate" ? f.Ganador! : order[0].Id;
            return t with
            {
                Duelistas = duelists.Select(x => x.Id != champ ? x with { Fuera = true } : x).ToList(),
                Campeon = champ, Eliminado = alive.First(x => x.Id != champ).Id,
            };
        }
        var worst = Shuffle(alive, rnd).OrderBy(x => x.Puntos).ThenBy(x => x.Dif).First();
        duelists = duelists.Select(x => x.Id == worst.Id ? x with { Fuera = true } : x).ToList();
        return Pair(t with { Ronda = t.Ronda + 1, Duelistas = duelists, Eliminado = worst.Id }, rnd);
    }

    /// <summary>Llave: pasa quien gana su duelo (en empate, mejor diferencia de PV acumulada y luego puntos; si sigue igual, sorteo).</summary>
    static Tournament CloseBracketRound(Tournament t, Func<double> rnd)
    {
        var out_ = new HashSet<string>();
        foreach (var d in t.Duelos)
        {
            var (a, b) = (t.Get(d.A), t.Get(d.B));
            var win = d.Ganador != "empate" ? d.Ganador!
                : Shuffle([a, b], rnd).OrderByDescending(x => x.Dif).ThenByDescending(x => x.Puntos).First().Id;
            out_.Add(win == d.A ? d.B : d.A);
        }
        var duelists = t.Duelistas.Select(x => out_.Contains(x.Id) ? x with { Fuera = true } : x).ToList();
        var next = t with { Duelistas = duelists, Cuadro = [.. t.Pasadas, t.Duelos], Eliminado = null };
        var alive = next.Active;
        return alive.Count == 1 ? next with { Campeon = alive[0].Id } : Pair(next with { Ronda = t.Ronda + 1 }, rnd);
    }
}
