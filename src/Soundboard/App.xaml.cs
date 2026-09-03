using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soundboard.Audio;
using Soundboard.Authentication;
using Soundboard.Core.Interfaces;
using Soundboard.Services;
using Soundboard.ViewModels;
using Soundboard.Views;

namespace Soundboard;

public partial class App : Application
{
    internal IHost? HostInstance { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Shown immediately so the app never just sits invisible while the DI host builds,
        // settings load, and (for a returning, auto-login user) a real network call
        // (TryRestoreSessionAsync below) completes. Closed exactly once, right before whichever
        // window is first to actually take over — see splashClosed below.
        //
        // ShutdownMode is switched away from its default (OnLastWindowClose) for the duration of
        // this dance: closing the splash briefly leaves zero windows open before the next one
        // opens, and OnLastWindowClose's check runs synchronously inside Close() — without this,
        // the app could shut itself down right there instead of ever reaching authWindow/
        // mainWindow. Restored once a real window is confirmed up (see below).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new Views.SplashWindow();
        splash.Show();
        var splashClosed = false;

        try
        {
            HostInstance = Host.CreateDefaultBuilder()
                .ConfigureServices(ConfigureServices)
                .Build();

            await HostInstance.StartAsync().ConfigureAwait(true);

            var paths = HostInstance.Services.GetRequiredService<IAppPaths>();
            // Captured before anything touches settings.json — SettingsService.LoadAsync()
            // below creates that file with defaults as a side effect on a fresh install, so
            // checking for it has to happen first or every run would look like the first one.
            var isFirstRun = !File.Exists(paths.SettingsFile);

            var settingsService = HostInstance.Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync().ConfigureAwait(true);

            var sessionService = HostInstance.Services.GetRequiredService<ISessionService>();
            var licenseService = HostInstance.Services.GetRequiredService<ILicenseService>();

            if (isFirstRun)
            {
                // Account status first (Welcome / Create Account / Login / Continue Offline),
                // then the existing audio device setup wizard, then the main window — matches
                // "offline mode should allow using the free version" as a real, first-class path.
                splash.Close();
                splashClosed = true;

                var authWindow = HostInstance.Services.GetRequiredService<Views.Auth.AuthWindow>();
                authWindow.ShowDialog();

                var wizard = HostInstance.Services.GetRequiredService<FirstRunWizardWindow>();
                wizard.ShowDialog();
            }
            else if (settingsService.Settings.Account.AutoLoginEnabled)
            {
                // Silent — only first run and an explicit "Log In" click from the toolbar ever
                // show the auth window again, so offline use never gets nagged on every launch.
                await sessionService.TryRestoreSessionAsync().ConfigureAwait(true);
            }

            licenseService.UpdateFromProfile(sessionService.CurrentProfile);

            var mainWindow = HostInstance.Services.GetRequiredService<MainWindow>();
            if (!splashClosed)
            {
                splash.Close();
            }

            // WPF auto-assigns Application.MainWindow to the FIRST window shown via Show()/
            // ShowDialog() if nothing sets it explicitly — since the splash (and, on first run,
            // authWindow/wizard) are shown before this point, that auto-detection lands on one
            // of those instead of the real main window, and never gets reassigned once they
            // close. Every place that does `window.Owner = Application.Current.MainWindow`
            // (ShowSettings, OpenPluginMarketplace, etc.) would then throw "Cannot set Owner
            // property to a Window that has not been shown previously" against an already-closed
            // window. Setting this explicitly overrides WPF's guess with the window that's
            // actually going to stay open for the rest of the app's lifetime.
            Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            // Unawaited on purpose — a slow or failed GitHub API call must never delay launch.
            // IUpdateService itself swallows all failures, so this can't throw into the void.
            if (settingsService.Settings.General.CheckForUpdatesOnLaunch &&
                mainWindow.DataContext is MainViewModel mainViewModel)
            {
                _ = mainViewModel.CheckForUpdatesInBackgroundAsync();
            }

            // Launched by double-clicking a .sbpack file — only possible if the installer's
            // optional file-association task was checked, since nothing else ever passes Sonar
            // a command-line argument. Unawaited for the same reason as the update check above:
            // never delay the window actually showing.
            var sbpackArg = e.Args.FirstOrDefault(a => a.EndsWith(".sbpack", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (sbpackArg is not null && mainWindow.DataContext is MainViewModel mainViewModelForImport)
            {
                _ = mainViewModelForImport.ImportCollectionFromPathAsync(sbpackArg);
            }
        }
        catch (Exception ex)
        {
            if (!splashClosed)
            {
                splash.Close();
            }

            ReportCrash("Startup", ex);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash("UI thread", e.Exception);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportCrash("Background thread (fatal)", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportCrash("Unobserved task", e.Exception);
        e.SetObserved();
    }

    private static void ReportCrash(string source, Exception ex)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";

        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Soundboard", "logs");
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(Path.Combine(logsDir, "crash.log"), text);
        }
        catch
        {
            // Logging is best-effort; still show the dialog below.
        }

        MessageBox.Show(
            $"Sonar hit an unexpected error and needs to close this operation.\n\nSource: {source}\n\n{ex.Message}\n\n(Full details were written to %LocalAppData%\\Soundboard\\logs\\crash.log)",
            "Sonar - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (HostInstance is not null)
        {
            await HostInstance.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            HostInstance.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<IAudioEngine, AudioEngine>();
        services.AddSingleton<IPlaybackManager, PlaybackManager>();
        services.AddSingleton<IHotkeyManager, HotkeyManager>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ISoundFileWatcher, SoundFileWatcher>();
        services.AddSingleton<ICollectionExportService, CollectionExportService>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        services.AddSingleton<IPluginPackService, PluginPackService>();
        services.AddSingleton<IPluginScriptRunner, PluginScriptRunner>();
        services.AddSingleton<ICommunityPluginRuntime, CommunityPluginRuntime>();
        services.AddSingleton<IProfanityFilterService, ProfanityFilterService>();

        services.AddSingleton(SupabaseConfig.Load());
        services.AddSingleton<SecureTokenStorage>();
        services.AddSingleton<LocalAvatarStore>();
        services.AddSingleton<IAuthenticationService, SupabaseAuthService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<ILicenseService, LicenseService>();
        services.AddSingleton<ICloudService, SupabaseCloudService>();
        services.AddSingleton<IAdminService, SupabaseAdminService>();
        services.AddSingleton<IPluginTrustService, SupabasePluginTrustService>();
        services.AddSingleton<ICommunityPluginService, SupabaseCommunityPluginService>();
        services.AddSingleton<ICommunityPackService, SupabaseCommunityPackService>();
        services.AddSingleton<IAdminMessageService, SupabaseAdminMessageService>();
        services.AddSingleton<IContentReportService, SupabaseContentReportService>();
        services.AddSingleton<ISupportTicketService, SupabaseSupportTicketService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<QuickPlayOverlayViewModel>();
        services.AddSingleton<QuickPlayOverlayWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<FirstRunWizardViewModel>();
        services.AddTransient<FirstRunWizardWindow>();

        services.AddTransient<ViewModels.Auth.AuthViewModel>();
        services.AddTransient<Views.Auth.AuthWindow>();
        services.AddTransient<AccountViewModel>();
        services.AddTransient<AccountWindow>();
        services.AddTransient<SupportViewModel>();
        services.AddTransient<SupportWindow>();
        services.AddTransient<PluginMarketplaceViewModel>();
        services.AddTransient<PluginMarketplaceWindow>();
        services.AddTransient<PluginAuthoringViewModel>();
        services.AddTransient<PluginAuthoringWindow>();
        services.AddTransient<ScriptPluginAuthoringViewModel>();
        services.AddTransient<ScriptPluginAuthoringWindow>();
        services.AddTransient<AdminViewModel>();
        services.AddTransient<AdminWindow>();
    }
}
