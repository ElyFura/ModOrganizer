using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModOrganizer.App.Services;
using ModOrganizer.App.Views;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Comments;
using ModOrganizer.Core.Links;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Penumbra;
using ModOrganizer.Core.Pmp;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.App.ViewModels;

public sealed partial class ModDetailViewModel : ObservableObject
{
    private readonly ModLibraryService _library;
    private readonly ThumbnailCache _thumbnails;
    private readonly ModDetailQuery _detailQuery;
    private readonly TagService _tagSvc;
    private readonly LinkService _linkSvc;
    private readonly CommentService _commentSvc;
    private readonly RenameService _renameSvc;
    private readonly DeleteService _deleteSvc;
    private readonly PenumbraService? _penumbra;
    private readonly PenumbraSyncService? _penumbraSync;

    public long ModId { get; }
    public string FolderPath { get; private set; } = "";

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _folderName = "";
    [ObservableProperty] private string _categoryName = "";
    [ObservableProperty] private ImageSource? _primaryImage;
    [ObservableProperty] private string _commentMarkdown = "";
    [ObservableProperty] private string _commentHtml = "";
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _newLinkUrl = "";
    [ObservableProperty] private string _newFolderName = "";

    [ObservableProperty] private int _rating;

    public ObservableCollection<string> ImagePaths { get; } = new();
    public ObservableCollection<FileEntry> Files { get; } = new();
    public ObservableCollection<TagEntry> Tags { get; } = new();
    public ObservableCollection<TagEntry> AllTags { get; } = new();
    public ObservableCollection<LinkInfo> Links { get; } = new();
    public ObservableCollection<string> LinkSuggestions { get; } = new();
    public ObservableCollection<PmpInspector.PmpDetail> PmpDetails { get; } = new();

    [ObservableProperty] private PenumbraStatus _penumbraStatus;
    public ObservableCollection<string> PenumbraActiveInCollections { get; } = new();
    public ObservableCollection<string> PenumbraOtherCollections { get; } = new();
    public bool PenumbraVisible => PenumbraStatus != PenumbraStatus.NotInstalled;
    public string PenumbraStatusText => PenumbraStatus switch
    {
        PenumbraStatus.ActiveDefault => "● aktiv",
        PenumbraStatus.Imported => "importiert",
        _ => ""
    };
    partial void OnPenumbraStatusChanged(PenumbraStatus value)
    {
        OnPropertyChanged(nameof(PenumbraVisible));
        OnPropertyChanged(nameof(PenumbraStatusText));
    }

    public CommentThreadViewModel Thread { get; }

    public event EventHandler? ModChanged;

    /// <summary>Wires up the mention-broadcast post-hook. Called by the factory.</summary>
    public void AttachMentionDispatch(Func<long, string, Task> dispatch)
    {
        // CommentThreadViewModel is built in ctor; we replace the no-op afterPost
        // by replacing the Thread reference's hook. Simplest: tell Thread directly.
        Thread.SetAfterPost(dispatch);
    }

    /// <summary>
    /// Pure wiring — no database access. The data arrives via <see cref="ReloadAsync"/>,
    /// which the factory awaits before the window is shown. Loading here is what let a
    /// query timeout surface as an unhandled exception out of a mouse-click handler.
    /// </summary>
    public ModDetailViewModel(long modId, ModLibraryService library,
        ModDetailQuery detailQuery,
        ThumbnailCache thumbnails, TagService tagSvc, LinkService linkSvc,
        CommentService commentSvc, ModCommentService threadSvc,
        RenameService renameSvc, DeleteService deleteSvc, IUserContext user,
        PenumbraService? penumbra = null, PenumbraSyncService? penumbraSync = null)
    {
        _penumbraSync = penumbraSync;
        _library = library;
        _detailQuery = detailQuery;
        _thumbnails = thumbnails;
        _tagSvc = tagSvc;
        _linkSvc = linkSvc;
        _commentSvc = commentSvc;
        _renameSvc = renameSvc;
        _deleteSvc = deleteSvc;
        _penumbra = penumbra;
        ModId = modId;
        Thread = new CommentThreadViewModel(modId, threadSvc, user);
    }

