using System.Windows;
using ModOrganizer.Core.Health;
using ModOrganizer.Core.Models;

namespace ModOrganizer.App.Views;

public partial class HealthWindow : Window
{
    private readonly HealthChecker _checker;
    private readonly long? _rootId;

    public HealthWindow(HealthChecker checker, long? rootId)
    {
        InitializeComponent();
        _checker = checker;
        _rootId = rootId;
        Loaded += (_, _) => _ = RunAndBindAsync();
    }

    private bool _running;

    private async Task RunAndBindAsync()
    {
        if (_running) return;
        _running = true;
        SummaryText.Text = "Prüfe…";

        try
        {
            var count = await _checker.RunAsync(_rootId);
            var rows = await _checker.GetIssuesAsync(_rootId);

            var issues = rows
                .Select(i => new
                {
                    i.FolderName, i.CategoryName, i.Detail,
                    SeverityLabel = SeverityLabelFor((HealthSeverity)i.Severity),
                    KindLabel = KindLabelFor((HealthIssueKind)i.Kind)
                }).ToList();

            Grid.ItemsSource = issues;
            var broken = rows.Count(i => i.Kind == (int)HealthIssueKind.BrokenArchive);
            SummaryText.Text = $"{issues.Count} Befund(e)" +
                               (broken > 0 ? $" - davon {broken} mit nicht lesbaren Archiven" : "");
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Health-Check fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            _running = false;
        }
    }

    private static string SeverityLabelFor(HealthSeverity s) => s switch
    {
        HealthSeverity.Error => "Fehler",
        HealthSeverity.Warn  => "Warnung",
        _                    => "Hinweis"
    };

    private static string KindLabelFor(HealthIssueKind k) => k switch
    {
        HealthIssueKind.NoImage       => "Kein Bild",
        HealthIssueKind.MultiImage    => "Mehrere Bilder",
        HealthIssueKind.BrokenImage   => "Bild defekt",
        HealthIssueKind.OrphanImage   => "Bild ohne Mod",
        HealthIssueKind.BrokenArchive => "Archiv defekt",
        _                             => k.ToString()
    };

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAndBindAsync();
}
