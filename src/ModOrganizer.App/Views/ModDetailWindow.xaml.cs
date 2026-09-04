using System.Windows;
using System.Windows.Input;
using ModOrganizer.App.ViewModels;

namespace ModOrganizer.App.Views;

public partial class ModDetailWindow : Window
{
    public ModDetailWindow(ModDetailViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.DeletedRequestingClose += (_, _) => Dispatcher.Invoke(Close);
    }

    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ModDetailViewModel vm)
            vm.OpenLightboxCommand.Execute(null);
    }
}
