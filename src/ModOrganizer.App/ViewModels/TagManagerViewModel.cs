using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.App.ViewModels;

/// <summary>One row in the tag manager.</summary>
public sealed partial class TagRowViewModel : ObservableObject
{
    public long Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _usageCount;
    [ObservableProperty] private string? _colorHex;
    [ObservableProperty] private string? _description;

    public TagRowViewModel(TagInfo tag)
    {
        Id = tag.Id;
        _name = tag.Name;
        _usageCount = tag.UsageCount;
        _colorHex = tag.ColorHex;
        _description = tag.Description;
    }

    public Brush Swatch => TagBrushes.Solid(ColorHex, Name);

    public string UsageText => UsageCount == 1 ? "1 Mod" : $"{UsageCount} Mods";

    partial void OnColorHexChanged(string? value) => OnPropertyChanged(nameof(Swatch));
    partial void OnUsageCountChanged(int value) => OnPropertyChanged(nameof(UsageText));
}

/// <summary>
/// Manages the global tag vocabulary: create, rename, recolour, describe, delete.
/// Deleting a tag drops only its assignments — mods themselves are never touched, which
/// the mod_tags cascade from tags gives us for free.
/// </summary>
public sealed partial class TagManagerViewModel : ObservableObject
{
    private readonly TagService _tags;

    public ObservableCollection<TagRowViewModel> Tags { get; } = new();

    [ObservableProperty] private TagRowViewModel? _selectedTag;
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>The palette offered in the colour picker.</summary>
    public IReadOnlyList<string> Palette { get; } = new[]
    {
        "#7A5CFA", "#FF8A65", "#26A69A", "#FFB300", "#5C6BC0",
        "#EC407A", "#26C6DA", "#9CCC65", "#AB47BC", "#42A5F5",
        "#EF5350", "#8D6E63", "#78909C", "#D4E157", "#FF7043"
    };

    /// <summary>Raised whenever the vocabulary changed, so the gallery can reload its chips.</summary>
    public event EventHandler? TagsChanged;

    public TagManagerViewModel(TagService tags) => _tags = tags;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var previous = SelectedTag?.Id;
            var rows = await _tags.GetAllTagsAsync().ConfigureAwait(true);

            Tags.Clear();
            foreach (var t in rows) Tags.Add(new TagRowViewModel(t));

            SelectedTag = previous is null
                ? Tags.FirstOrDefault()
                : Tags.FirstOrDefault(t => t.Id == previous.Value) ?? Tags.FirstOrDefault();

            var assigned = rows.Sum(t => t.UsageCount);
            StatusText = $"{rows.Count} Tags · {assigned} Zuordnungen";
        }
        catch (Exception ex)
        {
            StatusText = "Laden fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateTag()
    {
        var name = NewTagName.Trim();
        if (name.Length == 0) return;

        if (await RunAsync(() => _tags.CreateTag(name), "Tag konnte nicht angelegt werden").ConfigureAwait(true))
        {
            NewTagName = "";
            await ReloadAndNotifyAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RenameTag()
    {
        if (SelectedTag is null) return;

        var dialog = new Views.PromptDialog("Tag umbenennen", "Neuer Name:", SelectedTag.Name);
        if (Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is { } owner)
            dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        var newName = (dialog.ResultText ?? "").Trim();
        if (newName.Length == 0 || newName == SelectedTag.Name) return;

        var tagId = SelectedTag.Id;
        if (await RunAsync(() => _tags.RenameTag(tagId, newName), "Umbenennen fehlgeschlagen").ConfigureAwait(true))
            await ReloadAndNotifyAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SetColor(string? hex)
    {
        if (SelectedTag is null || string.IsNullOrWhiteSpace(hex)) return;

        var tagId = SelectedTag.Id;
        if (await RunAsync(() => _tags.SetColor(tagId, hex), "Farbe konnte nicht gesetzt werden").ConfigureAwait(true))
        {
            SelectedTag.ColorHex = hex;
            TagsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task SaveDescription()
    {
        if (SelectedTag is null) return;

        var tagId = SelectedTag.Id;
        var text = SelectedTag.Description;
        if (await RunAsync(() => _tags.SetDescription(tagId, text), "Beschreibung konnte nicht gespeichert werden")
                .ConfigureAwait(true))
        {
            StatusText = $"Beschreibung für „{SelectedTag.Name}“ gespeichert";
        }
    }

    [RelayCommand]
    private async Task DeleteTag()
    {
        if (SelectedTag is null) return;
        var tag = SelectedTag;

        var body = tag.UsageCount == 0
            ? $"Tag „{tag.Name}“ löschen?"
            : $"Tag „{tag.Name}“ ist {tag.UsageText} zugeordnet.\n\n" +
              "Das Tag und alle Zuordnungen werden entfernt. Die Mods selbst bleiben unangetastet.";

        if (MessageBox.Show(body, "Tag löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        if (await RunAsync(() => _tags.DeleteTag(tag.Id), "Löschen fehlgeschlagen").ConfigureAwait(true))
        {
            SelectedTag = null;
            await ReloadAndNotifyAsync().ConfigureAwait(true);
        }
    }

    private async Task ReloadAndNotifyAsync()
    {
        await LoadAsync().ConfigureAwait(true);
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a write off the UI thread and reports failure instead of throwing out of a command.</summary>
    private async Task<bool> RunAsync(Action work, string failureTitle)
    {
        IsBusy = true;
        try
        {
            await Task.Run(work).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, failureTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
