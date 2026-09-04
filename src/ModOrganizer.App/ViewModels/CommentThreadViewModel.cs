using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Comments;

namespace ModOrganizer.App.ViewModels;

public sealed partial class CommentThreadViewModel : ObservableObject
{
    private readonly ModCommentService _service;
    private readonly IUserContext _user;
    private readonly long _modId;
    private Func<long, string, Task>? _afterPost;

    public ObservableCollection<CommentBubbleViewModel> Comments { get; } = new();

    [ObservableProperty] private string _newCommentText = "";
    [ObservableProperty] private string _statusText = "";

    public CommentThreadViewModel(long modId, ModCommentService service, IUserContext user,
        Func<long, string, Task>? afterPost = null)
    {
        _modId = modId;
        _service = service;
        _user = user;
        _afterPost = afterPost;
        // No load here: ModDetailViewModel.ReloadAsync awaits ReloadAsync instead, so
        // opening a mod does not spend a round trip inside a constructor.
    }

    public void SetAfterPost(Func<long, string, Task> hook) => _afterPost = hook;

    public void Reload() => _ = ReloadAsync();

    public async Task ReloadAsync()
    {
        try
        {
            var entries = await Task.Run(() => _service.GetForMod(_modId)).ConfigureAwait(true);
            Comments.Clear();
            foreach (var e in entries)
                Comments.Add(new CommentBubbleViewModel(e, _user));
            StatusText = entries.Count == 0 ? "Noch keine Kommentare." : $"{entries.Count} Kommentar(e)";
        }
        catch (Exception ex)
        {
            StatusText = "Fehler: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task Post()
    {
        if (string.IsNullOrWhiteSpace(NewCommentText)) return;
        var body = NewCommentText.Trim();
        try
        {
            await Task.Run(() => _service.Post(_modId, body)).ConfigureAwait(true);
            NewCommentText = "";
            await ReloadAsync().ConfigureAwait(true);
            if (_afterPost is not null)
                await _afterPost(_modId, body);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Posten fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void Delete(CommentBubbleViewModel? bubble)
    {
        if (bubble is null) return;
        if (MessageBox.Show("Diesen Kommentar löschen?", "Löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _service.Delete(bubble.Id);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Löschen fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed class CommentBubbleViewModel
{
    public long Id { get; }
    public string AuthorName { get; }
    public string Initials { get; }
    public Brush AuthorBrush { get; }
    public string BodyMd { get; }
    public string TimeText { get; }
    public bool IsMine { get; }

    private static readonly string[] Palette =
    {
        "#7A5CFA", "#FF8A65", "#26A69A", "#FFB300", "#5C6BC0",
        "#EC407A", "#26C6DA", "#9CCC65", "#AB47BC", "#42A5F5"
    };

    public CommentBubbleViewModel(ModCommentEntry e, IUserContext currentUser)
    {
        Id = e.Id;
        AuthorName = e.UserDisplayName ?? "?";
        Initials = MakeInitials(AuthorName);
        AuthorBrush = MakeBrush(e.UserColor, AuthorName);
        BodyMd = e.BodyMd;
        TimeText = HumanTime(e.CreatedAt);
        IsMine = e.UserId == currentUser.UserId;
    }

    private static string MakeInitials(string name)
    {
        var parts = name.Split(new[] { ' ', '@', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return ($"{parts[0][0]}{parts[1][0]}").ToUpperInvariant();
    }

    private static Brush MakeBrush(string? color, string name)
    {
        var hex = !string.IsNullOrEmpty(color) ? color! : DerivedColor(name);
        try
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
            b.Freeze();
            return b;
        }
        catch { return Brushes.Gray; }
    }

    private static string DerivedColor(string name)
    {
        unchecked
        {
            int hash = 0;
            foreach (var c in name) hash = hash * 31 + c;
            return Palette[Math.Abs(hash) % Palette.Length];
        }
    }

    private static string HumanTime(DateTimeOffset when)
    {
        var diff = DateTimeOffset.Now - when;
        if (diff.TotalSeconds < 60) return "gerade";
        if (diff.TotalMinutes < 60) return $"vor {(int)diff.TotalMinutes} min";
        if (diff.TotalHours < 24) return $"vor {(int)diff.TotalHours} h";
        if (diff.TotalDays < 7) return $"vor {(int)diff.TotalDays} d";
        return when.ToString("yyyy-MM-dd HH:mm");
    }
}
