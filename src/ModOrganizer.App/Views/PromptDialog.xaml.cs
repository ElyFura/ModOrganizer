using System.Windows;

namespace ModOrganizer.App.Views;

public partial class PromptDialog : Window
{
    public string ResultText { get; private set; } = "";

    public PromptDialog(string title, string label, string initial = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text;
        DialogResult = true;
    }
}
