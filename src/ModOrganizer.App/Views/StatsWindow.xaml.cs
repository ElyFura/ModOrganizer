using System.Windows;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Views;

public partial class StatsWindow : Window
{
    public StatsWindow(StatsService stats, long? rootId)
    {
        InitializeComponent();
        SubtitleText.Text = "Berechne…";
        Loaded += (_, _) => _ = LoadAsync(stats, rootId);
    }

    private async Task LoadAsync(StatsService stats, long? rootId)
    {
        LibraryStats data;
        try
        {
            // Nine aggregate queries; over a remote database that is most of a second.
            data = await Task.Run(() => stats.Compute(rootId));
        }
        catch (Exception ex)
        {
            SubtitleText.Text = "Statistik fehlgeschlagen: " + ex.Message;
            return;
        }

        StatMods.Text = data.TotalMods.ToString("N0");
        StatFiles.Text = data.TotalFiles.ToString("N0");
        StatSize.Text = FormatSize(data.TotalSizeBytes);
        var clamped = Math.Clamp(data.AvgRating, 0, 5);
        StatRating.Text = clamped > 0 ? new string('★', clamped) + new string('☆', 5 - clamped) : "—";
        SubtitleText.Text = rootId is null ? "All roots" : $"Root #{rootId}";

        var maxCatCount = data.ByCategory.Count > 0 ? data.ByCategory.Max(c => c.Count) : 1;
        CategoriesList.ItemsSource = data.ByCategory.Select(c => new
        {
            c.Category,
            Name = c.Category,
            CountText = c.Count.ToString("N0"),
            SizeText = FormatSize(c.Size),
            BarWidth = (double)c.Count / maxCatCount * 360
        }).ToList();

        TopSizeList.ItemsSource = data.TopBySize.Select(m => new
        {
            m.FolderName,
            m.CategoryName,
            SizeText = FormatSize(m.Size)
        }).ToList();

        var maxMonth = data.AddedByMonth.Count > 0 ? data.AddedByMonth.Max(x => x.Count) : 1;
        MonthsList.ItemsSource = data.AddedByMonth.Select(m => new
        {
            m.Month,
            m.Count,
            BarWidth = (double)m.Count / maxMonth * 220
        }).ToList();

        TagsList.ItemsSource = data.TopTags;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F1} {units[unit]}";
    }
}
