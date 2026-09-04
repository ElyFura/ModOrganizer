using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.App.ViewModels;

/// <summary>How a single tag participates in the gallery filter.</summary>
public enum TagFilterState
{
    /// <summary>Ignored.</summary>
    Off = 0,
    /// <summary>Mod must carry it.</summary>
    Include = 1,
    /// <summary>Mod must not carry it — the "-tag" negation from the plan.</summary>
    Exclude = 2
}

/// <summary>Combine mode for the included tags.</summary>
public enum TagFilterMode
{
    /// <summary>Mod must carry all included tags.</summary>
    And = 0,
    /// <summary>Mod must carry at least one.</summary>
    Or = 1
}

/// <summary>
/// One chip in the sidebar's tag filter. Clicking cycles Off → Include → Exclude → Off,
/// which covers the plan's include/exclude requirement without needing the user to type
/// "-tagname" into a parser.
/// </summary>
public sealed partial class TagFilterViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    public int UsageCount { get; }
    public string? ColorHex { get; }

    [ObservableProperty] private TagFilterState _state;

    public TagFilterViewModel(TagInfo tag)
    {
        Id = tag.Id;
        Name = tag.Name;
        UsageCount = tag.UsageCount;
        ColorHex = tag.ColorHex;
    }

    public event EventHandler? StateCycled;

    public void Cycle()
    {
        State = State switch
        {
            TagFilterState.Off => TagFilterState.Include,
            TagFilterState.Include => TagFilterState.Exclude,
            _ => TagFilterState.Off
        };
        StateCycled?.Invoke(this, EventArgs.Empty);
    }

    partial void OnStateChanged(TagFilterState value)
    {
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(Prefix));
        OnPropertyChanged(nameof(IsActive));
    }

    public bool IsActive => State != TagFilterState.Off;

    /// <summary>"−" marks an excluded tag, mirroring the plan's -tagname syntax.</summary>
    public string Prefix => State == TagFilterState.Exclude ? "−" : "";

    public Brush Background => State switch
    {
        TagFilterState.Include => TagBrushes.Solid(ColorHex, Name),
        TagFilterState.Exclude => TagBrushes.Frozen("#5A2530"),
        _ => TagBrushes.Frozen("#22222C")
    };

    public Brush Foreground => State == TagFilterState.Off
        ? TagBrushes.Frozen("#A9A9BC")
        : Brushes.White;

    public Brush BorderBrush => State switch
    {
        TagFilterState.Include => TagBrushes.Solid(ColorHex, Name),
        TagFilterState.Exclude => TagBrushes.Frozen("#E5534B"),
        _ => TagBrushes.Frozen("#31313D")
    };
}

/// <summary>Shared brush helpers so every tag chip in the app resolves colours the same way.</summary>
public static class TagBrushes
{
    private static readonly Dictionary<string, Brush> Cache = new();
    private static readonly object Lock = new();

    /// <summary>The tag's own colour, or a stable colour derived from its name.</summary>
    public static Brush Solid(string? colorHex, string name) =>
        Frozen(string.IsNullOrWhiteSpace(colorHex) ? TagService.DefaultColorFor(name) : colorHex!);

    public static Brush Frozen(string hex)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            Brush brush;
            try
            {
                brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
                brush.Freeze();
            }
            catch
            {
                brush = Brushes.Gray;
            }

            Cache[hex] = brush;
            return brush;
        }
    }
}
