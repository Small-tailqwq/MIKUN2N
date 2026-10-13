using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MikuN2N.Services;

public static class AppIconService
{
    private const string AppUserModelId = "MikuN2N.Client";
    internal static readonly Uri IconUri = new("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute);
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

    // A frame from the icon decoder lets WPF pick the matching size for the title bar
    // and the taskbar instead of scaling one bitmap.
    private static ImageSource CreateIcon()
    {
        var frame = BitmapFrame.Create(IconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
