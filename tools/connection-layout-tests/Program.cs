using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MikuN2N;
using MikuN2N.Models;
using MikuN2N.Services;

internal static class ConnectionLayoutEntry
{
    private static int _checks;
    [STAThread]
    public static void Main(string[] args)
    {
        if (args[0] == "--header-review") { HeaderBoundaryReview.Run(args[1], args[2], args[3]); return; }
        if (args[0] == "--status-review") { StatusCardReview.Run(args[1], args[2]); return; }
        var root = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app, root);
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            app.ThemeManager.Apply(theme);
            var window = new MainWindow();
            window.Show(); Pump();
            Check(Shown(window, "ConnectionConfigurationCard") && !Shown(window, "ConnectionActionsPanel"), theme + " disconnected starts expanded");
            window.Advance(ConnectionState.Connecting); Pump();
            Check(Shown(window, "ConnectionConfigurationCard"), theme + " initial connection stays expanded");
            window.Advance(ConnectionState.Error); Pump();
            Check(Shown(window, "ConnectionConfigurationCard"), theme + " failed initial connection stays expanded");
            window.Advance(ConnectionState.Reconnecting); Pump();
            Check(Shown(window, "ConnectionConfigurationCard"), theme + " no successful connection means no automatic collapse");
            window.Advance(ConnectionState.Connected); Pump();
            Check(!Shown(window, "ConnectionConfigurationCard") && Shown(window, "ConnectionActionsPanel"), theme + " first success automatically collapses settings");
            var disconnect = (Button)window.FindName("CompactDisconnectButton");
            Check(disconnect.IsVisible && disconnect.IsEnabled && disconnect.Content as string == "断开连接", theme + " compact disconnect is available");
            var identity = ((TextBlock)window.FindName("ConnectionIdentityText")).Text;
            Check(((TextBlock)window.FindName("SupernodeText")).Text.Contains("测试节点") && identity.Contains("FixtureUser") && ((TextBlock)window.FindName("RoomNameText")).Text.Contains("fixture-group"), theme + " status retains node, nickname and community");
            Check(((TextBlock)window.FindName("VirtualIpText")).Text == "192.0.2.55" && ((TextBlock)window.FindName("PeerCountText")).Text == "8" &&
                ((TextBlock)window.FindName("DirectCountText")).Text == "6" && ((TextBlock)window.FindName("UptimeText")).Text == "00:12:34", theme + " layout does not replace connection metrics");
            Check(((DataGrid)window.FindName("PeersGrid")).MaxHeight > 228, theme + " collapsed configuration gives more height to friends");
            Check(((TextBlock)window.FindName("VersionBadgeText")).Text == "v" + BuildIdentity.Version && Shown(window, "VersionBadgeBorder"), theme + " complete build identity remains on home page");
            Capture(window, Path.Combine(output, theme + "-connected.png"));
            window.Advance(ConnectionState.Reconnecting); window.Advance(ConnectionState.Connecting); window.Advance(ConnectionState.Connected); Pump();
            Check(!Shown(window, "ConnectionConfigurationCard"), theme + " transient reconnect does not unfold layout");
            var toggle = (Button)window.FindName("ConnectionDetailsToggle");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check(Shown(window, "ConnectionConfigurationCard") && !((TextBox)window.FindName("NicknameBox")).IsEnabled, theme + " manual expansion remains read-only while connected");
            window.Advance(ConnectionState.Connected); window.Advance(ConnectionState.Reconnecting); window.Advance(ConnectionState.Connected); Pump();
            Check(Shown(window, "ConnectionConfigurationCard"), theme + " polling and reconnect preserve manual expansion");
            Capture(window, Path.Combine(output, theme + "-expanded.png"));
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.Advance(ConnectionState.Error); Pump();
            Check(Shown(window, "ConnectionConfigurationCard") && !Shown(window, "ConnectionActionsPanel"), theme + " terminal failure restores expanded configuration");
            window.Advance(ConnectionState.Connected); Pump();
            disconnect.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check(window.DisconnectRequests == 1 && Shown(window, "ConnectionConfigurationCard"), theme + " compact action reaches shared handler and disconnect restores expansion");
            window.Advance(ConnectionState.Connecting); window.Advance(ConnectionState.Connected); Pump();
            Check(!Shown(window, "ConnectionConfigurationCard"), theme + " a later successful session collapses again");
            window.Width = 760; Pump();
            var version = (TextBlock)window.FindName("VersionBadgeText");
            var bounds = version.TransformToAncestor(window).TransformBounds(new Rect(version.RenderSize));
            Check(bounds.Right < window.ActualWidth && version.Text == "v" + BuildIdentity.Version, theme + " full version fits minimum width without truncation");
            Check(version.TextWrapping == TextWrapping.NoWrap && version.TextTrimming == TextTrimming.None && version.ActualHeight > 0,
                theme + " complete version retains its original single-line style");
            ((Button)window.FindName("UpdateReadyButton")).Visibility = Visibility.Visible;
            Pump();
            Check(Shown(window, "VersionBadgeBorder") && ((Button)window.FindName("SettingsButton")).IsVisible,
                theme + " update-ready action and settings remain available beside complete version");
            Capture(window, Path.Combine(output, theme + "-minimum-width.png"));
            window.Close(); Pump();
        }
        var settings = XDocument.Load(Path.Combine(root, "SettingsWindow.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var privacy = settings.Descendants(p + "Button").Single(e => (string?)e.Attribute("Content") == "隐私政策");
        Check(privacy.Ancestors(p + "TabItem").Single().Attribute("Header")!.Value == "关于" &&
            (string?)privacy.Attribute("Tag") == "https://github.com/Small-tailqwq/MIKUN2N/blob/master/PRIVACY.md", "privacy link is only in About and targets the verified default branch");
        foreach (var name in new[] { "ExperimentalDiagnosticsBox", "DiagnosticCard", "DiagnosticUploadBox" })
            Check(settings.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name).Ancestors(p + "TabItem").Single().Attribute("Header")!.Value == "常规", name + " remains on its original tab");
        Check(!new AppSettings().ExperimentalDiagnosticUpload, "log upload remains off by default");
        Check(!File.ReadAllText(Path.Combine(root, "MainWindow.xaml")).Contains("隐私说明") && !settings.ToString().Contains("隐私与诊断"), "added first-level privacy material is removed");
        app.ThemeManager.Dispose(); app.Shutdown();
        File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new {
            passed = true, assertions = _checks, themes = new[] { "Light", "Dark" },
            actualMainXamlAndConnectionLayoutCode = true, otherMainHandlers = "isolated stubs", disconnectHandler = "fake shared handler",
            realAppStartup = false, realSettings = false, realNetwork = false, realTray = false,
            offscreen = true, showActivated = false, screenshots = 6
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static bool Shown(Window window, string name) => ((UIElement)window.FindName(name)).Visibility == Visibility.Visible;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++; Console.WriteLine("PASS: " + description);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
    internal static void LoadResources(App app, string root)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = XDocument.Load(Path.Combine(root, "App.xaml")).Descendants(p + "ResourceDictionary").First();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        dictionary.SetAttributeValue(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib");
        foreach (var item in dictionary.Descendants(p + "ResourceDictionary"))
            if (item.Attribute("Source") is { } uri) uri.Value = "pack://application:,,,/" + uri.Value;
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
}
