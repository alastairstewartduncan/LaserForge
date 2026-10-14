using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AsdLasercraft.Core;

namespace AsdLasercraft.App.Views;

/// <summary>
/// Start-up splash: a laser head runs along its gantry and "engraves" the A·S·D LASERCRAFT title, throwing
/// sparks from the beam spot, while the main window loads behind it. About two seconds end to end.
/// </summary>
public partial class SplashWindow : Window
{
    private const double EngraveSeconds = 1.85;
    private const double HoldSeconds = 0.35;

    private static readonly string[] Messages =
    {
        "Warming up the diode…",
        "Loading fonts…",
        "Focusing the beam…",
        "Aligning the gantry…",
        "Ready",
    };

    private readonly Stopwatch _clock = new();
    private readonly List<Spark> _sparks = new();
    private readonly Random _rng = new(7);
    private double _lastT;
    private double _textLeft, _textRight, _textTop, _textHeight;
    private bool _engraved, _finished;
    private Action? _onFinished;
    private string? _forcedStatus;

    private sealed class Spark
    {
        public required Ellipse Shape;
        public double X, Y, Vx, Vy, Life, MaxLife;
    }

    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        Loaded += OnLoaded;
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Overrides the rotating status line once loading has finished.</summary>
    public string Status
    {
        set => _forcedStatus = value;
    }

    /// <summary>Runs <paramref name="action"/> once the engraving animation has played (immediately if it already has).</summary>
    public void WhenFinished(Action action)
    {
        if (_finished) action();
        else _onFinished = action;
    }

    public void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() };
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        Layout();
        _clock.Start();
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Centres the title and works out where the beam has to travel.</summary>
    private void Layout()
    {
        TitlePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = TitlePanel.DesiredSize.Width, h = TitlePanel.DesiredSize.Height;
        double stageW = Stage.ActualWidth > 0 ? Stage.ActualWidth : Width - 50;
        double left = Math.Round((stageW - w) / 2);
        Canvas.SetLeft(TitlePanel, left);
        Canvas.SetLeft(EmberPanel, left);
        Rail.Width = stageW;
        _textLeft = left;
        _textRight = left + w;
        _textTop = Canvas.GetTop(TitlePanel);
        _textHeight = h;
        HeadShift.X = _textLeft - 70;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(t - _lastT, 0, 0.05);
        _lastT = t;

        double p = Math.Clamp(t / EngraveSeconds, 0, 1);
        double eased = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2; // ease in-out

        if (!_engraved)
        {
            // Head position along the gantry: lead-in, engrave across the title, lead-out.
            double x = _textLeft - 10 + (_textRight - _textLeft + 20) * eased;
            HeadShift.X = x - Head.Width / 2;

            // The beam spot raster-scans up and down across the letters as the head moves.
            double scan = 0.5 + 0.42 * Math.Sin(t * 38);
            double spotY = _textTop + 8 + (_textHeight - 16) * scan;
            double nozzleY = Canvas.GetTop(Head) + 52;
            Canvas.SetLeft(Beam, x - Beam.Width / 2);
            Canvas.SetTop(Beam, nozzleY);
            Beam.Height = Math.Max(1, spotY - nozzleY);
            Canvas.SetLeft(Dot, x - Dot.Width / 2);
            Canvas.SetTop(Dot, spotY - Dot.Height / 2);
            bool firing = p > 0.02 && p < 0.995;
            Beam.Opacity = firing ? 0.85 + 0.15 * Math.Sin(t * 90) : 0;
            Dot.Opacity = firing ? 1 : 0;

            // Reveal the burned text up to the beam, with a hot glowing band right behind it.
            double frac = Math.Clamp((x - _textLeft) / Math.Max(1, _textRight - _textLeft), 0, 1);
            RevealA.Offset = frac;
            RevealB.Offset = Math.Min(1, frac + 0.001);
            EmberA.Offset = Math.Max(0, frac - 0.07);
            EmberB.Offset = Math.Max(0, frac - 0.005);
            EmberC.Offset = Math.Min(1, frac + 0.001);

            if (firing)
            {
                int count = 2 + _rng.Next(3);
                for (int i = 0; i < count; i++) SpawnSpark(x, spotY);
            }

            ProgressFill.Width = ((FrameworkElement)ProgressFill.Parent).ActualWidth * eased;
            StatusText.Text = _forcedStatus != null && p >= 1 ? _forcedStatus : Messages[Math.Min(Messages.Length - 2, (int)(p * (Messages.Length - 1)))];

            if (p >= 1)
            {
                _engraved = true;
                RevealA.Offset = 1;
                RevealB.Offset = 1;
                EmberPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
                Led.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
                LedGlow.Color = Color.FromRgb(0x3D, 0xDC, 0x97);
                StatusText.Text = _forcedStatus ?? Messages[^1];
                ProgressFill.Width = ((FrameworkElement)ProgressFill.Parent).ActualWidth;
            }
        }

        UpdateSparks(dt);

        if (_engraved && !_finished && t >= EngraveSeconds + HoldSeconds && _sparks.Count == 0)
        {
            _finished = true;
            var callback = _onFinished;
            _onFinished = null;
            callback?.Invoke();
        }
    }

    private void SpawnSpark(double x, double y)
    {
        if (_sparks.Count > 160) return;
        double size = 1.5 + _rng.NextDouble() * 2.2;
        var shape = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(_rng.NextDouble() < 0.5 ? Color.FromRgb(0xFF, 0xD2, 0x8A) : Color.FromRgb(0xFF, 0x8A, 0x2A)),
            IsHitTestVisible = false,
        };
        double angle = -Math.PI / 2 + (_rng.NextDouble() - 0.5) * 2.6;  // mostly upward fan
        double speed = 90 + _rng.NextDouble() * 220;
        var s = new Spark
        {
            Shape = shape,
            X = x, Y = y,
            Vx = Math.Cos(angle) * speed,
            Vy = Math.Sin(angle) * speed,
            MaxLife = 0.3 + _rng.NextDouble() * 0.5,
        };
        s.Life = s.MaxLife;
        _sparks.Add(s);
        Sparks.Children.Add(shape);
    }

    private void UpdateSparks(double dt)
    {
        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.Life -= dt;
            if (s.Life <= 0)
            {
                Sparks.Children.Remove(s.Shape);
                _sparks.RemoveAt(i);
                continue;
            }
            s.Vy += 520 * dt;   // gravity
            s.Vx *= 1 - 1.5 * dt; // drag
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            Canvas.SetLeft(s.Shape, s.X);
            Canvas.SetTop(s.Shape, s.Y);
            s.Shape.Opacity = Math.Clamp(s.Life / s.MaxLife, 0, 1);
        }
    }
}
