using System.ComponentModel;
using System.IO;
using System.Windows;
using ModOrganizer.Core.Import;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.Dialogs;

public partial class ImportDialog : Window
{
    private readonly ImportService _svc;
    private readonly long _rootId;
    private readonly List<ImportRowViewModel> _rows = new();

    public ImportDialog(ImportService svc, ModLibraryService library, long rootId, IReadOnlyList<string> files)
    {
        InitializeComponent();
        _svc = svc;
        _rootId = rootId;

        var allCats = library.GetCategories(rootId).Where(c => !c.IsMissing).ToList();

        foreach (var path in files)
        {
            var candidate = svc.Analyze(path, rootId);
            _rows.Add(new ImportRowViewModel
            {
                SourcePath = path,
                FileName = Path.GetFileName(path),
                FolderName = candidate.SuggestedFolderName,
                SelectedCategoryId = candidate.SuggestedCategoryId ?? allCats.FirstOrDefault()?.Id ?? 0,
                AllCategories = allCats
            });
        }

        SubtitleText.Text = $"{_rows.Count} Datei(en) importieren";
        ItemsList.ItemsSource = _rows;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        bool copy = RbCopy.IsChecked == true;
        int ok = 0;
        foreach (var row in _rows)
        {
            try
            {
                _svc.Import(row.SourcePath, row.SelectedCategoryId, row.FolderName, copy);
                ok++;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import {row.FileName}:\n{ex.Message}",
                    "Import error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        if (ok > 0) DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public sealed class ImportRowViewModel : INotifyPropertyChanged
{
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public IReadOnlyList<CategoryInfo> AllCategories { get; set; } = Array.Empty<CategoryInfo>();

    private long _selectedCategoryId;
    public long SelectedCategoryId
    {
        get => _selectedCategoryId;
        set { _selectedCategoryId = value; PropertyChanged?.Invoke(this, new(nameof(SelectedCategoryId))); }
    }

    private string _folderName = "";
    public string FolderName
    {
        get => _folderName;
        set { _folderName = value; PropertyChanged?.Invoke(this, new(nameof(FolderName))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
