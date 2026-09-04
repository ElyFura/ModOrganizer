using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using ModOrganizer.Core.Categories;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Storage;

namespace ModOrganizer.App.ViewModels;

public sealed partial class CategoryManagerViewModel : ObservableObject
{
    private readonly ModLibraryService _library;
    private readonly CategoryService _catSvc;
    private readonly DeleteService _deleteSvc;
    private readonly DatabaseStore _store;

    public ObservableCollection<RootInfo> Roots { get; } = new();
    public ObservableCollection<CategoryRow> Categories { get; } = new();

    [ObservableProperty] private RootInfo? _selectedRoot;
    [ObservableProperty] private CategoryRow? _selectedCategory;
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private CategoryRow? _mergeTarget;

    public event EventHandler? CategoriesChanged;

    public CategoryManagerViewModel(ModLibraryService library, CategoryService catSvc, DeleteService deleteSvc, DatabaseStore store)
    {
        _library = library; _catSvc = catSvc; _deleteSvc = deleteSvc; _store = store;
        Load();
    }

    private void Load() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var roots = await _library.GetRootsAsync().ConfigureAwait(true);
        Roots.Clear();
        foreach (var r in roots) Roots.Add(r);

        if (SelectedRoot is null)
        {
            // Assigning this fires OnSelectedRootChanged, which loads the categories.
            SelectedRoot = Roots.FirstOrDefault();
            if (SelectedRoot is not null) return;
        }
        await LoadCategoriesAsync().ConfigureAwait(true);
    }

    partial void OnSelectedRootChanged(RootInfo? value) => _ = LoadCategoriesAsync();

    private void LoadCategories() => _ = LoadCategoriesAsync();

    private async Task LoadCategoriesAsync()
    {
        if (SelectedRoot is null) { Categories.Clear(); return; }

        var categories = await _library.GetCategoriesAsync(SelectedRoot.Id).ConfigureAwait(true);
        Categories.Clear();
        foreach (var c in categories)
            Categories.Add(new CategoryRow(c.Id, c.Name, c.ModCount, c.IsMissing));
    }

    [RelayCommand]
    private async Task AddCategory()
    {
        if (SelectedRoot is null || string.IsNullOrWhiteSpace(NewCategoryName)) return;
        var rootId = SelectedRoot.Id;
        var name = NewCategoryName.Trim();
        try
        {
            await Task.Run(() => _catSvc.Add(rootId, name)).ConfigureAwait(true);
            NewCategoryName = "";
            await LoadCategoriesAsync().ConfigureAwait(true);
            CategoriesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Add failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RenameCategory()
    {
        if (SelectedCategory is null || string.IsNullOrWhiteSpace(RenameText)) return;
        var categoryId = SelectedCategory.Id;
        var newName = RenameText.Trim();
        try
        {
            await Task.Run(() => _catSvc.Rename(categoryId, newName)).ConfigureAwait(true);
            await LoadCategoriesAsync().ConfigureAwait(true);
            CategoriesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Delete and Merge touch both the disk and the database, so they are the two most
    // likely commands to fail — a folder open in Explorer is enough. They used to have no
    // error handling at all, unlike Add and Rename above, so any failure escaped the relay
    // command and reached the user as a raw stack trace from the global handler.
    [RelayCommand]
    private async Task DeleteCategory()
    {
        if (SelectedCategory is null) return;
        var category = SelectedCategory;

        if (category.ModCount > 0)
        {
            var confirm = MessageBox.Show(
                $"Category '{category.Name}' contains {category.ModCount} mod(s).\n" +
                "Send the whole folder to the Recycle Bin?",
                "Delete category", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        try
        {
            if (category.ModCount == 0)
            {
                var removed = await Task.Run(() => _catSvc.DeleteIfEmpty(category.Id)).ConfigureAwait(true);
                if (!removed)
                {
                    MessageBox.Show(
                        $"'{category.Name}' konnte nicht gelöscht werden — der Ordner ist nicht leer.\n\n" +
                        "Ein Rescan aktualisiert die Mod-Anzahl.",
                        "Delete category", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            else
            {
                await Task.Run(() => _deleteSvc.DeleteCategory(category.Id)).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await LoadCategoriesAsync().ConfigureAwait(true);
        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task MergeIntoTarget()
    {
        if (SelectedCategory is null || MergeTarget is null || SelectedCategory.Id == MergeTarget.Id) return;
        var source = SelectedCategory;
        var target = MergeTarget;

        try
        {
            var plan = await Task.Run(() => _catSvc.CreateMergePlan(source.Id, target.Id)).ConfigureAwait(true);

            var strategy = CategoryService.MergeConflictStrategy.Skip;
            if (plan.Conflicts.Count > 0)
            {
                var result = MessageBox.Show(
                    $"{plan.Conflicts.Count} conflict(s) detected.\n" +
                    "Yes → rename conflicting mods with suffix\n" +
                    "No → skip conflicting mods",
                    "Merge conflicts", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Cancel) return;
                strategy = result == MessageBoxResult.Yes
                    ? CategoryService.MergeConflictStrategy.RenameWithSuffix
                    : CategoryService.MergeConflictStrategy.Skip;
            }

            // Moving every mod folder is minutes of work on a big category.
            await Task.Run(() => _catSvc.ExecuteMerge(plan, strategy)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Merge failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await LoadCategoriesAsync().ConfigureAwait(true);
        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task MoveUp()
    {
        if (SelectedCategory is null || SelectedRoot is null) return;
        var idx = Categories.IndexOf(SelectedCategory);
        if (idx <= 0) return;
        Categories.Move(idx, idx - 1);
        await PersistOrderAsync(SelectedRoot.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task MoveDown()
    {
        if (SelectedCategory is null || SelectedRoot is null) return;
        var idx = Categories.IndexOf(SelectedCategory);
        if (idx < 0 || idx >= Categories.Count - 1) return;
        Categories.Move(idx, idx + 1);
        await PersistOrderAsync(SelectedRoot.Id).ConfigureAwait(true);
    }

    /// <summary>
    /// The list is already reordered in the UI; this writes it. On failure the list is
    /// reloaded so what the user sees matches what is actually stored.
    /// </summary>
    private async Task PersistOrderAsync(long rootId)
    {
        var order = Categories.Select(c => c.Id).ToList();
        try
        {
            await Task.Run(() => _catSvc.Reorder(rootId, order)).ConfigureAwait(true);
            CategoriesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Reorder failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            await LoadCategoriesAsync().ConfigureAwait(true);
        }
    }
}

public sealed record CategoryRow(long Id, string Name, int ModCount, bool IsMissing);
