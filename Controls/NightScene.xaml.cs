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
        private const int PetalCount = 14;
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

            // Branch: a slow ±1.2° sway around the point where it enters the frame.
            Add(new DoubleAnimation(-1.2, 1.2, Seconds(4.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease },
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

        // A petal leaves the branch area, drifts left on the breeze with a side-to-side wobble,
        // tumbles, and fades out low in the frame — then starts over.
        private void AddPetal(Random rnd)
        {
            double size = 4 + rnd.NextDouble() * 3;
            var petal = new Ellipse
            {
                Width = size * 1.6, Height = size,
                Fill = new SolidColorBrush(rnd.Next(3) == 0 ? Color.FromRgb(0xFF, 0xB3, 0xD1) : Color.FromRgb(0xFF, 0xD1, 0xE3)),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var rotate = new RotateTransform(rnd.Next(360));
            var move = new TranslateTransform();
            petal.RenderTransform = new TransformGroup { Children = { rotate, move } };
            Canvas.SetLeft(petal, 540 + rnd.NextDouble() * 150);
            Canvas.SetTop(petal, 10 + rnd.NextDouble() * 80);
            Petals.Children.Add(petal);

            var fall = Seconds(9 + rnd.NextDouble() * 7);
            var begin = SecondsSpan(rnd.NextDouble() * 12);
            double drift = -(140 + rnd.NextDouble() * 260);
            double wobble = 10 + rnd.NextDouble() * 18;

            Add(new DoubleAnimation(0, 150 + rnd.NextDouble() * 60, fall) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever },
                move, TranslateTransform.YProperty);

            var x = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = fall, RepeatBehavior = RepeatBehavior.Forever };
            for (int k = 0; k <= 4; k++)
                x.KeyFrames.Add(new SplineDoubleKeyFrame(drift * k / 4 + (k % 2 == 0 ? 0 : wobble), KeyTime.FromPercent(k / 4.0), new KeySpline(0.4, 0, 0.6, 1)));
            Add(x, move, TranslateTransform.XProperty);

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
