using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MikuN2N;
using MikuN2N.Models;

internal static class HeaderBoundaryReview
{
    internal static void Run(string root, string output, string version)
    {
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ConnectionLayoutEntry.LoadResources(app, root);
        var checks = new List<object>();
        try
        {
            app.ThemeManager.Apply(ThemePreference.Light);
            var window = new MainWindow();
            try
            {
                ((TextBlock)window.FindName("VersionBadgeText")).Text = "v" + version;
                window.Show();
                window.Advance(ConnectionState.Connected); window.SettleStatus();
                foreach (var width in new[] { 920d, 760d })
                {
                    window.Width = width;
                    window.UpdateLayout();
                    var frame = new DispatcherFrame();
                    window.Dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
                    Dispatcher.PushFrame(frame);
                    var badge = (Border)window.FindName("VersionBadgeBorder");
                    var settings = (Button)window.FindName("SettingsButton");
                    var header = (Grid)((StackPanel)badge.Parent).Parent;
                    var titles = header.Children.OfType<StackPanel>().Single(p => Grid.GetColumn(p) == 1);
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                    var title = titles.Children.OfType<TextBlock>().First();
                    var subtitle = titles.Children.OfType<TextBlock>().Last();
                    var titleRect = Bounds(title);
                    var versionRect = Bounds(badge);
                    var settingsRect = Bounds(settings);
                    var titleFits = title.DesiredSize.Width <= title.ActualWidth + .1 && subtitle.DesiredSize.Width <= subtitle.ActualWidth + .1;
                    var fits = titleFits && titleRect.Right <= versionRect.Left && versionRect.Right <= settingsRect.Left && settingsRect.Right <= window.ActualWidth - 20;
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(output, "header-" + width + ".png"))) png.Save(stream);
                    checks.Add(new { width, version, fits, titleFits, titleWidth = title.ActualWidth, titleDesired = title.DesiredSize.Width,
                        subtitleDesired = subtitle.DesiredSize.Width, versionLeft = versionRect.Left, versionRight = versionRect.Right,
                        settingsLeft = settingsRect.Left, settingsRight = settingsRect.Right });
                    Console.WriteLine(JsonSerializer.Serialize(checks[^1]));
                }
            }
            finally { window.Close(); }
        }
        finally { app.ThemeManager.Dispose(); app.Shutdown(); }
        File.WriteAllText(Path.Combine(output, "header-boundary.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
