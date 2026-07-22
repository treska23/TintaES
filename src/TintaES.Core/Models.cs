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
    public int FontWeight { get; set; } = 700;
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

    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public string Original { get => _original; set => Set(ref _original, value); }
    public string Translation { get => _translation; set => Set(ref _translation, value); }
    public string DisplayText => string.IsNullOrWhiteSpace(Translation) ? Original : Translation;
    public string Type { get => _type; set => Set(ref _type, value); }
    public double Confidence { get; set; } = 0.75;
    public NormalizedRect TextBox { get; set; } = new(100, 100, 200, 80);
    public NormalizedRect RenderBox { get; set; } = new(90, 85, 220, 110);
    public IReadOnlyList<NormalizedPoint> SafePolygon { get; set; } = [];
    public double Rotation { get; set; }
    public bool Vertical { get; set; }
    public ComicTextStyle Style { get; set; } = new();
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public string CleanupMode { get => _cleanupMode; set => Set(ref _cleanupMode, value); }
    public double FontScale { get => _fontScale; set => Set(ref _fontScale, value); }
    public bool IsManual { get; set; }

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
