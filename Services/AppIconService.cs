using System.Runtime.InteropServices;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;

namespace MikuN2N.Services;

public static class AppIconService
{
    internal const string IconResourceName = "MikuN2N.AppIcon";
    internal const string AppUserModelId = "MikuN2N.Client";
    private static readonly Lazy<ImageSource> CachedIcon = new(CreateIcon);

    public static ImageSource Icon => CachedIcon.Value;

    public static void RegisterProcessIdentity()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // Older Windows shells can ignore an explicit application identity.
        }
    }

    private static ImageSource CreateIcon()
    {
        var icon = (DrawingImage)WpfApplication.Current.FindResource("MikuN2NLogo");
        icon.Freeze();
        return icon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
