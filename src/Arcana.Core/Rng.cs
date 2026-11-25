// Aleatoriedad reproducible (port de core/random/prng.ts): mulberry32 con estado explícito, bit a bit igual que TS.
namespace Arcana.Core;

public static class Rng
{
    public static uint SeedToState(long seed) => unchecked((uint)seed);

    /// <summary>Siguiente flotante en [0, 1) y nuevo estado.</summary>
    public static (double value, uint next) NextFloat(uint state)
    {
        unchecked
        {
            var s = state + 0x6d2b79f5u;
            var t = s;
            t = (t ^ (t >> 15)) * (t | 1u);
            t ^= t + (t ^ (t >> 7)) * (t | 61u);
            return ((t ^ (t >> 14)) / 4294967296.0, s);
        }
    }

    /// <summary>Fisher–Yates. No muta la entrada.</summary>
    public static (T[] items, uint next) Shuffle<T>(IReadOnlyList<T> items, uint state)
    {
        var a = items.ToArray();
        var s = state;
        for (var i = a.Length - 1; i > 0; i--)
        {
            var (r, next) = NextFloat(s);
            s = next;
            var j = (int)Math.Floor(r * (i + 1));
            (a[i], a[j]) = (a[j], a[i]);
        }
        return (a, s);
    }

    public static (bool heads, uint next) CoinFlip(uint state)
    {
        var (r, next) = NextFloat(state);
        return (r < 0.5, next);
    }

    /// <summary>Mezcla estable (FNV-1a) del iid de la fuente: dos dados sobre el mismo estado no salen iguales.</summary>
    public static uint Salt(string iid)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in iid) h = (h ^ ch) * 16777619u;
            return h;
        }
    }

    /// <summary>Number.prototype.toString(36) para enteros sin signo.</summary>
    public static string ToBase36(uint v)
    {
        if (v == 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var s = new System.Text.StringBuilder();
        while (v > 0) { s.Insert(0, digits[(int)(v % 36)]); v /= 36; }
        return s.ToString();
    }
}
