using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TintaES.Core;

public sealed record NormalizedRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;

    public NormalizedRect Clamp()
    {
        double x = Math.Clamp(X, 0, 995);
        double y = Math.Clamp(Y, 0, 995);
        return new NormalizedRect(
            x,
            y,
            Math.Clamp(Width, 5, 1000 - x),
            Math.Clamp(Height, 5, 1000 - y));
    }

    public NormalizedRect Expand(double factorX, double factorY)
    {
        double extraX = Width * factorX;
        double extraY = Height * factorY;
        return new NormalizedRect(
            X - extraX / 2,
            Y - extraY / 2,
            Width + extraX,
            Height + extraY).Clamp();
    }
}

public sealed record NormalizedPoint(double X, double Y);

public sealed class ComicTextStyle
{
    public string FontCategory { get; set; } = "comic";
    public string? FontFamily { get; set; }
    public int FontWeight { get; set; } = 700;
    public double FontSize { get; set; }
    public double FontWidthRatio { get; set; } = 1;
    public double LineHeightRatio { get; set; } = 1.08;
    public int OriginalLineCount { get; set; }
    public bool Italic { get; set; }
    public bool Uppercase { get; set; }
    public string TextColor { get; set; } = "#111111";
    public string? OutlineColor { get; set; }
    public double OutlineWidth { get; set; }
    public string Alignment { get; set; } = "center";
    public string? BackgroundColor { get; set; }
    public bool Shadow { get; set; }
}

public sealed class ComicRegion : INotifyPropertyChanged
{
    private string _original = string.Empty;
    private string _translation = string.Empty;
    private string _type = "dialogue";
    private bool _isEnabled = true;
    private string _cleanupMode = "auto";
    private double _fontScale = 1;
    private double _manualFontScale = 1;
    private double _textOffsetX;
    private double _textOffsetY;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Original { get => _original; set => Set(ref _original, value); }
    // Lecturas del mismo bloque obtenidas por otros OCR. No se muestran ni se exportan:
    // sirven para que el traductor pueda reconstruir letras dudosas sin mezclar bocadillos.
    public IReadOnlyList<string> OcrAlternatives { get; set; } = [];
    public string Translation { get => _translation; set => Set(ref _translation, value); }

    // El lienzo de resultado nunca debe volver a colocar el OCR inglés sobre un fondo ya
    // reconstruido. Mientras la traducción todavía no exista se muestra una caja vacía; al
    // terminar, OllamaClient garantiza una traducción o el aviso español "Traducción pendiente".
    // El original continúa disponible en el inspector mediante Original.
    public string DisplayText => Translation;
    public string Type { get => _type; set => Set(ref _type, value); }
    public double Confidence { get; set; } = 0.75;

    // Confianza independiente de que el bloque de texto esté realmente dentro de un
    // bocadillo. El motor orgánico ya calcula este dato y nos permite conservar una
    // exclamación corta dentro de un globo sin empezar a traducir onomatopeyas exteriores.
    public double BubbleConfidence { get; set; }

    public NormalizedRect TextBox { get; set; } = new(100, 100, 200, 80);
    public NormalizedRect RenderBox { get; set; } = new(90, 85, 220, 110);
    public IReadOnlyList<NormalizedPoint> SafePolygon { get; set; } = [];
    public double Rotation { get; set; }
    public bool Vertical { get; set; }
    public ComicTextStyle Style { get; set; } = new();
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public string CleanupMode { get => _cleanupMode; set => Set(ref _cleanupMode, value); }

    // FontScale se conserva por compatibilidad con análisis/cachés anteriores. El control
    // interactivo usa ManualFontScale para no volver a ejecutar el algoritmo de autoajuste
    // cada vez que el usuario mueve el slider.
    public double FontScale { get => _fontScale; set => Set(ref _fontScale, value); }
    public double ManualFontScale { get => _manualFontScale; set => Set(ref _manualFontScale, value); }

    // Desplazamiento manual del texto en coordenadas normalizadas de página. No mueve ni
    // redimensiona RenderBox/SafePolygon, por lo que arrastrar deja de recalcular la fuente.
    public double TextOffsetX { get => _textOffsetX; set => Set(ref _textOffsetX, value); }
    public double TextOffsetY { get => _textOffsetY; set => Set(ref _textOffsetY, value); }

    // Una región analizada empieza siempre en modo automático. Solo pasa a manual cuando el
    // usuario edita su composición. ManualLayoutSeedText conserva las líneas automáticas que
    // había justo antes de editar y ManualBaseFontSize congela el tamaño de partida para que
    // cambiar un Enter no reduzca ni amplíe la fuente por sorpresa.
    public bool IsManual { get; set; }
    public string? ManualLayoutSeedText { get; set; }
    public double ManualBaseFontSize { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyVisualChange()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Original) or nameof(Translation))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }
}

public sealed record ComicImageTile(
    int Index,
    int Total,
    int X,
    int Y,
    int Width,
    int Height,
    int PageWidth,
    int PageHeight,
    byte[] ImageBytes);

public sealed record AnalysisProgress(int Completed, int Total, string Message)
{
    public double Percentage => Total <= 0 ? 0 : Completed * 100d / Total;
}

public sealed record ComicAnalysis(string SourceLanguage, IReadOnlyList<ComicRegion> Regions);

public sealed record OllamaModel(string Name, long Size);
