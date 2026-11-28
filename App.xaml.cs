using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using VNotch.Windows;

namespace VNotch;

public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private static int _fatalUiExceptionInProgress;
    private static string MutexName => SingleInstanceGuard.GetCurrentUserMutexName();
    private Task? _shutdownTask;
    private bool _servicesDisposed;
    internal bool IsShutdownRequested => _shutdownTask != null;

    internal static Task RequestShutdownAsync(int exitCode = 0)
    {
        if (Current is not App app) return Task.CompletedTask;
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.InvokeAsync(() => RequestShutdownAsync(exitCode)).Task.Unwrap();
        return app._shutdownTask ??= app.ShutdownCoreAsync(exitCode);
    }

    private async Task ShutdownCoreAsync(int exitCode)
    {
        // Keep the dispatcher pumping until queued disk writes and media shutdown finish.
        await Task.Yield();
        try
        {
            if (MainWindow is MainWindow window) await window.CleanupAsync();
            await VNotch.Services.AiUsage.AiAccountUsageMonitor.Shared.DisposeAsync();
            if (Services?.GetService<IMediaDetectionService>() is MediaDetectionService media)
                await media.DrainShutdownAsync();
            if (Services is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (Services is IDisposable disposable)
                disposable.Dispose();
            _servicesDisposed = true;
            await GpuMonitorService.DisposeSharedAsync();
        }
        catch (Exception ex) { RuntimeLog.Error("APP-SHUTDOWN", ex, "Cleanup failed"); }
        finally
        {
            // Watch-VNotch.ps1 leaves the app closed after a plain "Application exit" (quit on purpose) and restarts it otherwise.
            RuntimeLog.Log("SYSTEM", exitCode == 0 ? "Application exit" : $"Application exit after an error (code {exitCode})");
            await RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(2));
            Shutdown(exitCode);
        }
    }

    private const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);

    public App()
    {
        TextRenderingPolicy.Initialize();
        WindowAnimationPolicy.Initialize();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_SYSTEM32);
            }
        }
        catch
        {
            // Best effort on supported Windows platforms
        }

        CrashReporter.Initialize();
    }

    private const string IntegrityWarningTitleKey = "integrity.warningTitle";

    public static IServiceProvider Services { get; private set; } = null!;

    private static void SetServices(IServiceProvider services)
    {
        Services = services;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            RegisterExceptionHandlers();
            VNotch.Services.ClaudeCode.ClaudeSessionEnvironment.ScrubInherited();
            // The notch explains itself; a tooltip there is a second, white window popping up over it.
            // Settings and the other windows keep theirs.
            System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.FrameworkElement),
                System.Windows.FrameworkElement.ToolTipOpeningEvent,
                new System.Windows.Controls.ToolTipEventHandler((sender, args) =>
                {
                    if (sender is System.Windows.DependencyObject element &&
                        System.Windows.Window.GetWindow(element) is VNotch.MainWindow) args.Handled = true;
                }));

            if (e.Args.Length > 0 && e.Args[0] == "--preview-quota")
            {
                int result = e.Args.Length is 2 or 3
                    ? VNotch.Services.AiUsage.AiQuotaPreview.Send(e.Args[1], e.Args.Length == 3 ? e.Args[2] : "Codex")
                    : -1;
                Shutdown(result == 0 ? 0 : 2);
                return;
            }

            bool isSetupOrUninstall = IsSetupOrUninstall(e.Args);
            bool specialStartup = isSetupOrUninstall;
#if DEBUG
            bool isDebugIntegrityTest = e.Args.Contains("--test-integrity") || e.Args.Contains("--test-hash");
            specialStartup |= isDebugIntegrityTest;
