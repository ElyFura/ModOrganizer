using System.IO;
using System.Text.Json;

namespace ModOrganizer.App.Services;

public sealed class AppConfig
{
    public SupabaseConfig Supabase { get; set; } = new();
    public PostgresConfig Postgres { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    public sealed class UpdateConfig
    {
        /// <summary>Set to false to stop the app looking for new releases at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>"owner/name" on GitHub. Overridable so a fork can point elsewhere.</summary>
        public string Repository { get; set; } = "ElyFura/ModOrganizer";

        /// <summary>Off by default: a prerelease is published to be tested, not rolled out.</summary>
        public bool IncludePrereleases { get; set; }
    }

    public sealed class AuthConfig
    {
        /// <summary>
        /// Last successfully used email, so the login only asks for the password.
        /// Not a secret — the password and tokens never land here.
        /// </summary>
        public string LastEmail { get; set; } = "";

        /// <summary>
        /// When false (the default) the refresh token is not kept and the login appears on
        /// every start with the email prefilled. Ticking "Angemeldet bleiben" persists the
        /// token and skips the dialog next time.
        /// </summary>
        public bool StaySignedIn { get; set; }
    }

    public sealed class SupabaseConfig
    {
        public string Url { get; set; } = "";
        public string AnonKey { get; set; } = "";
    }

    public sealed class PostgresConfig
    {
        public string ConnectionString { get; set; } = "";
    }

    public sealed class UiConfig
    {
        public int ThumbnailSize { get; set; } = 240;
    }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Postgres.ConnectionString);

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVModOrganizer",
            "appsettings.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }
}
