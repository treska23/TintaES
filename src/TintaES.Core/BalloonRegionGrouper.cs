namespace TintaES.Core;

/// <summary>
/// Convierte fragmentos OCR en unidades de lectura. Dos bloques solo se reúnen cuando
/// comparten un contenedor geométrico plausible y además son líneas próximas. De este modo
/// el traductor y el lector trabajan con un bocadillo completo, no con cada línea aislada.
/// </summary>
public static class BalloonRegionGrouper
{
    public static IReadOnlyList<ComicRegion> Group(IEnumerable<ComicRegion> source)
    {
        ComicRegion[] ordered = source
            .Where(region => !string.IsNullOrWhiteSpace(region.Original))
            .OrderBy(region => region.TextBox.Y)
            .ThenBy(region => region.TextBox.X)
            .ToArray();
        var groups = new List<List<ComicRegion>>();

        foreach (ComicRegion region in ordered)
        {
            List<ComicRegion>? target = groups
                .Where(group => CanJoin(group, region))
                .OrderByDescending(group => SharedContainerScore(group, region))
                .FirstOrDefault();
            if (target is null)
            {
                groups.Add([region]);
            }
            else
            {
                target.Add(region);
            }
        }

        var result = groups
            .Select(MergeGroup)
            .OrderBy(region => region.TextBox.Y)
            .ThenBy(region => region.TextBox.X)
            .ToList();
        for (int index = 0; index < result.Count; index++)
        {
            result[index].Order = index + 1;
        }
        return result;
    }

    private static bool CanJoin(IReadOnlyList<ComicRegion> group, ComicRegion candidate)
    {
        if (!IsMergeableBalloonPart(candidate))
        {
            return false;
        }

        foreach (ComicRegion member in group)
        {
            bool crossTypeHeader = IsShortSfxHeader(member) != IsShortSfxHeader(candidate);
            if (!IsMergeableBalloonPart(member)
                || (!IsBalloonText(member) && !IsBalloonText(candidate))
                || (crossTypeHeader && !HasSharedOcrEvidence(member, candidate))
                || !TryGetContainer(member, out NormalizedRect firstContainer)
                || !TryGetContainer(candidate, out NormalizedRect secondContainer))
            {
                continue;
            }

            double shared = OverlapOverSmaller(firstContainer, secondContainer);
            bool mutuallyContained = Contains(firstContainer, Centre(candidate.TextBox))
                && Contains(secondContainer, Centre(member.TextBox));
            if (shared < 0.62 && !mutuallyContained)
            {
                continue;
            }

            double verticalGap = AxisGap(
                member.TextBox.Y,
                member.TextBox.Bottom,
                candidate.TextBox.Y,
                candidate.TextBox.Bottom);
            double horizontalGap = AxisGap(
                member.TextBox.X,
                member.TextBox.Right,
                candidate.TextBox.X,
                candidate.TextBox.Right);
            double maximumVerticalGap = Math.Max(
                28,
                Math.Min(member.TextBox.Height, candidate.TextBox.Height) * 1.8);
            double maximumHorizontalGap = Math.Max(
                34,
                Math.Min(firstContainer.Width, secondContainer.Width) * 0.24);
            double horizontalOverlap = AxisOverlap(
                member.TextBox.X,
                member.TextBox.Right,
                candidate.TextBox.X,
                candidate.TextBox.Right)
                / Math.Max(1, Math.Min(member.TextBox.Width, candidate.TextBox.Width));
            double verticalOverlap = AxisOverlap(
                member.TextBox.Y,
                member.TextBox.Bottom,
                candidate.TextBox.Y,
                candidate.TextBox.Bottom)
                / Math.Max(1, Math.Min(member.TextBox.Height, candidate.TextBox.Height));

            // Las líneas de un mismo bocadillo se apilan y conservan solape horizontal.
            // Dos globos contiguos suelen quedar a la misma altura: aunque el detector les
            // asigne contenedores solapados, el hueco lateral y su borde deben separarlos.
            bool verticallyStacked = horizontalOverlap >= 0.18
                && verticalGap <= maximumVerticalGap;
            bool sameLineFragment = verticalOverlap >= 0.55
                && horizontalGap <= Math.Max(
                    10,
                    Math.Min(member.TextBox.Height, candidate.TextBox.Height) * 0.35);
            if ((verticallyStacked || sameLineFragment)
                && horizontalGap <= maximumHorizontalGap)
            {
                return true;
            }
        }
        return false;
    }

    private static double SharedContainerScore(
        IReadOnlyList<ComicRegion> group,
        ComicRegion candidate) =>
        group
            .Select(member =>
                TryGetContainer(member, out NormalizedRect first)
                && TryGetContainer(candidate, out NormalizedRect second)
                    ? OverlapOverSmaller(first, second)
                    : 0)
            .DefaultIfEmpty(0)
            .Max();

    private static bool IsBalloonText(ComicRegion region) =>
        region.Type is "dialogue" or "thought" or "caption";

    private static bool IsMergeableBalloonPart(ComicRegion region) =>
        IsBalloonText(region) || IsShortSfxHeader(region);

