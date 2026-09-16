using System.Windows;
using System.Threading;
using MikuN2N.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MikuN2N;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    public ThemeManager ThemeManager { get; } = new();
    public EasterEggManager EasterEggs { get; } = new();
    public LogCleanupService LogCleanup { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        HookExceptionLogging();
        AppIconService.RegisterProcessIdentity();
        _singleInstanceMutex = new Mutex(true, @"Local\MikuN2N.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(
                "MikuN2N 已经在运行。请使用现有窗口，避免两个连接同时占用虚拟网卡。",
                "MikuN2N",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load(out var upgraded);
        // A settings file written before the node model still carried the old server and
        // community fields. Write the converted result once so no address lingers on disk.
        // Saving needs the clear key, and a missing key means DPAPI could not decrypt it -
        // in that case rewriting would throw the remembered key away, so leave the file.
        if (upgraded)
        {
            var key = settingsStore.LoadKey(settings);
            if (!settings.RememberKey || !string.IsNullOrEmpty(key))
            {
                settingsStore.Save(settings, key);
            }
        }
        ThemeManager.Apply(settings.Theme);
        LogCleanup.Configure(settings.LogRetentionDays);
        base.OnStartup(e);
    }

    private void HookExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogService.Record("UI 线程", args.Exception, fatal: false);
            // Left unhandled this kills the process before n3n-edge is stopped, which
            // strands it on the TAP adapter and blocks the next launch.
            args.Handled = true;
            EdgeController.KillLaunchedEdgeProcesses();
            MessageBox.Show(
                "MikuN2N 遇到了意外错误，虚拟网络已断开，程序即将关闭。\n\n" +
                $"诊断信息已保存到：\n{CrashLogService.LogPath}",
                "MikuN2N",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLogService.Record(
                "后台线程",
                args.ExceptionObject as Exception,
                args.IsTerminating);
            if (args.IsTerminating)
            {
                EdgeController.KillLaunchedEdgeProcesses();
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogService.Record("未观察的后台任务", args.Exception, fatal: false);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        ThemeManager.Dispose();
        LogCleanup.Dispose();
        base.OnExit(e);
    }
}
