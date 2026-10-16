using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MikuN2N;
using MikuN2N.Models;
using MikuN2N.Services;

internal static class StatusCardReview
{
    private static int _checks;
    private static readonly List<object> Evidence = new();

    internal static void Run(string repository, string output)
    {
        repository = Path.GetFullPath(repository);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ConnectionLayoutEntry.LoadResources(app, repository);
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                app.ThemeManager.Apply(theme);
                var window = new MainWindow();
                try
                {
                    window.Show(); Pump(window);
                    window.Advance(ConnectionState.Connected);
                    window.SettleStatus(); Pump(window);
                    var panel = Get<Border>(window, "StatusPanel");
                    var card = Get<Border>(window, "ConnectionStatusCard");
                    var node = Get<TextBlock>(window, "SupernodeText");
                    var nickname = Get<TextBlock>(window, "ConnectionIdentityText");
                    var disconnect = Get<Button>(window, "CompactDisconnectButton");
                    Check(NodeRows(panel) == 1 && node.Text == "节点：测试节点 · 20 ms", theme + " connected node and latency appear exactly once");
                    Check(nickname.Text == "昵称：FixtureUser" && nickname.IsVisible, theme + " nickname is a separate compact field");
                    Check(Get<TextBlock>(window, "RoomNameText").Text == "小组：fixture-group", theme + " community stays visible");
                    Check(!Get<Border>(window, "ConnectionConfigurationCard").IsVisible && disconnect.IsVisible, theme + " successful connection still collapses configuration");
                    Check(panel.Background == app.Resources["SuccessSoft"], theme + " preview uses real connected-state color");
                    window.SetServer("—"); Pump(window);
                    Check(node.Text == "节点：测试节点" && NodeRows(panel) == 1, theme + " unavailable latency falls back to configured node without duplication");
                    window.SetServer("203.0.113.8:3076 · 31 ms"); Pump(window);
                    Check(node.Text == "节点：203.0.113.8:3076 · 31 ms" && NodeRows(panel) == 1, theme + " a different active server endpoint is retained");
                    window.SetServer("测试节点 · 20 ms"); Pump(window);

                    var version = Get<TextBlock>(window, "VersionBadgeText");
                    var badge = Get<Border>(window, "VersionBadgeBorder");
                    var headerActions = badge.Parent as StackPanel;
                    Check(headerActions?.Orientation == Orientation.Horizontal && Grid.GetColumn(headerActions) == 2,
                        theme + " version is back in the original top-right header row");
                    Check(headerActions!.Children.IndexOf(badge) + 1 == headerActions.Children.IndexOf(Get<Button>(window, "SettingsButton")),
                        theme + " version is immediately before Settings");
                    Check(version.Text == "v" + BuildIdentity.Version && version.TextWrapping == TextWrapping.NoWrap && version.TextTrimming == TextTrimming.None,
                        theme + " full version string and original text style are retained");
                    Check(badge.Padding == new Thickness(12, 8, 12, 8) && badge.Margin == new Thickness(0, 0, 8, 0) && version.FontSize == 12,
                        theme + " version badge original spacing and font are restored");

                    disconnect.ApplyTemplate();
                    var buttonBorder = (Border)disconnect.Template.FindName("ButtonBorder", disconnect);
                    Check(buttonBorder.Background == app.Resources["SurfaceAlt"], theme + " disconnect has the normal neutral background");
                    Capture((FrameworkElement)window.Content, Path.Combine(output, theme + "-normal.png"));
                    Capture(card, Path.Combine(output, theme + "-card-normal.png"));
                    SetReadOnly(disconnect, typeof(UIElement), "IsMouseOverPropertyKey", true); Pump(window);
                    Check(buttonBorder.Background == app.Resources["DisconnectHover"], theme + " disconnect hover is clearly red");
                    Check(White(disconnect.Foreground) && Descendants(disconnect).OfType<TextBlock>().Any(t => t.Text == "断开连接" && White(t.Foreground)),
                        theme + " rendered hover label is white");
                    var hover = ((SolidColorBrush)buttonBorder.Background).Color;
                    var ratio = ContrastAgainstWhite(hover);
                    Check(ratio >= 4.5, theme + " hover text contrast is at least 4.5:1");
                    Capture((FrameworkElement)window.Content, Path.Combine(output, theme + "-hover.png"));
                    Capture(card, Path.Combine(output, theme + "-card-hover.png"));
                    SetReadOnly(disconnect, typeof(ButtonBase), "IsPressedPropertyKey", true); Pump(window);
                    Check(buttonBorder.Background == app.Resources["DisconnectPressed"] && White(disconnect.Foreground), theme + " pressed state is darker red with white text");
                    SetReadOnly(disconnect, typeof(ButtonBase), "IsPressedPropertyKey", false);
                    disconnect.IsEnabled = false; Pump(window);
                    Check(buttonBorder.Background == app.Resources["SurfaceAlt"] && buttonBorder.Opacity < 1,
                        theme + " disabled state overrides hover");
                    disconnect.IsEnabled = true;
                    SetReadOnly(disconnect, typeof(UIElement), "IsMouseOverPropertyKey", false); Pump(window);
                    Check(window.DisconnectRequests == 0 && !Get<Border>(window, "ConnectionConfigurationCard").IsVisible,
                        theme + " hover and press styling do not invoke disconnection or unfold configuration");
                    Evidence.Add(new { theme = theme.ToString(), connectedBackground = ((SolidColorBrush)panel.Background).Color.ToString(),
                        node = node.Text, nickname = nickname.Text, hover = hover.ToString(), hoverTextContrast = Math.Round(ratio, 2),
                        version = version.Text, normal = theme + "-normal.png", hoverPreview = theme + "-hover.png" });
                }
                finally { window.Close(); Pump(window); }
            }
        }
        finally { app.ThemeManager.Dispose(); app.Shutdown(); }
        File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new {
            passed = true, assertions = _checks, evidence = Evidence, actualMainXaml = true,
            actualConnectionLayoutAndStatusAnimation = true, simulatedConnectionSnapshots = true,
            unrelatedEventHandlers = "isolated stubs", realAppStartup = false, realSettings = false,
            realNetwork = false, realTray = false, offscreen = true, showActivated = false,
            systemMouseOrKeyboardMovement = false, packageGenerated = false, screenshots = 8
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T Get<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static int NodeRows(DependencyObject panel) => Descendants(panel).OfType<TextBlock>().Count(t => t.IsVisible && t.Text.StartsWith("节点：", StringComparison.Ordinal));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static bool White(Brush brush) => brush is SolidColorBrush solid && solid.Color == Colors.White;
    private static double ContrastAgainstWhite(Color color)
    {
        static double Linear(byte channel) { var c = channel / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        var luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return 1.05 / (luminance + .05);
    }
    private static void SetReadOnly(DependencyObject target, Type owner, string field, bool value)
    {
        var key = (DependencyPropertyKey)owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        target.SetValue(key, value);
    }
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++; Console.WriteLine("PASS: " + description);
    }
}
