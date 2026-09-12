using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModOrganizer.App.Services;
using ModOrganizer.Core.Penumbra;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.ViewModels;

public sealed partial class ModCardViewModel : ObservableObject
{
    public ModCard Model { get; }

    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    /// <summary>
    /// Lazy by design: the getter is first hit when the virtualizing panel realizes this
    /// card's container, which is exactly when the image is needed. The decode runs on a
    /// background thread and raises a change notification when it lands, so nothing blocks
    /// the UI thread — the old code decoded every image in the constructor.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                BeginLoadThumbnail();
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

    private void BeginLoadThumbnail()
    {
        var path = Model.PrimaryImageAbsPath;
        if (string.IsNullOrEmpty(path)) return;

        // Already decoded? Assign inline so the first frame paints with the image.
        var cached = _thumbnails.PeekMemory(path, ThumbTier.Card);
        if (cached is not null)
        {
            _thumbnail = cached;
            return;
        }

        _ = LoadThumbnailAsync(path);
    }

    private async Task LoadThumbnailAsync(string path)
    {
        try
        {
            var img = await _thumbnails.GetAsync(path, ThumbTier.Card).ConfigureAwait(false);
            if (img is null) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Thumbnail = img;
            else await dispatcher.InvokeAsync(() => Thumbnail = img);
        }
        catch { /* a missing or corrupt preview just stays blank */ }
    }

    /// <summary>Forces a re-decode — used after a preview image is replaced on disk.</summary>
    public void ReloadThumbnail()
    {
        _thumbnails.Invalidate(Model.PrimaryImageAbsPath);
        _thumbnail = null;
        _thumbnailRequested = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    [ObservableProperty] private int _rating;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private PenumbraStatus _penumbraStatus;
    public List<string> PenumbraActiveInCollections { get; set; } = new();
    public List<string> PenumbraAllCollections { get; set; } = new();

    public bool PenumbraVisible => PenumbraStatus != PenumbraStatus.NotInstalled;
    public string PenumbraText => PenumbraStatus switch
    {
        PenumbraStatus.ActiveDefault => "● active",
        PenumbraStatus.Imported => "imported",
        _ => ""
    };
    public Brush PenumbraBrush => PenumbraStatus switch
    {
        PenumbraStatus.ActiveDefault => Brushes.MediumSeaGreen,
        PenumbraStatus.Imported => Brushes.SteelBlue,
        _ => Brushes.Transparent
    };

    partial void OnPenumbraStatusChanged(PenumbraStatus value)
    {
        OnPropertyChanged(nameof(PenumbraVisible));
        OnPropertyChanged(nameof(PenumbraText));
        OnPropertyChanged(nameof(PenumbraBrush));
    }

    public long Id => Model.Id;
    public string FolderName => Model.FolderName;
    public string DisplayName => Model.DisplayName ?? Model.FolderName;
    public string CategoryName => Model.CategoryName;
    public int PmpCount => Model.PmpCount;
    public int TtmpCount => Model.TtmpCount;
    public int ImageCount => Model.ImageCount;
    public bool IsMissing => Model.IsMissing;

    /// <summary>
    /// How long the folder has been gone. The age is the whole point: "seit heute" is a
    /// sync that may still be running, "seit 12 Tagen" is a real deletion.
    /// </summary>
    public string MissingText
    {
        get
        {
            if (Model.MissingSince is not { } since) return "nicht im Ordner";

            var days = (int)(DateTimeOffset.UtcNow - since).TotalDays;
            return days switch
            {
                <= 0 => "fehlt seit heute",
                1    => "fehlt seit gestern",
                _    => $"fehlt seit {days} Tagen"
            };
        }
    }

    public string MissingTooltip
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(Model.MissingByName)
                ? ""
                : $"\nBemerkt beim Scan von: {Model.MissingByName}";
            var when = Model.MissingSince is { } s ? s.ToLocalTime().ToString("g") : "unbekannt";
            return $"Der Ordner dieses Mods wurde beim letzten Scan nicht gefunden.\n" +
                   $"Zuletzt gesehen: {when}{who}";
        }
    }

    public bool HasImage => Model.PrimaryImageAbsPath is not null;

    /// <summary>
    /// Tags for the chips on the card. The template binds this by name, so it has to be
    /// exposed here — binding straight to Model.Tags is not visible to the DataTemplate.
    /// </summary>
    public IReadOnlyList<ModTagRef> Tags => Model.Tags;
    public bool HasTags => Model.Tags.Count > 0;
    public string? UpdatedByName => Model.UpdatedByName;
    public bool HasUpdatedBy => !string.IsNullOrEmpty(Model.UpdatedByName);
    public string UpdatedByInitials => InitialsFrom(Model.UpdatedByName);
    public System.Windows.Media.Brush UpdatedByBrush => BrushFrom(Model.UpdatedByColor, Model.UpdatedByName);

    private static string InitialsFrom(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(new[] { ' ', '@', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return ($"{parts[0][0]}{parts[1][0]}").ToUpperInvariant();
    }

    private static readonly string[] Palette =
    {
        "#7A5CFA", "#FF8A65", "#26A69A", "#FFB300", "#5C6BC0",
        "#EC407A", "#26C6DA", "#9CCC65", "#AB47BC", "#42A5F5"
    };

    private static System.Windows.Media.Brush BrushFrom(string? color, string? name)
    {
        var hex = !string.IsNullOrEmpty(color) ? color! : DeriveColor(name ?? "");
        try
        {
            var b = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;
            b.Freeze();
            return b;
        }
        catch { return System.Windows.Media.Brushes.Gray; }
    }

    private static string DeriveColor(string name)
    {
        unchecked
        {
            int hash = 0;
            foreach (var c in name) hash = hash * 31 + c;
            return Palette[Math.Abs(hash) % Palette.Length];
        }
    }

    public event EventHandler<int>? RatingChangeRequested;
    public event EventHandler<string[]>? ImagesDroppedOnCard;

    private readonly ThumbnailCache _thumbnails;

    public ModCardViewModel(ModCard model, ThumbnailCache thumbnails)
    {
        Model = model;
        _thumbnails = thumbnails;
        _rating = model.Rating;
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
        RatingChangeRequested?.Invoke(this, value);
    }

    public void NotifyDropped(string[] files)
    {
        ImagesDroppedOnCard?.Invoke(this, files);
    }
}
