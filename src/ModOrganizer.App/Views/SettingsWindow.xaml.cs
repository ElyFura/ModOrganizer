using System.Windows;
using ModOrganizer.App.ViewModels;

namespace ModOrganizer.App.Views;

public partial class SettingsWindow : Window
{
    private readonly RootSettingsViewModel _vm;

    /// <summary>True when anything about the roots changed, so the gallery must reload.</summary>
    public bool RootsChanged { get; private set; }

    public SettingsWindow(RootSettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.RootsChanged += (_, _) => RootsChanged = true;
        Loaded += (_, _) => _ = vm.LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