    private async Task RefreshPenumbraAsync()
    {
        PenumbraActiveInCollections.Clear();
        PenumbraOtherCollections.Clear();
        PenumbraStatus = PenumbraStatus.NotInstalled;

        // Source list: every user's snapshot we know about. Falls back to a synthetic
        // "self" entry holding the local snapshot when nothing is in the DB.
        // LoadAll() is a remote query and Read() parses a file, so both run off-thread.
        var sync = _penumbraSync;
        var local = _penumbra;
        var users = await Task.Run(() =>
        {
            var list = new List<PenumbraUserSnapshot>();
            try { if (sync is not null) list.AddRange(sync.LoadAll()); } catch { }
            if (list.Count == 0 && local is not null)
            {
                try
                {
                    var snapshot = local.Read();
                    if (snapshot.IsAvailable)
                        list.Add(new PenumbraUserSnapshot { DisplayName = "du", IsSelf = true, Snapshot = snapshot });
                }
                catch { }
            }
            return list;
        }).ConfigureAwait(true);

        var status = PenumbraStatus.NotInstalled;
        foreach (var u in users.OrderByDescending(x => x.IsSelf).ThenBy(x => x.DisplayName))
        {
            var entry = u.Snapshot.Lookup(FolderName)
                ?? (string.IsNullOrEmpty(DisplayName) ? null : u.Snapshot.Lookup(DisplayName));
            if (entry is null) continue;

            if ((int)entry.Status > (int)status) status = entry.Status;
            var prefix = u.IsSelf ? "du" : u.DisplayName;
            foreach (var c in entry.ActiveInCollections)
                PenumbraActiveInCollections.Add($"{c} ({prefix})");
            foreach (var c in entry.AllInCollections.Except(entry.ActiveInCollections))
                PenumbraOtherCollections.Add($"{c} ({prefix})");
        }
        PenumbraStatus = status;
    }

    [RelayCommand]
    private void SetRating(object? parameter)
    {
        if (parameter is null) return;
        int value;
        if (parameter is int i) value = i;
        else if (!int.TryParse(parameter.ToString(), out value)) return;
        if (value == Rating) value = 0;
        Rating = value;
        _ = PersistRatingAsync(value);
    }

