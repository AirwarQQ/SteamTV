// Animates the Home banner: petals drifting off the branch, the branch swaying, stars
// twinkling and the TV glow breathing. Everything runs on one storyboard that's paused whenever
// the banner isn't on screen (window in the tray, another page open), so it costs nothing then.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SteamTV.Controls
{
    public partial class NightScene : UserControl
    {
        private const int PetalCount = 24;
        private readonly Storyboard _story = new Storyboard();
        private bool _started;

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
        }

        private void BuildAnimations()
        {
            var rnd = new Random(7);   // fixed seed: the same gentle choreography every launch
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            // Branch: a breezy ±2.6° sway around the point where it enters the frame.
            Add(new DoubleAnimation(-2.6, 2.6, Seconds(3.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                BranchSway, RotateTransform.AngleProperty);

            // TV glow breathes.
            Add(new DoubleAnimation(0.65, 1.0, Seconds(3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
                TvGlow, OpacityProperty);

            // Stars twinkle, each on its own beat.
            foreach (UIElement star in Stars.Children)
                Add(new DoubleAnimation(0.25, 0.9, Seconds(1.6 + rnd.NextDouble() * 2.4))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        BeginTime = SecondsSpan(rnd.NextDouble() * 3), EasingFunction = ease,
                    }, star, OpacityProperty);

            for (int i = 0; i < PetalCount; i++) AddPetal(rnd);
        }

        // A petal leaves the branch (or drifts in from above), floats left on the breeze with a
        // side-to-side wobble, tumbles — flipping edge-on and back, like a real petal — and fades
        // out before it leaves the visible strip of the banner. Then it starts over.
        // The banner shows roughly the top 150 of the scene's 236 units, so that's where they fall.
        private void AddPetal(Random rnd)
        {
            double size = 5 + rnd.NextDouble() * 3.5;
            var petal = new Ellipse
            {
                Width = size * 1.7, Height = size,
                Fill = new SolidColorBrush(rnd.Next(3) == 0 ? Color.FromRgb(0xFF, 0xA8, 0xCB) : Color.FromRgb(0xFF, 0xD1, 0xE3)),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var flip = new ScaleTransform(1, 1);
            var rotate = new RotateTransform(rnd.Next(360));
            var move = new TranslateTransform();
            petal.RenderTransform = new TransformGroup { Children = { flip, rotate, move } };
            bool fromBranch = rnd.NextDouble() < 0.6;
            Canvas.SetLeft(petal, fromBranch ? 560 + rnd.NextDouble() * 130 : 380 + rnd.NextDouble() * 300);
            Canvas.SetTop(petal, fromBranch ? 10 + rnd.NextDouble() * 80 : -10);
            Petals.Children.Add(petal);

            var fall = Seconds(6 + rnd.NextDouble() * 4.5);
            var begin = SecondsSpan(rnd.NextDouble() * 8);
            double drift = -(90 + rnd.NextDouble() * 170);
            double wobble = 8 + rnd.NextDouble() * 14;

            Add(new DoubleAnimation(0, 95 + rnd.NextDouble() * 60, fall) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever },
                move, TranslateTransform.YProperty);

            var x = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = fall, RepeatBehavior = RepeatBehavior.Forever };
            for (int k = 0; k <= 6; k++)
                x.KeyFrames.Add(new SplineDoubleKeyFrame(drift * k / 6 + (k % 2 == 0 ? 0 : wobble), KeyTime.FromPercent(k / 6.0), new KeySpline(0.4, 0, 0.6, 1)));
            Add(x, move, TranslateTransform.XProperty);

            Add(new DoubleAnimation(1, 0.2, Seconds(0.6 + rnd.NextDouble() * 0.9))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } },
                flip, ScaleTransform.ScaleYProperty);

            double spin = (rnd.Next(2) == 0 ? -1 : 1) * (240 + rnd.Next(360));
            Add(new DoubleAnimation(rotate.Angle, rotate.Angle + spin, fall) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever },
                rotate, RotateTransform.AngleProperty);

            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = fall, RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.1)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromPercent(0.75)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            Add(fade, petal, OpacityProperty);
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
