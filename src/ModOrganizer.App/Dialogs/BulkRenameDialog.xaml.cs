using System.Windows;
using System.Windows.Controls;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Dialogs;

public partial class BulkRenameDialog : Window
{
    private readonly RenameService _svc;
    private readonly IReadOnlyList<long> _modIds;
    private BulkRenamePlan? _plan;

    public BulkRenameDialog(RenameService svc, IReadOnlyList<long> modIds, ModLibraryService lib)
    {
        InitializeComponent();
        _svc = svc;
        _modIds = modIds;
        SubtitleText.Text = $"{modIds.Count} Mods ausgewählt";
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        BuildPreview();
    }

    private void BuildPreview()
    {
        var pattern = PatternBox.Text ?? "";
        var kind = (KindBox.SelectedIndex == 1) ? BulkPatternKind.Regex : BulkPatternKind.Template;
        _plan = _svc.CreateBulkPlan(_modIds, pattern, kind);

        Grid.ItemsSource = _plan.Entries.Select(e => new
        {
            e.OldFolderName,
            e.NewFolderName,
            Status = e.Conflict ?? (e.Enabled ? "OK" : "skip")
        }).ToList();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) BuildPreview();
        if (_plan is null || _plan.Entries.Count == 0)
        {
            MessageBox.Show("Nothing to do.", "Bulk Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var applicable = _plan.Entries.Count(x => x.Enabled && x.Conflict is null);
        if (applicable == 0)
        {
            MessageBox.Show("No applicable changes (conflicts/invalid names).", "Bulk Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var result = _svc.ExecuteBulk(_plan);

        if (!result.AllSucceeded)
        {
            var detail = string.Join("\n", result.Failures
                .Take(10)
                .Select(f => $"• {f.AttemptedName}: {f.Reason}"));
            if (result.Failures.Count > 10)
                detail += $"\n… und {result.Failures.Count - 10} weitere";

            MessageBox.Show(
                $"{result.Succeeded} umbenannt, {result.Failures.Count} fehlgeschlagen:\n\n{detail}",
                "Bulk Rename", MessageBoxButton.OK,
                result.Succeeded > 0 ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
