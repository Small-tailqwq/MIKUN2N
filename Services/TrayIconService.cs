using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
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
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Update(ConnectionSnapshot snapshot, bool isRunning)
    {
        _connectionItem.Text = isRunning ? "断开连接" : "连接";
        var state = snapshot.State switch
        {
            ConnectionState.Connected => $"已连接 · {snapshot.VirtualIp}",
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
        _notifyIcon.BalloonTipText = "双击托盘图标可恢复窗口，右键可连接、设置或退出。";
        _notifyIcon.ShowBalloonTip(3500);
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
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var fill = new SolidBrush(Color.FromArgb(54, 183, 180));
        using var path = new GraphicsPath();
        const float radius = 8;
        path.AddArc(1, 1, radius * 2, radius * 2, 180, 90);
        path.AddArc(31 - radius * 2, 1, radius * 2, radius * 2, 270, 90);
        path.AddArc(31 - radius * 2, 31 - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(1, 31 - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        graphics.FillPath(fill, path);
        using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("M", font, text, new RectangleF(0, 0, 32, 31), format);
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
