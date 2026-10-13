using System.Drawing;
using System.Diagnostics;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly ShellNotificationIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _connectionItem;
    private readonly Icon _icon;
    private readonly DispatcherTimer _requestTipTimer;
    private readonly Dictionary<string, PeerAssistRequest> _queuedRequests = [];
    private int _pendingRequestCount;
    private bool _disposed;
    private bool _balloonShowsPeerAssist;
    private string? _notifiedCircuitId;
    private long? _lastPeerAssistTip;

    public event EventHandler? ShowRequested;
    public event EventHandler? PeerAssistRequestsRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ConnectionRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _icon = CreateIcon();
        _showItem = new Forms.ToolStripMenuItem("显示 MikuN2N", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold)
        };
        _connectionItem = new Forms.ToolStripMenuItem("连接", null, (_, _) => ConnectionRequested?.Invoke(this, EventArgs.Empty));
        var settingsItem = new Forms.ToolStripMenuItem("设置与关于", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        var exitItem = new Forms.ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.AddRange([
            _showItem,
            new Forms.ToolStripSeparator(),
            _connectionItem,
            settingsItem,
            new Forms.ToolStripSeparator(),
            exitItem
        ]);

        _notifyIcon = new ShellNotificationIcon(_icon, _menu);
        _notifyIcon.Activated += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.NotificationClicked += (_, _) =>
        {
            if (_balloonShowsPeerAssist)
                PeerAssistRequestsRequested?.Invoke(this, EventArgs.Empty);
            else
                ShowRequested?.Invoke(this, EventArgs.Empty);
        };
        _requestTipTimer = new DispatcherTimer(DispatcherPriority.Background);
        _requestTipTimer.Tick += (_, _) => ShowQueuedPeerAssistTip();
    }

    public void Update(ConnectionSnapshot snapshot, bool isRunning)
    {
        _connectionItem.Text = isRunning ? "断开连接" : "连接";
        var state = snapshot.State switch
        {
            ConnectionState.Connected => $"已连接 · {snapshot.VirtualIp} · {snapshot.PeerCount} 位朋友在线",
            ConnectionState.Connecting => "正在连接",
            ConnectionState.Reconnecting => "正在恢复连接",
            ConnectionState.Error => "连接异常",
            _ => "尚未连接"
        };
        var tooltip = $"MikuN2N · {state}";
        _notifyIcon.SetTooltip(tooltip);
    }

    public void ShowMinimizedTip()
    {
        if (_balloonShowsPeerAssist) return;
        _balloonShowsPeerAssist = false;
        _notifyIcon.ShowNotification("MikuN2N 仍在后台运行", "点击托盘图标可恢复窗口，右键可连接、设置或退出。");
    }

    public void ShowUpdateReadyTip(string version)
    {
        if (_balloonShowsPeerAssist) return;
        _balloonShowsPeerAssist = false;
        _notifyIcon.ShowNotification($"MikuN2N {version} 已准备好", "点击这里打开窗口，选择是否现在更新。");
    }

    public void ShowPeerAssistRequestTip(PeerAssistRequest request, int pendingCount)
    {
        if (_disposed) return;
        _queuedRequests[request.CircuitId] = request;
        _pendingRequestCount = pendingCount;
        if (_requestTipTimer.IsEnabled) return;
        // Coalesce bursts, but keep later requests until the short cooldown ends.
        var delay = _lastPeerAssistTip is { } last ? 10 - Stopwatch.GetElapsedTime(last).TotalSeconds : 0;
        _requestTipTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.75, delay));
        _requestTipTimer.Start();
    }

    private void ShowQueuedPeerAssistTip()
    {
        _requestTipTimer.Stop();
        if (_disposed || _queuedRequests.Count == 0) return;
        var request = _queuedRequests.Values.Last();
        _queuedRequests.Clear();
        _lastPeerAssistTip = Stopwatch.GetTimestamp();
        _balloonShowsPeerAssist = true;
        _notifiedCircuitId = request.CircuitId;
        _notifyIcon.ClearNotification();
        var text = _pendingRequestCount > 1
            ? $"有 {_pendingRequestCount} 份协助申请。点击查看。"
            : $"{NotificationName(request.RequesterNickname)} 请求你协助与 {NotificationName(request.TargetNickname)} 联机。点击查看。";
        _notifyIcon.ShowNotification("收到协助申请", text, realtime: true);
    }

    public void UpdatePeerAssistRequests(IReadOnlyList<PeerAssistRequest> requests)
    {
        _pendingRequestCount = requests.Count;
        foreach (var id in _queuedRequests.Keys.ToArray())
            if (!requests.Any(request => request.CircuitId == id)) _queuedRequests.Remove(id);
        if (_queuedRequests.Count == 0) _requestTipTimer.Stop();
        if (_notifiedCircuitId is null || requests.Any(request => request.CircuitId == _notifiedCircuitId)) return;
        _notifyIcon.ClearNotification();
        _notifiedCircuitId = null;
        _balloonShowsPeerAssist = false;
    }

    private static string NotificationName(string name) =>
        new string(name.Where(character => !char.IsControl(character)).Take(40).ToArray());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _requestTipTimer.Stop();
        _queuedRequests.Clear();
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var stream = typeof(AppIconService).Assembly.GetManifestResourceStream(AppIconService.IconResourceName)!;
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
}
