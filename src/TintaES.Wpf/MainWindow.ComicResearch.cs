using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly ComicResearchService _comicResearchService = new();
    private readonly Dictionary<string, ComicResearchContext> _comicResearchContexts =
        new(StringComparer.OrdinalIgnoreCase);

    private Button? _comicResearchButton;
    private string? _sessionTavilyApiKey;
    private bool _comicResearchBusy;

    private void InstallComicResearch()
    {
        if (_comicResearchButton is not null)
        {
            return;
        }

        if (AnalyzeButton?.Parent is not StackPanel actionPanel)
        {
            Dispatcher.BeginInvoke(InstallComicResearch, DispatcherPriority.ApplicationIdle);
            return;
        }

        _comicResearchButton = new Button
        {
            Content = "Contexto web",
            ToolTip = "Investigar la obra y revisar la ficha que usará el traductor",
            Style = FindResource("ToolbarButton") as Style,
            Margin = new Thickness(0, 0, 7, 0),
            MinWidth = 108
        };
        _comicResearchButton.Click += async (_, _) =>
            await EnsureComicResearchContextAsync(forceInteractive: true);

        int index = actionPanel.Children.IndexOf(AnalyzeButton);
        actionPanel.Children.Insert(Math.Max(0, index), _comicResearchButton);
    }

    /// <summary>
    /// Prepara el contexto antes de cualquier traducción. Una cancelación del diálogo no bloquea
    /// la función principal: se continúa sin investigación, pero nunca se reutiliza la ficha de
    /// otro documento.
    /// </summary>
    private async Task<bool> EnsureComicResearchContextAsync(bool forceInteractive = false)
    {
        if (_comicResearchBusy)
        {
            return false;
        }

        string suggestedTitle = ResolveSuggestedResearchTitle();
        string suggestedIdentity = ComicResearchService.BuildIdentityKey(suggestedTitle);
        ComicResearchContext? existing = null;
        if (_comicResearchContexts.TryGetValue(suggestedIdentity, out ComicResearchContext? inMemory))
        {
            existing = inMemory;
        }
        else
        {
            existing = _comicResearchService.TryLoad(suggestedTitle);
            if (existing is not null)
            {
                _comicResearchContexts[existing.IdentityKey] = existing;
            }
        }

        if (existing is not null && !forceInteractive)
        {
            ActivateResearchContext(existing);
            SetFooterStatus($"Contexto web preparado · {existing.ComicTitle}", "#58A77D");
            return true;
        }

        var dialog = new ComicResearchSetupWindow(
            this,
            existing?.ComicTitle ?? suggestedTitle,
            _sessionTavilyApiKey ?? Environment.GetEnvironmentVariable("TAVILY_API_KEY"),
            existing);
        if (dialog.ShowDialog() != true)
        {
            if (existing is not null)
            {
                ActivateResearchContext(existing);
            }
            else
            {
                ComicResearchAmbient.CurrentPrompt = null;
                SetFooterStatus("Traducción sin contexto web.", "#C99A35");
            }
            return true;
        }

        string title = dialog.ComicTitle;
        string identity = ComicResearchService.BuildIdentityKey(title);
        ComicResearchContext? selected = existing;
        if (!dialog.ForceRefresh)
        {
            if (_comicResearchContexts.TryGetValue(identity, out ComicResearchContext? exact))
            {
                selected = exact;
            }
            else
            {
                selected = _comicResearchService.TryLoad(title);
            }
        }

        if (selected is null || dialog.ForceRefresh)
        {
            string apiKey = dialog.ApiKey.Length > 0
                ? dialog.ApiKey
                : Environment.GetEnvironmentVariable("TAVILY_API_KEY")?.Trim() ?? string.Empty;
            _sessionTavilyApiKey = apiKey;
            _comicResearchBusy = true;
            try
            {
                BusyOverlay.Visibility = Visibility.Visible;
                BusyTitleText.Text = "Investigando la obra antes de traducir…";
                BusyProgressBar.IsIndeterminate = true;
                FooterProgressBar.Visibility = Visibility.Visible;
                FooterProgressBar.IsIndeterminate = true;
                SetFooterStatus("Consultando argumento, personajes y terminología…", "#C99A35");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                selected = await _comicResearchService.ResearchAsync(
                    title,
                    apiKey,
                    CancellationToken.None);
                _comicResearchContexts[selected.IdentityKey] = selected;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or IOException
                    or JsonException
                    or InvalidOperationException
                    or ArgumentException
                    or TaskCanceledException)
            {
                ComicResearchAmbient.CurrentPrompt = null;
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    "No se pudo preparar el contexto web.\n\n" + exception.Message +
                    "\n\n¿Continuar la traducción sin investigación?",
                    "Contexto web",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Yes);
                return answer == MessageBoxResult.Yes;
            }
            finally
            {
                _comicResearchBusy = false;
                BusyOverlay.Visibility = Visibility.Collapsed;
                BusyProgressBar.IsIndeterminate = false;
                FooterProgressBar.Visibility = Visibility.Collapsed;
                FooterProgressBar.IsIndeterminate = false;
            }
        }

        if (selected is null)
        {
            ComicResearchAmbient.CurrentPrompt = null;
            return true;
        }

        ActivateResearchContext(selected);
        SetFooterStatus(
            $"Contexto web listo · {selected.Findings.Count} datos · {selected.Sources.Count} fuentes",
            "#58A77D");

        if (forceInteractive)
        {
            MessageBox.Show(
                this,
                selected.ToDisplayText(),
                "Contexto web de la obra",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        return true;
    }

    private void ActivateResearchContext(ComicResearchContext context)
    {
        _comicResearchContexts[context.IdentityKey] = context;
        ComicResearchAmbient.CurrentPrompt = context.ToTranslationPrompt();
    }

    private string ResolveSuggestedResearchTitle()
    {
        string candidate = !string.IsNullOrWhiteSpace(_comicTitle)
            && !string.Equals(_comicTitle, "Cargando…", StringComparison.OrdinalIgnoreCase)
                ? _comicTitle
                : !string.IsNullOrWhiteSpace(_currentProjectPath)
                    ? Path.GetFileNameWithoutExtension(_currentProjectPath)
                    : _comicPages.Count > 0
                        ? Path.GetFileNameWithoutExtension(_comicPages[0].DisplayName)
                        : !string.IsNullOrWhiteSpace(_sourcePath)
                            ? Path.GetFileNameWithoutExtension(_sourcePath)
                            : "";

        candidate = candidate
            .Replace('_', ' ')
            .Replace('.', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(candidate) ? "Cómic sin identificar" : candidate;
    }
}
