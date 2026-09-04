using System.IO;
using System.Windows;
using Microsoft.Win32;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Scanning;

namespace ModOrganizer.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ModLibraryService _library;
    private readonly ModScanner _scanner;

    public bool RootsChanged { get; private set; }

    public SettingsWindow(ModLibraryService library, ModScanner scanner)
    {
        InitializeComponent();
        _library = library;
        _scanner = scanner;
        Refresh();
    }

    private void Refresh()
    {
        Grid.ItemsSource = _library.GetRoots().ToList();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose a mods root folder"
        };
        if (dlg.ShowDialog(this) != true) return;

        var path = dlg.FolderName;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;

        var rootId = _library.EnsureRoot(path, name);
        RootsChanged = true;
        Refresh();

        var confirm = MessageBox.Show("Scan this root now? (may take a while on HDDs)",
            "Initial scan", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            _ = System.Threading.Tasks.Task.Run(() => _scanner.Scan(rootId));
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
