using System.Windows;
using ModOrganizer.App.ViewModels;

namespace ModOrganizer.App.Views;

public partial class DuplicatesWindow : Window
{
    public DuplicatesWindow(DuplicatesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => _ = vm.LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
