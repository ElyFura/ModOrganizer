using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ModOrganizer.App.ViewModels;

/// <summary>
/// One level of the category hierarchy in the sidebar.
///
/// A category is stored as the folder path below the library ("Solo\NSFW\Sitzend"), so a
/// nested library produced 121 flat entries with backslashes in them. Split into a tree,
/// the same data reads as Solo &gt; NSFW &gt; Sitzend - and picking a parent shows
/// everything underneath it, which is what you want when browsing "all of Solo".
/// </summary>
public sealed partial class CategoryNodeViewModel : ObservableObject
{
    /// <summary>Just this level ("Sitzend").</summary>
    public string Name { get; }

    /// <summary>The whole path, as stored in the database ("Solo\NSFW\Sitzend").</summary>
    public string FullPath { get; }

    public ObservableCollection<CategoryNodeViewModel> Children { get; } = new();

    /// <summary>
    /// The category row for this exact path, if one exists. An intermediate level such as
    /// "Solo" holds no mods of its own and has none.
    /// </summary>
    public long? OwnCategoryId { get; set; }

    /// <summary>Mods directly in this folder.</summary>
    public int OwnModCount { get; set; }

    [ObservableProperty] private bool _isExpanded;

    public CategoryNodeViewModel(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    /// <summary>Mods here and in everything below - what the sidebar badge shows.</summary>
    public int TotalModCount => OwnModCount + Children.Sum(c => c.TotalModCount);

    /// <summary>
    /// Every category the gallery must include when this node is picked: this one plus all
    /// its descendants.
    /// </summary>
    public IReadOnlyList<long> AllCategoryIds
    {
        get
        {
            var ids = new List<long>();
            Collect(this, ids);
            return ids;
        }
    }

    private static void Collect(CategoryNodeViewModel node, List<long> into)
    {
        if (node.OwnCategoryId is { } id) into.Add(id);
        foreach (var c in node.Children) Collect(c, into);
    }

    public bool HasChildren => Children.Count > 0;

    /// <summary>Shown when a parent is selected, so it is obvious the view is not filtered to one folder.</summary>
    public string BadgeText => HasChildren && OwnModCount != TotalModCount
        ? $"{TotalModCount}"
        : $"{OwnModCount}";
}
