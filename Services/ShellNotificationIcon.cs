using System.Drawing;
using System.Runtime.InteropServices;
using DrawingPoint = System.Drawing.Point;
using Forms = System.Windows.Forms;

namespace MikuN2N.Services;

// Shell notifications support the elevated client. App SDK app notifications do not.
// Use Shell flags directly because NotifyIcon does not expose quiet-time or realtime flags.
internal sealed class ShellNotificationIcon : Forms.NativeWindow, ITrayNotificationIcon
{
    private const uint CallbackMessage = 0x8001;
    private const uint Info = 0x10;
    private const uint Realtime = 0x40;
    private const uint RespectQuietTime = 0x80;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private string _tooltip = "MikuN2N · 尚未连接";
    private bool _registered;
    private bool _disposed;

    public event EventHandler? Activated;
    public event EventHandler? NotificationClicked;
    public event EventHandler? NotificationEnded;

    public ShellNotificationIcon(Icon icon, Forms.ContextMenuStrip menu)
    {
        _icon = icon;
        _menu = menu;
        // A hidden top-level window receives Explorer's TaskbarCreated broadcast.
        CreateHandle(new Forms.CreateParams { Caption = "MikuN2N", ExStyle = 0x80 });
        // Explorer runs without elevation; admit only the two Shell messages we consume.
        ChangeWindowMessageFilterEx(Handle, CallbackMessage, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(Handle, _taskbarCreated, 1, IntPtr.Zero);
        _menu.Closed += Menu_Closed;
        AddIcon();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length <= 127 ? text : text[..127];
        if (!_registered) AddIcon();
        var data = CreateData(0x4);
        data.Tooltip = _tooltip;
        Shell_NotifyIcon(1, ref data);
    }

    public bool ShowNotification(string title, string text, bool realtime = false)
    {
        if (_disposed) return false;
        if (!_registered) AddIcon();
        var data = CreateData(Info | (realtime ? Realtime : 0));
        data.InfoTitle = title;
        data.InfoText = text;
        data.InfoFlags = RespectQuietTime;
        // No custom sound, urgency, or forced foreground activation; Shell owns presentation.
        return Shell_NotifyIcon(1, ref data);
    }

    private void AddIcon()
    {
        if (_disposed) return;
        var data = CreateData(0x1 | 0x2 | 0x4 | 0x80);
        data.Callback = CallbackMessage;
        data.Icon = _icon.Handle;
        data.Tooltip = _tooltip;
        _registered = Shell_NotifyIcon(0, ref data);
        if (_registered)
        {
            data.TimeoutOrVersion = 4;
            Shell_NotifyIcon(4, ref data);
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if ((uint)message.Msg == _taskbarCreated)
        {
            _registered = false;
            AddIcon();
            NotificationEnded?.Invoke(this, EventArgs.Empty);
        }
        else if ((uint)message.Msg == CallbackMessage)
        {
            switch ((int)((long)message.LParam & 0xffff))
            {
                case 0x400: // NIN_SELECT
                case 0x401: // NIN_KEYSELECT
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x405: // NIN_BALLOONUSERCLICK
                    NotificationClicked?.Invoke(this, EventArgs.Empty);
                    NotificationEnded?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x403: // NIN_BALLOONHIDE, including quiet-time suppression.
                case 0x404: // NIN_BALLOONTIMEOUT
                    NotificationEnded?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x7b: // WM_CONTEXTMENU; coordinates also cover keyboard activation.
                    SetForegroundWindow(Handle);
                    var coordinates = (long)message.WParam;
                    var position = new DrawingPoint((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
                    _menu.Show(position.X == -1 && position.Y == -1 ? Forms.Control.MousePosition : position);
                    PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
                    break;
            }
        }
        base.WndProc(ref message);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = Handle,
        Id = 1,
        Flags = flags,
        Tooltip = "",
        InfoText = "",
        InfoTitle = ""
    };

    private void Menu_Closed(object? sender, Forms.ToolStripDropDownClosedEventArgs args)
    {
        if (_disposed || !_registered) return;
        var data = CreateData(0);
        Shell_NotifyIcon(3, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menu.Closed -= Menu_Closed;
        if (_registered)
        {
            var data = CreateData(0);
            Shell_NotifyIcon(2, ref data);
        }
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string InfoText;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeFilter);
}
