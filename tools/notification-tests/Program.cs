using System.Reflection;
using System.Windows.Threading;
using MikuN2N.Models;
using MikuN2N.Services;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main(string[] args)
    {
        using (var tray = Create(out var sink))
        {
            var requestsOpened = 0;
            var mainOpened = 0;
            tray.PeerAssistRequestsRequested += (_, _) => requestsOpened++;
            tray.ShowRequested += (_, _) => mainOpened++;
            tray.ShowPeerAssistRequestTip(Request("first"), 1);
            FlushRequests(tray);
            Check(sink.Shown.Count == 1 && sink.Shown[0].Realtime, "peer request uses realtime delivery");
            Check(requestsOpened == 0 && mainOpened == 0, "arrival never activates a window");
            sink.Activate();
            Check(mainOpened == 1 && requestsOpened == 0, "tray icon activation opens only the main window");
            tray.ShowUpdateReadyTip("0.0.1-2");
            Check(sink.Shown.Count == 1, "ready update waits for the active peer notification");
            sink.Click();
            Check(requestsOpened == 1 && mainOpened == 1, "peer notification click opens requests");
            Pump();
            Check(sink.Shown.Count == 2 && sink.Shown[1].Title.Contains("0.0.1-2"), "deferred update is delivered after peer click");
            sink.Click();
            Check(mainOpened == 2 && requestsOpened == 1, "update click opens main rather than requests");
            Pump();
        }
        foreach (var dismissal in new[] { "quiet-time suppression", "timeout" })
        {
            using var tray = Create(out var sink);
            tray.ShowPeerAssistRequestTip(Request("first"), 1);
            FlushRequests(tray);
            tray.ShowUpdateReadyTip("0.0.1-2");
            sink.End();
            Pump();
            Check(sink.Shown.Count == 2 && sink.Shown[1].Title.Contains("0.0.1-2"), $"{dismissal} releases deferred update");
        }
        using (var tray = Create(out var sink))
        {
            tray.ShowMinimizedTip();
            tray.ShowUpdateReadyTip("0.0.1-2");
            tray.ShowPeerAssistRequestTip(Request("expired"), 2);
            tray.ShowPeerAssistRequestTip(Request("remaining"), 2);
            tray.UpdatePeerAssistRequests([Request("remaining")]);
            sink.End();
            Pump();
            FlushRequests(tray);
            Check(sink.Shown.Count == 2 && sink.Shown[1].Title == "收到协助申请", "queued request precedes deferred update");
            Check(!sink.Shown[1].Text.Contains("2 份") && sink.Shown[1].Text.Contains("remaining"), "expired requests do not inflate the queued count");
            tray.UpdatePeerAssistRequests([]);
            Check(sink.Shown.Count == 2, "expiring the shown request does not retarget its callback");
            sink.End();
            Pump();
            Check(sink.Shown.Count == 3 && sink.Shown[2].Title.Contains("0.0.1-2"), "update survives request expiry");
        }
        using (var tray = Create(out var sink))
        {
            tray.ShowMinimizedTip();
            tray.ShowPeerAssistRequestTip(Request("expired"), 1);
            tray.ShowUpdateReadyTip("0.0.1-2");
            tray.UpdatePeerAssistRequests([]);
            sink.End();
            Pump();
            Check(sink.Shown.Count == 2 && sink.Shown[1].Title.Contains("0.0.1-2"), "expired queued request yields to update");
        }
        using (var tray = Create(out var sink))
        {
            sink.Succeed = false;
            tray.ShowPeerAssistRequestTip(Request("failed"), 1);
            FlushRequests(tray);
            sink.Succeed = true;
            tray.ShowUpdateReadyTip("0.0.1-2");
            Check(sink.Shown.Count == 2 && sink.Shown[1].Title.Contains("0.0.1-2"), "failed Shell submission does not latch peer state");
            tray.Dispose();
            sink.End();
            Pump();
            tray.ShowUpdateReadyTip("0.0.1-3");
            Check(sink.Shown.Count == 2, "disposed service never submits another notification");
        }
        if (args.Length == 2) UiPreview.Verify(args[0], args[1]);
        Console.WriteLine($"PASS: {_checks} notification assertions; fake sink only, no Shell registration, shortcut, network or edge process.");
    }

    private static TrayIconService Create(out FakeIcon sink)
    {
        sink = new FakeIcon();
        return new TrayIconService(sink);
    }

    private static PeerAssistRequest Request(string id) => new(id, "requester", id, "target", "target", "relay");

    private static void FlushRequests(TrayIconService tray) => typeof(TrayIconService)
        .GetMethod("ShowQueuedPeerAssistTip", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tray, null);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine($"PASS: {description}");
    }
}

internal sealed class FakeIcon : ITrayNotificationIcon
{
    internal readonly List<(string Title, string Text, bool Realtime)> Shown = [];
    internal bool Succeed = true;
    public event EventHandler? Activated;
    public event EventHandler? NotificationClicked;
    public event EventHandler? NotificationEnded;
    public void SetTooltip(string text) { }
    public bool ShowNotification(string title, string text, bool realtime = false)
    {
        Shown.Add((title, text, realtime));
        return Succeed;
    }
    internal void Activate() => Activated?.Invoke(this, EventArgs.Empty);
    internal void Click()
    {
        NotificationClicked?.Invoke(this, EventArgs.Empty);
        End();
    }
    internal void End() => NotificationEnded?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }
}

namespace MikuN2N.Services
{
    internal static class AppIconService
    {
        internal const string IconResourceName = "unused-in-isolated-tests";
    }
}
