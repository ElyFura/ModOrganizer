using System.Windows;
using ModOrganizer.App.Services;

namespace ModOrganizer.App.Views;

public partial class LoginWindow : Window
{
    private readonly SupabaseClientProvider _supabase;

    public bool SignedIn { get; private set; }
    public bool SkippedOffline { get; private set; }

    public LoginWindow(SupabaseClientProvider supabase)
    {
        InitializeComponent();
        _supabase = supabase;

        StaySignedInBox.IsChecked = supabase.StaySignedIn;
        ApplySavedAccount(supabase.SavedEmail);
    }

    /// <summary>
    /// With a remembered account the email box is replaced by a read-only chip, so the
    /// only thing left to type is the password.
    /// </summary>
    private void ApplySavedAccount(string? email)
    {
        var hasSaved = !string.IsNullOrWhiteSpace(email);

        SavedAccountPanel.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
        EmailPanel.Visibility = hasSaved ? Visibility.Collapsed : Visibility.Visible;

        if (hasSaved)
        {
            SavedEmailText.Text = email;
            EmailBox.Text = email;
            Loaded += (_, _) => PasswordBox.Focus();
        }
        else
        {
            EmailBox.Text = "";
            Loaded += (_, _) => EmailBox.Focus();
        }
    }

    /// <summary>"Anderer Benutzer": drop the remembered account and ask for an email again.</summary>
    private void SwitchUser_Click(object sender, RoutedEventArgs e)
    {
        _supabase.ForgetSavedEmail();
        ApplySavedAccount(null);
        PasswordBox.Password = "";
        ErrorText.Text = "";
        EmailBox.Focus();
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email))
        {
            ErrorText.Text = "Email ist erforderlich.";
            EmailBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Passwort ist erforderlich.";
            PasswordBox.Focus();
            return;
        }

        IsEnabled = false;
        SignInButton.Content = "Melde an…";
        try
        {
            var ok = await _supabase.SignInAsync(email, password,
                staySignedIn: StaySignedInBox.IsChecked == true);

            if (ok)
            {
                SignedIn = true;
                DialogResult = true;
                Close();
                return;
            }

            ErrorText.Text = "Anmeldung fehlgeschlagen — falsche Email oder falsches Passwort?";
            PasswordBox.Password = "";
            PasswordBox.Focus();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
            SignInButton.Content = "Anmelden";
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        SkippedOffline = true;
        DialogResult = false;
        Close();
    }
}
