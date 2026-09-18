using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModOrganizer.App.Services;
using ModOrganizer.App.Views;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Collections;
using ModOrganizer.Core.Duplicates;
using ModOrganizer.Core.Health;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Penumbra;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Scanning;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ModLibraryService _library;
    private readonly TagService _tagSvc;
    private readonly ThumbnailCache _thumbnails;
    private readonly ModScanner _scanner;
    private readonly ModDetailViewModelFactory _detailFactory;
    private readonly MoveService _moveSvc;
    private readonly HealthChecker _health;
    private readonly DuplicateFinder _dupes;
    private readonly IServiceProvider _sp;
    private readonly ILogger<MainViewModel> _log;

    public ObservableCollection<RootInfo> Roots { get; } = new();
    public ObservableCollection<CategoryItemViewModel> Categories { get; } = new();
    public ObservableCollection<ModCardViewModel> Mods { get; } = new();
    public ObservableCollection<FolderNodeViewModel> FolderTree { get; } = new();
    public ObservableCollection<SmartCollection> SmartCollections { get; } = new();

    // ---- tag filter ----
    public ObservableCollection<TagFilterViewModel> TagFilters { get; } = new();

    /// <summary>All tags, for the bulk-tag context menu.</summary>
    public ObservableCollection<TagInfo> AllTags { get; } = new();

    public IReadOnlyList<TagFilterMode> TagFilterModes { get; } =
        new[] { TagFilterMode.And, TagFilterMode.Or };

    [ObservableProperty] private TagFilterMode _tagMode = TagFilterMode.And;
    [ObservableProperty] private bool _onlyUntagged;

    // ---- mods that are no longer on disk ----

    /// <summary>
    /// Mods the last scan did not find. They are hidden from the gallery by default - a
    /// deleted mod is not part of the library any more - but never removed on their own,
    /// because with a synced folder "gone" can simply mean "not synced yet".
    /// </summary>
    [ObservableProperty] private int _missingCount;

    /// <summary>The review list: show only the gone-from-disk mods, so they can be cleaned up.</summary>
    [ObservableProperty] private bool _showMissingOnly;

    public bool HasMissing => MissingCount > 0;

    // ---- realtime link ----

    /// <summary>
    /// Whether we are still receiving the other user's changes. Shown because a dead socket
    /// used to look exactly like a quiet one.
    /// </summary>
    [ObservableProperty] private bool _realtimeConnected;

    /// <summary>Only worth showing once realtime was actually set up for this session.</summary>
    [ObservableProperty] private bool _realtimeEnabled;

    public string RealtimeText => RealtimeConnected ? "live" : "getrennt";

    public string RealtimeTooltip => RealtimeConnected
        ? "Änderungen des anderen Benutzers kommen sofort an."
        : "Keine Verbindung zum Live-Kanal. Die App versucht es automatisch erneut; " +
          "bis dahin siehst du Änderungen erst nach einem Neuladen.";

    partial void OnRealtimeConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RealtimeText));
        OnPropertyChanged(nameof(RealtimeTooltip));
    }

    public string MissingBannerText => MissingCount == 1
        ? "1 Mod ist nicht mehr im Ordner"
        : $"{MissingCount} Mods sind nicht mehr im Ordner";

    partial void OnMissingCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(MissingBannerText));
    }

    partial void OnShowMissingOnlyChanged(bool value)
    {
        if (!_suppressFilterRefresh) RefreshMods();
    }

    /// <summary>
    /// Set while several filter properties are being reset together, so the gallery
    /// reloads once at the end instead of once per property.
    /// </summary>
    private bool _suppressFilterRefresh;

    partial void OnTagModeChanged(TagFilterMode value)
    {
        if (!_suppressFilterRefresh) RefreshMods();
    }

    partial void OnOnlyUntaggedChanged(bool value)
    {
        OnPropertyChanged(nameof(TagFilterSummary));
        OnPropertyChanged(nameof(HasTagFilter));
        if (!_suppressFilterRefresh) RefreshMods();
    }

    /// <summary>Human-readable summary of the active tag filter, for the toolbar.</summary>
    public string TagFilterSummary
    {
        get
        {
            var include = TagFilters.Where(t => t.State == TagFilterState.Include).Select(t => t.Name).ToList();
            var exclude = TagFilters.Where(t => t.State == TagFilterState.Exclude).Select(t => t.Name).ToList();

            var parts = new List<string>();
            if (include.Count > 0)
                parts.Add(string.Join(TagMode == TagFilterMode.And ? " UND " : " ODER ", include));
            if (exclude.Count > 0)
                parts.Add("ohne " + string.Join(", ", exclude));
            if (OnlyUntagged) parts.Add("ohne Tags");

            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }

    public bool HasTagFilter =>
        OnlyUntagged || TagFilters.Any(t => t.State != TagFilterState.Off);

    private PenumbraSnapshot _penumbra = new();
    private IReadOnlyList<PenumbraUserSnapshot> _penumbraUsers = Array.Empty<PenumbraUserSnapshot>();
    public bool PenumbraAvailable => _penumbra.IsAvailable || _penumbraUsers.Count > 0;

    public IReadOnlyList<PenumbraFilterMode> PenumbraFilterModes { get; } =
        new[] { PenumbraFilterMode.All, PenumbraFilterMode.Imported, PenumbraFilterMode.Active };

    /// <summary>
    /// First entry of the collection dropdown, meaning "do not filter". The box used to be
    /// IsEditable so it could be cleared by deleting the text - but the app's ComboBox
    /// template has no PART_EditableTextBox, so an editable box rendered its selection as
    /// blank. Non-editable plus an explicit reset entry fixes both.
    /// </summary>
    public const string AllCollectionsOption = "Alle Collections";

    public ObservableCollection<string> PenumbraCollectionFilters { get; } = new();

    [ObservableProperty] private PenumbraFilterMode _penumbraFilter = PenumbraFilterMode.All;
    [ObservableProperty] private string? _penumbraCollectionFilter;

    partial void OnPenumbraFilterChanged(PenumbraFilterMode value) => RefreshMods();
    partial void OnPenumbraCollectionFilterChanged(string? value) => RefreshMods();

    public ToastHost? Toasts { get; set; }
    public PresenceService? Presence { get; set; }
    public ObservableCollection<PresenceChipViewModel> OnlineUsers { get; } = new();

    /// <summary>True once somebody besides this user is online.</summary>
    public bool HasOnlineUsers => OnlineUsers.Any(u => !u.IsSelf);

    /// <summary>
    /// Rebuilds the online list and marks the cards somebody currently has open.
    ///
    /// The presence data was already being broadcast and received before this - it simply
    /// never reached the window, so nobody could see who was looking at what.
    /// </summary>
    public void RefreshPresence()
    {
        if (Presence is null) return;

        var selfId = Presence.SelfId;
        var viewers = new Dictionary<long, PresenceChipViewModel>();

        OnlineUsers.Clear();
        foreach (var p in Presence.OnlineUsers.Values.OrderBy(u => u.DisplayName))
        {
            var isSelf = selfId is { } me && me == p.UserId;

            // Only this view knows the loaded cards, so the mod name is resolved here.
            string? modName = null;
            if (p.ViewingModId is { } id)
                modName = Mods.FirstOrDefault(m => m.Model.Id == id)?.DisplayName;

            var chip = new PresenceChipViewModel(p, modName, isSelf);
            OnlineUsers.Add(chip);

            // Your own open detail window is not news - only mark other people's.
            if (!isSelf && p.ViewingModId is { } modId) viewers[modId] = chip;
        }

        foreach (var card in Mods)
        {
            if (viewers.TryGetValue(card.Model.Id, out var chip))
            {
                card.ViewedByName = chip.DisplayName;
                card.ViewedByBrush = chip.Brush;
            }
            else if (card.ViewedByName is not null)
            {
                card.ViewedByName = null;
                card.ViewedByBrush = null;
            }
        }

        OnPropertyChanged(nameof(HasOnlineUsers));
    }

    public IReadOnlyList<SortOption> SortOptions { get; } = new SortOption[]
    {
        new(ModSort.CategoryThenName, "Kategorie · Name"),
        new(ModSort.Name, "Name"),
        new(ModSort.AddedNewest, "Zuletzt hinzugefügt"),
        new(ModSort.UpdatedNewest, "Zuletzt geändert"),
        new(ModSort.LastViewed, "Zuletzt angeschaut"),
        new(ModSort.SizeDesc, "Größe (groß → klein)"),
        new(ModSort.RatingDesc, "Rating (hoch → niedrig)")
    };

    [ObservableProperty] private RootInfo? _selectedRoot;
    [ObservableProperty] private CategoryItemViewModel? _selectedCategory;
    [ObservableProperty] private ModCardViewModel? _selectedMod;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _visibleModCount;
    [ObservableProperty] private bool _isFolderView;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private int _minRating;
    [ObservableProperty] private int _thumbnailSize = 280;

    // The info area is (ThumbHeight - ThumbImageHeight) tall and now has to fit a tag
    // chip row as well as name, category, stars and the file-count badges.
    public int ThumbHeight => (int)(ThumbnailSize * 1.36);
    public int ThumbImageHeight => (int)(ThumbnailSize * 0.786);

    private AppConfig? _config;
    public void AttachConfig(AppConfig cfg)
    {
        _config = cfg;
        if (cfg.Ui.ThumbnailSize > 0) ThumbnailSize = cfg.Ui.ThumbnailSize;
    }

    partial void OnThumbnailSizeChanged(int value)
    {
        OnPropertyChanged(nameof(ThumbHeight));
        OnPropertyChanged(nameof(ThumbImageHeight));
        if (_config is not null)
        {
            _config.Ui.ThumbnailSize = value;
            try { _config.Save(); } catch { /* config save best-effort */ }
        }
    }

    public int SelectedCount => Mods.Count(m => m.IsSelected);

    public MainViewModel(ModLibraryService library, TagService tagSvc, ThumbnailCache thumbnails,
        ModScanner scanner, ModDetailViewModelFactory detailFactory, MoveService moveSvc,
        HealthChecker health, DuplicateFinder dupes, IServiceProvider sp, ILogger<MainViewModel> log)
    {
        _library = library;
        _tagSvc = tagSvc;
        _thumbnails = thumbnails;
        _scanner = scanner;
        _detailFactory = detailFactory;
        _moveSvc = moveSvc;
        _health = health;
        _dupes = dupes;
        _sp = sp;
        _log = log;
        _selectedSort = SortOptions[0];
    }

    /// <summary>Kept for callers that fire and forget; the work happens in <see cref="LoadAsync"/>.</summary>
    public void Load() => _ = LoadAsync();

    public async Task LoadAsync()
    {
        try
        {
            var roots = await _library.GetRootsAsync().ConfigureAwait(true);

            var previous = SelectedRoot?.Id;
            Roots.Clear();
            foreach (var r in roots) Roots.Add(r);

            // Penumbra reads the local config and the remote snapshots - both off-thread.
            await ReloadSmartCollectionsAsync().ConfigureAwait(true);
            await ReloadTagsAsync().ConfigureAwait(true);
            await ReloadPenumbraAsync().ConfigureAwait(true);

            var restored = previous is null ? null : Roots.FirstOrDefault(r => r.Id == previous.Value);
            var target = restored ?? (Roots.Count > 0 ? Roots[0] : null);

            if (!ReferenceEquals(target, SelectedRoot))
                SelectedRoot = target;   // setter kicks off the category + mod reload
            else
                await ReloadRootAsync(target).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load failed");
            StatusText = "Laden fehlgeschlagen: " + ex.Message;
        }
    }

    private async Task ReloadRootAsync(RootInfo? value)
    {
        Categories.Clear();
        CategoryTree.Clear();
        SelectedCategory = null;
        SelectedCategoryNode = null;

        if (value is null)
        {
            await RefreshModsAsync().ConfigureAwait(true);
            return;
        }

        var categories = await _library.GetCategoriesAsync(value.Id).ConfigureAwait(true);
        foreach (var c in categories) Categories.Add(new CategoryItemViewModel(c));
        RebuildCategoryTree();

        await RefreshModsAsync().ConfigureAwait(true);
        await RefreshMissingCountAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Recounts the gone-from-disk mods of the current root. Kept out of RefreshModsAsync
    /// on purpose: that one runs on every keystroke, and this only changes after a scan.
    /// </summary>
    public async Task RefreshMissingCountAsync()
    {
        if (SelectedRoot is null)
        {
            MissingCount = 0;
            if (ShowMissingOnly) ShowMissingOnly = false;
            return;
        }

        try
        {
            MissingCount = await _library.CountMissingAsync(SelectedRoot.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CountMissing failed");
            MissingCount = 0;
        }

        // Leaving the review list up when it is empty would look like the gallery broke.
        if (MissingCount == 0 && ShowMissingOnly) ShowMissingOnly = false;
    }

    /// <summary>
    /// Gallery plus the missing-mod banner. Every scan that runs in the background - the
    /// folder watcher, an import, a partner's change pushed over realtime - has to go
    /// through here, otherwise the banner keeps showing a count from before the scan.
    /// </summary>
    public void RefreshAfterScan()
    {
        RefreshMods();
        _ = RefreshMissingCountAsync();
    }

    /// <summary>Opens the review list of mods that are no longer in the folder.</summary>
    [RelayCommand]
    private void ReviewMissing() => ShowMissingOnly = true;

    /// <summary>
    /// Clears the gone-from-disk mods out of the library. They go to the trash rather than
    /// being erased, so a mod that was only missing because a sync had not finished can be
    /// brought back with its rating, tags and comments intact.
    /// </summary>
    [RelayCommand]
    private async Task TrashMissing()
    {
        if (SelectedRoot is null || MissingCount == 0) return;
        var root = SelectedRoot;
        var count = MissingCount;

        var answer = MessageBox.Show(
            $"{count} Mod(s) sind nicht mehr im Ordner „{root.DisplayName}\".\n\n" +
            "Sie wandern in den Papierkorb - Bewertungen, Tags und Kommentare bleiben " +
            "erhalten und lassen sich von dort wiederherstellen. Auf der Festplatte wird " +
            "nichts gelöscht, die Ordner sind ja bereits weg.\n\n" +
            "Achtung: Wenn der Mod-Ordner gerade noch synchronisiert wird, sind die Mods " +
            "vielleicht nur noch nicht angekommen. Im Zweifel erst die Synchronisierung " +
            "abwarten und neu scannen.",
            "Fehlende Mods aufräumen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var moved = await Task.Run(() => _library.TrashMissing(root.Id)).ConfigureAwait(true);
            StatusText = $"{moved} fehlende Mod(s) in den Papierkorb verschoben";
            ShowMissingOnly = false;
            await RefreshMissingCountAsync().ConfigureAwait(true);
            RefreshMods();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TrashMissing failed");
            MessageBox.Show(ex.Message, "Aufräumen fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    partial void OnSelectedRootChanged(RootInfo? value) => _ = ReloadRootAsync(value);

    // All of these honour _suppressFilterRefresh so applying a smart collection, which
    // sets several of them at once, reloads the gallery once instead of four times.
    /// <summary>The category hierarchy shown in the sidebar.</summary>
    public ObservableCollection<CategoryNodeViewModel> CategoryTree { get; } = new();

    [ObservableProperty] private CategoryNodeViewModel? _selectedCategoryNode;

    /// <summary>Label above the gallery: the picked folder, or the whole library.</summary>
    public string CategoryHeader => SelectedCategoryNode?.FullPath.Replace(
        System.IO.Path.DirectorySeparatorChar.ToString(), " › ") ?? "Alle Mods";

    partial void OnSelectedCategoryNodeChanged(CategoryNodeViewModel? value)
    {
        OnPropertyChanged(nameof(CategoryHeader));

        // The flat selection stays in step for everything that still works on a single
        // category (import, the category manager, the move menu).
        SelectedCategory = value?.OwnCategoryId is { } id
            ? Categories.FirstOrDefault(c => c.Id == id)
            : null;

        if (!_suppressFilterRefresh) RefreshMods();
    }

    /// <summary>
    /// Turns the flat category rows into a tree by splitting their stored path. A library
    /// in fixed mode has no separators, so it simply yields one level - the sidebar looks
    /// exactly as before.
    /// </summary>
    private void RebuildCategoryTree()
    {
        CategoryTree.Clear();

        var byPath = new Dictionary<string, CategoryNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var cat in Categories.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var segments = cat.Name.Split(
                new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            CategoryNodeViewModel? parent = null;
            var path = "";

            for (var i = 0; i < segments.Length; i++)
            {
                path = i == 0
                    ? segments[i]
                    : path + System.IO.Path.DirectorySeparatorChar + segments[i];

                if (!byPath.TryGetValue(path, out var node))
                {
                    node = new CategoryNodeViewModel(segments[i], path);
                    byPath[path] = node;
                    if (parent is null) CategoryTree.Add(node);
                    else parent.Children.Add(node);
                }
                parent = node;
            }

            // Only the deepest level carries the actual category row.
            parent!.OwnCategoryId = cat.Id;
            parent.OwnModCount = cat.ModCount;
        }

        OnPropertyChanged(nameof(CategoryTree));
    }

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        if (!_suppressFilterRefresh) RefreshMods();
    }

    partial void OnSelectedSortChanged(SortOption value)
    {
        if (!_suppressFilterRefresh) RefreshMods();
    }

    partial void OnMinRatingChanged(int value)
    {
        if (!_suppressFilterRefresh) RefreshMods();
    }

    // ---- search debounce ----
    //
    // Every keystroke used to run the full gallery query plus a rebuild of every card.
    // Wait until typing settles before touching the database.
    private DispatcherTimer? _searchDebounce;

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressFilterRefresh) return;
        _searchDebounce ??= CreateSearchDebounce();
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private DispatcherTimer CreateSearchDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshMods();
        };
        return timer;
    }

    public void RefreshMods() => _ = RefreshModsAsync();

    /// <summary>Cancels an in-flight refresh when a newer one supersedes it.</summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>The cards behind the current filter, kept so the folder view can be built on demand.</summary>
    private IReadOnlyList<ModCard> _currentCards = Array.Empty<ModCard>();

    public async Task RefreshModsAsync()
    {
        // Cancel the previous refresh but let it dispose its own token source in its own
        // finally block: Npgsql still holds registrations on that token, and disposing it
        // from here races with them.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCts, cts)?.Cancel();

        // Every exit path has to clear the field before disposing, or the next refresh
        // cancels a disposed source and dies with ObjectDisposedException — silently,
        // since RefreshMods() discards the task.
        try
        {
            if (SelectedRoot is null)
            {
                Mods.Clear();
                FolderTree.Clear();
                _currentCards = Array.Empty<ModCard>();
                VisibleModCount = 0;
                return;
            }

            var cards = await _library.GetModsAsync(BuildQuery(), cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

            // Keep the cards that survived the Penumbra filter, not the raw query result:
            // the folder view builds from this list and must agree with the gallery.
            var visible = new List<ModCard>(cards.Count);

            Mods.Clear();
            foreach (var card in cards)
            {
                var vm = new ModCardViewModel(card, _thumbnails);
                vm.RatingChangeRequested += OnRatingChangeRequested;
                vm.ImagesDroppedOnCard += OnImagesDroppedOnCard;
                ApplyPenumbraToCard(vm, card);

                if (!PassesPenumbraFilter(vm)) continue;
                Mods.Add(vm);
                visible.Add(card);
            }

            _currentCards = visible;
            VisibleModCount = Mods.Count;

            // These are new card view models, so re-apply who is looking at what.
            RefreshPresence();
            StatusText = $"{VisibleModCount} Mods";

            // The folder tree is only built when that view is actually showing. It used to
            // be rebuilt on every refresh, decoding a second thumbnail for every mod.
            FolderTree.Clear();
            if (IsFolderView) BuildFolderTree();
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "RefreshMods failed");
            StatusText = "Laden fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            Interlocked.CompareExchange(ref _refreshCts, null, cts);
            cts.Dispose();
        }
    }

    /// <summary>Assembles the current sidebar/toolbar state into one query.</summary>
    private ModQuery BuildQuery() => new()
    {
        RootId = SelectedRoot!.Id,
        // A parent node means "this folder and everything below it".
        CategoryId = null,
        CategoryIds = SelectedCategoryNode?.AllCategoryIds ?? Array.Empty<long>(),
        SearchText = SearchText,
        Sort = SelectedSort?.Value ?? ModSort.CategoryThenName,
        MinRating = MinRating,
        // Included tags go into the AND or the OR bucket depending on the toggle; the
        // excluded ones always mean "must not carry".
        TagsAll = TagMode == TagFilterMode.And ? IncludedTagIds() : Array.Empty<long>(),
        TagsAny = TagMode == TagFilterMode.Or ? IncludedTagIds() : Array.Empty<long>(),
        TagsNone = TagFilters.Where(t => t.State == TagFilterState.Exclude).Select(t => t.Id).ToArray(),
        OnlyUntagged = OnlyUntagged,
        Missing = ShowMissingOnly ? MissingFilter.Only : MissingFilter.Hide
    };

    private long[] IncludedTagIds() =>
        TagFilters.Where(t => t.State == TagFilterState.Include).Select(t => t.Id).ToArray();

    public void ReloadTags() => _ = ReloadTagsAsync();

    /// <summary>
    /// Reloads the tag vocabulary, preserving whatever the user had selected in the filter
    /// so renaming or recolouring a tag elsewhere does not silently reset the gallery.
    /// </summary>
    public async Task ReloadTagsAsync()
    {
        try
        {
            var tags = await _tagSvc.GetAllTagsAsync().ConfigureAwait(true);

            var previous = TagFilters
                .Where(t => t.State != TagFilterState.Off)
                .ToDictionary(t => t.Id, t => t.State);

            foreach (var existing in TagFilters) existing.StateCycled -= OnTagFilterCycled;
            TagFilters.Clear();
            AllTags.Clear();

            foreach (var tag in tags)
            {
                AllTags.Add(tag);

                var vm = new TagFilterViewModel(tag);
                if (previous.TryGetValue(tag.Id, out var state)) vm.State = state;
                vm.StateCycled += OnTagFilterCycled;
                TagFilters.Add(vm);
            }

            OnPropertyChanged(nameof(TagFilterSummary));
            OnPropertyChanged(nameof(HasTagFilter));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tags could not be loaded");
        }
    }

    private void OnTagFilterCycled(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TagFilterSummary));
        OnPropertyChanged(nameof(HasTagFilter));
        RefreshMods();
    }

    [RelayCommand]
    private void ToggleTagFilter(TagFilterViewModel? tag) => tag?.Cycle();

    [RelayCommand]
    private void ClearTagFilter()
    {
        var changed = OnlyUntagged || TagFilters.Any(t => t.State != TagFilterState.Off);
        if (!changed) return;

        _suppressFilterRefresh = true;
        try
        {
            OnlyUntagged = false;
            foreach (var t in TagFilters) t.State = TagFilterState.Off;
        }
        finally
        {
            _suppressFilterRefresh = false;
        }

        OnPropertyChanged(nameof(TagFilterSummary));
        OnPropertyChanged(nameof(HasTagFilter));
        RefreshMods();
    }

    [RelayCommand]
    private void ToggleTagMode() =>
        TagMode = TagMode == TagFilterMode.And ? TagFilterMode.Or : TagFilterMode.And;

    [RelayCommand]
    private void OpenTagManager()
    {
        var vm = new TagManagerViewModel(_tagSvc);
        var window = new Views.TagManagerWindow(vm) { Owner = Application.Current.MainWindow };
        vm.TagsChanged += (_, _) => _ = ReloadAfterTagChangeAsync();
        window.ShowDialog();
        _ = ReloadAfterTagChangeAsync();
    }

    private async Task ReloadAfterTagChangeAsync()
    {
        await ReloadTagsAsync().ConfigureAwait(true);
        await RefreshModsAsync().ConfigureAwait(true);
    }

    // ---- bulk tagging ----
    //
    // The selection is what makes the filter useful: without a way to tag many mods at
    // once there is nothing to filter on.

    /// <summary>Mods the user has ticked, or the single focused card as a fallback.</summary>
    private List<long> TargetModIds()
    {
        var selected = Mods.Where(m => m.IsSelected).Select(m => m.Id).ToList();
        if (selected.Count > 0) return selected;
        return SelectedMod is null ? new List<long>() : new List<long> { SelectedMod.Id };
    }

    [RelayCommand]
    private async Task AddTagToSelection(TagInfo? tag)
    {
        if (tag is null) return;
        var ids = TargetModIds();
        if (ids.Count == 0) return;

        try
        {
            var added = await Task.Run(() => _tagSvc.AddTagToMods(tag.Id, ids)).ConfigureAwait(true);
            Toasts?.Show("Tag hinzugefügt",
                $"„{tag.Name}“ auf {added} von {ids.Count} Mods", colorHex: tag.ColorHex ?? "#7A5CFA");
            await ReloadAfterTagChangeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Tag konnte nicht gesetzt werden",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RemoveTagFromSelection(TagInfo? tag)
    {
        if (tag is null) return;
        var ids = TargetModIds();
        if (ids.Count == 0) return;

        try
        {
            var removed = await Task.Run(() => _tagSvc.RemoveTagFromMods(tag.Id, ids)).ConfigureAwait(true);
            Toasts?.Show("Tag entfernt", $"„{tag.Name}“ von {removed} Mods", colorHex: "#9E9E9E");
            await ReloadAfterTagChangeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Tag konnte nicht entfernt werden",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Creates a tag from a typed name and applies it to the selection in one step.</summary>
    [RelayCommand]
    private async Task CreateAndApplyTag()
    {
        var ids = TargetModIds();
        if (ids.Count == 0) return;

        var dialog = new Views.PromptDialog("Tag anlegen und zuweisen",
            $"Tag für {ids.Count} Mod(s):");
        if (Application.Current.MainWindow is { } owner) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        var name = (dialog.ResultText ?? "").Trim();
        if (name.Length == 0) return;

        try
        {
            var added = await Task.Run(() =>
            {
                var id = _tagSvc.CreateTag(name);
                return (Id: id, Count: _tagSvc.AddTagToMods(id, ids));
            }).ConfigureAwait(true);

            Toasts?.Show("Tag angelegt",
                $"„{name}“ auf {added.Count} von {ids.Count} Mods",
                colorHex: TagService.DefaultColorFor(name));
            await ReloadAfterTagChangeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Tag konnte nicht angelegt werden",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BuildFolderTree()
    {
        FolderTree.Clear();
        foreach (var group in _currentCards.GroupBy(c => c.CategoryName).OrderBy(g => g.Key))
        {
            var catNode = new FolderNodeViewModel(group.Key, "Category");
            foreach (var card in group.OrderBy(c => c.FolderName))
            {
                catNode.Children.Add(new FolderNodeViewModel(
                    card.FolderName, "Mod", card.Id, _thumbnails, card.PrimaryImageAbsPath));
            }
            FolderTree.Add(catNode);
        }
    }

    partial void OnIsFolderViewChanged(bool value)
    {
        if (value && FolderTree.Count == 0) BuildFolderTree();
    }

    private void OnRatingChangeRequested(object? sender, int rating)
    {
        if (sender is not ModCardViewModel vm) return;
        _ = SetRatingAsync(vm.Id, rating);
    }

    private async Task SetRatingAsync(long modId, int rating)
    {
        try { await _library.SetRatingAsync(modId, rating).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Setting rating for mod {Id} failed", modId); }
    }

    private void OnImagesDroppedOnCard(object? sender, string[] files)
    {
        if (sender is not ModCardViewModel vm) return;
        var targetFolder = vm.Model.FolderAbsPath;
        if (!Directory.Exists(targetFolder)) return;

        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
        bool any = false;
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!imageExtensions.Contains(ext)) continue;

            var stem = vm.FolderName;
            var dest = Path.Combine(targetFolder, stem + ext);
            int i = 2;
            while (File.Exists(dest))
                dest = Path.Combine(targetFolder, $"{stem} ({i++}){ext}");

            try
            {
                File.Copy(file, dest);
                any = true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to copy image {src} to {dest}", file, dest);
            }
        }

        if (any && SelectedRoot is not null)
        {
            // The card may already be showing a cached decode of the previous preview.
            _thumbnails.Invalidate(vm.Model.PrimaryImageAbsPath);

            var rootId = SelectedRoot.Id;
            _ = Task.Run(() =>
            {
                try { _scanner.Scan(rootId); }
                catch (Exception ex) { _log.LogError(ex, "Rescan after image drop failed"); }
            }).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(RefreshAfterScan);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// True for a database timeout at any depth. Checked by type rather than by message so
    /// it does not depend on Npgsql's wording, and without pulling Npgsql into the UI layer.
    /// </summary>
    private static bool IsTimeout(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is TimeoutException) return true;
        return false;
    }

    private CancellationTokenSource? _scanCts;

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (SelectedRoot is null) return;
        if (IsBusy)
        {
            _scanCts?.Cancel();
            return;
        }

        IsBusy = true;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var rootId = SelectedRoot.Id;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var lastReport = DateTime.UtcNow;
        var progress = new Progress<ScanProgress>(p =>
        {
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 200) return;
            lastReport = DateTime.UtcNow;
            var elapsed = sw.Elapsed;
            StatusText =
                $"{p.CurrentCategory} · {p.ModsDone}/{p.ModsTotal} · {p.FilesDone} Dateien · {elapsed.TotalSeconds:F0}s · {p.CurrentMod}";
        });

        try
        {
            var summary = await Task.Run(() => _scanner.Scan(rootId, progress, ct), ct);
            StatusText = $"Scan fertig: {summary.CategoriesSeen} Kat., {summary.ModsSeen} Mods, {summary.FilesSeen} Dateien in {summary.Duration.TotalSeconds:F1}s";
            if (summary.ModsMarkedMissing > 0)
                StatusText += $" · {summary.ModsMarkedMissing} nicht mehr im Ordner";
            _log.LogInformation("Rescan: {Mods} mods in {Sec}s",
                summary.ModsSeen, summary.Duration.TotalSeconds);
            await LoadAsync().ConfigureAwait(true);

            // A scan that found almost nothing is reported instead of acted on: with a
            // synced folder that is a transfer in progress far more often than a deletion.
            if (summary.MissingMarkSkipped)
            {
                MessageBox.Show(
                    $"Dieser Scan hat {summary.ModsNotFound} von bisher bekannten Mods nicht " +
                    "im Ordner gefunden - das ist so viel auf einmal, dass sie NICHT als " +
                    "fehlend markiert wurden.\n\n" +
                    "Wahrscheinlich läuft die Synchronisierung noch, oder die Bibliothek " +
                    "zeigt auf den falschen Ordner. Warte, bis Nextcloud fertig ist, und " +
                    "scanne dann erneut.",
                    "Sehr viele Mods fehlen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan abgebrochen.";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (ScanAlreadyRunningException ex)
        {
            StatusText = "Scan läuft bereits.";
            MessageBox.Show(ex.Message, "Scan läuft bereits",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            // Npgsql reports a command timeout as "Exception while reading from stream",
            // which tells the user nothing. It happens when another scan of the same root
            // is still writing.
            StatusText = "Zeitüberschreitung beim Scan.";
            MessageBox.Show(
                "Die Datenbank hat zu lange nicht geantwortet.\n\n" +
                "Meist läuft gerade ein zweiter Scan derselben Bibliothek - am anderen PC " +
                "oder automatisch nach einer Änderung im Ordner. Warte kurz und starte den " +
                "Scan erneut.",
                "Scan-Zeitüberschreitung", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (RootNotMappedException ex)
        {
            // Expected for a user who has not pointed this library at a local folder yet.
            StatusText = "Bibliothek nicht zugeordnet.";
            MessageBox.Show(ex.Message, "Nicht zugeordnet",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (DirectoryNotFoundException ex)
        {
            // The mapping exists but the folder is gone (drive not mounted, sync paused).
            StatusText = "Ordner nicht gefunden.";
            MessageBox.Show(ex.Message, "Ordner nicht gefunden",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rescan failed");
            StatusText = "Scan fehlgeschlagen: " + ex.Message;
            MessageBox.Show(ex.Message, "Scan fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    public void OpenDetail(ModCardViewModel? card)
    {
        if (card is null) return;
        OpenDetailById(card.Id);
    }

    [RelayCommand]
    public void OpenDetailById(long modId) => _ = OpenDetailByIdAsync(modId);

    /// <summary>
    /// Loads the mod's detail data before showing the window. This used to run inside the
    /// ModDetailViewModel constructor, straight from the mouse-down handler: a dozen
    /// sequential remote queries on the UI thread, and any one of them timing out threw
    /// out of a click handler as an unhandled dispatcher exception.
    /// </summary>
    public async Task OpenDetailByIdAsync(long modId)
    {
        if (modId <= 0) return;

        try
        {
            var vm = await _detailFactory.CreateAsync(modId).ConfigureAwait(true);
            var window = new ModDetailWindow(vm);
            vm.ModChanged += (_, _) => Load();
            window.Owner = Application.Current.MainWindow;

            // Tell presence we're now viewing this mod
            _ = Presence?.SetViewingModAsync(modId);
            window.Closed += (_, _) => _ = Presence?.SetViewingModAsync(null);

            window.Show();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opening mod {Id} failed", modId);
            Toasts?.Show("Mod konnte nicht geöffnet werden", ex.Message, colorHex: "#E5534B");
        }
    }

    [RelayCommand]
    private void OpenCategoryManager()
    {
        var vm = _sp.GetRequiredService<CategoryManagerViewModel>();
        var window = new CategoryManagerWindow(vm);
        vm.CategoriesChanged += (_, _) => Load();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenHealth()
    {
        var window = new HealthWindow(_health, SelectedRoot?.Id)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }

    [RelayCommand]
    private void OpenDuplicates()
    {
        var vm = new DuplicatesViewModel(
            _dupes,
            _sp.GetRequiredService<Core.Categories.CategoryService>(),
            _moveSvc,
            _sp.GetRequiredService<DeleteService>(),
            SelectedRoot?.Id);

        // Archiving or deleting a duplicate changes the library, so refresh behind it.
        vm.LibraryChanged += (_, _) => _ = ReloadAfterTagChangeAsync();

        var window = new DuplicatesWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>Who is signed in, for the sidebar.</summary>
    public string AccountLabel
    {
        get
        {
            var user = _sp.GetService<IUserContext>();
            if (user?.UserId is null) return "Offline-Modus";
            return user.DisplayName ?? user.Email ?? "angemeldet";
        }
    }

    public void RefreshAccountLabel() => OnPropertyChanged(nameof(AccountLabel));

    /// <summary>
    /// Signs out and offers the login again. Without this there was no way back to the
    /// login screen once a session had been remembered — you had to delete token.dat.
    /// </summary>
    [RelayCommand]
    private async Task SignOut()
    {
        var supabase = _sp.GetService<SupabaseClientProvider>();
        if (supabase is null || !supabase.IsConfigured)
        {
            MessageBox.Show("Ohne Supabase-Konfiguration läuft die App im Offline-Modus.",
                "Abmelden", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                "Abmelden und zum Login zurück?\n\n" +
                "Die Email bleibt gespeichert, beim nächsten Anmelden ist nur das Passwort nötig.",
                "Abmelden", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try { await supabase.SignOutAsync().ConfigureAwait(true); }
        catch (Exception ex) { _log.LogWarning(ex, "Sign-out failed"); }

        RefreshAccountLabel();

        var login = new Views.LoginWindow(supabase) { Owner = Application.Current.MainWindow };
        login.ShowDialog();

        RefreshAccountLabel();
        if (login.SignedIn) await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = _sp.GetRequiredService<RootSettingsViewModel>();
        var window = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (window.RootsChanged) Load();
    }

    [RelayCommand]
    private void OpenStats()
    {
        var statsSvc = _sp.GetRequiredService<StatsService>();
        var window = new StatsWindow(statsSvc, SelectedRoot?.Id)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }

    [RelayCommand]
    private void OpenActivityFeed()
    {
        var feed = _sp.GetRequiredService<Core.Queries.ActivityFeedService>();
        var window = new Views.ActivityFeedWindow(feed)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }

    public void ReloadPenumbra() => _ = ReloadPenumbraAsync();

    public async Task ReloadPenumbraAsync()
    {
        var penumbraSvc = _sp.GetRequiredService<PenumbraService>();
        var sync = _sp.GetRequiredService<PenumbraSyncService>();

        // Read() parses Penumbra's config off disk and Push/LoadAll are remote queries.
        // None of that belongs on the UI thread.
        var (snapshot, users) = await Task.Run(() =>
        {
            PenumbraSnapshot snap;
            try { snap = penumbraSvc.Read(); }
            catch { snap = new PenumbraSnapshot(); }

            try
            {
                if (snap.IsAvailable && sync.IsLoggedIn) sync.Push(snap);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Penumbra push failed"); }

            IReadOnlyList<PenumbraUserSnapshot> loaded;
            try { loaded = sync.LoadAll(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Penumbra pull failed");
                loaded = Array.Empty<PenumbraUserSnapshot>();
            }

            return (snap, loaded);
        }).ConfigureAwait(true);

        _penumbra = snapshot;
        _penumbraUsers = users;

        OnPropertyChanged(nameof(PenumbraAvailable));
        OnPropertyChanged(nameof(PenumbraUsersText));
        OnPropertyChanged(nameof(PenumbraHasStale));
        RebuildCollectionFilters();
    }

    private void RebuildCollectionFilters()
    {
        var current = PenumbraCollectionFilter;
        PenumbraCollectionFilters.Clear();
        PenumbraCollectionFilters.Add(AllCollectionsOption);

        // Self first, then others, alphabetical inside each user.
        var groups = _penumbraUsers
            .OrderByDescending(u => u.IsSelf)
            .ThenBy(u => u.DisplayName);
        foreach (var u in groups)
        {
            foreach (var c in u.Snapshot.Collections.OrderBy(c => c.Name))
                PenumbraCollectionFilters.Add(FormatCollectionFilter(u, c.Name));
        }
        // If logged-out and we have a local snapshot only, fall back to plain names.
        if (PenumbraCollectionFilters.Count == 0 && _penumbra.IsAvailable)
            foreach (var c in _penumbra.Collections.OrderBy(c => c.Name))
                PenumbraCollectionFilters.Add(c.Name);

        // Preserve the selection if it survived the rebuild, otherwise fall back to "all"
        // rather than leaving a filter applied that no longer has a matching entry.
        PenumbraCollectionFilter =
            !string.IsNullOrEmpty(current) && PenumbraCollectionFilters.Contains(current)
                ? current
                : AllCollectionsOption;
    }

    private static string FormatCollectionFilter(PenumbraUserSnapshot u, string collectionName)
    {
        if (u.IsSelf) return $"{collectionName} (du)";

        // A day-old snapshot looks identical to a current one unless it says so.
        return u.IsStale
            ? $"{collectionName} ({u.DisplayName}, {u.AgeText})"
            : $"{collectionName} ({u.DisplayName})";
    }

    /// <summary>
    /// Who is sharing their Penumbra state and how fresh it is. Shown next to the filter,
    /// because acting on a stale "is enabled" is worse than knowing nothing.
    /// </summary>
    public string PenumbraUsersText
    {
        get
        {
            var others = _penumbraUsers.Where(u => !u.IsSelf).ToList();
            if (others.Count == 0) return "nur dein Stand";
            return string.Join(" · ", others.Select(u => $"{u.DisplayName}: {u.AgeText}"));
        }
    }

    public bool PenumbraHasStale => _penumbraUsers.Any(u => u.IsStale);

    private void ApplyPenumbraToCard(ModCardViewModel vm, ModCard card)
    {
        var active = new List<string>();
        var all = new List<string>();
        var status = PenumbraStatus.NotInstalled;

        // Walk all known users (self + remote). If we have no remote, _penumbraUsers
        // is empty and we fall back to the local snapshot under the synthetic "self".
        var sources = _penumbraUsers.Count > 0
            ? _penumbraUsers
            : (_penumbra.IsAvailable
                ? new[] { new PenumbraUserSnapshot { DisplayName = "du", IsSelf = true, Snapshot = _penumbra } }
                : Array.Empty<PenumbraUserSnapshot>());

        foreach (var u in sources)
        {
            // Every plausible match, not just the best one: the same mod often exists twice
            // in Penumbra ("… (2)", or a second variant under a hashed folder name) and the
            // enabled copy is not necessarily the one a single lookup would return.
            foreach (var entry in u.Snapshot.MatchesFor(card.FolderName, card.DisplayName))
            {
                if ((int)entry.Status > (int)status) status = entry.Status;
                foreach (var c in entry.ActiveInCollections) active.Add(FormatCollectionFilter(u, c));
                foreach (var c in entry.AllInCollections)    all.Add(FormatCollectionFilter(u, c));
            }
        }

        vm.PenumbraStatus = status;
        // Several matches can report the same collection; the filter only needs it once.
        vm.PenumbraActiveInCollections = active.Distinct().ToList();
        vm.PenumbraAllCollections = all.Distinct().ToList();
    }

    private bool PassesPenumbraFilter(ModCardViewModel vm)
    {
        if (PenumbraFilter == PenumbraFilterMode.Imported && vm.PenumbraStatus == PenumbraStatus.NotInstalled)
            return false;
        if (PenumbraFilter == PenumbraFilterMode.Active && vm.PenumbraStatus != PenumbraStatus.ActiveDefault)
            return false;
        if (!string.IsNullOrEmpty(PenumbraCollectionFilter)
            && PenumbraCollectionFilter != AllCollectionsOption
            && !vm.PenumbraAllCollections.Contains(PenumbraCollectionFilter))
            return false;
        return true;
    }

    [RelayCommand]
    private async Task RefreshPenumbra()
    {
        await ReloadPenumbraAsync().ConfigureAwait(true);
        await RefreshModsAsync().ConfigureAwait(true);
        var others = _penumbraUsers.Count(u => !u.IsSelf);
        Toasts?.Show("Penumbra",
            _penumbra.IsAvailable
                ? $"{_penumbra.Entries.Count} Mods · {_penumbra.Collections.Count(c => c.IsActive)} aktiv · {others} weitere User"
                : (_penumbraUsers.Count > 0
                    ? $"Lokal nicht gefunden · {_penumbraUsers.Count} User remote"
                    : "Nicht gefunden"),
            colorHex: (_penumbra.IsAvailable || _penumbraUsers.Count > 0) ? "#26A69A" : "#9E9E9E");
    }

    public void ReloadSmartCollections() => _ = ReloadSmartCollectionsAsync();

    public async Task ReloadSmartCollectionsAsync()
    {
        try
        {
            var svc = _sp.GetRequiredService<SmartCollectionService>();
            var list = await Task.Run(() => svc.List()).ConfigureAwait(true);
            SmartCollections.Clear();
            foreach (var c in list) SmartCollections.Add(c);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Smart collections could not be loaded");
        }
    }

    [RelayCommand]
    private void SaveSmartCollection()
    {
        var dlg = new Views.PromptDialog("Smart Collection speichern", "Name:");
        if (Application.Current.MainWindow is { } owner) dlg.Owner = owner;
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultText)) return;

        // The tag filter is part of what the user sees, so it has to be part of what gets
        // saved — otherwise the collection silently reproduces a different result set.
        var filter = new SmartCollectionFilter
        {
            CategoryId = SelectedCategory?.Id,
            MinRating = MinRating,
            Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            TagIds = TagFilters.Where(t => t.State == TagFilterState.Include).Select(t => t.Id).ToList(),
            ExcludedTagIds = TagFilters.Where(t => t.State == TagFilterState.Exclude).Select(t => t.Id).ToList(),
            TagsAnyMode = TagMode == TagFilterMode.Or,
            OnlyUntagged = OnlyUntagged
        };
        var svc = _sp.GetRequiredService<SmartCollectionService>();
        svc.Create(dlg.ResultText.Trim(), filter);
        ReloadSmartCollections();
        Toasts?.Show("Smart Collection", $"„{dlg.ResultText}" + "“ gespeichert", colorHex: "#7A5CFA");
    }

    [RelayCommand]
    private void ApplySmartCollection(SmartCollection? sc)
    {
        if (sc is null) return;
        var f = sc.Filter;

        // Apply everything under suppression, then refresh once.
        _suppressFilterRefresh = true;
        try
        {
            SearchText = f.Search ?? "";
            MinRating = f.MinRating;
            SelectedCategory = f.CategoryId is null
                ? null
                : Categories.FirstOrDefault(c => c.Id == f.CategoryId.Value);

            TagMode = f.TagsAnyMode ? TagFilterMode.Or : TagFilterMode.And;
            OnlyUntagged = f.OnlyUntagged;

            var include = f.TagIds.ToHashSet();
            var exclude = f.ExcludedTagIds.ToHashSet();
            foreach (var t in TagFilters)
            {
                t.State = include.Contains(t.Id) ? TagFilterState.Include
                        : exclude.Contains(t.Id) ? TagFilterState.Exclude
                        : TagFilterState.Off;
            }
        }
        finally
        {
            _suppressFilterRefresh = false;
        }

        OnPropertyChanged(nameof(TagFilterSummary));
        OnPropertyChanged(nameof(HasTagFilter));
        RefreshMods();
    }

    [RelayCommand]
    private void DeleteSmartCollection(SmartCollection? sc)
    {
        if (sc is null) return;
        if (MessageBox.Show($"Smart Collection „{sc.Name}“ löschen?", "Bestätigen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _sp.GetRequiredService<SmartCollectionService>().Delete(sc.Id);
        ReloadSmartCollections();
    }

    [RelayCommand]
    private async Task ExportHtmlCatalog()
    {
        if (SelectedRoot is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Zielordner für HTML-Katalog wählen" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var cards = await _library.GetModsAsync(BuildQuery()).ConfigureAwait(true);

            var exporter = _sp.GetRequiredService<Services.HtmlCatalogExporter>();
            var title = "FFXIV Mods – " + (SelectedRoot.DisplayName ?? "");
            await Task.Run(() => exporter.Export(dlg.FolderName, cards, title)).ConfigureAwait(true);
            var indexPath = System.IO.Path.Combine(dlg.FolderName, "index.html");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = indexPath,
                UseShellExecute = true
            });
            Toasts?.Show("Export fertig", $"{cards.Count} Mods exportiert", colorHex: "#26A69A");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Export fehlgeschlagen",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenHistory()
    {
        var window = _sp.GetRequiredService<Views.HistoryWindow>();
        window.Owner = Application.Current.MainWindow;
        window.Show();
    }

    [RelayCommand]
    private void OpenTrash()
    {
        var window = new TrashWindow(_library)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
        RefreshMods();
    }

    [RelayCommand]
    private void MoveSelectedToCategory(CategoryItemViewModel? target)
    {
        if (SelectedMod is null || target is null) return;
        if (target.Id == SelectedMod.Model.CategoryId) return;

        var plan = _moveSvc.CreatePlan(new[] { SelectedMod.Id }, target.Id);
        if (plan.Conflicts.Count > 0)
        {
            MessageBox.Show(string.Join("\n", plan.Conflicts), "Move conflict",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (plan.CanExecute)
        {
            _moveSvc.Execute(plan);
            RefreshMods();
            StatusText = $"Nach {target.Name} verschoben";
        }
    }

    [RelayCommand]
    public void ImportFiles(IEnumerable<string>? files)
    {
        if (files is null || SelectedRoot is null) return;
        var list = files.Where(File.Exists).ToList();
        if (list.Count == 0) return;

        var rootId = SelectedRoot.Id;
        var importSvc = _sp.GetRequiredService<Core.Import.ImportService>();
        var dialog = new Dialogs.ImportDialog(importSvc, _library, rootId, list)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            // rootId is captured, not re-read: the selection can change between the
            // dialog closing and this task starting.
            _ = Task.Run(() =>
            {
                try { _scanner.Scan(rootId); }
                catch (Exception ex) { _log.LogError(ex, "Rescan after import failed"); }
            }).ContinueWith(_ => Application.Current.Dispatcher.Invoke(Load), TaskScheduler.Default);
        }
    }

    [RelayCommand]
    public void BulkRename()
    {
        var selected = Mods.Where(m => m.IsSelected).ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show("Mindestens zwei Mods auswählen (Strg+Klick), um sammelweise umzubenennen.",
                "Bulk Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var svc = _sp.GetRequiredService<RenameService>();
        var dialog = new Dialogs.BulkRenameDialog(svc, selected.Select(m => m.Id).ToList(), _library)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            RefreshMods();
        }
    }

    [RelayCommand]
    private void ToggleViewMode() => IsFolderView = !IsFolderView;

    [RelayCommand]
    public void OpenDetailFromNode(FolderNodeViewModel? node)
    {
        if (node?.ModId is null) return;
        var card = Mods.FirstOrDefault(m => m.Id == node.ModId.Value);
        if (card is not null) OpenDetail(card);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var m in Mods) m.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    public event EventHandler? FocusSearchRequested;

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task ReloadFromDb()
    {
        StatusText = "Lade neu aus der Datenbank…";
        await LoadAsync().ConfigureAwait(true);
        StatusText = $"{VisibleModCount} Mods · neu aus der Datenbank geladen";
    }

    public void RefreshSelectedCount() => OnPropertyChanged(nameof(SelectedCount));
}

public sealed record SortOption(ModSort Value, string Label)
{
    public override string ToString() => Label;
}
