using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfPoint = System.Windows.Point;

namespace MikuN2N.Services;

public static class AppIconService
{
    private const string AppUserModelId = "MikuN2N.Client";
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
        const int size = 64;
        const double pixelsPerDip = 1.0;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var fill = new LinearGradientBrush(
                MediaColor.FromRgb(76, 200, 196),
                MediaColor.FromRgb(39, 156, 154),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1));
            drawing.DrawRoundedRectangle(fill, null, new Rect(3, 3, 58, 58), 16, 16);

            var text = new FormattedText(
                "M",
                CultureInfo.InvariantCulture,
                WpfFlowDirection.LeftToRight,
                new Typeface(new MediaFontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                35,
                MediaBrushes.White,
                pixelsPerDip);
            drawing.DrawText(text, new WpfPoint((size - text.Width) / 2, (size - text.Height) / 2 - 1));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
