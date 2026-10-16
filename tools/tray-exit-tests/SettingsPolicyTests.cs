using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Text.Json;
using MikuN2N;
using MikuN2N.Models;
using MikuN2N.Services;

internal static class SettingsPolicyTests
{
    internal static int Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TrayExitEntry.LoadResources(app, source);
        var owner = new MainWindow();
        owner.Show();
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            app.ThemeManager.Apply(theme);
            var settings = new SettingsWindow(new AppSettings { Theme = theme }, connected: true) { Owner = owner };
            var option = (CheckBox)settings.FindName("AutoConfigureNetworkBox");
            TestState.Check(option.IsChecked == true && option.IsEnabled, theme + " network option is enabled by default, editable without disconnect");
            settings.Loaded += (_, _) => settings.Dispatcher.BeginInvoke(() =>
            {
                option.IsChecked = false;
                settings.UpdateLayout();
                Capture(settings, Path.Combine(output, theme + "-network-settings.png"));
                Buttons(settings).First(button => button.Content as string == "保存").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }, DispatcherPriority.Background);
            TestState.Check(settings.ShowDialog() == true && !settings.SelectedAutoConfigureNetwork, theme + " real Settings Save returns the disabled option");
            TestState.Check(owner.Edge.Disposals == 0 && owner.IsVisible, theme + " saving network preference does not request disconnect or exit");

            var cancelled = new SettingsWindow(new AppSettings { Theme = theme, AutoConfigureNetwork = false }) { Owner = owner };
            TestState.Check(((CheckBox)cancelled.FindName("AutoConfigureNetworkBox")).IsChecked == false, theme + " saved disabled value is shown on reopen");
            cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(() =>
            {
                ((CheckBox)cancelled.FindName("AutoConfigureNetworkBox")).IsChecked = true;
                cancelled.Close();
            });
            TestState.Check(cancelled.ShowDialog() != true && !cancelled.SelectedAutoConfigureNetwork, theme + " Cancel does not accept an edited preference");

            var node = new SupernodeNode { Name = "Fixture", Server = "node.invalid:3076", Community = "fixture" };
            var profile = TestBuildProfile.ForNode(node, "node.invalid", new string('A', 64));
            foreach (var persistent in new[] { false, true })
            {
                var consent = new LogUploadConsentDialog(profile, persistent, node);
                MainWindow.FitToWorkArea(consent);
                var text = ((TextBlock)consent.FindName("ConsentPolicyText")).Text;
                TestState.Check(!text.Contains("24") && text.Contains("服务提供商") && text.Contains("不能保证"), theme + " consent mode " + persistent + " discloses provider retention without fixed promise");
                consent.Show(); consent.UpdateLayout();
                if (!persistent) Capture(consent, Path.Combine(output, theme + "-consent.png"));
                consent.Close();
            }
            RenderMain(source, output, theme);
        }
        TestState.Check(owner.Edge.Disposals == 0, "policy UI does not execute edge teardown or network setup");
        app.Shutdown(); app.ThemeManager.Dispose();
        File.WriteAllText(Path.Combine(output, "policy-ui-validation.json"), JsonSerializer.Serialize(new {
            passed = true, assertions = TestState.Assertions, themes = new[] { "Light", "Dark" },
            actualSettingsAndConsentXamlAndHandlers = true, mainXamlLayoutOnly = true,
            realAppStartup = false, realSettings = false, realNetwork = false, systemChanges = false,
            screenshotCount = 6, offscreen = true, showActivated = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void RenderMain(string source, string output, ThemePreference theme)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(source, "MainWindow.xaml"));
        document.Root!.Attribute(x + "Class")!.Remove();
        document.Root.SetAttributeValue(XNamespace.Xmlns + "models", "clr-namespace:MikuN2N.Models;assembly=TrayExitTests");
        var events = new HashSet<string> { "Click", "MouseEnter", "MouseLeave", "PreviewKeyDown", "LostKeyboardFocus", "MouseLeftButtonUp",
            "SelectionChanged", "TextChanged", "KeyDown", "PasswordChanged", "PreviewMouseWheel", "PreviewMouseRightButtonUp" };
        foreach (var element in document.Descendants())
            foreach (var attribute in element.Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray()) attribute.Remove();
        var window = (Window)System.Windows.Markup.XamlReader.Parse(document.ToString());
        MainWindow.FitToWorkArea(window);
        window.Show(); window.UpdateLayout();
        var button = (Button)window.FindName("ConnectButton");
        TestState.Check(button.IsVisible && window.FindName("NetworkSetupNoticeText") is null, theme + " main connection action remains without the removed policy notice");
        Capture(window, Path.Combine(output, theme + "-main-notice.png"));
        window.Close();
    }

    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Button button) yield return button;
            foreach (var descendant in Buttons(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
}