#endif
            if (!specialStartup && !EnsureSingleInstance(e)) return;

            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);

            if (isSetupOrUninstall)
            {
                using var earlySettings = new SettingsService();
                var loadedSettings = earlySettings.Load();
                NetworkPrivacy.Current.Apply(loadedSettings);
                AccentTheme.Apply(loadedSettings.AccentColor);
                WindowAnimationPolicy.SetInvisibleMode(loadedSettings.InvisibleMode);
                WindowAnimationPolicy.SetShadowEnabled(loadedSettings.EnableShadow);
                LocalizedPresentation.Initialize();
                Loc.SetLanguage(loadedSettings.Language);
                AnimationConfig.Configure(loadedSettings.AnimationFps, loadedSettings.AutoAnimationFps);
                base.OnStartup(e);
                RunSetupOrUninstallAsync(e, loadedSettings.Language).SafeFireAndForget("SETUP-STARTUP");
                return;
            }

            if (!specialStartup) AppPaths.MigrateLegacyLocalData();
            _ = MediaProtocolConfiguration.Current;
            var services = new ServiceCollection();
            ServiceConfigurator.ConfigureServices(services);
            SetServices(services.BuildServiceProvider());
            var mainSettingsService = Services.GetRequiredService<ISettingsService>();
            var mainSettings = mainSettingsService.Load();
            AccentTheme.Apply(mainSettings.AccentColor);
            NetworkPrivacy.Current.Apply(mainSettings);
            WindowTitleScanner.UpdateInspectionAllowed(mainSettings.EnableBrowserUrlInspection);
            WindowAnimationPolicy.SetInvisibleMode(mainSettings.InvisibleMode);
            WindowAnimationPolicy.SetShadowEnabled(mainSettings.EnableShadow);
            LocalizedPresentation.Initialize();
            Loc.SetLanguage(mainSettings.Language);
            AppIntegrityService.IntegrityAlertPrompt = ShowIntegrityConfirmationDialog;
            AnimationConfig.Configure(mainSettings.AnimationFps, mainSettings.AutoAnimationFps);

#if DEBUG
            if (isDebugIntegrityTest)
            {
                if (e.Args.Contains("--test-hash"))
                {
                    AppIntegrityService.ShowIntegrityAlert(
                        Loc.Get(IntegrityWarningTitleKey),
                        Loc.Get("integrity.hashMismatch", AppIntegrityService.GetAppVersion()));
                }
                else
                {
                    AppIntegrityService.ShowIntegrityAlert(
                        Loc.Get(IntegrityWarningTitleKey),
                        Loc.Get("integrity.untrustedSource", "https://untrusted.example.invalid/V-Notch.exe"));
                }
                Shutdown(0);
                return;
            }
