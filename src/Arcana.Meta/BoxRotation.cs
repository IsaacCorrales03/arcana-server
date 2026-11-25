namespace Arcana.Meta;

public sealed record BoxWeek(DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<string> ActiveBoxIds)
{
    public bool Contains(string family) => ActiveBoxIds.Contains(family);
}

public sealed record BoxRotation(DateTimeOffset Now, BoxWeek Week, IReadOnlyList<BoxWeek> Calendar)
{
    public const int BoxesPerWeek = 3;

    public BoxWeek? ReturnOf(string family) =>
        Calendar.FirstOrDefault(week => week.StartsAt >= Week.EndsAt && week.Contains(family));
}
