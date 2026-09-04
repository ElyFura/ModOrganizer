using System.Windows;
using System.Windows.Input;
using ModOrganizer.App.ViewModels;

namespace ModOrganizer.App.Views;

public partial class TagManagerWindow : Window
{
    private readonly TagManagerViewModel _vm;

    public TagManagerWindow(TagManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) =>
        {
            NewTagBox.Focus();
            _ = vm.LoadAsync();
        };
    }

    private void NewTag_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.CreateTagCommand.Execute(null);
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
