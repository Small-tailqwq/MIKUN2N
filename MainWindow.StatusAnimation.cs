using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MikuN2N.Models;

namespace MikuN2N;

public partial class MainWindow
{
    private ConnectionState? _animatedConnectionState;
    private string _statusBackgroundKey = "SurfaceAlt";

    private void InitializeStatusAnimation()
    {
        Loaded += (_, _) =>
        {
            UpdateStatusIndicator(_lastSnapshot.State);
            RefreshStatusMotion(transition: false);
        };
        IsVisibleChanged += (_, _) => RefreshStatusMotion(transition: false);
        StateChanged += (_, _) => RefreshStatusMotion(transition: false);
        SystemParameters.StaticPropertyChanged += StatusAnimationPreferenceChanged;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= StatusAnimationPreferenceChanged;
            StopStatusMotion();
        };
    }

    private void StatusAnimationPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            Dispatcher.InvokeAsync(() => RefreshStatusMotion(transition: false));
        }
    }

    private void UpdateStatusIndicator(ConnectionState state)
    {
        // Snapshots arrive every 1.5 seconds; only a state transition restarts the feedback.
        if (_animatedConnectionState == state)
        {
            return;
        }
        var (background, foreground, outline, geometry) = state switch
        {
            ConnectionState.Connecting => ("AccentSoft", "AccentText", "AccentText", "M 12,3 A 9,9 0 1 1 3,12"),
            ConnectionState.Reconnecting => ("WarningSoft", "TextPrimary", "Warning", "M 12,3 A 9,9 0 1 1 3,12"),
            ConnectionState.Connected => ("SuccessSoft", "SuccessText", "SuccessText", "M 5,12 L 10,17 L 20,6"),
            ConnectionState.Error => ("DangerSoft", "DangerText", "DangerText", "M 12,4 L 12,13 M 12,18 L 12,18.2"),
            _ => ("SurfaceAlt", "TextSecondary", "TextSecondary", "M 12,4 A 8,8 0 1 1 12,20 A 8,8 0 1 1 12,4")
        };
        var transition = _animatedConnectionState is not null;
        _animatedConnectionState = state;
        // Fade between resource-backed surfaces so a live theme switch also updates the overlay.
        StatusTransitionOverlay.SetResourceReference(Border.BackgroundProperty, _statusBackgroundKey);
        _statusBackgroundKey = background;
        StatusPanel.SetResourceReference(Border.BackgroundProperty, background);
        StatusIndicator.SetResourceReference(Border.BackgroundProperty, background);
        StatusIndicator.SetResourceReference(Border.BorderBrushProperty, outline);
        StatusGlyph.SetResourceReference(Shape.StrokeProperty, foreground);
        StatusGlyph.Data = Geometry.Parse(geometry);
        StatusGlyph.StrokeDashArray = state == ConnectionState.Connected ? new DoubleCollection { 12, 12 } : null;
        RefreshStatusMotion(transition);
    }

    private void RefreshStatusMotion(bool transition)
    {
        StopStatusMotion();
        if (!IsLoaded || !IsVisible || WindowState == WindowState.Minimized ||
            !SystemParameters.ClientAreaAnimation || _animatedConnectionState is not { } state)
        {
            return;
        }
        if (transition)
        {
            StatusTransitionOverlay.BeginAnimation(OpacityProperty, Ease(1, 0, 260));
            StatusTitle.BeginAnimation(OpacityProperty, Ease(0.4, 1, 220));
            StatusTitleOffset.BeginAnimation(TranslateTransform.YProperty, Ease(4, 0, 260));
            StatusGlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(0.8, 1, 300));
            StatusGlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(0.8, 1, 300));
        }
        switch (state)
        {
            case ConnectionState.Disconnected:
                StatusGlyph.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromSeconds(2.2))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
                break;
            case ConnectionState.Connecting:
            case ConnectionState.Reconnecting:
                StatusGlyphRotation.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    });
                break;
            case ConnectionState.Connected when transition:
                StatusGlyph.BeginAnimation(Shape.StrokeDashOffsetProperty, Ease(12, 0, 360));
                break;
            case ConnectionState.Error when transition:
                var shake = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                foreach (var (milliseconds, offset) in new[] { (0, 0d), (70, -3d), (140, 3d), (210, -2d), (280, 1d), (350, 0d) })
                {
                    shake.KeyFrames.Add(new EasingDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                        new SineEase { EasingMode = EasingMode.EaseInOut }));
                }
                StatusGlyphOffset.BeginAnimation(TranslateTransform.XProperty, shake);
                break;
        }
    }

    private static DoubleAnimation Ease(double from, double to, int milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

    private void StopStatusMotion()
    {
        StatusTransitionOverlay.BeginAnimation(OpacityProperty, null);
        StatusTitle.BeginAnimation(OpacityProperty, null);
        StatusTitleOffset.BeginAnimation(TranslateTransform.YProperty, null);
        StatusGlyph.BeginAnimation(OpacityProperty, null);
        StatusGlyph.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        StatusGlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StatusGlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        StatusGlyphRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        StatusGlyphOffset.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
