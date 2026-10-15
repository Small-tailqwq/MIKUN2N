namespace MikuN2N.Services;

internal interface ITrayNotificationIcon : IDisposable
{
    event EventHandler? Activated;
    event EventHandler? NotificationClicked;
    event EventHandler? NotificationEnded;
    void SetTooltip(string text);
    bool ShowNotification(string title, string text, bool realtime = false);
}
