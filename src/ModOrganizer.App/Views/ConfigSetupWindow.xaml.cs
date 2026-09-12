using System.Windows;
using ModOrganizer.App.Services;

namespace ModOrganizer.App.Views;

public partial class ConfigSetupWindow : Window
{
    private readonly AppConfig _cfg;

    public ConfigSetupWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        ConnBox.Text = cfg.Postgres.ConnectionString;
        UrlBox.Text = cfg.Supabase.Url;
        KeyBox.Text = cfg.Supabase.AnonKey;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ConnBox.Text))
        {
            MessageBox.Show("Die Verbindungszeichenfolge ist Pflicht.", "Fehlt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _cfg.Postgres.ConnectionString = ConnBox.Text.Trim();
        _cfg.Supabase.Url = UrlBox.Text.Trim();
        _cfg.Supabase.AnonKey = KeyBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
