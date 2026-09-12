using System.Windows.Media;
using ModOrganizer.App.Services;

namespace ModOrganizer.App.ViewModels;

public sealed class PresenceChipViewModel
{
    public Guid UserId { get; }
    public string DisplayName { get; }
    public string Initials { get; }
    public Brush Brush { get; }
    public string Tooltip { get; }
    public long? ViewingModId { get; }

    private static readonly string[] Palette =
    {
        "#7A5CFA", "#FF8A65", "#26A69A", "#FFB300", "#5C6BC0",
        "#EC407A", "#26C6DA", "#9CCC65", "#AB47BC", "#42A5F5"
    };

    /// <summary>True for the entry representing the current user.</summary>
    public bool IsSelf { get; }

    public PresenceChipViewModel(PresenceState p, string? viewingModName = null, bool isSelf = false)
    {
        UserId = p.UserId;
        DisplayName = p.DisplayName;
        ViewingModId = p.ViewingModId;
        IsSelf = isSelf;
        Initials = MakeInitials(DisplayName);
        Brush = MakeBrush(DisplayName);

        var who = isSelf ? "du" : DisplayName;

        // The mod id alone told the user nothing; the name is resolved by the caller,
        // which is the only place that knows the loaded cards.
        Tooltip = p.ViewingModId.HasValue
            ? $"{who} · sieht gerade {viewingModName ?? $"Mod #{p.ViewingModId}"} an"
            : $"{who} · online";
    }

    private static string MakeInitials(string name)
    {
        var parts = name.Split(new[] { ' ', '@', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return ($"{parts[0][0]}{parts[1][0]}").ToUpperInvariant();
    }

    private static Brush MakeBrush(string name)
    {
        unchecked
        {
            int hash = 0;
            foreach (var c in name) hash = hash * 31 + c;
            var hex = Palette[Math.Abs(hash) % Palette.Length];
            try
            {
                var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
                b.Freeze();
                return b;
            }
            catch { return Brushes.Gray; }
        }
    }
}
