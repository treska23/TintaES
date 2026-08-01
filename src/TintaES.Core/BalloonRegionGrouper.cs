namespace TintaES.Core;

/// <summary>
/// Convierte fragmentos OCR en unidades de lectura. La unidad real es el bocadillo:
/// color, peso o clasificación visual de una palabra no pueden dividir una frase que
/// comparte el mismo contenedor.
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
            if (!IsMergeableBalloonPart(member)
                || (!IsBalloonText(member) && !IsBalloonText(candidate)))
            {
                continue;
            }

            bool memberIsBalloon = IsBalloonText(member);
            bool candidateIsBalloon = IsBalloonText(candidate);
            bool mixedClassifierPair = memberIsBalloon != candidateIsBalloon;

            bool memberHasContainer = TryGetContainer(member, out NormalizedRect memberContainer);
            bool candidateHasContainer = TryGetContainer(candidate, out NormalizedRect candidateContainer);
            if (!memberHasContainer && !candidateHasContainer)
            {
                continue;
            }

            NormalizedRect referenceContainer;
            if (memberIsBalloon && memberHasContainer)
            {
                referenceContainer = memberContainer;
            }
            else if (candidateIsBalloon && candidateHasContainer)
            {
                referenceContainer = candidateContainer;
            }
            else if (memberHasContainer)
            {
                referenceContainer = memberContainer;
            }
            else
            {
                referenceContainer = candidateContainer;
            }

            NormalizedPoint memberCentre = Centre(member.TextBox);
            NormalizedPoint candidateCentre = Centre(candidate.TextBox);
            bool referenceContainsBoth = Contains(referenceContainer, memberCentre)
                && Contains(referenceContainer, candidateCentre);

            double sharedContainer = memberHasContainer && candidateHasContainer
                ? OverlapOverSmaller(memberContainer, candidateContainer)
                : 0;
            bool mutuallyContained = memberHasContainer
                && candidateHasContainer
                && Contains(memberContainer, candidateCentre)
                && Contains(candidateContainer, memberCentre);

            if (!referenceContainsBoth && sharedContainer < 0.62 && !mutuallyContained)
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
            double minimumHeight = Math.Max(
                1,
                Math.Min(member.TextBox.Height, candidate.TextBox.Height));
            double maximumVerticalGap = Math.Max(
                28,
                referenceContainer.Height * 0.30);
            double maximumHorizontalGap = Math.Max(
                34,
                referenceContainer.Width * 0.28);
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
                / minimumHeight;
            double horizontalCentreDistance = Math.Abs(
                memberCentre.X - candidateCentre.X);
            double verticalCentreDistance = Math.Abs(
                memberCentre.Y - candidateCentre.Y);

            // Líneas apiladas del mismo bocadillo: pueden tener anchuras muy distintas,
            // pero deben ocupar alturas diferentes dentro de un único contenedor.
            bool verticallyStacked = verticalCentreDistance >= minimumHeight * 0.45
                && verticalGap <= maximumVerticalGap
                && (horizontalOverlap >= 0.12
                    || horizontalCentreDistance <= referenceContainer.Width * 0.34);

            // Fragmentos normales de una misma línea. El límite estrecho evita fusionar
            // dos bocadillos paralelos cuyas cajas de detección se solapan.
            bool sameLineBalloonFragments = memberIsBalloon
                && candidateIsBalloon
                && verticalOverlap >= 0.55
                && horizontalGap <= Math.Max(10, minimumHeight * 0.35);

            // Una palabra enfatizada por color —por ejemplo TAKES!— puede ser clasificada
            // como SFX. Dentro del bocadillo se admite un hueco lateral mayor, pero solo
            // cuando el fragmento sigue siendo pequeño respecto al contenedor.
            bool sameLineEmphasis = mixedClassifierPair
                && verticalOverlap >= 0.30
                && horizontalGap <= Math.Max(24, minimumHeight * 1.20);

            if ((!verticallyStacked && !sameLineBalloonFragments && !sameLineEmphasis)
                || horizontalGap > maximumHorizontalGap)
            {
                continue;
            }

            if (mixedClassifierPair)
            {
                ComicRegion emphasis = memberIsBalloon ? candidate : member;
                ComicRegion balloon = memberIsBalloon ? member : candidate;
                if (!TryGetContainer(balloon, out NormalizedRect balloonContainer))
                {
                    continue;
                }

                bool embeddedInBalloon = Contains(balloonContainer, Centre(emphasis.TextBox))
                    && emphasis.TextBox.Area <= balloonContainer.Area * 0.24
                    && emphasis.TextBox.Width <= balloonContainer.Width * 0.62
                    && emphasis.TextBox.Height <= balloonContainer.Height * 0.52;
                if (!embeddedInBalloon && !HasSharedOcrEvidence(member, candidate))
                {
                    continue;
                }
            }

            return true;
        }

        return false;
    }

    private static double SharedContainerScore(
        IReadOnlyList<ComicRegion> group,
        ComicRegion candidate) =>
        group
            .Select(member =>
            {
                bool memberHas = TryGetContainer(member, out NormalizedRect first);
                bool candidateHas = TryGetContainer(candidate, out NormalizedRect second);
                if (memberHas && candidateHas)
                {
                    return OverlapOverSmaller(first, second);
                }

                if (memberHas && Contains(first, Centre(candidate.TextBox)))
                {
                    return 0.61;
                }

                if (candidateHas && Contains(second, Centre(member.TextBox)))
                {
                    return 0.61;
                }

                return 0;
            })
            .DefaultIfEmpty(0)
            .Max();

    private static bool IsBalloonText(ComicRegion region) =>
        region.Type is "dialogue" or "thought" or "caption";

    private static bool IsMergeableBalloonPart(ComicRegion region) =>
        IsBalloonText(region) || IsInlineEmphasisFragment(region);

    private static bool IsInlineEmphasisFragment(ComicRegion region)
    {
        if (IsBalloonText(region)
            || region.Type is not ("sfx" or "text" or "sign"))
        {
            return false;
        }

        string compact = string.Concat(region.Original.Where(char.IsLetterOrDigit));
        int wordCount = region.Original.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries).Length;
        return compact.Length is >= 2 and <= 28
            && wordCount <= 3;
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
        bool mergedClassifierFragment = ordered.Any(IsInlineEmphasisFragment)
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
            target.BubbleBox = SelectBestContainer(containers, target.TextBox);
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

    private static NormalizedRect SelectBestContainer(
        IReadOnlyList<NormalizedRect> containers,
        NormalizedRect mergedText)
    {
        NormalizedPoint centre = Centre(mergedText);
        NormalizedRect? best = containers
            .Select(container => container.Clamp())
            .Where(container => Contains(container, centre))
            .Where(container =>
                mergedText.X >= container.X - 4
                && mergedText.Y >= container.Y - 4
                && mergedText.Right <= container.Right + 4
                && mergedText.Bottom <= container.Bottom + 4)
            .OrderBy(container => container.Area)
            .FirstOrDefault();

        return best ?? Union(containers);
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
