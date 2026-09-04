using System.IO;
using System.Windows;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Views;

public partial class TrashWindow : Window
{
    private readonly ModLibraryService _library;

    public TrashWindow(ModLibraryService library)
    {
        InitializeComponent();
        _library = library;
        Refresh();
    }

    private void Refresh()
    {
        Grid.ItemsSource = _library.GetDeletedMods().Select(m => new
        {
            m.Id,
            m.FolderName,
            m.CategoryName,
            DeletedAt = m.DeletedAt,
            FolderExistsText = Directory.Exists(m.FolderAbsPath) ? "✓" : "—"
        }).ToList();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItems.Count == 0) return;
        foreach (var item in Grid.SelectedItems)
        {
            dynamic d = item;
            _library.RestoreDeleted((long)d.Id);
        }
        MessageBox.Show(
            "Restored in the database. If the folder is gone, restore it from the Windows Recycle Bin manually, then run Rescan.",
            "Restored", MessageBoxButton.OK, MessageBoxImage.Information);
        Refresh();
    }

    private void HardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItems.Count == 0) return;
        var r = MessageBox.Show(
            $"Permanently remove {Grid.SelectedItems.Count} mod(s) from the database?\n" +
            "This cannot be undone.",
            "Delete forever", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        foreach (var item in Grid.SelectedItems)
        {
            dynamic d = item;
            _library.HardDelete((long)d.Id);
        }
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
