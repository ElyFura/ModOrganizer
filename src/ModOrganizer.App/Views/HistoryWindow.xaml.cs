using System.Windows;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Models;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Views;

public partial class HistoryWindow : Window
{
    private readonly ActivityFeedService _feed;
    private readonly UndoService _undo;

    public HistoryWindow(ActivityFeedService feed, UndoService undo)
    {
        InitializeComponent();
        _feed = feed;
        _undo = undo;
        Refresh();
    }

    private void Refresh()
    {
        var entries = _feed.GetRecent(500);
        // Collapse multi-row transactions to a single history row (first/most-recent per tx_id).
        var rows = entries
            .GroupBy(e => string.IsNullOrEmpty(e.TxId) ? $"_id:{e.Id}" : e.TxId)
            .Select(g => new HistoryRow(g.First()))
            .ToList();
        Grid.ItemsSource = rows;
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not HistoryRow row) return;
        if (MessageBox.Show($"Aktion rückgängig machen?\n\n{row.KindText}: {row.ModName}",
                "Bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            var ok = _undo.Undo(row.TxId);
            if (!ok)
                MessageBox.Show("Konnte nicht rückgängig gemacht werden.", "Undo",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Undo fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed class HistoryRow
{
    public string TxId { get; }
    public string TimeText { get; }
    public string UserName { get; }
    public string KindText { get; }
    public string ModName { get; }
    public bool CanUndo { get; }

    public HistoryRow(ActivityEntry e)
    {
        TxId = e.TxId ?? "";
        TimeText = e.When == DateTimeOffset.MinValue ? "" : e.When.ToString("yyyy-MM-dd HH:mm");
        UserName = e.UserDisplayName ?? "?";
        KindText = e.Kind.ToString();
        ModName = e.ModFolderName ?? "";
        CanUndo = !string.IsNullOrEmpty(TxId) && UndoService.CanUndo(e.Kind);
    }
}
