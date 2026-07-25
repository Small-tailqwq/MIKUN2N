using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfLine = System.Windows.Shapes.Line;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfSize = System.Windows.Size;

namespace MikuN2N.Services;

public sealed class EasterEggVisualController : IDisposable
{
    private const double KillDistance = 11;
    private const double ChaseRadius = 340;

    private readonly Window _window;
    private readonly Canvas _overlay;
    private readonly EasterEggManager _manager;
    private readonly List<FlySprite> _flies = [];
    private readonly List<SpiderSprite> _spiders = [];
    private readonly List<Particle> _particles = [];
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private DateTime _lastFrameAt = DateTime.UtcNow;
    private WpfPoint _pointer;
    private bool _pointerValid;
    private bool _disposed;

    public EasterEggVisualController(Window window, Canvas overlay, EasterEggManager manager)
    {
        _window = window;
        _overlay = overlay;
        _manager = manager;
        _manager.FliesChanged += SyncFlies;
        _manager.SpidersChanged += SyncSpiders;
        _manager.JackpotActivated += ShowJackpot;
        _window.Loaded += Window_Loaded;
        _window.Closed += Window_Closed;
        _window.PreviewMouseMove += Window_PreviewMouseMove;
        _window.MouseLeave += Window_MouseLeave;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _manager.FliesChanged -= SyncFlies;
        _manager.SpidersChanged -= SyncSpiders;
        _manager.JackpotActivated -= ShowJackpot;
        _window.Loaded -= Window_Loaded;
        _window.Closed -= Window_Closed;
        _window.PreviewMouseMove -= Window_PreviewMouseMove;
        _window.MouseLeave -= Window_MouseLeave;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SyncFlies();
        SyncSpiders();
        _manager.ApplyRainbow(_window);
    }

    private void Window_Closed(object? sender, EventArgs e) => Dispose();

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointer = e.GetPosition(_overlay);
        _pointerValid = true;
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        _pointerValid = false;

