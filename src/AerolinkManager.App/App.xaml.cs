using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Diagnostics;
using AerolinkManager.Core.Models;
using Forms = System.Windows.Forms;

namespace AerolinkManager.App;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstance;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private readonly AppPaths _paths = AppPaths.Default;
    private JsonFileStore? _store;
    private DispatcherTimer? _monitor;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new System.Threading.Mutex(true, SingleInstanceMutexName(), out var createdNew);
        _ownsSingleInstance = createdNew;
        if (!createdNew)
        {
            Environment.Exit(0);
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                Environment.SetEnvironmentVariable("WINDIR", systemRoot);
                Environment.SetEnvironmentVariable("windir", systemRoot);
            }
        }
        base.OnStartup(e);
        var logger = new AppLogger(_paths);
        try
        {
            logger.Write("app_start", "WPF startup entered");
            DispatcherUnhandledException += (_, args) =>
            {
                logger.Write("ui_error", args.Exception.ToString());
                args.Handled = true;
            };
            _store = new JsonFileStore(_paths);
            LocalizationService.Apply(_store.LoadConfig().Language);
            logger.Write("language", LocalizationService.CurrentLanguage);
            _window = new MainWindow();
            MainWindow = _window;
            logger.Write("main_window_created", "MainWindow constructor completed");
            _window.Loaded += (_, _) => logger.Write("main_window_loaded", "MainWindow Loaded event fired");
            if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
                logger.Write("smoke_test", $"window-handle-ready={handle != IntPtr.Zero}; language={LocalizationService.CurrentLanguage}");
                // Avoid WPF shutdown telemetry during smoke tests. On some locked-down or
                // framework-dependent Windows environments, graceful Application.Shutdown
                // can throw while logging PresentationFramework telemetry and show a native
                // crash dialog even though the window was created successfully.
                Environment.Exit(handle == IntPtr.Zero ? 1 : 0);
                return;
            }
            _window.Show();
            logger.Write("main_window_shown", $"visible={_window.IsVisible}; handle-ready={new System.Windows.Interop.WindowInteropHelper(_window).Handle != IntPtr.Zero}");
            CreateTray();
            _monitor = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, MonitorState, Dispatcher);
            _monitor.Start();
        }
        catch (Exception ex)
        {
            logger.Write("startup_error", ex.ToString());
            Environment.Exit(1);
            return;
        }
    }

    private static string SingleInstanceMutexName()
    {
        var suffix = Environment.GetEnvironmentVariable("CLAUDE_MANAGER_INSTANCE_SUFFIX");
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return @"Local\ClaudeManagerDesktopApp";
        }

        var safeSuffix = string.Concat(suffix.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        return string.IsNullOrWhiteSpace(safeSuffix)
            ? @"Local\ClaudeManagerDesktopApp"
            : $@"Local\ClaudeManagerDesktopApp.{safeSuffix}";
    }

    public void Notify(string title, string message)
    {
        _tray?.ShowBalloonTip(3500, title, message, Forms.ToolTipIcon.Info);
    }

    public void RefreshLocalization()
    {
        _tray?.Dispose();
        CreateTray();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        CleanupShell();
        Shutdown(0);
        Environment.Exit(0);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        CleanupShell();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupShell();
        if (_ownsSingleInstance)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void CleanupShell()
    {
        _monitor?.Stop();
        _tray?.Dispose();
        _tray = null;
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "Claude Manager",
            Icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SystemIcons.Application
                : SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.ContextMenuStrip.Items.Add(LocalizationService.Text("TrayShowStatus"), null, (_, _) => ShowStatus());
        _tray.ContextMenuStrip.Items.Add(LocalizationService.Text("TrayOpen"), null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add(LocalizationService.Text("TrayMarkFive"), null, (_, _) => MarkCurrentLimited());
        _tray.ContextMenuStrip.Items.Add(LocalizationService.Text("TrayReset"), null, (_, _) => ResetCurrent());
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add(LocalizationService.Text("TrayExit"), null, (_, _) => ExitApplication());
    }

    private void ShowWindow()
    {
        _window?.Show();
        _window?.Activate();
        _window?.RefreshData();
    }

    private void ShowStatus()
    {
        var config = _store!.LoadConfig();
        var available = config.Keys.Count(key => key.Enabled && key.Status is KeyStatus.Available or KeyStatus.Active or KeyStatus.Unknown);
        Notify(LocalizationService.Text("AppTitle"), LocalizationService.Format("TrayStatus", available, config.Keys.Count, config.ManagedCommandEnabled ? LocalizationService.Text("StatusActive") : LocalizationService.Text("StatusDisabled")));
    }

    private void MarkCurrentLimited() => UpdateCurrent(key => key with
    {
        Status = KeyStatus.FiveHourLimited,
        FiveHourResetAt = DateTimeOffset.Now.AddHours(5),
        FiveHourResetEstimated = true
    });

    private void ResetCurrent() => UpdateCurrent(key => key with
    {
        Status = KeyStatus.Available,
        FiveHourResetAt = null,
        FiveHourResetEstimated = false,
        WeeklyBlockedUntil = null,
        WeeklyBlockedUnknown = false
    });

    private void UpdateCurrent(Func<ApiKeyRecord, ApiKeyRecord> update)
    {
        var current = _store!.LoadState().CurrentKeyId;
        if (current is null)
        {
            Notify(LocalizationService.Text("AppTitle"), LocalizationService.Text("NoCurrentKey"));
            return;
        }
        _store.UpdateConfig(config => config with { Keys = config.Keys.Select(key => key.Id == current ? update(key) : key).ToList() });
        _window?.RefreshData();
    }

    private void MonitorState(object? sender, EventArgs e)
    {
        try
        {
            // Keep background mode quiet. Explicit tray actions may show status, but
            // passive gateway/key/quota polling should never interrupt coding flow.
            _window?.RefreshDataIfVisible();
        }
        catch
        {
            // The UI exposes repository errors during explicit actions; tray polling stays non-disruptive.
        }
    }
}
