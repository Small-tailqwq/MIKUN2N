using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfControl = System.Windows.Controls.Control;
using WpfPoint = System.Windows.Point;

namespace MikuN2N.Services;

public sealed class EasterEggManager
{
    private WpfBrush? _rainbowBrush;
    private int _rollsSinceJackpot;

    public int FlyCount { get; private set; }
    public int SpiderCount { get; private set; }
    public bool IsJackpot { get; private set; }
    public int IsaacLatency { get; private set; } = -42;

    public event Action? FliesChanged;
    public event Action? SpidersChanged;
    public event Action<string>? JackpotActivated;

    public bool ShouldAwardJackpot()
    {
        _rollsSinceJackpot++;
        if (_rollsSinceJackpot < 20 && Random.Shared.Next(0, 18) != 0)
        {
            return false;
        }

        _rollsSinceJackpot = 0;
        return true;
    }

    public void AddFly()
    {
        FlyCount = Math.Min(FlyCount + 1, 12);
        FliesChanged?.Invoke();
    }

    public void AddSpider()
    {
        SpiderCount = Math.Min(SpiderCount + 1, 8);
        SpidersChanged?.Invoke();
    }

    // A blue fly rams a spider and both die, Isaac-style. Counts are global so
    // every window's overlay stays in sync; the visual controller that detected
    // the collision plays the death poofs locally.
    public bool TryKillFlyAndSpider()
    {
        if (FlyCount == 0 || SpiderCount == 0)
        {
            return false;
        }
        FlyCount--;
        SpiderCount--;
        FliesChanged?.Invoke();
        SpidersChanged?.Invoke();
        return true;
    }

    public void ActivateJackpot(string result)
    {
        IsJackpot = true;
        IsaacLatency = -Random.Shared.Next(13, 667);
        _rainbowBrush ??= CreateRainbowBrush();
        ApplyRainbowToAllWindows();
        JackpotActivated?.Invoke(result);
    }

    public void ApplyRainbow(Window window)
    {
        if (!IsJackpot)
        {
            return;
        }

        _rainbowBrush ??= CreateRainbowBrush();
        ApplyRainbowRecursive(window, _rainbowBrush);
    }

    private void ApplyRainbowToAllWindows()
    {
        if (WpfApplication.Current is null || _rainbowBrush is null)
        {
            return;
        }

        foreach (Window window in WpfApplication.Current.Windows)
        {
            ApplyRainbowRecursive(window, _rainbowBrush);
        }
    }

    private static void ApplyRainbowRecursive(DependencyObject element, WpfBrush brush)
    {
        switch (element)
        {
            case TextBlock text:
                text.Foreground = brush;
                break;
            case WpfControl control:
                control.Foreground = brush;
                break;
        }

        var children = VisualTreeHelper.GetChildrenCount(element);
        for (var index = 0; index < children; index++)
        {
            ApplyRainbowRecursive(VisualTreeHelper.GetChild(element, index), brush);
        }
    }

    private static WpfBrush CreateRainbowBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(-0.4, 0.5),
            EndPoint = new WpfPoint(1.4, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Repeat
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(255, 82, 119), 0.00));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(255, 191, 72), 0.18));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(116, 225, 111), 0.36));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(72, 211, 224), 0.54));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(104, 143, 255), 0.72));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(210, 105, 255), 0.90));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(255, 82, 119), 1.00));

        var transform = new TranslateTransform();
        brush.RelativeTransform = transform;
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-0.7, 0.7, TimeSpan.FromSeconds(1.8))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        return brush;
    }
}
