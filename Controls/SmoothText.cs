// Large display text drawn as a vector shape. WPF anti-aliases ordinary text only across
// (horizontally), so at heading sizes the tops and bottoms of round letters show stair steps;
// a filled geometry gets full anti-aliasing in both directions. Use it for big headings only.
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SteamTV.Controls
{
    public class SmoothText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(SmoothText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(SmoothText),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(SmoothText),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(SmoothText),
            new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SmoothText),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
            nameof(TextWrapping), typeof(TextWrapping), typeof(SmoothText),
            new FrameworkPropertyMetadata(TextWrapping.NoWrap, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
        public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }

        private Geometry _shape;

        private FormattedText Format(double maxWidth)
        {
            var ft = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection,
                new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (TextWrapping != TextWrapping.NoWrap && !double.IsInfinity(maxWidth) && maxWidth > 0)
                ft.MaxTextWidth = maxWidth;
            return ft;
        }

        protected override Size MeasureOverride(Size available)
        {
            var ft = Format(available.Width);
            _shape = ft.BuildGeometry(new Point(0, 0));
            _shape.Freeze();
            return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_shape != null) dc.DrawGeometry(Foreground, null, _shape);
        }
    }
}
