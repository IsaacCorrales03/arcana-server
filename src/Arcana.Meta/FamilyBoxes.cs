using Arcana.Content;

namespace Arcana.Meta;

public static partial class Economy
{
    public const int BoxPackCount = 12;
    internal const int BoxFormat = 1;

    private static readonly Lazy<Dictionary<string, string[]>> BoxCards = new(() =>
        Families.ToDictionary(family => family.Key, family =>
        {
            var baseCards = Cards.Where(card => card.Id[0] == family.Letter && !ContentLoader.IsCampaignCard(card.Id)).Select(card => card.Id).ToArray();
            var cards = baseCards.Concat(baseCards.Where(id => WithAlt.Contains(id)).Select(id => $"{id}@alt")).ToArray();
            if (cards.Length != BoxPackCount)
                throw new InvalidOperationException($"La caja de {family.Key} debe contener {BoxPackCount} cartas distintas.");
            return cards;
        }, StringComparer.Ordinal));

    public static bool HasFamilyBox(string family) => BoxCards.Value.ContainsKey(family);

    public static int BoxSize(string family) => HasFamilyBox(family) ? BoxPackCount * CardsPerPack : 0;

    static List<string> FullBox(string family) =>
        BoxCards.Value[family].SelectMany(id => Enumerable.Repeat(id, MaxCopies)).ToList();

    public static int BoxRemaining(Profile profile, string family) =>
        profile.CompletedFamilyBoxes.Contains(family) ? 0
        : profile.FamilyBoxes.TryGetValue(family, out var remaining) ? remaining.Count : BoxSize(family);

    public static int BoxOpenedPacks(Profile profile, string family) => (BoxSize(family) - BoxRemaining(profile, family)) / CardsPerPack;

    public static string? BoxRestriction(Profile profile, string family, BoxRotation? rotation)
    {
        if (!HasFamilyBox(family)) return "Esta caja no existe.";
        if (profile.CompletedFamilyBoxes.Contains(family)) return "Esta caja ya está completada.";
        if (rotation is null || rotation.Now < rotation.Week.StartsAt || rotation.Now >= rotation.Week.EndsAt
            || !rotation.Week.Contains(family))
            return "No disponible en este momento. Tu progreso se conserva hasta que la caja vuelva a la tienda.";
        return null;
    }

    static void RequireFamilyPack(Profile profile, string family, int quantity, BoxRotation? rotation)
    {
        if (BoxRestriction(profile, family, rotation) is string message) throw new InvalidOperationException(message);
        if (quantity > BoxRemaining(profile, family) / CardsPerPack)
            throw new InvalidOperationException("No quedan tantos sobres en esta caja.");
    }

    public static IReadOnlyDictionary<string, int> BoxContents(Profile profile, string family)
    {
        var current = profile.FamilyBoxes.GetValueOrDefault(family);
        bool complete = profile.CompletedFamilyBoxes.Contains(family);
        return BoxCards.Value[family].ToDictionary(id => id, id =>
            complete ? 0 : current is null ? MaxCopies : current.Count(card => card == id));
    }

    private static List<string>? ReadBox(string family, List<string> remaining, bool legacy)
    {
        var additions = legacy ? FullBox(family).Where(ContentLoader.IsBoxExpansion).ToArray() : [];
        var full = FullBox(family).Except(additions).ToHashSet();
        if (remaining.Count == 0 || remaining.Count >= full.Count * MaxCopies || remaining.Count % CardsPerPack != 0)
            return null;
        if (remaining.GroupBy(id => id).Any(group => !full.Contains(group.Key) || group.Count() > MaxCopies))
            return null;
        return [.. remaining, .. additions];
    }

    private static Opened OpenFamily(Profile profile, string family, Func<double> random, BoxRotation? rotation)
    {
        RequireFamilyPack(profile, family, 1, rotation);
        if (profile.SobresFam.GetValueOrDefault(family) < 1)
            throw new InvalidOperationException("No tienes sobres de esta caja");
        var next = profile.Clone();
        if (next.SobresFam[family] == 1) next.SobresFam.Remove(family);
        else next.SobresFam[family]--;
        var pulled = new List<string>(CardsPerPack);
        if (!next.FamilyBoxes.TryGetValue(family, out var remaining))
        {
            remaining = FullBox(family);
            string major = remaining.First(id => MajorKey(id) == family);
            pulled.Add(major);
            remaining.Remove(major);
        }
        while (pulled.Count < CardsPerPack)
        {
            int index = Math.Clamp((int)(random() * remaining.Count), 0, remaining.Count - 1);
            pulled.Add(remaining[index]);
            remaining.RemoveAt(index);
        }
        if (remaining.Count == 0)
        {
            next.FamilyBoxes.Remove(family);
            next.CompletedFamilyBoxes.Add(family);
        }
        else next.FamilyBoxes[family] = remaining;
        next = WithCards(next, pulled.Where(id => !id.EndsWith("@alt", StringComparison.Ordinal)));
        foreach (var id in pulled.Where(id => id.EndsWith("@alt", StringComparison.Ordinal)))
        {
            string original = id[..^4];
            if (next.Alts.GetValueOrDefault(original) == 0)
            {
                next.Vistas.Remove(id);
                next.AcquisitionOrder.Remove(id);
                next.AcquisitionOrder.Add(id);
            }
            next.Alts[original] = Math.Min(MaxCopies, next.Alts.GetValueOrDefault(original) + 1);
        }
        var majors = pulled.Where(IsMajor).ToList();
        foreach (var id in majors) UnlockFamily(next, id);
        return new Opened(next, pulled, NewOnes(profile, pulled), majors.Count > 0, majors.Count);
    }
}
