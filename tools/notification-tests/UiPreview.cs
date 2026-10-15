using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

internal static class UiPreview
{
    private static int _checks;

    internal static void Verify(string repository, string output)
    {
        repository = Path.GetFullPath(repository);
        Directory.CreateDirectory(output);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = XDocument.Load(Path.Combine(repository, "App.xaml"))
            .Descendants(presentation + "ResourceDictionary").First();
        resources.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        resources.SetAttributeValue(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib");
        foreach (var dictionary in resources.Descendants(presentation + "ResourceDictionary"))
            if (dictionary.Attribute("Source") is { } source)
                source.Value = new Uri(Path.Combine(repository, source.Value)).AbsoluteUri;
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString())
        };
        var sourceWindow = XDocument.Load(Path.Combine(repository, "MainWindow.xaml"));
        sourceWindow.Root!.Attribute(x + "Class")!.Remove();
        sourceWindow.Root.SetAttributeValue(XNamespace.Xmlns + "models",
            "clr-namespace:MikuN2N.Models;assembly=NotificationTests");
        var events = new HashSet<string>
        {
            "Click", "MouseEnter", "MouseLeave", "PreviewKeyDown", "LostKeyboardFocus", "MouseLeftButtonUp",
            "SelectionChanged", "TextChanged", "KeyDown", "PasswordChanged", "PreviewMouseWheel", "PreviewMouseRightButtonUp"
        };
        foreach (var element in sourceWindow.Descendants())
            foreach (var attribute in element.Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray())
                attribute.Remove();
        var window = (Window)XamlReader.Parse(sourceWindow.ToString());
        window.Title = "Notification controls preview (isolated)";
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Width = 760;
        window.Height = 820;
        var button = (Button)window.FindName("UpdateReadyButton");
        var badge = (Border)window.FindName("VersionBadgeBorder");
        var requests = (Button)window.FindName("PeerAssistRequestsButton");
        button.Visibility = Visibility.Visible;
        badge.Visibility = Visibility.Collapsed;
        requests.Visibility = Visibility.Visible;
        requests.Content = "协助申请（2）";
        var clicked = false;
        button.Click += (_, _) => clicked = true;
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                using (var themeFile = File.OpenRead(Path.Combine(repository, "Themes", theme + ".xaml")))
                    app.Resources.MergedDictionaries[0] = (ResourceDictionary)XamlReader.Load(themeFile);
                window.Show();
                Layout(window);
                button.ApplyTemplate();
                var border = (Border)button.Template.FindName("ButtonBorder", button);
                Check(button.IsVisible && button.ActualWidth > 0, theme + " ready action is visible");
                Check(button.Foreground == app.Resources["TextPrimary"], theme + " ready action follows palette");
                Check(button.FocusVisualStyle == app.Resources["FocusRing"], theme + " keyboard focus uses themed ring");
                var normal = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                Check(normal.Right < window.ActualWidth - 20 && normal.Left > 0, theme + " minimum-width header fits");
                Capture(window, Path.Combine(output, theme + "-normal.png"));
                SetReadOnly(button, typeof(UIElement), "IsMouseOverPropertyKey", true);
                Layout(window);
                Check(border.Background == app.Resources["ButtonHover"], theme + " hover uses palette");
                Capture(window, Path.Combine(output, theme + "-hover.png"));
                SetReadOnly(button, typeof(ButtonBase), "IsPressedPropertyKey", true);
                Layout(window);
                Check(border.Background == app.Resources["ButtonPressed"], theme + " pressed uses palette");
                Capture(window, Path.Combine(output, theme + "-pressed.png"));
                SetReadOnly(button, typeof(ButtonBase), "IsPressedPropertyKey", false);
                SetReadOnly(button, typeof(UIElement), "IsMouseOverPropertyKey", false);
                button.IsEnabled = false;
                Layout(window);
                Check(border.Opacity < 1, theme + " disabled state is visible");
                Capture(window, Path.Combine(output, theme + "-disabled.png"));
                button.IsEnabled = true;
                clicked = false;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(clicked, theme + " action can be clicked");
                button.Visibility = Visibility.Collapsed;
                badge.Visibility = Visibility.Visible;
                Layout(window);
                Check(!button.IsVisible && badge.IsVisible, theme + " ready action closes back to version badge");
                button.Visibility = Visibility.Visible;
                badge.Visibility = Visibility.Collapsed;
                window.Hide();
                Check(!window.IsVisible, theme + " isolated window closes and reopens");
            }
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
        Console.WriteLine($"PASS: {_checks} isolated MainWindow XAML assertions in both themes; application constructor and handlers were not loaded.");
    }

    private static void SetReadOnly(DependencyObject target, Type owner, string field, bool value)
    {
        var key = (DependencyPropertyKey)owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        target.SetValue(key, value);
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
}
