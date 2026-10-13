using System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _connectionItem;
    private readonly Icon _icon;
    private bool _disposed;

    public event EventHandler? ShowRequested;
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
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([
            _showItem,
            new Forms.ToolStripSeparator(),
            _connectionItem,
            settingsItem,
            new Forms.ToolStripSeparator(),
            exitItem
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "MikuN2N · 尚未连接",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _notifyIcon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
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
        _notifyIcon.Text = tooltip[..Math.Min(63, tooltip.Length)];
    }

    public void ShowMinimizedTip()
    {
        _notifyIcon.BalloonTipTitle = "MikuN2N 仍在后台运行";
        _notifyIcon.BalloonTipText = "点击托盘图标可恢复窗口，右键可连接、设置或退出。";
        _notifyIcon.ShowBalloonTip(3500);
    }

    public void ShowUpdateReadyTip(string version)
    {
        _notifyIcon.BalloonTipTitle = $"MikuN2N {version} 已准备好";
        _notifyIcon.BalloonTipText = "点击这里打开窗口，选择是否现在更新。";
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        var resource = WpfApplication.GetResourceStream(AppIconService.IconUri)
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var stream = resource.Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
