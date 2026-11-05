// Animates the Home banner: petals blowing off the branch (one of three pictures, taking turns) and across the whole banner, the
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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace NekoCouch.Controls
{
    public partial class NightScene : UserControl
    {
        private const int PetalCount = 9;
        private readonly Storyboard _story = new Storyboard();
        private bool _started;

        // The sakura branch: three hand-drawn pictures (Assets/Branches) that take turns, cross-
        // fading now and then. Each: file, width in scene units, where its trunk meets the
        // picture's right edge (0..1 from the top), and the scene height the trunk enters at —
        // low enough that no top twig is cut off, high enough that the lowest flowers show.
        private static readonly (string File, double Width, double TrunkY, double EnterY)[] BranchArt =
        {
            ("branch_1.png", 290, 0.920, 152), ("branch_2.png", 255, 0.650, 115), ("branch_3.png", 245, 0.873, 152),
        };
        private const double BranchEnterX = 700;
        private const double BranchSwitchSec = 40, BranchFadeSec = 2.5;
        private readonly Image[] _branchImg = new Image[3];
        private readonly List<Point>[] _branchSpots = new List<Point>[3];
        private int _branchIdx;
        private double _branchClock;

        // Where the falling petals let go: blossoms of the branch on show.
        private List<Point> _blossomSpots = new List<Point>();
        public NightScene()
        {
            InitializeComponent();
            BuildAnimations();
            IsVisibleChanged += (s, e) => UpdateRunning();
            Loaded += (s, e) => UpdateRunning();
        }

        // Off: a still picture (no petals) and no work per frame at all — a setting for anyone who'd
        // rather keep the CPU idle.
        private bool _animate = true;
        public bool Animate
        {
            get => _animate;
            set { _animate = value; Petals.Visibility = value ? Visibility.Visible : Visibility.Collapsed; UpdateRunning(); }
        }

        private void UpdateRunning()
        {
            bool run = IsVisible && _animate;
            // Stop, not Pause: a paused storyboard keeps its clocks in WPF's timing tree, and the
            // render loop keeps ticking at full rate even with the window hidden in the tray.
            if (run && !_started) { _story.Begin(this, true); _started = true; }
            else if (!run && _started) { _story.Stop(this); _story.Remove(this); _started = false; }
            SetTicking(run);
        }

        private void BuildAnimations()
        {
            var rnd = new Random(7);   // fixed seed: the same choreography every launch
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            // Branch: a breezy sway around the point where it enters the frame.
            Add(new DoubleAnimation(-2.2, 1.8, Seconds(4.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                BranchSway, RotateTransform.AngleProperty);

            BuildBranches();

            // Moon floats, its halo breathes.
            Add(new DoubleAnimation(-7, 7, Seconds(9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonDrift, TranslateTransform.YProperty);
            Add(new DoubleAnimation(-5, 5, Seconds(13)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonDrift, TranslateTransform.XProperty);
            Add(new DoubleAnimation(0.85, 1.2, Seconds(4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                MoonGlowScale, ScaleTransform.ScaleXProperty);
            Add(new DoubleAnimation(0.85, 1.2, Seconds(4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
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

        // ---------------------------------------------------------------------------------
        // Branch pictures: all three loaded once, one shown at a time; the petals' starting points
        // are found in each picture's own pink pixels, so they always fall from real blossoms.
        // ---------------------------------------------------------------------------------

        private void BuildBranches()
        {
            Branch.CacheMode = new BitmapCache(2.5);   // the sway then only turns a cached picture
            _branchIdx = new Random().Next(BranchArt.Length);
            for (int i = 0; i < BranchArt.Length; i++)
            {
                var art = BranchArt[i];
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri("pack://application:,,,/Assets/Branches/" + art.File);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                double w = art.Width, h = w * bmp.PixelHeight / bmp.PixelWidth;
                double left = BranchEnterX - w, top = art.EnterY - art.TrunkY * h;
                var img = new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill, Opacity = i == _branchIdx ? 1 : 0 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(img, left);
                Canvas.SetTop(img, top);
                Branch.Children.Add(img);
                _branchImg[i] = img;
                _branchSpots[i] = FindBlossoms(bmp, left, top, w, h);
            }
            _blossomSpots = _branchSpots[_branchIdx];
        }

        private static List<Point> FindBlossoms(BitmapSource bmp, double left, double top, double w, double h)
        {
            var src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int pw = src.PixelWidth, ph = src.PixelHeight, stride = pw * 4;
            var px = new byte[stride * ph];
            src.CopyPixels(px, stride, 0);
            var pink = new List<Point>();
            for (int y = 0; y < ph; y += 3)
                for (int x = 0; x < pw; x += 3)
                {
                    int i = y * stride + x * 4;
                    byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                    if (a > 200 && r > 215 && r - g > 30 && b > g && b > 150)
                        pink.Add(new Point(left + x * w / pw, top + y * h / ph));
                }
            var rnd = new Random(3);
            var spots = new List<Point>();
            for (int k = 0; k < 60 && pink.Count > 0; k++) spots.Add(pink[rnd.Next(pink.Count)]);
            if (spots.Count == 0) spots.Add(new Point(left + w * 0.3, top + h * 0.3));
            return spots;
        }

        private void NextBranch()
        {
            var fade = new Duration(TimeSpan.FromSeconds(BranchFadeSec));
            _branchImg[_branchIdx].BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
            _branchIdx = (_branchIdx + 1) % BranchArt.Length;
            _branchImg[_branchIdx].BeginAnimation(OpacityProperty, new DoubleAnimation(1, fade));
            _blossomSpots = _branchSpots[_branchIdx];
        }
        // ---------------------------------------------------------------------------------
        // Petals and clouds: a small per-frame simulation rather than storyboards — each petal
        // keeps its own wind, wobble, spin and flip, and is re-spawned at the branch (or the right
        // edge) once it has blown across. Runs only while the banner is visible, every frame.
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
        private double _cloudDt;

        private void BuildParticles()
        {
            for (int i = 0; i < PetalCount; i++)
            {
                var p = new Petal
                {
                    Move = new TranslateTransform(), Spin = new RotateTransform(), Flip = new ScaleTransform(),
                    Shape = new Path
                    {
                        Data = PetalShape, Width = 11, Height = 9, Stretch = Stretch.None, Opacity = 0, CacheMode = new BitmapCache(6),
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        Fill = new SolidColorBrush(_rnd.Next(3) == 0 ? Color.FromRgb(0xFF, 0xB6, 0xD3) : Color.FromRgb(0xFF, 0xDD, 0xEA)),
                    },
                };
                p.Shape.RenderTransform = new TransformGroup { Children = { p.Flip, p.Spin, p.Move } };
                Petals.Children.Add(p.Shape);
                Respawn(p, firstTime: true);
                _petals.Add(p);
            }

            AddCloud(60, 30, 1.1, 4.0, 0.0);
            AddCloud(330, 74, 0.85, 3.0, 0.4);
            AddCloud(-140, 118, 1.3, 2.4, 0.7);
        }

        private void Respawn(Petal p, bool firstTime)
        {
            // Mostly let go of a blossom; now and then one drifts in from beyond the right edge.
            var spot = _blossomSpots[_rnd.Next(_blossomSpots.Count)];
            bool fromBlossom = _rnd.NextDouble() < 0.75;
            p.X = fromBlossom ? spot.X - 4 + _rnd.NextDouble() * 8 : 690 + _rnd.NextDouble() * 20;
            p.Y = fromBlossom ? spot.Y - 3 + _rnd.NextDouble() * 6 : _rnd.NextDouble() * 90;
            p.Vx = -(11 + _rnd.NextDouble() * 9);           // units per second: an unhurried drift to the left
            p.Vy = 2.5 + _rnd.NextDouble() * 4;
            p.Turn = (_rnd.Next(2) == 0 ? -1 : 1) * (12 + _rnd.NextDouble() * 28);
            p.FlipSpeed = 0.7 + _rnd.NextDouble() * 1.0;
            p.WobbleAmp = 5 + _rnd.NextDouble() * 7;
            p.WobbleFreq = 0.18 + _rnd.NextDouble() * 0.2;
            p.Phase = _rnd.NextDouble() * Math.PI * 2;
            p.Size = 0.75 + _rnd.NextDouble() * 0.65;
            p.Spin.Angle = _rnd.Next(360);
            p.Age = 0;
            // On start, spread them out along their paths so the banner isn't empty for a while.
            p.Delay = firstTime ? 0 : 1 + _rnd.NextDouble() * 4;
            if (firstTime) Advance(p, _rnd.NextDouble() * 30);
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
            double fadeIn = Math.Min(1, p.Age / 1.2);
            double fadeOut = Math.Min(1, Math.Max(0, (p.X + 10) / 90)) * Math.Min(1, Math.Max(0, (200 - p.Y) / 45));
            // Softer over the text on the left, so it stays easy to read.
            double overText = p.Y > 175 ? 1 : Math.Min(1, Math.Max(0.3, 0.3 + (p.X - 230) / 260));
            p.Shape.Opacity = 0.95 * fadeIn * fadeOut * overText;
            if (p.X < -20 || p.Y > 205) Respawn(p, firstTime: false);
        }

        // A cloud is a few overlapping soft puffs drifting right; past the edge it re-enters left.
        private void AddCloud(double left, double top, double size, double speed, double tint)
        {
            var cloud = new Canvas { Opacity = 0.9, CacheMode = new BitmapCache(2) };   // soft puffs: a cached picture, only moved
            var move = new TranslateTransform(left, top);
            cloud.RenderTransform = move;
            byte g = (byte)(0xE8 - tint * 40);
            for (int i = 0; i < 4; i++)
            {
                double w = (70 + _rnd.NextDouble() * 50) * size, h = (22 + _rnd.NextDouble() * 14) * size;
                var puff = new Ellipse
                {
                    Width = w, Height = h,
                    Fill = new RadialGradientBrush(Color.FromArgb(0x48, 0xF4, g, 0xFF), Color.FromArgb(0x00, 0xF4, g, 0xFF)),
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
            _lastFrame = now;
            dt = Math.Min(dt, 0.1);             // after a hiccup, don't teleport
            foreach (var p in _petals) Advance(p, dt);
            _branchClock += dt;
            if (_branchClock >= BranchSwitchSec) { _branchClock = 0; NextBranch(); }
            // Clouds crawl a few pixels a second: 15 moves a second look the same and cost less.
            _cloudDt += dt;
            if (_cloudDt >= 1.0 / 15)
            {
                foreach (var c in _clouds)
                {
                    c.X += c.Speed * _cloudDt;
                    if (c.X > 700) c.X = -260;
                    c.Move.X = c.X;
                }
                _cloudDt = 0;
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
