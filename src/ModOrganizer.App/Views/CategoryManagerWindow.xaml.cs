using System.Windows;
using ModOrganizer.App.ViewModels;

namespace ModOrganizer.App.Views;

public partial class CategoryManagerWindow : Window
{
    public CategoryManagerWindow(CategoryManagerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
