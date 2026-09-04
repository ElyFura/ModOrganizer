using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Views;

public partial class ActivityFeedWindow : Window
{
    private readonly ActivityFeedService _feed;

    public ActivityFeedWindow(ActivityFeedService feed)
    {
        InitializeComponent();
        _feed = feed;
        Refresh();
    }

    private void Refresh()
    {
        var entries = _feed.GetRecent(200);
        SubtitleText.Text = $"{entries.Count} aktuelle Aktionen";
        ItemsList.ItemsSource = entries.Select(Wrap).ToList();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Refresh();

    private static dynamic Wrap(ActivityEntry e)
    {
        var name = e.UserDisplayName ?? "?";
        var initials = Initials(name);
        var color = !string.IsNullOrEmpty(e.UserColor)
            ? e.UserColor!
            : ColorFromName(name);
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!;
        brush.Freeze();

        return new
        {
            UserDisplayName = name,
            Initials = initials,
            AvatarBrush = brush,
            e.Summary,
            ModFolderName = e.ModFolderName ?? "",
            TimeAgo = HumanTime(e.When),
        };
    }

    private static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '@', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpper(CultureInfo.InvariantCulture);
        return ($"{parts[0][0]}{parts[1][0]}").ToUpper(CultureInfo.InvariantCulture);
    }

    private static readonly string[] Palette =
    {
        "#7A5CFA", "#FF8A65", "#26A69A", "#FFB300", "#5C6BC0",
        "#EC407A", "#26C6DA", "#9CCC65", "#AB47BC", "#42A5F5"
    };

    private static string ColorFromName(string name)
    {
        unchecked
        {
            int hash = 0;
            foreach (var c in name) hash = hash * 31 + c;
            return Palette[Math.Abs(hash) % Palette.Length];
        }
    }

    private static string HumanTime(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue) return "";
        var now = DateTimeOffset.Now;
        var diff = now - when;
        if (diff.TotalSeconds < 60) return "gerade";
        if (diff.TotalMinutes < 60) return $"vor {(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"vor {(int)diff.TotalHours} h";
        if (diff.TotalDays < 7) return $"vor {(int)diff.TotalDays} d";
        return when.ToString("yyyy-MM-dd HH:mm");
    }
}
