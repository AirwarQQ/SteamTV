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
        private const int PetalCount = 9;
        private readonly Storyboard _story = new Storyboard();
        private bool _started;

        // The branch, in scene units. Each limb is a run of (x, y, thickness) points; side limbs
        // start on the main one so the joins hide. Drawn as filled, tapering outlines.
        private static readonly double[][] Limbs =
        {
            // Main limb: in from the right edge low down, zigzagging up and left across the moon.
            new double[] { 724, 132, 13,   700, 122, 11,   682, 100, 9.6,   656, 92, 8.2,   638, 72, 6.9,   612, 66, 5.7,
                           594, 48, 4.6,   570, 44, 3.6,   553, 30, 2.6,   533, 26, 1.7,   518, 15, 0.8 },
            new double[] { 682, 100, 3.8,  690, 82, 2.8,   686, 66, 1.9,   692, 52, 1.0 },     // up from the first bend
            new double[] { 656, 92, 3.4,   642, 106, 2.4,  626, 110, 1.6,  612, 121, 0.8 },    // drooping down-left
            new double[] { 638, 72, 3.2,   642, 56, 2.3,   634, 42, 1.5,   640, 30, 0.8 },     // up past the moon's right
            new double[] { 612, 66, 2.6,   598, 78, 1.9,   582, 82, 1.2,   572, 92, 0.7 },     // under the moon
            new double[] { 570, 44, 2.0,   574, 32, 1.4,   568, 21, 0.9,   572, 12, 0.6 },     // over the top of the moon
        };

        // Flower clusters at the twig tips and a few nodes (x, y, flowers, flower radius).
        private static readonly double[,] Clusters =
        {
            { 692, 50, 3, 7.4 }, { 611, 123, 3, 7.0 }, { 640, 28, 3, 7.0 }, { 571, 94, 2, 6.4 },
            { 572, 10, 2, 6.0 }, { 516, 13, 2, 6.0 }, { 706, 116, 2, 6.6 }, { 600, 52, 2, 5.6 },
        };

        // The branch is drawn a little larger, scaled from where it enters the frame.
        private const double BranchScale = 1.12;
        private static readonly Point BranchAnchor = new Point(724, 132);
        // Where the falling petals let go: the front flowers' centres, filled while building.
        private readonly List<Point> _blossomSpots = new List<Point>();
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

            // Bark, then the flower clusters (each sways a little on its twig).
            BuildBark();
            for (int i = 0; i < Clusters.GetLength(0); i++)
                AddCluster(Clusters[i, 0], Clusters[i, 1], (int)Clusters[i, 2], Clusters[i, 3], rnd, ease);
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
        // Branch: tapering bark with a moonlit upper edge;
        // clusters of five-petalled flowers with stamens, some half-turned, some behind the twig.
        // ---------------------------------------------------------------------------------

        private void BuildBark()
        {
            Bark.CacheMode = new BitmapCache(4);
            var bark = new SolidColorBrush(Color.FromRgb(0x27, 0x16, 0x2F));
            var lit = new SolidColorBrush(Color.FromArgb(0xB0, 0x52, 0x37, 0x5C));
            for (int l = 0; l < Limbs.Length; l++)
            {
                var pts = Sample(Limbs[l]);
                Bark.Children.Add(new Path { Data = Outline(pts, -0.5, 0.5), Fill = bark });
                Bark.Children.Add(new Path { Data = Outline(pts, -0.46, -0.12), Fill = lit });   // the moonlit upper side
            }
        }

        // Smooth the limb's points (Catmull-Rom) into (x, y, thickness, normal x, normal y) samples.
        private static List<(double x, double y, double w, double nx, double ny)> Sample(double[] limb)
        {
            int n = limb.Length / 3;
            var raw = new List<(double x, double y, double w)>();
            for (int i = 0; i < n - 1; i++)
            {
                int i0 = Math.Max(0, i - 1), i2 = i + 1, i3 = Math.Min(n - 1, i + 2);
                for (int k = 0; k < 8; k++)
                {
                    double t = k / 8.0, t2 = t * t, t3 = t2 * t;
                    double Cr(int c) => 0.5 * (2 * limb[i * 3 + c] + (-limb[i0 * 3 + c] + limb[i2 * 3 + c]) * t
                        + (2 * limb[i0 * 3 + c] - 5 * limb[i * 3 + c] + 4 * limb[i2 * 3 + c] - limb[i3 * 3 + c]) * t2
                        + (-limb[i0 * 3 + c] + 3 * limb[i * 3 + c] - 3 * limb[i2 * 3 + c] + limb[i3 * 3 + c]) * t3);
                    raw.Add((Cr(0), Cr(1), limb[i * 3 + 2] + (limb[i2 * 3 + 2] - limb[i * 3 + 2]) * t));
                }
            }
            raw.Add((limb[(n - 1) * 3], limb[(n - 1) * 3 + 1], limb[(n - 1) * 3 + 2]));

            var result = new List<(double, double, double, double, double)>();
            for (int i = 0; i < raw.Count; i++)
            {
                var a = raw[Math.Max(0, i - 1)]; var b = raw[Math.Min(raw.Count - 1, i + 1)];
                double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) len = 1;
                // Normal pointing to the upper side (towards the moon).
                double nx = dy / len, ny = -dx / len;
                if (ny > 0) { nx = -nx; ny = -ny; }
                result.Add((raw[i].x, raw[i].y, raw[i].w, nx, ny));
            }
            return result;
        }

        // A band along the limb between two offsets (fractions of the thickness, negative = upper
        // side), closing to a point at the tip.
        private static Geometry Outline(List<(double x, double y, double w, double nx, double ny)> pts, double from, double to)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                Point P(int i, double f) => new Point(pts[i].x - pts[i].nx * pts[i].w * f, pts[i].y - pts[i].ny * pts[i].w * f);
                c.BeginFigure(P(0, from), true, true);
                var side = new List<Point>();
                for (int i = 1; i < pts.Count; i++) side.Add(P(i, from));
                for (int i = pts.Count - 1; i >= 0; i--) side.Add(P(i, to));
                c.PolyLineTo(side, false, true);
            }
            g.Freeze();
            return g;
        }

        // One petal pointing up from the origin, 10 units long, with the notch sakura petals have.
        private static readonly Geometry FlowerPetal = Geometry.Parse(
            "M0,0 C-2.6,-1.4 -5.3,-4.8 -4.7,-7.9 C-4.2,-10.3 -2.2,-10.9 -0.9,-10 L0,-8.7 L0.9,-10 C2.2,-10.9 4.2,-10.3 4.7,-7.9 C5.3,-4.8 2.6,-1.4 0,0 Z");

        private void AddCluster(double cx, double cy, int count, double size, Random rnd, IEasingFunction ease)
        {
            // Cached as bitmaps: the sway then only moves a picture instead of redrawing every
            // petal, gradient and stamen each frame. Rendered at the banner's on-screen scale.
            var front = new Canvas { CacheMode = new BitmapCache(4) }; var back = new Canvas { CacheMode = new BitmapCache(4) };
            var sway = new RotateTransform(0, cx, cy);
            front.RenderTransform = sway; back.RenderTransform = sway;
            Blossoms.Children.Add(front); BlossomsBack.Children.Add(back);

            // A bud or two on short stems, then the flowers, the biggest first and facing us.
            for (int b = 0; b < 1 + rnd.Next(2); b++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, d = size * (1.5 + rnd.NextDouble() * 0.5);
                double bx = cx + Math.Cos(ang) * d, by = cy + Math.Sin(ang) * d;
                back.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = bx, Y2 = by, Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0x2E, 0x4A)), StrokeThickness = 0.6 });
                var bud = new Ellipse { Width = size * 0.42, Height = size * 0.6, Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0x7E, 0xAE)),
                                        RenderTransform = new RotateTransform(ang * 180 / Math.PI + 90, size * 0.21, size * 0.3) };
                Canvas.SetLeft(bud, bx - size * 0.21); Canvas.SetTop(bud, by - size * 0.3);
                back.Children.Add(bud);
            }
            for (int i = 0; i < count; i++)
            {
                bool first = i == 0;
                bool behind = !first && rnd.NextDouble() < 0.4;
                double ang = rnd.NextDouble() * Math.PI * 2, d = first ? 0 : size * (0.9 + rnd.NextDouble() * 0.4);
                double fx = cx + Math.Cos(ang) * d, fy = cy + Math.Sin(ang) * d;
                double r = size * (first ? 1 : 0.75 + rnd.NextDouble() * 0.2) * (behind ? 0.85 : 1);
                double facing = first ? 0.92 + rnd.NextDouble() * 0.08 : 0.6 + rnd.NextDouble() * 0.35;   // squash = half-turned
                AddFlower(behind ? back : front, fx, fy, r, rnd.Next(72), facing, rnd.Next(180), behind);
                if (!behind) _blossomSpots.Add(new Point(BranchAnchor.X + (fx - BranchAnchor.X) * BranchScale, BranchAnchor.Y + (fy - BranchAnchor.Y) * BranchScale));
            }

            Add(new DoubleAnimation(-4, 4, Seconds(3.6 + rnd.NextDouble() * 2.4))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = SecondsSpan(rnd.NextDouble() * 3), EasingFunction = ease },
                sway, RotateTransform.AngleProperty);
        }

        private static void AddFlower(Canvas into, double cx, double cy, double radius, double spin, double facing, double facingAngle, bool behind)
        {
            double s = radius / 10.0;
            var petals = new GeometryGroup();
            for (int k = 0; k < 5; k++)
            {
                var p = FlowerPetal.Clone();
                p.Transform = new TransformGroup { Children = { new ScaleTransform(s, s), new RotateTransform(spin + k * 72) } };
                petals.Children.Add(p);
            }
            // Turn the whole flower a little away from us: squash along one direction.
            var view = new TransformGroup
            {
                Children =
                {
                    new RotateTransform(-facingAngle), new ScaleTransform(1, facing), new RotateTransform(facingAngle),
                    new TranslateTransform(cx, cy),
                },
            };
            var fill = behind
                ? new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromRgb(0xC9, 0x5E, 0x93), 0), new GradientStop(Color.FromRgb(0xDF, 0x98, 0xBC), 0.4), new GradientStop(Color.FromRgb(0xEA, 0xB9, 0xD0), 1) } }
                : new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromRgb(0xF2, 0x86, 0xB3), 0), new GradientStop(Color.FromRgb(0xFF, 0xCF, 0xE2), 0.38), new GradientStop(Color.FromRgb(0xFF, 0xF5, 0xF9), 1) } };
            fill.MappingMode = BrushMappingMode.Absolute;
            fill.Center = fill.GradientOrigin = new Point(0, 0);
            fill.RadiusX = fill.RadiusY = radius;
            into.Children.Add(new Path
            {
                Data = petals, Fill = fill, RenderTransform = view,
                Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xD2, 0x6E, 0x9E)), StrokeThickness = 0.3,
            });

            // Stamens: fine pink threads with pale yellow tips, a deep pink heart.
            var threads = new GeometryGroup(); var tips = new GeometryGroup();
            for (int k = 0; k < 7; k++)
            {
                double a = (spin + 18 + k * 360.0 / 7) * Math.PI / 180, len = radius * (0.34 + (k % 3) * 0.05);
                var end = new Point(Math.Sin(a) * len, -Math.Cos(a) * len);
                threads.Children.Add(new LineGeometry(new Point(0, 0), end));
                tips.Children.Add(new EllipseGeometry(end, radius * 0.06, radius * 0.06));
            }
            into.Children.Add(new Path { Data = threads, Stroke = new SolidColorBrush(Color.FromRgb(0xD9, 0x4F, 0x8A)), StrokeThickness = Math.Max(0.25, radius * 0.035), RenderTransform = view });
            into.Children.Add(new Path { Data = tips, Fill = new SolidColorBrush(behind ? Color.FromRgb(0xE8, 0xC9, 0x9A) : Color.FromRgb(0xFF, 0xE6, 0xA6)), RenderTransform = view });
            into.Children.Add(new Path { Data = new EllipseGeometry(new Point(0, 0), radius * 0.12, radius * 0.12), Fill = new SolidColorBrush(Color.FromRgb(0xC9, 0x3F, 0x7C)), RenderTransform = view });
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
