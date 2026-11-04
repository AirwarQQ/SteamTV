// Animates the Home banner: petals blowing off the branch and across the whole banner, the
// branch swaying with its blossoms breathing, clouds drifting, the moon floating, stars
// twinkling, now and then a shooting star, lit windows flickering, the TV glow breathing.
// Everything runs on one storyboard that's paused whenever the banner isn't on screen (window in
// the tray, another page open), so it costs nothing then.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SteamTV.Controls
{
    public partial class NightScene : UserControl
    {
        private const int PetalCount = 22;
        private readonly Storyboard _story = new Storyboard();
        private bool _started;

        // Blossom centres on the branch (scene units) and their size.
        private static readonly double[,] BlossomSpots =
        {
            { 636, 30, 7.5 }, { 648, 40, 6.5 }, { 624, 44, 6.5 }, { 652, 84, 7.5 }, { 642, 72, 5.5 },
            { 582, 70, 6.5 }, { 560, 96, 6.5 }, { 596, 58, 5.5 }, { 668, 16, 6.5 }, { 674, 52, 6 }, { 610, 50, 5 },
        };

        public NightScene()
        {
            InitializeComponent();
            BuildAnimations();
            IsVisibleChanged += (s, e) => UpdateRunning();
            Loaded += (s, e) => UpdateRunning();
        }

        private void UpdateRunning()
        {
            if (IsVisible)
            {
                if (!_started) { _story.Begin(this, true); _started = true; }
                else _story.Resume(this);
            }
            else if (_started) _story.Pause(this);
            SetTicking(IsVisible);
        }

        private void BuildAnimations()
        {
            var rnd = new Random(7);   // fixed seed: the same choreography every launch
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            // Branch: a breezy sway around the point where it enters the frame.
            Add(new DoubleAnimation(-3.5, 3.0, Seconds(3.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                BranchSway, RotateTransform.AngleProperty);

            // Blossoms: five-petal flowers that breathe, each on its own beat.
            for (int i = 0; i < BlossomSpots.GetLength(0); i++)
                AddBlossom(BlossomSpots[i, 0], BlossomSpots[i, 1], BlossomSpots[i, 2], rnd, ease);

            // Moon floats, its halo breathes.
            Add(new DoubleAnimation(-4, 4, Seconds(7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonDrift, TranslateTransform.YProperty);
            Add(new DoubleAnimation(-3, 3, Seconds(11)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonDrift, TranslateTransform.XProperty);
            Add(new DoubleAnimation(0.92, 1.1, Seconds(4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonGlowScale, ScaleTransform.ScaleXProperty);
            Add(new DoubleAnimation(0.92, 1.1, Seconds(4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonGlowScale, ScaleTransform.ScaleYProperty);

            // TV glow breathes.
            Add(new DoubleAnimation(0.6, 1.0, Seconds(3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                TvGlow, OpacityProperty);

            // Stars twinkle; a few lit windows flicker slowly.
            foreach (UIElement star in Stars.Children)
                Add(new DoubleAnimation(0.2, 0.95, Seconds(1.4 + rnd.NextDouble() * 2.4))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = SecondsSpan(rnd.NextDouble() * 3), EasingFunction = ease },
                    star, OpacityProperty);
            int w = 0;
            foreach (UIElement window in Windows.Children)
                if (w++ % 3 == 0)
                    Add(new DoubleAnimation(1, 0.25, Seconds(2.5 + rnd.NextDouble() * 4))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = SecondsSpan(rnd.NextDouble() * 6), EasingFunction = ease },
                        window, OpacityProperty);

            // A shooting star every ~13 s: a quick streak down-right across the upper sky.
            var streak = new DoubleAnimationUsingKeyFrames { Duration = Seconds(13), RepeatBehavior = RepeatBehavior.Forever, BeginTime = SecondsSpan(4) };
            streak.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            streak.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
            streak.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9))));
            Add(streak, ShootingStar, OpacityProperty);
            AddStreakMove(ShootingMove, TranslateTransform.XProperty, 120);
            AddStreakMove(ShootingMove, TranslateTransform.YProperty, 36);

            BuildParticles();
            Timeline.SetDesiredFrameRate(_story, 30);   // slow, soft motion: 30 fps is enough and halves the cost
        }

        private void AddStreakMove(TranslateTransform t, DependencyProperty p, double distance)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = Seconds(13), RepeatBehavior = RepeatBehavior.Forever, BeginTime = SecondsSpan(4) };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(distance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
            Add(a, t, p);
        }

        private void AddBlossom(double cx, double cy, double r, Random rnd, IEasingFunction ease)
        {
            var petals = new GeometryGroup();
            for (int k = 0; k < 5; k++)
            {
                double a = (k * 72 - 90) * Math.PI / 180;
                petals.Children.Add(new EllipseGeometry(new Point(cx + Math.Cos(a) * r * 0.55, cy + Math.Sin(a) * r * 0.55), r * 0.52, r * 0.52));
            }
            var scale = new ScaleTransform(1, 1, cx, cy);
            var flower = new Path { Data = petals, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0xD4)), RenderTransform = scale };
            Blossoms.Children.Add(flower);
            Blossoms.Children.Add(new Ellipse { Width = r * 0.55, Height = r * 0.55, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x7E, 0xB6)) });
            Canvas.SetLeft(Blossoms.Children[Blossoms.Children.Count - 1], cx - r * 0.275);
            Canvas.SetTop(Blossoms.Children[Blossoms.Children.Count - 1], cy - r * 0.275);

            var breathe = Seconds(1.8 + rnd.NextDouble() * 1.6);
            var begin = SecondsSpan(rnd.NextDouble() * 2);
            Add(new DoubleAnimation(0.9, 1.08, breathe) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, EasingFunction = ease },
                scale, ScaleTransform.ScaleXProperty);
            Add(new DoubleAnimation(0.9, 1.08, breathe) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin, EasingFunction = ease },
                scale, ScaleTransform.ScaleYProperty);
        }

        // ---------------------------------------------------------------------------------
        // Petals and clouds: a small per-frame simulation rather than storyboards — each petal
        // keeps its own wind, wobble, spin and flip, and is re-spawned at the branch (or the right
        // edge) once it has blown across. Runs only while the banner is visible, ~40 updates/s.
        // ---------------------------------------------------------------------------------

        private sealed class Petal
        {
            public Path Shape; public TranslateTransform Move; public RotateTransform Spin; public ScaleTransform Flip;
            public double X, Y, Vx, Vy, Turn, FlipSpeed, WobbleAmp, WobbleFreq, Phase, Age, Size, Delay;
        }

        private sealed class Cloud { public TranslateTransform Move; public double X, Speed; }

        private static readonly Geometry PetalShape = Geometry.Parse("M0,4.5 C2.5,0.3 8.5,0.1 11,3.3 L9.8,4.5 L11,5.7 C8.5,8.9 2.5,8.7 0,4.5 Z");
        private readonly List<Petal> _petals = new List<Petal>();
        private readonly List<Cloud> _clouds = new List<Cloud>();
        private readonly Random _rnd = new Random(11);
        private TimeSpan _lastFrame = TimeSpan.Zero;
        private bool _ticking;

        private void BuildParticles()
        {
            for (int i = 0; i < PetalCount; i++)
            {
                var p = new Petal
                {
                    Move = new TranslateTransform(), Spin = new RotateTransform(), Flip = new ScaleTransform(),
                    Shape = new Path
                    {
                        Data = PetalShape, Width = 11, Height = 9, Stretch = Stretch.None, Opacity = 0,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        Fill = new SolidColorBrush(_rnd.Next(3) == 0 ? Color.FromRgb(0xFF, 0xB6, 0xD3) : Color.FromRgb(0xFF, 0xDD, 0xEA)),
                    },
                };
                p.Shape.RenderTransform = new TransformGroup { Children = { p.Flip, p.Spin, p.Move } };
                Petals.Children.Add(p.Shape);
                Respawn(p, firstTime: true);
                _petals.Add(p);
            }

            AddCloud(40, 34, 1.0, 7.5, 0.0);
            AddCloud(380, 88, 0.75, 5.5, 0.4);
            AddCloud(-120, 128, 1.25, 4.5, 0.7);
        }

        private void Respawn(Petal p, bool firstTime)
        {
            bool fromBranch = _rnd.NextDouble() < 0.65;
            p.X = fromBranch ? 565 + _rnd.NextDouble() * 115 : 690 + _rnd.NextDouble() * 20;
            p.Y = fromBranch ? 18 + _rnd.NextDouble() * 85 : -10 + _rnd.NextDouble() * 110;
            p.Vx = -(28 + _rnd.NextDouble() * 34);          // units per second, leftwards on the breeze
            p.Vy = 5 + _rnd.NextDouble() * 11;
            p.Turn = (_rnd.Next(2) == 0 ? -1 : 1) * (40 + _rnd.NextDouble() * 90);
            p.FlipSpeed = 2.5 + _rnd.NextDouble() * 4;
            p.WobbleAmp = 6 + _rnd.NextDouble() * 12;
            p.WobbleFreq = 0.5 + _rnd.NextDouble() * 0.7;
            p.Phase = _rnd.NextDouble() * Math.PI * 2;
            p.Size = 0.75 + _rnd.NextDouble() * 0.65;
            p.Spin.Angle = _rnd.Next(360);
            p.Age = 0;
            // On start, spread them out along their paths so the banner isn't empty for a while.
            p.Delay = firstTime ? 0 : _rnd.NextDouble() * 1.5;
            if (firstTime) Advance(p, _rnd.NextDouble() * 14);
        }

        private void Advance(Petal p, double dt)
        {
            if (p.Delay > 0) { p.Delay -= dt; p.Shape.Opacity = 0; return; }
            p.Age += dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Spin.Angle += p.Turn * dt;
            double wobble = Math.Sin(p.Age * p.WobbleFreq * Math.PI * 2 + p.Phase);
            p.Move.X = p.X + wobble * p.WobbleAmp * 0.4;
            p.Move.Y = p.Y + wobble * p.WobbleAmp;
            p.Flip.ScaleX = p.Size;
            p.Flip.ScaleY = p.Size * (0.15 + 0.85 * Math.Abs(Math.Cos(p.Age * p.FlipSpeed + p.Phase)));   // edge-on and back
            double fadeIn = Math.Min(1, p.Age / 0.6);
            double fadeOut = Math.Min(1, Math.Max(0, (p.X + 10) / 90)) * Math.Min(1, Math.Max(0, (200 - p.Y) / 45));
            // Softer over the text on the left, so it stays easy to read.
            double overText = p.Y > 175 ? 1 : Math.Min(1, Math.Max(0.3, 0.3 + (p.X - 230) / 260));
            p.Shape.Opacity = 0.95 * fadeIn * fadeOut * overText;
            if (p.X < -20 || p.Y > 205) Respawn(p, firstTime: false);
        }

        // A cloud is a few overlapping soft puffs drifting right; past the edge it re-enters left.
        private void AddCloud(double left, double top, double size, double speed, double tint)
        {
            var cloud = new Canvas { Opacity = 0.55 };
            var move = new TranslateTransform(left, top);
            cloud.RenderTransform = move;
            byte g = (byte)(0xE8 - tint * 40);
            for (int i = 0; i < 4; i++)
            {
                double w = (70 + _rnd.NextDouble() * 50) * size, h = (22 + _rnd.NextDouble() * 14) * size;
                var puff = new Ellipse
                {
                    Width = w, Height = h,
                    Fill = new RadialGradientBrush(Color.FromArgb(0x3A, 0xFF, g, 0xF4), Color.FromArgb(0x00, 0xFF, g, 0xF4)),
                };
                Canvas.SetLeft(puff, i * 34 * size);
                Canvas.SetTop(puff, (i % 2 == 0 ? 6 : 0) * size);
                cloud.Children.Add(puff);
            }
            Clouds.Children.Add(cloud);
            _clouds.Add(new Cloud { Move = move, X = left, Speed = speed });
        }

        private void SetTicking(bool on)
        {
            if (on == _ticking) return;
            _ticking = on;
            _lastFrame = TimeSpan.Zero;
            if (on) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
            double dt = (now - _lastFrame).TotalSeconds;
            if (dt < 1.0 / 32) return;          // ~30 updates a second is plenty for drifting petals
            _lastFrame = now;
            dt = Math.Min(dt, 0.1);             // after a hiccup, don't teleport
            foreach (var p in _petals) Advance(p, dt);
            foreach (var c in _clouds)
            {
                c.X += c.Speed * dt;
                if (c.X > 700) c.X = -260;
                c.Move.X = c.X;
            }
        }

        private void Add(Timeline animation, DependencyObject target, DependencyProperty property)
        {
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            _story.Children.Add(animation);
        }

        private static Duration Seconds(double s) => new Duration(TimeSpan.FromSeconds(s));
        private static TimeSpan? SecondsSpan(double s) => TimeSpan.FromSeconds(s);
    }
}
