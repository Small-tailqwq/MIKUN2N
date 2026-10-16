using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MikuN2N;
using MikuN2N.Models;
using MikuN2N.Services;

internal static class Ipv6CardReview
{
    private static int _checks;
    internal static void Run(string root, string output)
    {
        Directory.CreateDirectory(output);
        var now = DateTimeOffset.UtcNow;
        Ipv6NetworkStatus Read(string json) { using var doc = JsonDocument.Parse(json); return Ipv6NetworkStatusReader.Read(doc.RootElement, true, now); }
        Check(Read("{\"ipv6_enabled\":true,\"ipv6_nat66_supported\":true}") is { Availability: Ipv6Availability.Unverified, Mapping: Ipv6MappingKind.Unknown }, "support flag alone never proves NAT66 or connectivity");
        Check(Read("{}\n").Availability == Ipv6Availability.Unsupported, "old runtime does not become network unavailable");
        Check(Read("{\"ipv6_enabled\":false}").Availability == Ipv6Availability.Disabled, "runtime switch remains distinct from user preference");
        Check(Read("{\"ipv6_enabled\":true,\"ipv6_status\":\"unverified\",\"ipv6_address_kind\":\"global\",\"ipv6_mapping\":\"address-translated\"}") is { Availability: Ipv6Availability.Unverified, Mapping: Ipv6MappingKind.Unknown }, "unverified global address never establishes address translation");
        Check(Read("{\"ipv6_enabled\":true,\"ipv6_status\":false}").Availability == Ipv6Availability.Unverified, "malformed optional status remains unverified");
        var translated = Read("{\"ipv6_enabled\":true,\"ipv6_status\":\"available\",\"ipv6_address_kind\":\"ula\",\"ipv6_mapping\":\"address-translated\"}");
        Check(translated is { Availability: Ipv6Availability.Available, AddressKind: Ipv6AddressKind.UniqueLocal, Mapping: Ipv6MappingKind.AddressTranslated }, "confirmed translated mapping reaches the view model");
        Check(Ipv6NetworkStatusReader.Current(true, true, translated, now.AddSeconds(13)) is { Availability: Ipv6Availability.Unknown, Mapping: Ipv6MappingKind.Unknown }, "stale polling result cannot retain green availability or NAT66");
        Check(Ipv6NetworkStatusReader.Current(true, false, null, now).Availability == Ipv6Availability.Unsupported, "legacy edge reports unsupported");
        Check(Ipv6NetworkStatusReader.Current(true, true, null, now).Availability == Ipv6Availability.Checking, "initial poll is checking");
        Check(!Ipv6NetworkStatusReader.Current(false, true, translated, now).Requested, "disabled preference at session start stays disabled");

        var cases = new (string Key, string Caption, bool Enabled, Ipv6NetworkStatus? Status, string Title)[] {
            ("off", "IPv6 关闭", false, null, ""),
            ("checking", "正在探测", true, new(true, Ipv6Availability.Checking), "检测中"),
            ("global-unverified", "仅有全局地址", true, new(true, Ipv6Availability.Unverified, Ipv6AddressKind.Global), "待验证"),
            ("global-verified", "外部端点一致", true, new(true, Ipv6Availability.Available, Ipv6AddressKind.Global, Ipv6MappingKind.Untranslated), "可用"),
            ("nat66", "已确认地址转换", true, translated, "可用"),
            ("unavailable", "无地址或持续失败", true, new(true, Ipv6Availability.Unavailable), "不可用"),
            ("port-translation", "只确认端口转换", true, new(true, Ipv6Availability.Available, Ipv6AddressKind.Global, Ipv6MappingKind.PortTranslated), "可用"),
            ("ula-unverified", "仅有本地地址", true, new(true, Ipv6Availability.Unverified, Ipv6AddressKind.UniqueLocal), "待验证"),
            ("stale", "探测结果已过期", true, new(true, Ipv6Availability.Unknown), "状态待确认"),
            ("old-runtime", "旧版运行时", true, new(true, Ipv6Availability.Unsupported), "状态未报告"),
            ("next-connection", "当前连接未开启", true, new(false, Ipv6Availability.Disabled), "待启用"),
            ("disabled-runtime", "运行时未开启", true, new(true, Ipv6Availability.Disabled), "未启用")
        };
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ConnectionLayoutEntry.LoadResources(app, root);
        var evidence = new List<object>();
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                app.ThemeManager.Apply(theme);
                var window = new MainWindow();
                try
                {
                    window.Show(); window.Advance(ConnectionState.Connected); window.SettleStatus(); Pump(window);
                    var board = new DrawingVisual();
                    using (var drawing = board.RenderOpen())
                    {
                        drawing.DrawRectangle((Brush)app.Resources["WindowBackground"], null, new Rect(0, 0, 900, 774));
                        for (var i = 0; i < cases.Length; i++)
                        {
                            var item = cases[i]; window.SetNetwork(item.Enabled, item.Status); Pump(window);
                            var panel = (Border)window.FindName("Ipv6StatusPanel");
                            var title = (TextBlock)window.FindName("Ipv6TypeText");
                            var detail = (TextBlock)window.FindName("Ipv6ConditionText");
                            Check(panel.IsVisible == item.Enabled && title.Text == item.Title, theme + " " + item.Key + " has truthful status and conditional visibility");
                            Check(((TextBlock)window.FindName("NatTypeText")).Text == "NAT3", theme + " " + item.Key + " retains IPv4 state");
                            if (!item.Enabled) Check(!((TextBlock)window.FindName("Ipv4LabelText")).IsVisible && ((TextBlock)window.FindName("NatTypeText")).FontSize == 21,
                                theme + " disabled IPv6 preserves original IPv4 layout");
                            var badge = (Border)window.FindName("NatTypeBadge");
                            Check(title.ActualWidth <= 158 && badge.ActualWidth == 190 && detail.TextWrapping == TextWrapping.Wrap,
                                theme + " " + item.Key + " fits the existing card width");
                            var capture = Render(badge);
                            Save(capture, Path.Combine(output, theme + "-" + item.Key + ".png"));
                            var x = 18 + i % 4 * 222; var y = 14 + i / 4 * 254;
                            var label = new FormattedText(item.Caption, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                                new Typeface("Microsoft YaHei UI"), 13, (Brush)app.Resources["TextPrimary"], 1);
                            drawing.DrawText(label, new Point(x, y));
                            drawing.DrawImage(capture, new Rect(x, y + 27, capture.PixelWidth, capture.PixelHeight));
                            if (item.Key is "off" or "nat66") Save(Render((FrameworkElement)window.Content), Path.Combine(output, theme + "-home-" + item.Key + ".png"));
                            evidence.Add(new { theme = theme.ToString(), item.Key, title = title.Text, detail = detail.Text, visible = panel.IsVisible,
                                cardWidth = badge.ActualWidth, cardHeight = badge.ActualHeight });
                        }
                    }
                    var boardImage = new RenderTargetBitmap(900, 774, 96, 96, PixelFormats.Pbgra32); boardImage.Render(board);
                    Save(boardImage, Path.Combine(output, theme + "-states.png"));
                    window.Width = 760; window.SetNetwork(true, translated); Pump(window);
                    Check(((Border)window.FindName("NatTypeBadge")).ActualWidth == 190, theme + " dual-stack card retains width at minimum window size");
                    window.Advance(ConnectionState.Disconnected); window.SetNetwork(true, translated); Pump(window);
                    Check(((TextBlock)window.FindName("Ipv6TypeText")).Text == "待连接", theme + " disconnected state cannot retain a stale available result");
                    window.Advance(ConnectionState.Connecting); window.SetNetwork(true, translated); Pump(window);
                    Check(((TextBlock)window.FindName("Ipv6TypeText")).Text == "检测中", theme + " connecting state shows pending detection");
                }
                finally { window.Close(); }
            }
        }
        finally { app.ThemeManager.Dispose(); app.Shutdown(); }
        File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new {
            passed = true, assertions = _checks, cases = evidence, actualXaml = true, actualIpv6Presentation = true,
            actualManagementStatusReader = true, simulatedStatesOnly = true, realNetwork = false,
            realAppStartup = false, realSettings = false, offscreen = true, versionChanged = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Pump(Window window)
    {
        window.UpdateLayout(); var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame);
    }
    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message); _checks++; Console.WriteLine("PASS: " + message);
    }
}
