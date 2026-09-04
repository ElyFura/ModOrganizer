using CommunityToolkit.Mvvm.ComponentModel;
using ModOrganizer.Core.Queries;

namespace ModOrganizer.App.ViewModels;

public sealed partial class CategoryItemViewModel : ObservableObject
{
    public CategoryInfo Model { get; }

    public long Id => Model.Id;
    public string Name => Model.Name;
    public int ModCount => Model.ModCount;
    public bool IsMissing => Model.IsMissing;

    public CategoryItemViewModel(CategoryInfo model)
    {
        Model = model;
    }
}
