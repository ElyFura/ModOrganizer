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
                    SeverityLabel = ((HealthSeverity)i.Severity).ToString(),
                    KindLabel = ((HealthIssueKind)i.Kind).ToString()
                }).ToList();

            Grid.ItemsSource = issues;
            SummaryText.Text = $"{issues.Count} issue(s) across {count} newly computed";
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

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAndBindAsync();
}
