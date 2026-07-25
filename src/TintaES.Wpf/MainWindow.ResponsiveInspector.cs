using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Evita que el inspector derecho reconstruya la página completa por cada clic o carácter. Los
/// cambios tipográficos invalidan solo la zona seleccionada y la limpieza se calcula con debounce.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ResponsiveInspectorRegistered = RegisterResponsiveInspector();

    private bool _responsiveInspectorInstalled;
    private DispatcherTimer? _inspectorCleanupTimer;
    private int _inspectorCleanupVersion;

    private static bool RegisterResponsiveInspector()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ResponsiveInspectorLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ResponsiveInspectorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallResponsiveInspector,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallResponsiveInspector()
    {
        if (_responsiveInspectorInstalled)
        {
            return;
        }
        _responsiveInspectorInstalled = true;

        RegionVisibleCheckBox.Checked -= RegionVisualControl_Changed;
        RegionVisibleCheckBox.Unchecked -= RegionVisualControl_Changed;
        TypeComboBox.SelectionChanged -= RegionVisualControl_Changed;
        FontCategoryComboBox.SelectionChanged -= RegionVisualControl_Changed;
        BoldCheckBox.Checked -= RegionVisualControl_Changed;
        BoldCheckBox.Unchecked -= RegionVisualControl_Changed;
        ItalicCheckBox.Checked -= RegionVisualControl_Changed;
        ItalicCheckBox.Unchecked -= RegionVisualControl_Changed;
        UppercaseCheckBox.Checked -= RegionVisualControl_Changed;
        UppercaseCheckBox.Unchecked -= RegionVisualControl_Changed;
        TextColorTextBox.TextChanged -= RegionVisualControl_Changed;
        CleanupComboBox.SelectionChanged -= CleanupComboBox_SelectionChanged;
        BackgroundColorTextBox.TextChanged -= CleanupStyleTextBox_Changed;

        RegionVisibleCheckBox.Checked += InspectorVisualControl_Changed;
        RegionVisibleCheckBox.Unchecked += InspectorVisualControl_Changed;
        TypeComboBox.SelectionChanged += InspectorVisualControl_Changed;
        FontCategoryComboBox.SelectionChanged += InspectorVisualControl_Changed;
        BoldCheckBox.Checked += InspectorVisualControl_Changed;
        BoldCheckBox.Unchecked += InspectorVisualControl_Changed;
        ItalicCheckBox.Checked += InspectorVisualControl_Changed;
        ItalicCheckBox.Unchecked += InspectorVisualControl_Changed;
        UppercaseCheckBox.Checked += InspectorVisualControl_Changed;
        UppercaseCheckBox.Unchecked += InspectorVisualControl_Changed;
        CleanupComboBox.SelectionChanged += InspectorCleanupControl_Changed;

        TextColorTextBox.LostKeyboardFocus += (_, _) => CommitInspectorTextColor();
        TextColorTextBox.KeyDown += InspectorTextColor_KeyDown;
        BackgroundColorTextBox.LostKeyboardFocus += (_, _) => CommitInspectorBackgroundColor();
        BackgroundColorTextBox.KeyDown += InspectorBackgroundColor_KeyDown;

        _inspectorCleanupTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(280),
            DispatcherPriority.Background,
            InspectorCleanupTimer_Tick,
            Dispatcher)
        {
            IsEnabled = false
        };
    }

    private void InspectorVisualControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        region.IsEnabled = RegionVisibleCheckBox.IsChecked == true;
        region.Type = TypeComboBox.SelectedValue as string ?? region.Type;
        region.Style.FontCategory = FontCategoryComboBox.SelectedValue as string ?? region.Style.FontCategory;
        region.Style.FontWeight = BoldCheckBox.IsChecked == true ? 800 : 400;
        region.Style.Italic = ItalicCheckBox.IsChecked == true;
        region.Style.Uppercase = UppercaseCheckBox.IsChecked == true;
        region.NotifyVisualChange();
        InvalidateRegionVisual(region);
        SetSelectedRegionLayerVisibility(region);
        SelectedRegionTitle.Text = $"Zona {region.Order} · {TypeLabel(region.Type)}";
        PersistVisibleComicPageRegions();
    }

    private void SetSelectedRegionLayerVisibility(ComicRegion region)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (ReferenceEquals(layer.Tag, region))
            {
                layer.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                break;
            }
        }
    }

    private void CommitInspectorTextColor()
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.Style.TextColor = string.IsNullOrWhiteSpace(TextColorTextBox.Text)
            ? "#111111"
            : TextColorTextBox.Text.Trim();
        _selectedRegion.NotifyVisualChange();
        InvalidateRegionVisual(_selectedRegion);
        PersistVisibleComicPageRegions();
    }

    private void InspectorTextColor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        CommitInspectorTextColor();
        Keyboard.Focus(this);
        e.Handled = true;
    }

    private void InspectorCleanupControl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.CleanupMode = CleanupComboBox.SelectedValue as string ?? "auto";
        ScheduleInspectorCleanupPreview();
    }

    private void CommitInspectorBackgroundColor()
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.Style.BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColorTextBox.Text)
            ? null
            : BackgroundColorTextBox.Text.Trim();
        ScheduleInspectorCleanupPreview();
    }

    private void InspectorBackgroundColor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        CommitInspectorBackgroundColor();
        Keyboard.Focus(this);
        e.Handled = true;
    }

    private void ScheduleInspectorCleanupPreview()
    {
        PersistVisibleComicPageRegions();
        _inspectorCleanupVersion++;
        if (_inspectorCleanupTimer is null)
        {
            return;
        }
        _inspectorCleanupTimer.Stop();
        _inspectorCleanupTimer.Start();
        SetFooterStatus("Actualizando el fondo…", "#4CB2BB");
    }

    private async void InspectorCleanupTimer_Tick(object? sender, EventArgs e)
    {
        _inspectorCleanupTimer?.Stop();
        if (_selectedRegion is null
            || _originalBitmap is null
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        int version = _inspectorCleanupVersion;
        int pageIndex = _comicPageIndex;
        ComicRegion region = CloneEditorRegion(_selectedRegion);
        BitmapSource original = _originalBitmap;
        BitmapSource source = _cleanedBaseBitmap ?? original;

        try
        {
            BitmapSource result = await Task.Run(() =>
            {
                if (region.CleanupMode == "none")
                {
                    return RestoreOriginalArea(source, original, GetRegionMaskBounds(region));
                }
                return _processingService.CleanText(source, [region]);
            });

            if (version != _inspectorCleanupVersion || pageIndex != _comicPageIndex)
            {
                return;
            }

            _cleanedBaseBitmap = result;
            _cleanedBitmap = result;
            ComicBookPageState page = _comicPages[pageIndex];
            page.Processed = true;
            page.Error = null;
            page.Regions.Clear();
            page.Regions.AddRange(_regions);
            UpdateFastDeletionBitmapCache(pageIndex, page, original, result, _maskBitmap);
            if (_previewMode is "result" or "clean")
            {
                PageImage.Source = result;
            }
            SetFooterStatus("Fondo actualizado. Guarda la página cuando termines.", "#58A77D");
        }
        catch (Exception exception)
        {
            SetFooterStatus($"No se pudo actualizar el fondo: {exception.Message}", "#EE594B");
        }
    }
}
