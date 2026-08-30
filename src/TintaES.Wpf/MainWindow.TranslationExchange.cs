using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TintaES.Core;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private void ExportTranslationScriptMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0 || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        var dialog = new SaveFileDialog
        {
            Title = "Exportar guion de traducción para revisión",
            FileName = MakeSafeFileName(_comicTitle ?? "comic") + ".tinta-traduccion.txt",
            DefaultExt = ".txt",
            Filter = "Guion de traducción TintaES (*.txt)|*.txt|JSON (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            TranslationExchangeDocument document = BuildTranslationExchangeDocument();
            string text = TranslationExchange.Serialize(document);
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetFooterStatus(
                $"Guion de traducción exportado · {Path.GetFileName(dialog.FileName)}",
                "#58A77D");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(
                this,
                $"No se pudo exportar el guion de traducción.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("No se pudo exportar el guion de traducción.", "#EE594B");
        }
    }

    private void ImportTranslationScriptMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0 || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Importar guion de traducción revisado",
            Filter = "Guion de traducción TintaES (*.txt;*.json)|*.txt;*.json|Todos los archivos (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            PersistVisibleComicPageRegions();
            string text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            IReadOnlyDictionary<Guid, string> imported = TranslationExchange.ReadTranslations(text);
            TranslationImportSummary summary = ApplyImportedTranslations(imported);

            if (summary.Matched == 0)
            {
                MessageBox.Show(
                    this,
                    "El archivo no contiene ningún regionId de este cómic. No se ha cambiado nada.",
                    "Guion de otro proyecto",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (summary.ChangedPages.Contains(_comicPageIndex))
            {
                RegionListBox.Items.Refresh();
                foreach (ComicRegion region in _regions)
                {
                    region.NotifyVisualChange();
                }
                ShowRegionEditor(_selectedRegion);
                RebuildOverlay();
            }

            SynchronizeActiveDocumentState();
            UpdateClassicMenuAvailability();
            UpdateProjectCommandAvailability();

            string skipped = summary.InvalidOrEmpty > 0
                ? $" · {summary.InvalidOrEmpty} omitidas por estar vacías o no superar la validación"
                : string.Empty;
            MessageBox.Show(
                this,
                $"Traducciones actualizadas: {summary.Changed}.\n" +
                $"Zonas reconocidas: {summary.Matched} de {imported.Count}{skipped}.",
                "Guion de traducción importado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SetFooterStatus(
                $"Guion importado · {summary.Changed} traducciones actualizadas",
                "#58A77D");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
        {
            MessageBox.Show(
                this,
                $"No se pudo importar el guion de traducción.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("No se pudo importar el guion de traducción.", "#EE594B");
        }
    }

    private TranslationExchangeDocument BuildTranslationExchangeDocument()
    {
        var document = new TranslationExchangeDocument
        {
            ComicTitle = _comicTitle ?? "comic",
            PageCount = _comicPages.Count
        };

        for (int pageIndex = 0; pageIndex < _comicPages.Count; pageIndex++)
        {
            ComicBookPageState page = _comicPages[pageIndex];
            IReadOnlyDictionary<Guid, string?> bubbleIds = BuildExchangeBubbleIds(page.Regions);
            var exportedPage = new TranslationExchangePage
            {
                Page = pageIndex + 1,
                Name = page.DisplayName,
                SourceLanguage = page.SourceLanguage
            };

            foreach (ComicRegion region in page.Regions.OrderBy(region => region.Order))
            {
                exportedPage.Regions.Add(new TranslationExchangeRegion
                {
                    RegionId = region.Id,
                    Order = region.Order,
                    Type = region.Type,
                    Enabled = region.IsEnabled,
                    Original = region.Original,
                    Translation = region.Translation,
                    BubbleId = bubbleIds.GetValueOrDefault(region.Id),
                    TextBox = ToExchangeRect(region.TextBox),
                    BubbleBox = region.BubbleBox is { } bubble ? ToExchangeRect(bubble) : null,
                    RenderBox = ToExchangeRect(region.RenderBox),
                    Rotation = region.Rotation,
                    Vertical = region.Vertical,
                    Style = new TranslationExchangeStyle
                    {
                        FontCategory = region.Style.FontCategory,
                        OriginalLineCount = region.Style.OriginalLineCount,
                        Uppercase = region.Style.Uppercase,
                        Italic = region.Style.Italic,
                        Alignment = region.Style.Alignment
                    }
                });
            }

            document.Pages.Add(exportedPage);
        }

        return document;
    }

    private TranslationImportSummary ApplyImportedTranslations(
        IReadOnlyDictionary<Guid, string> imported)
    {
        var current = new Dictionary<Guid, (int PageIndex, ComicRegion Region)>();
        for (int pageIndex = 0; pageIndex < _comicPages.Count; pageIndex++)
        {
            foreach (ComicRegion region in _comicPages[pageIndex].Regions)
            {
                current[region.Id] = (pageIndex, region);
            }
        }

        int matched = 0;
        int changed = 0;
        int invalidOrEmpty = 0;
        var changedPages = new HashSet<int>();

        foreach ((Guid id, string incoming) in imported)
        {
            if (!current.TryGetValue(id, out (int PageIndex, ComicRegion Region) target))
            {
                continue;
            }

            matched++;
            string candidate = (incoming ?? string.Empty).Trim();
            if (candidate.Length == 0)
            {
                invalidOrEmpty++;
                continue;
            }

            ComicRegion region = target.Region;
            string previous = region.Translation;
            if (string.Equals(previous.Trim(), candidate, StringComparison.Ordinal))
            {
                continue;
            }

            region.Translation = candidate;
            if (!region.HasRenderableTranslation)
            {
                region.Translation = previous;
                invalidOrEmpty++;
                continue;
            }

            changed++;
            changedPages.Add(target.PageIndex);
            region.NotifyVisualChange();
        }

        foreach (int pageIndex in changedPages)
        {
            ComicBookPageState page = _comicPages[pageIndex];
            if (page.Regions
                    .Where(region => region.IsEnabled)
                    .All(region => region.HasRenderableTranslation)
                && !string.IsNullOrWhiteSpace(page.CleanedPath)
                && File.Exists(page.CleanedPath)
                && page.Error?.Contains("tradu", StringComparison.OrdinalIgnoreCase) == true)
            {
                page.Error = null;
                page.Processed = true;
            }

            MarkActiveDocumentDirty(pageIndex);
        }

        return new TranslationImportSummary(matched, changed, invalidOrEmpty, changedPages);
    }

    private static IReadOnlyDictionary<Guid, string?> BuildExchangeBubbleIds(
        IReadOnlyList<ComicRegion> regions)
    {
        var knownBubbles = new List<NormalizedRect>();
        var result = new Dictionary<Guid, string?>();
        foreach (ComicRegion region in regions.OrderBy(region => region.Order))
        {
            if (region.BubbleBox is not { } bubble)
            {
                result[region.Id] = null;
                continue;
            }

            int existingIndex = knownBubbles.FindIndex(existing => SameExchangeBubble(existing, bubble));
            if (existingIndex < 0)
            {
                knownBubbles.Add(bubble);
                existingIndex = knownBubbles.Count - 1;
            }

            result[region.Id] = $"B{existingIndex + 1:D2}";
        }

        return result;
    }

    private static bool SameExchangeBubble(NormalizedRect first, NormalizedRect second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double overlapOverSmaller = intersection / Math.Max(1, Math.Min(first.Area, second.Area));

        double firstCenterX = first.X + first.Width / 2;
        double firstCenterY = first.Y + first.Height / 2;
        double secondCenterX = second.X + second.Width / 2;
        double secondCenterY = second.Y + second.Height / 2;
        return overlapOverSmaller >= 0.70
            && Math.Abs(firstCenterX - secondCenterX) <= Math.Max(12, Math.Min(first.Width, second.Width) * 0.22)
            && Math.Abs(firstCenterY - secondCenterY) <= Math.Max(12, Math.Min(first.Height, second.Height) * 0.22);
    }

    private static TranslationExchangeRect ToExchangeRect(NormalizedRect rect) => new()
    {
        X = Math.Round(rect.X, 2),
        Y = Math.Round(rect.Y, 2),
        Width = Math.Round(rect.Width, 2),
        Height = Math.Round(rect.Height, 2)
    };

    private sealed record TranslationImportSummary(
        int Matched,
        int Changed,
        int InvalidOrEmpty,
        HashSet<int> ChangedPages);
}
