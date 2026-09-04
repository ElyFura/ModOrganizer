using Dapper;
using ModOrganizer.App.ViewModels;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Comments;
using ModOrganizer.Core.Links;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Penumbra;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Storage;
using ModOrganizer.Core.Tagging;

namespace ModOrganizer.App.Services;

public sealed class ModDetailViewModelFactory
{
    private readonly DatabaseStore _store;
    private readonly ModLibraryService _library;
    private readonly ThumbnailCache _thumbnails;
    private readonly ModDetailQuery _detailQuery;
    private readonly TagService _tagSvc;
    private readonly LinkService _linkSvc;
    private readonly CommentService _commentSvc;
    private readonly ModCommentService _threadSvc;
    private readonly MentionResolver _mentionResolver;
    private readonly MentionBroadcaster _mentionBroadcaster;
    private readonly RenameService _renameSvc;
    private readonly DeleteService _deleteSvc;
    private readonly IUserContext _user;
    private readonly PenumbraService _penumbra;
    private readonly PenumbraSyncService _penumbraSync;

    public ModDetailViewModelFactory(DatabaseStore store, ModLibraryService library,
        ModDetailQuery detailQuery, ThumbnailCache thumbnails,
        TagService tagSvc, LinkService linkSvc, CommentService commentSvc, ModCommentService threadSvc,
        MentionResolver mentionResolver, MentionBroadcaster mentionBroadcaster,
        RenameService renameSvc, DeleteService deleteSvc, IUserContext user,
        PenumbraService penumbra, PenumbraSyncService penumbraSync)
    {
        _detailQuery = detailQuery;
        _store = store; _library = library; _thumbnails = thumbnails;
        _tagSvc = tagSvc; _linkSvc = linkSvc; _commentSvc = commentSvc; _threadSvc = threadSvc;
        _mentionResolver = mentionResolver; _mentionBroadcaster = mentionBroadcaster;
        _renameSvc = renameSvc; _deleteSvc = deleteSvc; _user = user;
        _penumbra = penumbra;
        _penumbraSync = penumbraSync;
    }

    /// <summary>
    /// Builds the view model and loads its data before handing it back, so the caller can
    /// show a fully populated window and handle load failures itself.
    /// </summary>
    public async Task<ModDetailViewModel> CreateAsync(long modId, CancellationToken ct = default)
    {
        var vm = new ModDetailViewModel(modId, _library, _detailQuery, _thumbnails,
            _tagSvc, _linkSvc, _commentSvc, _threadSvc, _renameSvc, _deleteSvc, _user,
            _penumbra, _penumbraSync);
        vm.AttachMentionDispatch(DispatchMentionsAsync);

        await vm.ReloadAsync(ct).ConfigureAwait(true);

        // Fire-and-forget: nobody waits on the "last viewed" stamp, and it must never be
        // able to fail the act of opening a mod.
        _ = MarkViewedAsync(modId);

        return vm;
    }

    private async Task MarkViewedAsync(long modId)
    {
        try { await _library.SetLastViewedAsync(modId).ConfigureAwait(false); }
        catch { /* advisory only */ }
    }

    private async Task DispatchMentionsAsync(long modId, string body)
    {
        try
        {
            var targets = _mentionResolver.Resolve(body);
            if (targets.Count == 0) return;

            using var conn = _store.Open();
            var modName = conn.QuerySingleOrDefault<string>(
                "SELECT folder_name FROM mods WHERE id=@m", new { m = modId }) ?? $"Mod #{modId}";

            var snippet = body.Length > 120 ? body.Substring(0, 117) + "…" : body;
            var fromName = _user.DisplayName ?? _user.Email ?? "?";
            var fromId = _user.UserId?.ToString() ?? "";

            await _mentionBroadcaster.SendAsync(targets, new MentionPayload
            {
                FromUserId = fromId,
                FromName = fromName,
                ModId = modId,
                ModName = modName,
                Snippet = snippet
            });
        }
        catch { /* don't fail the post on dispatch errors */ }
    }
}
