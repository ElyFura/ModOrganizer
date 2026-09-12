using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Scanning;

namespace ModOrganizer.App.ViewModels;

/// <summary>One library row in the settings window, as it looks to the current user.</summary>
public sealed partial class RootRowViewModel : ObservableObject
{
    public RootMapping Model { get; }

    public RootRowViewModel(RootMapping model) => Model = model;

    public long Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public bool IsMapped => Model.IsMapped;
    public bool PathExists => Model.PathExists;
    public string StatusText => Model.StatusText;
    public int CategoryCount => Model.CategoryCount;
    public int ModCount => Model.ModCount;

    public string PathText => Model.IsMapped
        ? Model.Path!
        : $"nicht zugeordnet  (angelegt als: {Model.OriginalPath})";

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            OnPropertyChanged();
            EnabledChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? EnabledChanged;

    /// <summary>Shared libraries are the normal case once a second user maps them.</summary>
    public string SharedText => Model.MappedUserCount switch
    {
        0 => "von niemandem zugeordnet",
        1 => "nur von 1 Benutzer zugeordnet",
        _ => $"von {Model.MappedUserCount} Benutzern zugeordnet"
    };

    public string CountsText => $"{CategoryCount} Kategorien · {ModCount} Mods";
}

/// <summary>
/// Root management. The central idea: a root is a shared *library*, and every user maps it
/// to a folder on their own machine. Before this, a root was one absolute path for
/// everyone, so a second user inherited the first user's drive letters.
/// </summary>
public sealed partial class RootSettingsViewModel : ObservableObject
{
    private readonly RootService _roots;
    private readonly ModScanner _scanner;

    public ObservableCollection<RootRowViewModel> Roots { get; } = new();

    [ObservableProperty] private RootRowViewModel? _selected;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Raised whenever the root list or a mapping changed.</summary>
    public event EventHandler? RootsChanged;

    private readonly UserProfileService? _profile;

    public RootSettingsViewModel(RootService roots, ModScanner scanner,
        UserProfileService? profile = null)
    {
        _roots = roots;
        _scanner = scanner;
        _profile = profile;
    }

    // ---- the name everyone else sees ----

    /// <summary>
    /// Without this the second user shows up as their email address everywhere - on cards,
    /// in comments, in the presence chips - because the sign-up trigger falls back to the
    /// address when Supabase carries no display_name.
    /// </summary>
    [ObservableProperty] private string _displayName = "";

    [ObservableProperty] private string _profileStatus = "";

    /// <summary>Only offered when signed in; an offline session has no profile row.</summary>
    public bool CanEditProfile => _profile?.GetCurrent() is not null;

    private void LoadProfile()
    {
        var me = _profile?.GetCurrent();
        if (me is null) return;
        DisplayName = me.DisplayName;
        ProfileStatus = string.Equals(me.DisplayName, me.Email, StringComparison.OrdinalIgnoreCase)
            ? "Zurzeit wird deine E-Mail-Adresse angezeigt."
            : "";
    }

