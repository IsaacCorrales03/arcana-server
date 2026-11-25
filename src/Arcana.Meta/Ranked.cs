using Arcana.Content;

namespace Arcana.Meta;

public sealed record DuelRecord(DateTimeOffset At, string Mode, string Result, string Rival, string DeckKey, string DeckName,
    Dictionary<string, int> Played, string? Replay = null, int? Points = null, string? MatchId = null);

public sealed record RankRules(int PointsPerDivision = 100, int Win = 20, int StreakBonus = 5, int BonusAt = 3,
    int Loss = 15, int Grace = 1, int Placements = 5, int SeasonDays = 60);

public sealed record RankReward(int Gems, int Essence, string? Cosmetic);
public sealed record RankTier(string Name, string Color, RankReward Reward);
public sealed record Season(string Id, string Name, DateTimeOffset EndsAt);
public sealed record RankProgress(string Season = "", int Ladder = 0, int Best = 0, int Streak = 0, int Played = 0, int Grace = 0);
public sealed record RankedEntry(string Id, string Name, string? Avatar, RankProgress Rank);
public sealed record RankedReceipt(string MatchId, RankProgress Before, RankProgress After, int Change,
    string? Result = null, string? Rival = null, string? DeckKey = null, string? DeckName = null, DateTimeOffset? At = null);

public static class Ranked
{
    public static readonly RankRules Rules = new();
    public static readonly RankTier[] Tiers =
    [
        new("Iniciado", "bd9376", new(100, 50, null)),
        new("Aprendiz", "db966f", new(180, 100, null)),
        new("Adepto", "b7c8e6", new(260, 180, "Marco lunar")),
        new("Vidente", "e7c579", new(350, 260, "Vidente")),
        new("Hechicero", "78cde9", new(450, 420, "Reverso celeste")),
        new("Oráculo", "cfa1f7", new(600, 600, "Marco de amatista")),
        new("Arcano Mayor", "ecc2a5", new(850, 850, "Arcano Mayor")),
    ];

    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static Season SeasonAt(DateTimeOffset now, RankRules? config = null)
    {
        int duration = (config ?? Rules).SeasonDays;
        int number = Math.Max(0, (int)Math.Floor((now - Epoch).TotalDays / duration));
        return new($"arcana-{number + 1}", $"Temporada {number + 1} · El Telar", Epoch.AddDays((number + 1) * duration));
    }

    public static int Tier(int ladder, RankRules? rules = null) => Math.Clamp(ladder / ((rules ?? Rules).PointsPerDivision * 3), 0, 6);
    public static int Points(int ladder, RankRules? rules = null)
    {
        int per = (rules ?? Rules).PointsPerDivision;
        return Tier(ladder, rules) == 6 ? ladder - 18 * per : ladder % per;
    }
    public static string Label(int ladder, RankRules? rules = null)
    {
        int tier = Tier(ladder, rules);
        int division = ladder / (rules ?? Rules).PointsPerDivision % 3;
        return tier == 6 ? Tiers[tier].Name : Tiers[tier].Name + " " + new[] { "III", "II", "I" }[division];
    }

    public static RankedReceipt Resolve(string matchId, RankProgress rank, Season season, string outcome, RankRules? rules = null)
    {
        var config = rules ?? Rules;
        if (rank.Season != season.Id) rank = new(season.Id);
        int next = rank.Ladder, grace = rank.Grace;
        int streak = outcome == "victory" ? rank.Streak + 1 : 0;
        if (outcome == "victory")
        {
            next += config.Win + (streak >= config.BonusAt ? config.StreakBonus : 0);
            if (next / config.PointsPerDivision > rank.Ladder / config.PointsPerDivision) grace = config.Grace;
        }
        if (outcome == "defeat" && Tier(next, config) > 0)
        {
            next -= config.Loss;
            int floor = rank.Ladder / config.PointsPerDivision * config.PointsPerDivision;
            if (next < floor && grace > 0) { next = floor; grace--; }
            else if (next < floor) grace = config.Grace;
        }
        var after = rank with { Ladder = next, Best = Math.Max(rank.Best, next), Streak = streak, Played = rank.Played + 1, Grace = grace };
        return new(matchId, rank, after, next - rank.Ladder);
    }

    public static (int Level, int Value, int Goal) Level(Profile profile)
    {
        long xp = (long)profile.Partidas * 50 + profile.History.Count(row => row.Result == "victory") * 25L;
        int level = 1;
        while (level < 100 && xp >= 250 + level * 50) xp -= 250 + level++ * 50;
        int goal = 250 + level * 50;
        return (level, (int)Math.Min(goal, xp), goal);
    }
}