    private void SyncFlies()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.Invoke(SyncFlies);
            return;
        }

        while (_flies.Count < _manager.FlyCount)
        {
            var sprite = CreateFly(_flies.Count);
            _flies.Add(sprite);
            _overlay.Children.Add(sprite.Visual);
        }
        while (_flies.Count > _manager.FlyCount)
        {
            RemoveFly(_flies[^1]);
        }
    }

    private void SyncSpiders()
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.Invoke(SyncSpiders);
            return;
        }

        while (_spiders.Count < _manager.SpiderCount)
        {
            var sprite = CreateSpider();
            _spiders.Add(sprite);
            _overlay.Children.Add(sprite.Visual);
        }
        while (_spiders.Count > _manager.SpiderCount)
        {
            RemoveSpider(_spiders[^1]);
        }
    }

    private void RemoveFly(FlySprite fly)
    {
        _flies.Remove(fly);
        _overlay.Children.Remove(fly.Visual);
    }

    private void RemoveSpider(SpiderSprite spider)
    {
        _spiders.Remove(spider);
        _overlay.Children.Remove(spider.Visual);
        foreach (var fly in _flies)
        {
            if (fly.TargetSpider == spider)
            {
                fly.TargetSpider = null;
            }
        }
    }

    private static FlySprite CreateFly(int index)
    {
        var visual = new Grid { Width = 20, Height = 16, IsHitTestVisible = false };
        var leftWing = new Ellipse
        {
            Width = 10,
            Height = 7,
            Fill = new SolidColorBrush(WpfColor.FromArgb(150, 167, 232, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var rightWing = new Ellipse
        {
            Width = 10,
            Height = 7,
            Fill = new SolidColorBrush(WpfColor.FromArgb(150, 167, 232, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        var body = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = new RadialGradientBrush(WpfColor.FromRgb(98, 206, 255), WpfColor.FromRgb(24, 91, 196)),
            Stroke = new SolidColorBrush(WpfColor.FromRgb(8, 47, 116)),
            StrokeThickness = 1.3,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = WpfHorizontalAlignment.Center
        };
        visual.Children.Add(leftWing);
        visual.Children.Add(rightWing);
        visual.Children.Add(body);
        return new FlySprite(visual, leftWing, rightWing, index * 1.37);
    }

    private SpiderSprite CreateSpider()
    {
        var visual = new Canvas { Width = 26, Height = 20, IsHitTestVisible = false };
        var legStroke = new SolidColorBrush(WpfColor.FromRgb(38, 26, 18));
        var legs = new List<WpfLine>(8);
        for (var side = 0; side < 2; side++)
        {
            for (var index = 0; index < 4; index++)
            {
                var spread = -1.05 + index * 0.7;
                var direction = side == 0 ? -1 : 1;
                var leg = new WpfLine
                {
                    X1 = 13,
                    Y1 = 11,
                    X2 = 13 + direction * (9 + Math.Abs(spread) * 2),
                    Y2 = 11 + Math.Sin(spread) * 6,
                    Stroke = legStroke,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    RenderTransformOrigin = new WpfPoint(0.5, 0.55),
                    RenderTransform = new RotateTransform()
                };
                legs.Add(leg);
                visual.Children.Add(leg);
            }
        }

        var body = new Ellipse
        {
            Width = 13,
            Height = 11,
            Fill = new RadialGradientBrush(WpfColor.FromRgb(88, 62, 46), WpfColor.FromRgb(36, 24, 18)),
            Stroke = new SolidColorBrush(WpfColor.FromRgb(20, 13, 9)),
            StrokeThickness = 1.2
        };
        Canvas.SetLeft(body, 6.5);
        Canvas.SetTop(body, 5.5);
        visual.Children.Add(body);

        var leftEye = new Ellipse { Width = 2.6, Height = 2.6, Fill = new SolidColorBrush(WpfColor.FromRgb(214, 40, 40)) };
        var rightEye = new Ellipse { Width = 2.6, Height = 2.6, Fill = new SolidColorBrush(WpfColor.FromRgb(214, 40, 40)) };
        Canvas.SetLeft(leftEye, 9.4);
        Canvas.SetTop(leftEye, 8.2);
        Canvas.SetLeft(rightEye, 14);
        Canvas.SetTop(rightEye, 8.2);
        visual.Children.Add(leftEye);
        visual.Children.Add(rightEye);

        var (width, height) = OverlayBounds();
        var sprite = new SpiderSprite(visual, legs)
        {
            X = Random.Shared.NextDouble() * Math.Max(1, width - 40) + 20,
            Y = Random.Shared.NextDouble() * Math.Max(1, height - 40) + 20,
            IdleUntil = Elapsed() + Random.Shared.NextDouble() * 0.6
        };
        return sprite;
    }

    private (double Width, double Height) OverlayBounds() =>
        (Math.Max(_overlay.ActualWidth, 60), Math.Max(_overlay.ActualHeight, 60));

    private double Elapsed() => (DateTime.UtcNow - _startedAt).TotalSeconds;

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (_disposed || !_window.IsVisible ||
            _overlay.ActualWidth <= 0 || _overlay.ActualHeight <= 0)
        {
            return;
        }
        if (_flies.Count == 0 && _spiders.Count == 0 && _particles.Count == 0)
        {
            _lastFrameAt = DateTime.UtcNow;
            return;
        }

        var now = DateTime.UtcNow;
        var deltaSeconds = Math.Clamp((now - _lastFrameAt).TotalSeconds, 0, 0.05);
        _lastFrameAt = now;
        var elapsed = Elapsed();

        if (!_window.IsMouseOver)
        {
            _pointerValid = false;
        }

        UpdateSpiders(elapsed, deltaSeconds);
        UpdateFlies(elapsed);
        UpdateParticles(deltaSeconds);
        ResolveCollisions();
    }

    private void UpdateSpiders(double elapsed, double deltaSeconds)
    {
        var (width, height) = OverlayBounds();
        foreach (var spider in _spiders)
        {
            // Isaac spiders scuttle in short erratic dashes with pauses between.
            if (elapsed >= spider.DashUntil && elapsed >= spider.IdleUntil)
            {
                var angle = Random.Shared.NextDouble() * Math.Tau;
                var centerBias = Math.Atan2(height / 2 - spider.Y, width / 2 - spider.X);
                if (spider.X < 40 || spider.X > width - 40 || spider.Y < 40 || spider.Y > height - 40)
                {
                    angle = centerBias + (Random.Shared.NextDouble() - 0.5) * 1.6;
                }
                var speed = 240 + Random.Shared.NextDouble() * 150;
                spider.VelocityX = Math.Cos(angle) * speed;
                spider.VelocityY = Math.Sin(angle) * speed;
                spider.DashUntil = elapsed + 0.20 + Random.Shared.NextDouble() * 0.16;
                spider.IdleUntil = spider.DashUntil + 0.30 + Random.Shared.NextDouble() * 0.85;
            }

            var dashing = elapsed < spider.DashUntil;
            if (dashing)
            {
                spider.X += spider.VelocityX * deltaSeconds;
                spider.Y += spider.VelocityY * deltaSeconds;
                if (spider.X < 14 || spider.X > width - 14)
                {
                    spider.VelocityX = -spider.VelocityX;
                    spider.X = Math.Clamp(spider.X, 14, width - 14);
                }
                if (spider.Y < 14 || spider.Y > height - 14)
                {
                    spider.VelocityY = -spider.VelocityY;
                    spider.Y = Math.Clamp(spider.Y, 14, height - 14);
                }
            }

            Canvas.SetLeft(spider.Visual, spider.X - 13);
            Canvas.SetTop(spider.Visual, spider.Y - 10);
            spider.Visual.RenderTransform = new RotateTransform(
                dashing ? Math.Clamp(spider.VelocityX * 0.02, -10, 10) : 0, 13, 10);

            for (var index = 0; index < spider.Legs.Count; index++)
            {
                var wiggle = dashing
                    ? Math.Sin(elapsed * 26 + index * 1.35) * 9
                    : Math.Sin(elapsed * 3 + index * 1.35) * 1.5;
                ((RotateTransform)spider.Legs[index].RenderTransform).Angle = wiggle;
            }
        }
    }

    private void UpdateFlies(double elapsed)
    {
        var (width, height) = OverlayBounds();
        var pointer = _pointer;
        if (_pointerValid)
        {
            pointer.X = Math.Clamp(pointer.X, 10, Math.Max(10, width - 10));
            pointer.Y = Math.Clamp(pointer.Y, 10, Math.Max(10, height - 10));
        }

        for (var index = 0; index < _flies.Count; index++)
        {
            var fly = _flies[index];
            if (!fly.Initialized)
            {
                if (_pointerValid)
                {
                    fly.X = pointer.X + Random.Shared.Next(-45, 46);
                    fly.Y = pointer.Y + Random.Shared.Next(-45, 46);
                }
                else
                {
                    fly.X = Random.Shared.NextDouble() * (width - 40) + 20;
                    fly.Y = Random.Shared.NextDouble() * (height - 40) + 20;
                }
                fly.WanderTarget = new WpfPoint(fly.X, fly.Y);
                fly.Initialized = true;
            }

            double targetX;
            double targetY;
            double gain;
            double damping;
            if (fly.TargetSpider is { } prey)
            {
                // Latched onto a spider: dart straight at it.
                targetX = prey.X;
                targetY = prey.Y;
                gain = 0.045;
                damping = 0.90;
            }
            else if (_pointerValid)
            {
                var angle = elapsed * (1.9 + index * 0.04) + fly.Phase;
                var radius = 24 + index * 3.2;
                targetX = pointer.X + Math.Cos(angle) * radius;
                targetY = pointer.Y + Math.Sin(angle * 1.16) * (radius * 0.68) - 18;
                gain = 0.018;
                damping = 0.91;
            }
            else
            {
                // Cursor left the window: drift between random nearby waypoints
                // instead of homing to a stale position.
                var reachedTarget =
                    Math.Abs(fly.X - fly.WanderTarget.X) + Math.Abs(fly.Y - fly.WanderTarget.Y) < 26;
                if (reachedTarget || elapsed >= fly.WanderUntil)
                {
                    fly.WanderTarget = new WpfPoint(
                        Math.Clamp(fly.X + Random.Shared.Next(-140, 141), 16, width - 16),
                        Math.Clamp(fly.Y + Random.Shared.Next(-110, 111), 16, height - 16));
                    fly.WanderUntil = elapsed + 1.2 + Random.Shared.NextDouble() * 1.4;
                }
                targetX = fly.WanderTarget.X;
                targetY = fly.WanderTarget.Y + Math.Sin(elapsed * 5 + fly.Phase) * 6;
                gain = 0.010;
                damping = 0.93;
            }

            fly.VelocityX = (fly.VelocityX + (targetX - fly.X) * gain) * damping;
            fly.VelocityY = (fly.VelocityY + (targetY - fly.Y) * gain) * damping;
            fly.X += fly.VelocityX;
            fly.Y += fly.VelocityY;
            Canvas.SetLeft(fly.Visual, fly.X - 10);
            Canvas.SetTop(fly.Visual, fly.Y - 8);

            var flap = 0.35 + Math.Abs(Math.Sin(elapsed * 18 + fly.Phase)) * 0.65;
            fly.LeftWing.Opacity = flap;
            fly.RightWing.Opacity = flap;
            fly.Visual.RenderTransform = new RotateTransform(Math.Clamp(fly.VelocityX * 2.2, -18, 18), 10, 8);
        }
    }

    private void ResolveCollisions()
    {
        if (_spiders.Count == 0 || _flies.Count == 0)
        {
            return;
        }

        // Assign each spider at most one hunter fly from within the chase radius.
        foreach (var spider in _spiders)
        {
            if (_flies.Any(fly => fly.TargetSpider == spider))
            {
                continue;
            }
            var hunter = _flies
                .Where(fly => fly.TargetSpider is null)
                .OrderBy(fly => Distance(fly.X, fly.Y, spider.X, spider.Y))
                .FirstOrDefault();
            if (hunter is not null && Distance(hunter.X, hunter.Y, spider.X, spider.Y) <= ChaseRadius)
            {
                hunter.TargetSpider = spider;
            }
        }

        FlySprite? deadFly = null;
        SpiderSprite? deadSpider = null;
        foreach (var fly in _flies)
        {
            if (fly.TargetSpider is { } prey &&
                Distance(fly.X, fly.Y, prey.X, prey.Y) < KillDistance)
            {
                deadFly = fly;
                deadSpider = prey;
                break;
            }
        }

        if (deadFly is null || deadSpider is null)
        {
            return;
        }

        // TryKillFlyAndSpider synchronously raises the changed events, and the
        // sync handlers trim sprite lists from the END — so move the collided
        // pair to the end first to guarantee they are the ones removed.
        _flies.Remove(deadFly);
        _flies.Add(deadFly);
        _spiders.Remove(deadSpider);
        _spiders.Add(deadSpider);
        if (!_manager.TryKillFlyAndSpider())
        {
            return;
        }
        if (_flies.Contains(deadFly))
        {
            RemoveFly(deadFly);
        }
        if (_spiders.Contains(deadSpider))
        {
            RemoveSpider(deadSpider);
        }
        SpawnPoof(deadSpider.X, deadSpider.Y,
        [
            WpfColor.FromRgb(179, 32, 42), WpfColor.FromRgb(126, 18, 24),
            WpfColor.FromRgb(58, 36, 28), WpfColor.FromRgb(214, 40, 40)
        ]);
        SpawnPoof(deadFly.X, deadFly.Y,
        [
            WpfColor.FromRgb(158, 215, 242), WpfColor.FromRgb(95, 168, 223),
            WpfColor.FromRgb(255, 255, 255)
        ]);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // Isaac-style death poof: a burst of little gibs that fly out, fall and fade.
    private void SpawnPoof(double x, double y, WpfColor[] colors)
    {
        for (var index = 0; index < 9; index++)
        {
            var size = 2.5 + Random.Shared.NextDouble() * 2.5;
            var visual = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(colors[index % colors.Length]),
                IsHitTestVisible = false
            };
            var angle = Random.Shared.NextDouble() * Math.Tau;
            var speed = 45 + Random.Shared.NextDouble() * 85;
            var particle = new Particle(visual)
            {
                X = x,
                Y = y,
                VelocityX = Math.Cos(angle) * speed,
                VelocityY = Math.Sin(angle) * speed - 35,
                Life = 0.5 + Random.Shared.NextDouble() * 0.3
            };
            _particles.Add(particle);
            _overlay.Children.Add(visual);
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
        }
    }

    private void UpdateParticles(double deltaSeconds)
    {
        for (var index = _particles.Count - 1; index >= 0; index--)
        {
            var particle = _particles[index];
            particle.Life -= deltaSeconds;
            if (particle.Life <= 0)
            {
                _overlay.Children.Remove(particle.Visual);
                _particles.RemoveAt(index);
                continue;
            }
            particle.VelocityY += 260 * deltaSeconds;
            particle.X += particle.VelocityX * deltaSeconds;
            particle.Y += particle.VelocityY * deltaSeconds;
            particle.Visual.Opacity = Math.Clamp(particle.Life / 0.45, 0, 1);
            Canvas.SetLeft(particle.Visual, particle.X);
            Canvas.SetTop(particle.Visual, particle.Y);
        }
    }

    private void ShowJackpot(string result)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.Invoke(() => ShowJackpot(result));
            return;
        }

        _manager.ApplyRainbow(_window);
        SpawnConfetti();

        var label = new TextBlock
        {
            Text = $"🎰  JACKPOT · {result}  🎰",
            FontSize = 34,
            FontWeight = FontWeights.Black,
            Foreground = WpfBrushes.White,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = WpfColor.FromRgb(46, 207, 255),
                BlurRadius = 18,
                ShadowDepth = 0
            },
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.55, 0.55),
            IsHitTestVisible = false
        };
        _overlay.Children.Add(label);
        label.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, Math.Max(12, (_overlay.ActualWidth - label.DesiredSize.Width) / 2));
        Canvas.SetTop(label, Math.Max(30, _overlay.ActualHeight * 0.22));
        ((ScaleTransform)label.RenderTransform).BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.55, 1.12, TimeSpan.FromMilliseconds(420)) { AutoReverse = true });
        ((ScaleTransform)label.RenderTransform).BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.55, 1.12, TimeSpan.FromMilliseconds(420)) { AutoReverse = true });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(2.4))
        {
            BeginTime = TimeSpan.FromSeconds(1.1)
        };
        fade.Completed += (_, _) => _overlay.Children.Remove(label);
        label.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void SpawnConfetti()
    {
        var colors = new[]
        {
            WpfColor.FromRgb(255, 80, 120), WpfColor.FromRgb(255, 205, 70),
            WpfColor.FromRgb(75, 220, 190), WpfColor.FromRgb(80, 165, 255),
            WpfColor.FromRgb(205, 100, 255)
        };
        var centerX = _overlay.ActualWidth / 2;
        var centerY = _overlay.ActualHeight * 0.3;
        for (var index = 0; index < 42; index++)
        {
            var piece = new WpfRectangle
            {
                Width = Random.Shared.Next(5, 10),
                Height = Random.Shared.Next(8, 15),
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(colors[index % colors.Length]),
                RenderTransform = new RotateTransform(Random.Shared.Next(0, 360)),
                IsHitTestVisible = false
            };
            _overlay.Children.Add(piece);
            Canvas.SetLeft(piece, centerX);
            Canvas.SetTop(piece, centerY);
            var duration = TimeSpan.FromMilliseconds(Random.Shared.Next(1050, 1900));
            piece.BeginAnimation(Canvas.LeftProperty,
                new DoubleAnimation(centerX, centerX + Random.Shared.Next(-340, 341), duration));
            piece.BeginAnimation(Canvas.TopProperty,
                new DoubleAnimation(centerY, centerY + Random.Shared.Next(-180, 390), duration)
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromMilliseconds(450) };
            fade.Completed += (_, _) => _overlay.Children.Remove(piece);
            piece.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private sealed class FlySprite(Grid visual, Ellipse leftWing, Ellipse rightWing, double phase)
    {
        public Grid Visual { get; } = visual;
        public Ellipse LeftWing { get; } = leftWing;
        public Ellipse RightWing { get; } = rightWing;
        public double Phase { get; } = phase;
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public bool Initialized { get; set; }
        public WpfPoint WanderTarget { get; set; }
        public double WanderUntil { get; set; }
        public SpiderSprite? TargetSpider { get; set; }
    }

    private sealed class SpiderSprite(Canvas visual, List<WpfLine> legs)
    {
        public Canvas Visual { get; } = visual;
        public List<WpfLine> Legs { get; } = legs;
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double DashUntil { get; set; }
        public double IdleUntil { get; set; }
    }

    private sealed class Particle(Ellipse visual)
    {
        public Ellipse Visual { get; } = visual;
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double Life { get; set; }
    }
}
