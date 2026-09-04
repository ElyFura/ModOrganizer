using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ModOrganizer.App.Services;

namespace ModOrganizer.App.ViewModels;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    public string Name { get; }
    public string NodeKind { get; }
    public long? ModId { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    private readonly ThumbnailCache? _thumbnails;
    private readonly string? _imagePath;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    /// <summary>
    /// Same lazy pattern as the gallery card, but at the Tiny tier — these rows are 36 px,
    /// and the previous code decoded them at 512 px for every mod in the library whether
    /// the folder view was even open.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                BeginLoad();
            }
            return _thumbnail;
        }
        private set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public FolderNodeViewModel(string name, string kind, long? modId = null,
        ThumbnailCache? thumbnails = null, string? imagePath = null)
    {
        Name = name;
        NodeKind = kind;
        ModId = modId;
        _thumbnails = thumbnails;
        _imagePath = imagePath;
    }

    private void BeginLoad()
    {
        if (_thumbnails is null || string.IsNullOrEmpty(_imagePath)) return;

        var cached = _thumbnails.PeekMemory(_imagePath, ThumbTier.Tiny);
        if (cached is not null)
        {
            _thumbnail = cached;
            return;
        }

        _ = LoadAsync(_imagePath);
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            var img = await _thumbnails!.GetAsync(path, ThumbTier.Tiny).ConfigureAwait(false);
            if (img is null) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Thumbnail = img;
            else await dispatcher.InvokeAsync(() => Thumbnail = img);
        }
        catch { /* a missing preview just stays blank */ }
    }
}