#endif

            ApplyProcessPriority(mainSettings.ProcessPriority);
            SettingsWindow.ApplyGpuPreference(SettingsWindow.GetEffectiveGpuPreference(mainSettings));
            InteractionSounds.Initialize(mainSettings.EnableInteractionSounds, mainSettings.InteractionSoundVolume);

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // Let WPF present the shell before optional disk work and service warmup.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (IsShutdownRequested || Dispatcher.HasShutdownStarted) return;
            await RuntimeLog.InitializeNewSessionAsync("vnotch-debug.log");
            RuntimeLog.Log("SYSTEM", $"Application startup. Log file: {RuntimeLog.LogPath}");
            Services.GetRequiredService<AppFileCleanupService>().Start();
            if (mainSettings.SettingsVersion <= SettingsMigrator.CurrentVersion
                && mainSettings.LastRunVersion != AppVersion.Current)
            {
                mainSettings.LastRunVersion = AppVersion.Current;
                mainSettingsService.SaveAsync(mainSettings).SafeFireAndForget("POST-UPDATE-SAVE");
            }
            ServicePrewarmer.Prewarm(Services).SafeFireAndForget("PREWARM");

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            HandleStartupFailure(ex);
        }
    }

    internal static bool IsSetupOrUninstall(string[] args, string? processPath = null)
    {
        var exeName = System.IO.Path.GetFileNameWithoutExtension(processPath ?? Environment.ProcessPath ?? "");
        return args.Contains("--setup", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--update", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(TryGetArgumentValue(args, "--setup-source")) ||
            exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunSetupOrUninstallAsync(StartupEventArgs e, string language)
    {
        try
        {
            await HandleSetupOrUninstallAsync(e, language);
        }
        catch (Exception ex)
        {
            HandleStartupFailure(ex);
        }
    }

    private void HandleStartupFailure(Exception ex)
    {
        CrashReporter.LogCrash("App.OnStartup", ex, "Fatal exception during application startup", isTerminating: true);
        try
        {
            MessageBox.Show(
                $"V-Notch encountered a fatal error during startup and must close.\n\nCrash details saved to:\n{CrashReporter.CrashLogPath}\n\nError: {ex.Message}",
                "V-Notch Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // The crash report remains available if displaying the error fails.
        }
        Shutdown(1);
    }

    private async Task<bool> HandleSetupOrUninstallAsync(StartupEventArgs e, string language)
    {
        await Task.Yield();
        var setupSource = TryGetArgumentValue(e.Args, "--setup-source");
        var exeName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        bool isAutoUpdate = e.Args.Contains("--update", StringComparer.OrdinalIgnoreCase);
        var launchSetup = e.Args.Contains("--setup", StringComparer.OrdinalIgnoreCase) ||
            isAutoUpdate ||
            !string.IsNullOrWhiteSpace(setupSource) ||
            exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase);

        if (launchSetup)
        {
            var installerPath = TryGetArgumentValue(e.Args, "--installer-path") ?? Environment.ProcessPath ?? "";
            var (isTrusted, untrustedUrl) = AppIntegrityService.CheckDownloadOrigin(installerPath);
            if (!isTrusted && !string.IsNullOrWhiteSpace(untrustedUrl))
            {
                var options = new ConfirmationDialog.DialogOptions(
                    Title: Loc.Get(IntegrityWarningTitleKey),
                    ConfirmText: Loc.Get("integrity.openOfficialRepo"),
                    CancelText: Loc.Get("integrity.ignore"),
                    Icon: ConfirmationDialog.DialogIcon.Warning,
                    Style: ConfirmationDialog.DialogStyle.Danger);

                if (ConfirmationDialog.Show(null, Loc.Get("integrity.untrustedSource", untrustedUrl), options))
                {
                    SafeLauncher.TryOpenUrl(AppIntegrityService.OfficialReleasesUrl);
                    Shutdown(0);
                    return true;
                }
            }

            var setupWindow = new SetupWindow(setupSource, language, isAutoUpdate: isAutoUpdate);
            setupWindow.ShowDialog();
            Shutdown(setupWindow.ResultExitCode);
            return true;
        }

        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            bool silent = e.Args.Any(a =>
                a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
            SetupOperations.RunUninstallFlow(silent);
            return true;
        }

        return false;
    }

    private bool EnsureSingleInstance(StartupEventArgs e)
    {
        _guard = new SingleInstanceGuard(MutexName);
        bool ownsMutex = _guard.TryAcquire();

        if (!ownsMutex && e.Args.Contains("--restart"))
        {
            ownsMutex = _guard.TryWaitForPreviousInstance(TimeSpan.FromSeconds(10));
        }

        if (!ownsMutex)
        {
            MessageBox.Show(Loc.Get("error.alreadyRunning"), "V-Notch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return false;
        }

        return true;
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledDomainException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static async void OnDispatcherUnhandledException(object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        // MessageBox.Show runs a nested dispatcher loop; guard against recursive
        // error dialogs from queued UI callbacks while a fatal dialog is open.
        if (Volatile.Read(ref _fatalUiExceptionInProgress) != 0)
        {
            args.Handled = true;
            return;
        }

        if (IsRenderThreadFailure(args.Exception))
        {
            args.Handled = true;
            RestartAfterRenderThreadFailure(args.Exception);
            return;
        }

        if (IsRecoverableException(args.Exception))
        {
            CrashReporter.LogCrash("DispatcherUnhandledException (Recovered)", args.Exception,
                "Recovered a known animation or media exception on the UI dispatcher", isTerminating: false);
            RuntimeLog.Error("UNHANDLED-UI-RECOVERED", args.Exception,
                "Recovered a known animation or media exception on the UI dispatcher");
            args.Handled = true;
            return;
        }

        if (Interlocked.Exchange(ref _fatalUiExceptionInProgress, 1) != 0)
        {
            args.Handled = true;
            return;
        }

        args.Handled = true;
        CrashReporter.LogCrash("DispatcherUnhandledException (Fatal)", args.Exception,
            "Fatal UI dispatcher exception; shutting down to avoid continuing in an unknown state", isTerminating: true);
        RuntimeLog.Error("UNHANDLED-UI-FATAL", args.Exception,
            "Unexpected UI dispatcher exception; shutting down to avoid continuing in an unknown state");
        try
        {
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Best-effort log flush during fatal UI error; ignore timeouts or flush failures
        }

        try
        {
            string message = Loc.Get("app.fatalClose", $"{RuntimeLog.LogPath}\n{CrashReporter.CrashLogPath}");
            MessageBox.Show(
                message,
                "V-Notch Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            await RequestShutdownAsync(1);
        }
    }

    private static void OnUnhandledDomainException(object? sender, UnhandledExceptionEventArgs args)
    {
        CrashReporter.LogCrash("AppDomain.UnhandledException", args.ExceptionObject,
            "Background thread crash", isTerminating: args.IsTerminating);

        if (args.ExceptionObject is Exception ex)
        {
            RuntimeLog.Error("UNHANDLED-BG", ex, "Background thread crash");
        }
        else if (args.ExceptionObject != null)
        {
            RuntimeLog.Error("UNHANDLED-BG", $"Background thread crash with non-CLS object: {args.ExceptionObject}");
        }

        // CrashReporter above synchronously persists the fatal report. This
        // callback cannot delay CLR termination by awaiting the regular log queue.
        if (args.IsTerminating)
        {
            try
            {
                RuntimeLog.FlushAsync().SafeFireAndForget("CRASH-LOG-FLUSH");
            }
            catch (Exception)
            {
                // Best-effort flush before process termination; ignore flush failures
            }
        }
    }

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs args)
    {
        CrashReporter.LogCrash("TaskScheduler.UnobservedTaskException", args.Exception,
            "Unobserved task exception", isTerminating: false);
        RuntimeLog.Error("UNOBSERVED-TASK", args.Exception?.InnerException ?? args.Exception!,
            "Unobserved task exception");
        args.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        InteractionSounds.Shutdown();
        try { WhiteNoiseService.Shutdown(); } catch { /* Best-effort pause during shutdown. */ }
        try
        {
            if (!_servicesDisposed && Services is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _guard?.Dispose();
            VNotch.Controllers.MagnifierCaptureSource.ShutdownShared();
            if (!_servicesDisposed) RuntimeLog.Log("SYSTEM", "Application exit");
            base.OnExit(e);
        }
        finally
        {
            RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(2)).SafeFireAndForget("EXIT-LOG-DRAIN");
        }
    }

    public static void RestartApplication()
    {
        var app = Current as App;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(RestartApplication);
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        if (app?.IsShutdownRequested == true) return;

        try
        {
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--restart");
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("APP", ex, "Restart failed");
            return;
        }

        RequestShutdownAsync().SafeFireAndForget("APP-RESTART");
    }

    private const int UceErrRenderThreadFailure = unchecked((int)0x88980406);
    private static int _renderRestartRequested;

    internal static bool IsRenderThreadFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException { HResult: UceErrRenderThreadFailure };

    // Once WPF's render thread has died, nothing in this process draws again. Swallowing the error
    // leaves an invisible notch, so start a fresh instance instead. A failure right after start is
    // not retried, so a broken GPU state cannot turn into a restart loop.
    private static void RestartAfterRenderThreadFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref _renderRestartRequested, 1) != 0) return;

        var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
        bool restart = uptime > TimeSpan.FromSeconds(30);
        CrashReporter.LogCrash("DispatcherUnhandledException (RenderThread)", ex,
            restart ? "WPF render thread failed; restarting V-Notch" : "WPF render thread failed right after start; not restarting",
            isTerminating: restart);
        RuntimeLog.Error("RENDER-THREAD", ex, restart ? "Render thread failed; restarting" : "Render thread failed shortly after start");
        if (!restart) return;

        // The normal shutdown closes windows, which can stall without a render thread.
        // The new instance waits up to 10 s for the mutex, so exit hard well before that.
        var fallback = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(4));
            Environment.Exit(1);
        }) { IsBackground = true, Name = "RenderRestartFallback" };
        fallback.Start();
        RestartApplication();
    }

    private static bool IsRecoverableException(Exception ex)
    {
        return ex is RecoverableAnimationException
            or RecoverableMediaException
            or System.Runtime.InteropServices.COMException { HResult: not UceErrRenderThreadFailure };
    }

    private static string? TryGetArgumentValue(string[] args, string argumentName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, argumentName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                return null;
            }

            var prefix = argumentName + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    internal static void ApplyProcessPriority(string priority)
    {
        ProcessPriorityPolicy.Apply(priority);
    }

    private static bool ShowIntegrityConfirmationDialog(string title, string message)
    {
        var options = new ConfirmationDialog.DialogOptions(
            Title: title,
            ConfirmText: Loc.Get("integrity.openOfficialRepo"),
            CancelText: Loc.Get("integrity.ignore"),
            Icon: ConfirmationDialog.DialogIcon.Warning,
            Style: ConfirmationDialog.DialogStyle.Danger);
        return ConfirmationDialog.Show(Current?.MainWindow, message, options);
    }
}