    private async Task PersistRatingAsync(int value)
    {
        try
        {
            await _library.SetRatingAsync(ModId, value).ConfigureAwait(true);
            ModChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rating konnte nicht gespeichert werden",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenLightbox(object? parameter)
    {
        if (ImagePaths.Count == 0) return;
        int startIdx = 0;
        if (parameter is string path)
        {
            var idx = ImagePaths.IndexOf(path);
            if (idx >= 0) startIdx = idx;
        }
        var win = new LightboxWindow(ImagePaths.ToList(), startIdx)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                 ?? Application.Current.MainWindow
        };
        win.Show();
    }

    public void Reload() => _ = ReloadAsync();

    /// <summary>Everything the view binds to, fetched in one round trip and applied here.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var snapshot = await _detailQuery.LoadAsync(ModId, ct).ConfigureAwait(true);
        if (snapshot is null) return;

        Rating = snapshot.Rating;
        FolderName = snapshot.FolderName;
        DisplayName = snapshot.DisplayName ?? snapshot.FolderName;
        CategoryName = snapshot.CategoryName;
        FolderPath = Path.Combine(snapshot.RootPath, snapshot.CategoryName, snapshot.FolderName);
        NewFolderName = snapshot.FolderName;

        Files.Clear();
        ImagePaths.Clear();
        foreach (var f in snapshot.Files)
        {
            var abs = Path.Combine(FolderPath, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Files.Add(new FileEntry(f.RelativePath, (ModOrganizer.Core.Models.ModFileKind)f.Kind, f.SizeBytes, abs));
            if (f.Kind == (int)ModOrganizer.Core.Models.ModFileKind.Image)
                ImagePaths.Add(abs);
        }

        PmpDetails.Clear();
        foreach (var d in snapshot.PmpDetails)
        {
            PmpDetails.Add(d);
            if (ImagePaths.Count == 0 && d.PreviewCachePath is not null && File.Exists(d.PreviewCachePath))
                ImagePaths.Add(d.PreviewCachePath);
        }

        CommentMarkdown = snapshot.CommentMarkdown;
        UpdateCommentHtml();

        Tags.Clear();
        foreach (var t in snapshot.Tags) Tags.Add(new TagEntry(t.Id, t.Name, t.ColorHex));

        AllTags.Clear();
        foreach (var t in snapshot.AllTags) AllTags.Add(new TagEntry(t.Id, t.Name, t.ColorHex));

        Links.Clear();
        foreach (var l in snapshot.Links) Links.Add(l);

        // Derived from the file list we already have — no extra query needed.
        LinkSuggestions.Clear();
        foreach (var suggestion in LinkService.SuggestLinksFromFileNames(
                     snapshot.Files.Where(f => f.Kind == (int)ModOrganizer.Core.Models.ModFileKind.Image)
                                   .Select(f => f.RelativePath)))
        {
            LinkSuggestions.Add(suggestion);
        }

        // Images decode off-thread; the hero image lands a moment after the text.
        var hero = ImagePaths.Count > 0 ? ImagePaths[0] : null;
        PrimaryImage = null;
        PrimaryImageLarge = null;
        if (hero is not null) _ = LoadHeroImagesAsync(hero);

        await Task.WhenAll(
            RefreshPenumbraAsync(),
            Thread.ReloadAsync()).ConfigureAwait(true);
    }

    private async Task LoadHeroImagesAsync(string path)
    {
        try
        {
            var small = await _thumbnails.GetAsync(path, ThumbTier.Card).ConfigureAwait(true);
            if (small is not null) PrimaryImage = small;

            var large = await _thumbnails.GetAsync(path, ThumbTier.Large).ConfigureAwait(true);
            if (large is not null) PrimaryImageLarge = large;
        }
        catch { /* a broken preview just stays blank */ }
    }

    [ObservableProperty] private ImageSource? _primaryImageLarge;

    partial void OnCommentMarkdownChanged(string value) => UpdateCommentHtml();

    private void UpdateCommentHtml()
    {
        try
        {
            CommentHtml = Markdig.Markdown.ToHtml(CommentMarkdown ?? "");
        }
        catch { CommentHtml = CommentMarkdown ?? ""; }
    }

    [RelayCommand]
    private async Task SaveComment()
    {
        var markdown = CommentMarkdown;
        await RunMutationAsync(() => _commentSvc.SetComment(ModId, markdown), reload: false);
    }

    [RelayCommand]
    private async Task AddTag()
    {
        if (string.IsNullOrWhiteSpace(NewTagName)) return;
        var name = NewTagName.Trim();
        NewTagName = "";
        await RunMutationAsync(() =>
        {
            var id = _tagSvc.CreateTag(name);
            _tagSvc.AddTagToMods(id, new[] { ModId });
        });
    }

    [RelayCommand]
    private async Task RemoveTag(TagEntry tag) =>
        await RunMutationAsync(() => _tagSvc.RemoveTagFromMods(tag.Id, new[] { ModId }));

    [RelayCommand]
    private async Task AttachExistingTag(TagEntry tag) =>
        await RunMutationAsync(() => _tagSvc.AddTagToMods(tag.Id, new[] { ModId }));

    [RelayCommand]
    private async Task AddLink()
    {
        if (string.IsNullOrWhiteSpace(NewLinkUrl)) return;
        var url = NewLinkUrl.Trim();
        NewLinkUrl = "";
        await RunMutationAsync(() => _linkSvc.AddLink(ModId, url), notifyChanged: false);
    }

    [RelayCommand]
    private async Task AddSuggestedLink(string url) =>
        await RunMutationAsync(() => _linkSvc.AddLink(ModId, url), notifyChanged: false);

    [RelayCommand]
    private async Task RemoveLink(LinkInfo link) =>
        await RunMutationAsync(() => _linkSvc.RemoveLink(link.Id), notifyChanged: false);

    /// <summary>
    /// Runs a write off the UI thread, then refreshes. The mutation services are still
    /// synchronous; keeping them behind Task.Run is what stops a tag click from freezing
    /// the window for a round trip.
    /// </summary>
    private async Task RunMutationAsync(Action mutation, bool reload = true, bool notifyChanged = true)
    {
        try
        {
            await Task.Run(mutation).ConfigureAwait(true);
            if (reload) await ReloadAsync().ConfigureAwait(true);
            if (notifyChanged) ModChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Änderung fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenLink(LinkInfo link)
    {
        try { Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (Directory.Exists(FolderPath))
            Process.Start("explorer.exe", $"\"{FolderPath}\"");
    }

    [RelayCommand]
    private async Task RenameMod()
    {
        if (string.IsNullOrWhiteSpace(NewFolderName) || NewFolderName == FolderName) return;
        var target = NewFolderName.Trim();

        try
        {
            var plan = await Task.Run(() => _renameSvc.CreatePlan(ModId, target)).ConfigureAwait(true);
            if (plan.Conflicts.Count > 0)
            {
                MessageBox.Show(string.Join("\n", plan.Conflicts.Select(c => $"{c.Reason}: {c.Path}")),
                    "Rename conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await Task.Run(() => _renameSvc.Execute(plan)).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            ModChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteMod()
    {
        var result = MessageBox.Show(
            $"Send '{FolderName}' to the Recycle Bin?\n\nPath: {FolderPath}",
            "Delete mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var deleted = await Task.Run(() => _deleteSvc.DeleteMod(ModId)).ConfigureAwait(true);
            if (deleted)
            {
                ModChanged?.Invoke(this, EventArgs.Empty);
                DeletedRequestingClose?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // DeleteMod returns false when archiving or the recycle-bin call failed.
                // Staying silent looks exactly like the dialog doing nothing at all.
                MessageBox.Show(
                    $"'{FolderName}' konnte nicht in den Papierkorb verschoben werden.\n\n" +
                    "Ist der Ordner in einem anderen Programm geöffnet? Details im Log unter " +
                    @"%LOCALAPPDATA%\FFXIVModOrganizer\logs.",
                    "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public event EventHandler? DeletedRequestingClose;
}

public sealed record TagEntry(long Id, string Name, string? ColorHex);
public sealed record FileEntry(string RelativePath, ModOrganizer.Core.Models.ModFileKind Kind, long SizeBytes, string AbsolutePath);