    private static bool IsShortSfxHeader(ComicRegion region)
    {
        if (!string.Equals(region.Type, "sfx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string compact = string.Concat(region.Original.Where(char.IsLetterOrDigit));
        return compact.Length is >= 2 and <= 14
            && region.Original.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }

    private static bool HasSharedOcrEvidence(ComicRegion first, ComicRegion second)
    {
        HashSet<string> firstEvidence = first.OcrAlternatives
            .Select(NormalizeEvidence)
            .Where(value => value.Length >= 12)
            .ToHashSet(StringComparer.Ordinal);
        return second.OcrAlternatives
            .Select(NormalizeEvidence)
            .Any(value => value.Length >= 12 && firstEvidence.Contains(value));
    }

    private static string NormalizeEvidence(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static bool TryGetContainer(ComicRegion region, out NormalizedRect container)
    {
        if (region.BubbleBox is { } bubble && IsPlausibleContainer(bubble, region.TextBox))
        {
            container = bubble.Clamp();
            return true;
        }

        if (region.SafePolygon.Count >= 3)
        {
            double left = region.SafePolygon.Min(point => point.X);
            double top = region.SafePolygon.Min(point => point.Y);
            double right = region.SafePolygon.Max(point => point.X);
            double bottom = region.SafePolygon.Max(point => point.Y);
            var bounds = new NormalizedRect(left, top, right - left, bottom - top).Clamp();
            if (IsPlausibleContainer(bounds, region.TextBox))
            {
                container = bounds;
                return true;
            }
        }

        container = region.TextBox;
        return false;
    }

    private static bool IsPlausibleContainer(NormalizedRect container, NormalizedRect text)
    {
        container = container.Clamp();
        NormalizedPoint centre = Centre(text);
        double areaRatio = container.Area / Math.Max(1, text.Area);
        return Contains(container, centre)
            && container.Area <= 150_000
            && areaRatio is >= 1.04 and <= 80
            && container.Width <= 520
            && container.Height <= 520;
    }

    private static ComicRegion MergeGroup(List<ComicRegion> fragments)
    {
        if (fragments.Count == 1)
        {
            return fragments[0];
        }

        ComicRegion[] ordered = fragments
            .OrderBy(region => region.TextBox.Y)
            .ThenBy(region => region.TextBox.X)
            .ToArray();
        ComicRegion target = ordered[0];
        bool mergedClassifierFragment = ordered.Any(IsShortSfxHeader)
            && ordered.Any(IsBalloonText);
        target.Type = ordered.FirstOrDefault(IsBalloonText)?.Type ?? target.Type;
        target.Original = JoinText(ordered.Select(region => region.Original));
        target.Translation = !mergedClassifierFragment
            && ordered.All(region => region.HasRenderableTranslation)
            ? JoinText(ordered.Select(region => region.Translation))
            : string.Empty;
        target.OcrAlternatives = ordered
            .SelectMany(region => region.OcrAlternatives ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        target.TextBox = Union(ordered.Select(region => region.TextBox));
        NormalizedRect[] containers = ordered
            .Select(region => region.BubbleBox)
            .Where(box => box is not null)
            .Select(box => box!)
            .ToArray();
        if (containers.Length > 0)
        {
            target.BubbleBox = Union(containers);
        }
        target.RenderBox = target.BubbleBox ?? target.TextBox.Expand(0.3, 0.45);
        target.SafePolygon = [];
        target.CleanupPolygon = [];
        target.CleanupMode = "none";
        target.Confidence = ordered.Min(region => region.Confidence);
        target.BubbleConfidence = ordered.Max(region => region.BubbleConfidence);
        target.IsEnabled = ordered.Any(region => region.IsEnabled);
        target.Style.OriginalLineCount = Math.Max(
            ordered.Length,
            ordered.Sum(region => Math.Max(0, region.Style.OriginalLineCount)));
        return target;
    }

    private static string JoinText(IEnumerable<string> values) =>
        string.Join(" ", values.Select(value => value.Trim()).Where(value => value.Length > 0));

    private static NormalizedRect Union(IEnumerable<NormalizedRect> rectangles)
    {
        NormalizedRect[] values = rectangles.ToArray();
        double left = values.Min(rectangle => rectangle.X);
        double top = values.Min(rectangle => rectangle.Y);
        double right = values.Max(rectangle => rectangle.Right);
        double bottom = values.Max(rectangle => rectangle.Bottom);
        return new NormalizedRect(left, top, right - left, bottom - top).Clamp();
    }

    private static NormalizedPoint Centre(NormalizedRect rectangle) =>
        new(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);

    private static bool Contains(NormalizedRect rectangle, NormalizedPoint point) =>
        point.X >= rectangle.X
        && point.X <= rectangle.Right
        && point.Y >= rectangle.Y
        && point.Y <= rectangle.Bottom;

    private static double AxisGap(double firstStart, double firstEnd, double secondStart, double secondEnd) =>
        Math.Max(0, Math.Max(firstStart, secondStart) - Math.Min(firstEnd, secondEnd));

    private static double AxisOverlap(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd) =>
        Math.Max(0, Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));

    private static double OverlapOverSmaller(NormalizedRect first, NormalizedRect second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        return intersection / Math.Max(1, Math.Min(first.Area, second.Area));
    }
}
