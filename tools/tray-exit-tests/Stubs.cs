using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MikuN2N.Models;
using MikuN2N.Services;

namespace MikuN2N
{
    // Never executes App.OnStartup, creates a mutex, reads settings, or starts an edge.
    public sealed class App : Application
    {
        public ThemeManager ThemeManager { get; } = new();
        public EasterEggManager EasterEggs { get; } = new();
        public UpdateService Updates { get; } = new();
    }

    public partial class MainWindow : Window
    {
        private bool _closeInProgress, _trayExitRequested, _allowClose;
        private bool _starting = false, _connectionBusy = false;
        private Task? _settingsCompletion;
        private readonly EdgeController _edgeController = new();
        private readonly TrayIconService _trayIcon = new();
        private readonly UpdateService _updates = new();
        private readonly DispatcherTimer _logFlushTimer = new(), _uptimeTimer = new(), _updateTimer = new();
        private readonly TextBlock StatusTitle = new();
        internal int FocusRequests;
        internal EdgeController Edge => _edgeController;
        internal bool ExitPending => _trayExitRequested;
        internal bool CloseAllowed => _allowClose;
        internal Task RequestExit() => RequestTrayExitAsync();
        internal void SettingsCompletion(Task completion) => _settingsCompletion = completion;
        public MainWindow()
        {
            ShowActivated = false; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Top = -20000; Width = Height = 200;
            Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
            Closed += (_, _) => TestState.Trace.Add("owner.closed");
        }
        public static void FitToWorkArea(Window window)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = window.Top = -20000;
        }
        private bool FocusOpenDialog() { FocusRequests++; return true; }
        private Task FinishDiagnosticsAsync()
        {
            TestState.Trace.Add("diagnostics.finished");
            return Task.CompletedTask;
        }
    }

    public sealed class PeerAssistWindow : Window { }
    public sealed class NodeEditDialog : Window
    {
        public SupernodeNode Result { get; }
        public NodeEditDialog(SupernodeNode? node, IEnumerable<string> names) { Result = node ?? new(); }
    }
    public enum MessageKind { Info, Warning, Error, Question }
    public enum DialogChoice { Primary, Secondary, Cancel }
    // Prompt choices are controlled. Real SettingsWindow closing/validation and modal
    // ShowDialog frames run unchanged, with no prompt that can take the foreground.
    public static class ThemedMessageDialog
    {
        public static DialogChoice Choice = DialogChoice.Secondary;
        public static bool Confirmation = true;
        public static DialogChoice Choose(Window? owner, string message, string title, string primary,
            string secondary, string cancel = "取消", MessageKind kind = MessageKind.Question)
        {
            TestState.Trace.Add("prompt.nodes." + Choice);
            return Choice;
        }
        public static bool Confirm(Window? owner, string message, string title, string confirm = "确定",
            string cancel = "取消", MessageKind kind = MessageKind.Question)
        {
            TestState.Check(message.Contains("断开"), "exit confirmation discloses disconnection");
            TestState.Trace.Add("prompt.exit." + Confirmation);
            return Confirmation;
        }
        public static void Show(Window? owner, string message, string title, MessageKind kind = MessageKind.Info)
            => TestState.Trace.Add("prompt.validation");
    }
}

namespace MikuN2N.Services
{
    public static class AppIconService { public static ImageSource? Icon => null; }
    public static class ClipboardService { public static bool TrySetText(string text) => true; }
    public static class CrashLogService
    {
        public static void Record(string category, Exception exception, bool fatal) => TestState.Trace.Add("error." + category);
    }
    public sealed class EdgeController
    {
        public static IEnumerable<string> SplitServers(string servers) =>
            servers.Split([',', ';', '、', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        public bool IsRunning;
        public bool FailDispose;
        public int Disposals;
        public ValueTask DisposeAsync()
        {
            Disposals++; TestState.Trace.Add("edge.dispose");
            if (FailDispose) throw new IOException("Controlled fake cleanup failure");
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
        public static void KillLaunchedEdgeProcesses() => TestState.Trace.Add("edge.fallback.fake");
    }
    public sealed class TrayIconService : IDisposable
    {
        public void Dispose() => TestState.Trace.Add("tray.disposed");
    }
    public enum UpdateStage { Idle, Checking, UpToDate, Downloading, Ready, Failed }
    public sealed record TestRelease(string Version);
    public sealed class UpdateService
    {
        public bool IsConfigured => false;
        public static string? Repository => null;
        public UpdateStage Stage => UpdateStage.Idle;
        public TestRelease? Available => null;
        public double Progress => 0;
        public string? Error => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task CheckAndPrepareAsync(bool force = false) => throw new InvalidOperationException("No network in fixture");
        public void InstallStaged() => throw new InvalidOperationException("No update installation in fixture");
        public static void Relaunch() => throw new InvalidOperationException("No process launch in fixture");
    }
}
