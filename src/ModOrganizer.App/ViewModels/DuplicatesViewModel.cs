using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModOrganizer.Core.Categories;
using ModOrganizer.Core.Duplicates;
using ModOrganizer.Core.Management;

namespace ModOrganizer.App.ViewModels;

/// <summary>One candidate inside a duplicate group.</summary>
public sealed class DuplicateModViewModel
{
    public DuplicateModRow Model { get; }

    public DuplicateModViewModel(DuplicateModRow model) => Model = model;

    public long ModId => Model.ModId;
    public string FolderName => Model.FolderName;
    public string CategoryName => Model.CategoryName;
    public string FolderAbsPath => Model.FolderAbsPath;
    public int Rating => Model.Rating;

    public string SizeText => FormatSize(Model.TotalSizeBytes);
    public string FilesText => Model.FileCount == 1 ? "1 Datei" : $"{Model.FileCount} Dateien";

    public string RatingText => Model.Rating > 0
        ? new string('★', Model.Rating) + new string('☆', 5 - Model.Rating)
        : "";

    public string ModifiedText =>
        DateTimeOffset.TryParse(Model.FolderMtime, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd")
            : "";

    /// <summary>False when the folder is gone from disk — acting on it would fail.</summary>
    public bool ExistsOnDisk => Directory.Exists(Model.FolderAbsPath);

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F1} {units[unit]}";
    }
}

public sealed class DuplicateGroupViewModel
{
    public string Key { get; }
    public string Kind { get; }
    public ObservableCollection<DuplicateModViewModel> Mods { get; } = new();

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Key = group.Key;
        Kind = group.Kind;
        foreach (var m in group.Mods) Mods.Add(new DuplicateModViewModel(m));
    }

    public string KindLabel => Kind == "hash" ? "identisch" : "Name";

    /// <summary>A hash group means byte-identical payloads, so it is safe to act on.</summary>
    public string Hint => Kind == "hash"
        ? "Byte-identische .pmp/.ttmp2 — eine Kopie genügt."
        : "Ähnlicher Name — Inhalte können sich unterscheiden, bitte prüfen.";

    public string SizeText
    {
        get
        {
            var total = Mods.Sum(m => m.Model.TotalSizeBytes);
            var largest = Mods.Max(m => m.Model.TotalSizeBytes);
            var recoverable = total - largest;
            return recoverable > 0
                ? $"{Mods.Count} Mods · bis zu {Format(recoverable)} freigebbar"
                : $"{Mods.Count} Mods";
        }
    }

    private static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F1} {units[unit]}";
    }
}

/// <summary>
/// The duplicates view. Beyond listing groups it now offers the two actions the plan calls
/// for: move a copy into an archive category, or send it to the Recycle Bin. Both are the
/// same services the gallery uses, so they stay transactional and undoable.
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    /// <summary>Where "archive" moves a duplicate to; created on first use.</summary>
    public const string ArchiveCategoryName = "_Archiv";

    private readonly DuplicateFinder _finder;
    private readonly CategoryService _categories;
    private readonly MoveService _move;
    private readonly DeleteService _delete;
    private readonly long? _rootId;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Raised after a mod was moved or deleted, so the gallery can refresh.</summary>
    public event EventHandler? LibraryChanged;

    public DuplicatesViewModel(DuplicateFinder finder, CategoryService categories,
        MoveService move, DeleteService delete, long? rootId)
    {
        _finder = finder;
        _categories = categories;
        _move = move;
        _delete = delete;
        _rootId = rootId;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "Suche Duplikate…";
        try
        {
            var groups = await Task.Run(() => _finder.FindAll(_rootId).ToList()).ConfigureAwait(true);

            Groups.Clear();
            foreach (var g in groups) Groups.Add(new DuplicateGroupViewModel(g));

            var byName = groups.Count(g => g.Kind == "name");
            var byHash = groups.Count(g => g.Kind == "hash");
            StatusText = groups.Count == 0
                ? "Keine Duplikate gefunden."
                : $"{groups.Count} Gruppen · {byName} nach Name, {byHash} byte-identisch";
        }
        catch (Exception ex)
        {
            StatusText = "Duplikat-Suche fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Reload() => await LoadAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task MoveToArchive(DuplicateModViewModel? mod)
    {
        if (mod is null) return;

        if (string.Equals(mod.CategoryName, ArchiveCategoryName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show($"„{mod.FolderName}“ liegt bereits in {ArchiveCategoryName}.",
                "Verschieben", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"„{mod.FolderName}“ nach „{ArchiveCategoryName}“ verschieben?\n\n" +
            $"Von: {mod.FolderAbsPath}\n\n" +
            "Die Dateien bleiben erhalten, nur die Kategorie ändert sich. " +
            "Tags, Links und Kommentare bleiben am Mod hängen.",
            "In Archiv verschieben", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await RunAsync($"„{mod.FolderName}“ archiviert", () =>
        {
            var targetId = _categories.EnsureCategory(mod.Model.RootId, ArchiveCategoryName);

            var plan = _move.CreatePlan(new[] { mod.ModId }, targetId);
            if (plan.Conflicts.Count > 0)
                throw new InvalidOperationException(string.Join("\n", plan.Conflicts));
            if (!plan.CanExecute)
                throw new InvalidOperationException("Verschieben ist für diesen Mod nicht möglich.");

            _move.Execute(plan);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SendToRecycleBin(DuplicateModViewModel? mod)
    {
        if (mod is null) return;

        var confirm = MessageBox.Show(
            $"„{mod.FolderName}“ in den Papierkorb verschieben?\n\n" +
            $"Pfad: {mod.FolderAbsPath}\n" +
            $"Größe: {mod.SizeText} · {mod.FilesText}\n\n" +
            "Der Mod wird zusätzlich als ZIP archiviert, damit er auch nach dem Leeren " +
            "des Papierkorbs wiederherstellbar bleibt.",
            "In den Papierkorb", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await RunAsync($"„{mod.FolderName}“ in den Papierkorb verschoben", () =>
        {
            // archiveBeforeDelete: a duplicate is exactly the case where a cheap ZIP
            // backup is worth it, because the user is deciding under uncertainty.
            if (!_delete.DeleteMod(mod.ModId, archiveBeforeDelete: true))
                throw new InvalidOperationException(
                    "Der Ordner konnte nicht in den Papierkorb verschoben werden. " +
                    "Ist er in einem anderen Programm geöffnet?");
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowInExplorer(DuplicateModViewModel? mod)
    {
        if (mod is null) return;
        try
        {
            if (Directory.Exists(mod.FolderAbsPath))
                Process.Start("explorer.exe", $"\"{mod.FolderAbsPath}\"");
            else
                MessageBox.Show($"Ordner existiert nicht mehr:\n{mod.FolderAbsPath}",
                    "Im Explorer öffnen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Im Explorer öffnen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunAsync(string successMessage, Action work)
    {
        IsBusy = true;
        try
        {
            await Task.Run(work).ConfigureAwait(true);
            LibraryChanged?.Invoke(this, EventArgs.Empty);

            // The group this mod belonged to has changed, so reload rather than patch.
            await LoadAsync().ConfigureAwait(true);
            StatusText = successMessage + " · " + StatusText;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Aktion fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
