using Microsoft.Extensions.Logging;
using ModOrganizer.Core.Auth;
using Supabase.Gotrue;
using Supabase.Gotrue.Interfaces;
using SbClient = Supabase.Client;

namespace ModOrganizer.App.Services;

public sealed class SupabaseClientProvider : IUserContext, IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly TokenStore _tokens;
    private readonly ILogger<SupabaseClientProvider> _log;
    private SbClient? _client;
    private Session? _session;

    public SbClient? Client => _client;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.Supabase.Url)
                              && !string.IsNullOrWhiteSpace(_config.Supabase.AnonKey);

    public Guid? UserId => _session?.User?.Id is string s && Guid.TryParse(s, out var g) ? g : null;
    public string? Email => _session?.User?.Email;
    public string? DisplayName => _session?.User?.UserMetadata is { } m
        && m.TryGetValue("display_name", out var v) && v is string ds ? ds : Email;
    public bool IsAuthenticated => _session?.User is not null;

    public event EventHandler? UserChanged;

    /// <summary>Email to prefill the login with, so only the password has to be typed.</summary>
    public string SavedEmail => _config.Auth.LastEmail;

    /// <summary>
    /// Whether the refresh token may be kept across restarts. While false the login is
    /// shown on every start — which is the point of the "Angemeldet bleiben" checkbox.
    /// </summary>
    public bool StaySignedIn
    {
        get => _config.Auth.StaySignedIn;
        private set => _config.Auth.StaySignedIn = value;
    }

    public SupabaseClientProvider(AppConfig config, TokenStore tokens, ILogger<SupabaseClientProvider> log)
    {
        _config = config;
        _tokens = tokens;
        _log = log;
    }

    public async Task InitializeAsync()
    {
        if (!IsConfigured)
        {
            _log.LogInformation("Supabase URL/AnonKey not configured — running anonymously.");
            return;
        }

        var options = new Supabase.SupabaseOptions
        {
            AutoRefreshToken = true,
            AutoConnectRealtime = false    // RealtimeHub does this explicitly after login
        };
        _client = new SbClient(_config.Supabase.Url, _config.Supabase.AnonKey, options);
        _client.Auth.AddStateChangedListener(OnAuthState);

        // Hard timeout so a hanging network call can't block app startup
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            await _client.InitializeAsync().WaitAsync(cts.Token);
            _log.LogInformation("Supabase client initialized");
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Supabase InitializeAsync timed out after 8s — continuing anyway");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Supabase initialize failed — continuing anyway");
        }

        if (!StaySignedIn)
        {
            // The user wants to enter their password each time. Drop any token so a
            // previously remembered session cannot silently skip the login.
            _tokens.Clear();
            _log.LogInformation("Stay-signed-in is off — login required");
            return;
        }

        var (access, refresh) = _tokens.TryLoad();
        if (!string.IsNullOrEmpty(access) && !string.IsNullOrEmpty(refresh))
        {
            try
            {
                using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                // forceAccessTokenRefresh=true: use refresh token to get a fresh access token
                _session = await _client.Auth.SetSession(access, refresh, forceAccessTokenRefresh: true)
                    .WaitAsync(rcts.Token);
                _log.LogInformation("Restored session for {Email}", _session?.User?.Email);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Saved token invalid; will require re-login");
                _tokens.Clear();
            }
        }
    }

    private void OnAuthState(IGotrueClient<User, Session> sender, Constants.AuthState state)
    {
        _session = sender.CurrentSession;

        // Persist only with consent. This listener also fires on every automatic token
        // refresh, so without the check it would re-create the file we just cleared.
        if (StaySignedIn && _session?.AccessToken is { } at && _session.RefreshToken is { } rt)
            _tokens.Save(at, rt);

        if (state == Constants.AuthState.SignedOut)
            _tokens.Clear();

        UserChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> SignInAsync(string email, string password, bool staySignedIn = false)
    {
        if (_client is null) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Set before the call: the auth-state listener fires during SignIn and decides
            // from this flag whether it may write the token.
            StaySignedIn = staySignedIn;

            _session = await _client.Auth.SignIn(email, password).WaitAsync(cts.Token);
            var ok = _session?.User is not null;

            if (ok)
            {
                // Remember the account so the next login only needs the password.
                _config.Auth.LastEmail = _session!.User!.Email ?? email.Trim();
                if (staySignedIn && _session.AccessToken is { } a && _session.RefreshToken is { } r)
                    _tokens.Save(a, r);
                else
                    _tokens.Clear();

                TrySaveConfig();
            }
            else
            {
                StaySignedIn = false;
            }

            return ok;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("SignIn timed out");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SignIn failed");
            return false;
        }
    }

    /// <summary>
    /// Signs out and forgets the token, but keeps the remembered email so the next login
    /// still only needs a password.
    /// </summary>
    public async Task SignOutAsync()
    {
        StaySignedIn = false;
        TrySaveConfig();

        _tokens.Clear();

        if (_client is not null)
        {
            try { await _client.Auth.SignOut(); }
            catch (Exception ex) { _log.LogWarning(ex, "Sign-out call failed; local session dropped anyway"); }
        }

        _session = null;
        UserChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the remembered account entirely.</summary>
    public void ForgetSavedEmail()
    {
        _config.Auth.LastEmail = "";
        TrySaveConfig();
    }

    private void TrySaveConfig()
    {
        try { _config.Save(); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist auth preferences"); }
    }

    public ValueTask DisposeAsync()
    {
        // Deliberately does not sign out: closing the app revoked the refresh token
        // server-side, so "Angemeldet bleiben" could never actually work.
        return ValueTask.CompletedTask;
    }
}