    [RelayCommand]
    private async Task SaveDisplayName()
    {
        if (_profile is null) return;
        var name = DisplayName;

        try
        {
            await Task.Run(() => _profile.SetDisplayName(name)).ConfigureAwait(true);
            LoadProfile();
            ProfileStatus = "Gespeichert. Beim anderen Benutzer erscheint der Name nach dem " +
                            "nächsten Neuladen.";
        }
        catch (Exception ex)
        {
            ProfileStatus = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var previous = Selected?.Id;

            // Pick up roots this user created before per-user mappings existed.
            var adopted = await Task.Run(() => _roots.AdoptLocalRoots()).ConfigureAwait(true);

            var rows = await _roots.GetAllAsync().ConfigureAwait(true);

            foreach (var old in Roots) old.EnabledChanged -= OnRowEnabledChanged;
            Roots.Clear();
            foreach (var r in rows)
            {
                var vm = new RootRowViewModel(r);
                vm.EnabledChanged += OnRowEnabledChanged;
                Roots.Add(vm);
            }

            Selected = previous is null
                ? Roots.FirstOrDefault()
                : Roots.FirstOrDefault(r => r.Id == previous.Value) ?? Roots.FirstOrDefault();

            LoadProfile();
            OnPropertyChanged(nameof(CanEditProfile));

            var mapped = rows.Count(r => r.IsMapped);
            var broken = rows.Count(r => r.IsMapped && !r.PathExists);
            StatusText = $"{rows.Count} Bibliotheken · {mapped} hier zugeordnet" +
                         (broken > 0 ? $" · {broken} mit fehlendem Ordner" : "") +
                         (adopted > 0 ? $" · {adopted} automatisch übernommen" : "");
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

    private void OnRowEnabledChanged(object? sender, bool enabled)
    {
        if (sender is not RootRowViewModel row) return;
        _ = RunAsync(() => _roots.SetEnabled(row.Id, enabled), reload: false);
    }

    /// <summary>Points the selected library at a folder on this machine.</summary>
    [RelayCommand]
    private async Task MapFolder()
    {
        if (Selected is null) return;
        var row = Selected;

        var dlg = new OpenFolderDialog
        {
            Title = $"Ordner für „{row.DisplayName}\" auf diesem PC wählen",
            InitialDirectory = row.IsMapped && Directory.Exists(row.Model.Path)
                ? row.Model.Path!
                : ""
        };
        if (dlg.ShowDialog() != true) return;

        var path = dlg.FolderName;
        await RunAsync(() => _roots.SetPath(row.Id, path)).ConfigureAwait(true);

        if (MessageBox.Show(
                $"„{row.DisplayName}\" ist jetzt zugeordnet.\n\nJetzt scannen?",
                "Zugeordnet", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await ScanAsync(row.Id, row.DisplayName).ConfigureAwait(true);
        }
    }

    /// <summary>Adds a new library and maps it to the chosen folder in one step.</summary>
    [RelayCommand]
    private async Task AddRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Mod-Ordner wählen" };
        if (dlg.ShowDialog() != true) return;

        var path = dlg.FolderName;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;

        long rootId = 0;
        await RunAsync(() => rootId = _roots.AddRoot(path, name)).ConfigureAwait(true);
        if (rootId == 0) return;

        if (MessageBox.Show("Diesen Ordner jetzt scannen?", "Neue Bibliothek",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await ScanAsync(rootId, name).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// One folder pick that maps every library at once. Two people syncing the same
    /// Nextcloud folder have identical trees under a different anchor, so picking that
    /// anchor is enough to place all of them.
    /// </summary>
    [RelayCommand]
    private async Task MapAllUnderBase()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Basisordner wählen (z. B. dein Nextcloud-Ordner)"
        };
        if (dlg.ShowDialog() != true) return;

        var basePath = dlg.FolderName;

        IsBusy = true;
        IReadOnlyList<RootService.RootMapProposal> proposals;
        try
        {
            proposals = await Task.Run(() => _roots.ProposeMappingsUnder(basePath)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            MessageBox.Show(ex.Message, "Zuordnen fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var (found, conflicts) = RootService.SplitConflicts(proposals);
        var missing = proposals.Where(p => p.ResolvedPath is null).ToList();

        if (found.Count == 0)
        {
            MessageBox.Show(
                $"Unter \u201E{basePath}\u201C wurde keine der Bibliotheken gefunden.\n\n" +
                "W\u00E4hle den Ordner, der dieselbe Struktur enth\u00E4lt wie beim anderen Benutzer \u2014 " +
                "meist der Ordner, den Nextcloud synchronisiert.",
                "Nichts gefunden", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Show exactly what will be written before writing it.
        var preview = string.Join("\n", found.Select(p =>
            $"  {p.DisplayName}\n      \u2192 {p.ResolvedPath}"));
        if (conflicts.Count > 0)
            preview += "\n\nUebersprungen (zeigen auf denselben Ordner):\n" +
                       string.Join("\n", conflicts.Select(p => $"  {p.DisplayName}"));
        if (missing.Count > 0)
            preview += "\n\nNicht gefunden:\n" + string.Join("\n", missing.Select(p => $"  {p.DisplayName}"));

        if (MessageBox.Show(
                $"{found.Count} Bibliothek(en) unter \u201E{basePath}\u201C gefunden:\n\n{preview}\n\nSo zuordnen?",
                "Zuordnung pr\u00FCfen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunAsync(() => _roots.ApplyMappings(found)).ConfigureAwait(true);
        StatusText = $"{found.Count} Bibliothek(en) zugeordnet · " + StatusText;
    }

    [RelayCommand]
    private async Task RenameRoot()
    {
        if (Selected is null) return;
        var row = Selected;

        var dlg = new Views.PromptDialog("Bibliothek umbenennen", "Name:", row.DisplayName);
        if (Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is { } owner)
            dlg.Owner = owner;
        if (dlg.ShowDialog() != true) return;

        var name = (dlg.ResultText ?? "").Trim();
        if (name.Length == 0 || name == row.DisplayName) return;

        await RunAsync(() => _roots.Rename(row.Id, name)).ConfigureAwait(true);
    }

    /// <summary>Forgets this user's mapping; the library and its mods stay for everyone else.</summary>
    [RelayCommand]
    private async Task RemoveMapping()
    {
        if (Selected is null) return;
        var row = Selected;

        if (MessageBox.Show(
                $"Zuordnung für „{row.DisplayName}\" auf diesem PC entfernen?\n\n" +
                "Die Bibliothek bleibt bestehen — sie verschwindet nur aus deiner Ansicht. " +
                "Mods, Tags und Kommentare bleiben für alle erhalten.",
                "Zuordnung entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        await RunAsync(() => _roots.RemoveMapping(row.Id)).ConfigureAwait(true);
    }

    /// <summary>Deletes the library for every user. Nothing on disk is touched.</summary>
    [RelayCommand]
    private async Task DeleteRoot()
    {
        if (Selected is null) return;
        var row = Selected;

        if (MessageBox.Show(
                $"Bibliothek „{row.DisplayName}\" für ALLE Benutzer löschen?\n\n" +
                $"{row.CountsText} werden aus der Datenbank entfernt, samt Tags, Bewertungen und Kommentaren.\n\n" +
                "Auf der Festplatte wird nichts gelöscht — die Mod-Ordner bleiben.\n\n" +
                "Wenn du sie nur bei dir ausblenden willst, nimm stattdessen „Zuordnung entfernen\".",
                "Bibliothek löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        await RunAsync(() => _roots.DeleteRoot(row.Id)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ScanSelected()
    {
        if (Selected is null) return;
        await ScanAsync(Selected.Id, Selected.DisplayName).ConfigureAwait(true);
    }

    private async Task ScanAsync(long rootId, string name)
    {
        IsBusy = true;
        StatusText = $"Scanne „{name}\"…";
        try
        {
            var summary = await Task.Run(() => _scanner.Scan(rootId)).ConfigureAwait(true);
            StatusText = $"„{name}\": {summary.CategoriesSeen} Kategorien, {summary.ModsSeen} Mods " +
                         $"in {summary.Duration.TotalSeconds:F1}s";
            RootsChanged?.Invoke(this, EventArgs.Empty);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (RootNotMappedException ex)
        {
            MessageBox.Show(ex.Message, "Nicht zugeordnet", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scan fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(Action work, bool reload = true)
    {
        IsBusy = true;
        try
        {
            await Task.Run(work).ConfigureAwait(true);
            RootsChanged?.Invoke(this, EventArgs.Empty);
            if (reload) await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Aktion fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
