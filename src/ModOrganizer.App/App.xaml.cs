using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModOrganizer.App.Services;
using ModOrganizer.App.ViewModels;
using ModOrganizer.Core.Archive;
using ModOrganizer.Core.Auth;
using ModOrganizer.Core.Categories;
using ModOrganizer.Core.Collections;
using ModOrganizer.Core.Comments;
using ModOrganizer.Core.Duplicates;
using ModOrganizer.Core.Health;
using ModOrganizer.Core.Import;
using ModOrganizer.Core.Links;
using ModOrganizer.Core.Management;
using ModOrganizer.Core.Penumbra;
using ModOrganizer.Core.Pmp;
using ModOrganizer.Core.Queries;
using ModOrganizer.Core.Scanning;
using ModOrganizer.Core.Storage;
using ModOrganizer.Core.Tagging;
using Serilog;

namespace ModOrganizer.App;

public partial class App : Application
{
    private IHost? _host;

    private static string LogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "FFXIVModOrganizer", "logs");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // CRITICAL: stay alive between LoginWindow.Close and MainWindow.Show —
        // otherwise WPF sees "zero windows" for a tick and shuts the app down.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Directory.CreateDirectory(LogFolder);

        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            MessageBox.Show(args.Exception.ToString(), "Unerwarteter Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log.Error(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(ex.ToString(), "Fataler Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var config = AppConfig.Load();
        if (!config.IsValid)
        {
            var dlg = new Views.ConfigSetupWindow(config);
            if (dlg.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            config.Save();
        }
        // If Supabase URL is given but Anon Key is empty, prompt for it — otherwise
        // auth/realtime silently stays disabled and the user wonders why.
        if (!string.IsNullOrWhiteSpace(config.Supabase.Url) && string.IsNullOrWhiteSpace(config.Supabase.AnonKey))
        {
            MessageBox.Show(
                "Supabase URL ist gesetzt, aber der Anon Key fehlt.\n\n" +
                "Hol ihn aus Supabase Studio → Settings → API → Project API keys → 'anon public' " +
                "und trag ihn im nächsten Dialog ein. Ohne Key läuft die App im Offline-Modus (kein Login, keine Live-Sync).",
                "Supabase Anon Key fehlt", MessageBoxButton.OK, MessageBoxImage.Information);
            var dlg = new Views.ConfigSetupWindow(config);
            if (dlg.ShowDialog() == true) config.Save();
        }

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((_, lc) => lc
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .WriteTo.File(Path.Combine(LogFolder, "app-.log"), rollingInterval: RollingInterval.Day))
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton<IDatabaseConnectionFactory>(_ =>
                    new PostgresConnectionFactory(config.Postgres.ConnectionString));
                services.AddSingleton<DatabaseStore>();

                services.AddSingleton<TokenStore>(_ => new TokenStore());
                services.AddSingleton<SupabaseClientProvider>();
                services.AddSingleton<IUserContext>(sp => sp.GetRequiredService<SupabaseClientProvider>());
                services.AddSingleton<RealtimeHub>();

                services.AddSingleton<ModLibraryService>();
                services.AddSingleton<ModDetailQuery>();
                services.AddSingleton<RootService>();

                // Shared by every service that writes into a mod root, so the filesystem
                // watcher can ignore changes the app made itself.
                services.AddSingleton<FileSystemActivityGate>();
                services.AddSingleton<StatsService>();
                services.AddSingleton<ActivityFeedService>();
                services.AddSingleton<PmpInspector>(_ => new PmpInspector());
                services.AddSingleton<ModScanner>();
                services.AddSingleton<ImportService>();

                services.AddSingleton<CategoryService>();
                services.AddSingleton<RenameService>();
                services.AddSingleton<UndoService>();
                services.AddTransient<Views.HistoryWindow>();
                services.AddSingleton<Services.HtmlCatalogExporter>();
                services.AddSingleton<SmartCollectionService>();
                services.AddSingleton<PenumbraService>();
                services.AddSingleton<PenumbraSyncService>();
                services.AddSingleton<MoveService>();
                services.AddSingleton<ArchiveService>(_ => new ArchiveService());
                services.AddSingleton<DeleteService>();
                services.AddSingleton<TagService>();
                services.AddSingleton<LinkService>();
                services.AddSingleton<CommentService>();
                services.AddSingleton<ModCommentService>();
                services.AddSingleton<ModOrganizer.Core.Comments.MentionResolver>();
                services.AddSingleton<MentionBroadcaster>();
                services.AddSingleton<ToastHost>();
                services.AddSingleton<PresenceService>();
                services.AddSingleton<HealthChecker>();
                services.AddSingleton<DuplicateFinder>();
                services.AddSingleton<RootWatcher>();

                services.AddSingleton<ThumbnailCache>();
                services.AddSingleton<ModDetailViewModelFactory>();
                services.AddTransient<CategoryManagerViewModel>();
                services.AddTransient<RootSettingsViewModel>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        try
        {
            var store = _host.Services.GetRequiredService<DatabaseStore>();
            store.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize DB");
            MessageBox.Show(
                $"Konnte keine Verbindung zu Postgres herstellen.\n\n{ex.Message}\n\nPrüfe appsettings.json:\n{AppConfig.DefaultPath()}",
                "DB-Verbindung fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Phase B: try Supabase auth if configured
        var supabase = _host.Services.GetRequiredService<SupabaseClientProvider>();
        if (supabase.IsConfigured)
        {
            try { await supabase.InitializeAsync(); }
            catch (Exception ex) { Log.Error(ex, "Supabase init failed"); }

            // Shown whenever there is no live session. With "Angemeldet bleiben" off
            // (the default) that is every start, with the email prefilled so only the
            // password has to be typed.
            if (!supabase.IsAuthenticated)
            {
                var login = new Views.LoginWindow(supabase);
                login.Owner = null;
                login.ShowDialog();
                // If the user skipped offline, continue without auth - single-user mode.
            }
        }
        // Inject current user into all action_log writes
        Core.Management.ActionLog.CurrentUser = supabase;

        // ShutdownMode stays OnExplicitShutdown until MainWindow is shown, so anything
        // throwing before that point leaves a live process with no window and no way to
        // close it but Task Manager.
        MainViewModel mainVm;
        try
        {
            var library = _host.Services.GetRequiredService<ModLibraryService>();
            var roots = await library.GetRootsAsync();
            if (roots.Count == 0)
            {
                const string defaultPath = @"E:\FFXIV\FFXIV\FF14 Mods\Mods\Dawntrail";
                if (Directory.Exists(defaultPath))
                    library.EnsureRoot(defaultPath, "Dawntrail");
            }

            // Carry this user over to per-user root paths: any root whose original path
            // exists on this machine is almost certainly theirs. Roots belonging to the
            // other user simply stay unmapped instead of being handed over as broken
            // absolute paths.
            try
            {
                var adopted = _host.Services.GetRequiredService<RootService>().AdoptLocalRoots();
                if (adopted > 0) Log.Information("Adopted {Count} root path(s) for this user", adopted);
            }
            catch (Exception ex) { Log.Warning(ex, "Root adoption failed"); }

            mainVm = _host.Services.GetRequiredService<MainViewModel>();
            mainVm.Toasts = _host.Services.GetRequiredService<ToastHost>();
            mainVm.AttachConfig(config);
            await mainVm.LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup failed before MainWindow could be shown");
            MessageBox.Show($"Start fehlgeschlagen.\n\n{ex.Message}",
                "Start fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var watcher = _host.Services.GetRequiredService<RootWatcher>();
        watcher.RescanCompleted += (_, _) => Dispatcher.Invoke(mainVm.RefreshAfterScan);
        watcher.Start();

        // Phase C: realtime sync — partner's DB changes pushed to our UI
        if (supabase.IsAuthenticated)
        {
            var hub = _host.Services.GetRequiredService<RealtimeHub>();
            hub.OnAnyChange(() => Dispatcher.Invoke(mainVm.RefreshAfterScan));

            // Phase 4A: @mention broadcast listener
            var mentions = _host.Services.GetRequiredService<MentionBroadcaster>();
            var toasts = _host.Services.GetRequiredService<ToastHost>();
            mentions.MentionReceived += (_, payload) =>
            {
                toasts.Show(
                    title: $"@{payload.FromName} hat dich erwähnt",
                    body: $"{payload.ModName}: {payload.Snippet}",
                    onClick: () => Dispatcher.Invoke(() => mainVm.OpenDetailByIdCommand.Execute(payload.ModId)),
                    colorHex: "#7A5CFA");
            };

            // Phase 4A: Presence
            var presence = _host.Services.GetRequiredService<PresenceService>();
            mainVm.Presence = presence;
            presence.StateChanged += (_, _) => Dispatcher.Invoke(mainVm.RefreshPresence);

            // Sequential on purpose. Mentions and presence open their own realtime
            // channels, which requires the socket to already be connected — starting all
            // three in parallel meant both reliably lost the race against the hub's
            // ConnectAsync and died with "Socket must exist, was `Connect` called?",
            // so @mentions and presence never worked at all.
            _ = Task.Run(async () =>
            {
                bool connected;
                try
                {
                    connected = await hub.StartAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Realtime hub start failed");
                    return;
                }

                if (!connected)
                {
                    Log.Warning("Realtime socket unavailable — mentions and presence stay off");
                    return;
                }

                try { await mentions.StartAsync(); }
                catch (Exception ex) { Log.Warning(ex, "Mention broadcaster start failed"); }

                try { await presence.StartAsync(); }
                catch (Exception ex) { Log.Warning(ex, "Presence start failed"); }
            });
        }

        mainVm.RefreshAccountLabel();
        supabase.UserChanged += (_, _) => Dispatcher.Invoke(mainVm.RefreshAccountLabel);

        var main = _host.Services.GetRequiredService<MainWindow>();
        main.DataContext = mainVm;
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        main.Activate();
        Log.Information("MainWindow shown");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync();

                // DisposeAsync, not Dispose: several singletons (SupabaseClientProvider,
                // RealtimeHub) implement only IAsyncDisposable, and the synchronous
                // ServiceProvider.Dispose() throws on those. IHost itself only declares
                // IDisposable, so go through the interface explicitly.
                if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
                else _host.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Host shutdown failed");
            }
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
