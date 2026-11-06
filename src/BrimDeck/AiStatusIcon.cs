using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// Adapted from Codex Pacer's MIT-licensed IconGlyph/ActivityBadge. Keep its
// original outline paths, provider orbit and 1.8 s rotation / .48 s bloom.
internal sealed class AiStatusIcon : Grid
{
    private readonly Image _orbit = new();
    private readonly AiStatusGlyph _glyph = new();
    private readonly RotateTransform _rotation = new();
    private readonly ScaleTransform _bloom = new(1, 1);
    private AnimationClock? _spin, _scaleX, _scaleY;
    private string _key = "";
    private bool _animate, _running;
    internal bool HasMotion => _spin is not null;
    internal string Symbol => _glyph.Symbol;

    internal AiStatusIcon(double size)
    {
        Width = Height = size;
        RenderTransform = _bloom; RenderTransformOrigin = new Point(.5, .5);
        _orbit.RenderTransform = _rotation; _orbit.RenderTransformOrigin = new Point(.5, .5);
        _glyph.Width = _glyph.Height = size * .64;
        _glyph.HorizontalAlignment = HorizontalAlignment.Center; _glyph.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_orbit); Children.Add(_glyph);
        Loaded += (_, _) => UpdateMotion();
        IsVisibleChanged += (_, _) => UpdateMotion();
        Unloaded += (_, _) => StopMotion();
    }

    internal void Update(string symbol, IReadOnlyDictionary<string, int> orbit, Brush ink, bool animate)
    {
        string key = symbol + "|" + string.Join("|", AiActivity.Providers.Select(p => orbit.GetValueOrDefault(p))) + "|" + animate;
        _glyph.Ink = ink;
        if (_key == key) return;
        bool changed = _glyph.Symbol.Length > 0 && _glyph.Symbol != symbol;
        _key = key; _animate = animate; _running = orbit.Values.Any(n => n > 0);
        _glyph.Symbol = symbol;
        _orbit.Source = PaintOrbit(orbit, animate);
        UpdateMotion();
        if (changed && animate && IsLoaded && IsVisible) Bloom();
    }

    private void UpdateMotion()
    {
        if (!IsLoaded || !IsVisible || !_animate) { StopMotion(); return; }
        if (!_running) { StopSpin(); return; }
        if (_spin is not null) return;
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 30);
        _spin = (AnimationClock)animation.CreateClock(true);
        _rotation.ApplyAnimationClock(RotateTransform.AngleProperty, _spin);
        // Text/rate updates rebuild the compact row. Keep a common phase so
        // these rebuilds do not restart the orbit and make it jump backwards.
        _spin.Controller!.Seek(TimeSpan.FromMilliseconds(Environment.TickCount64 % 1800), TimeSeekOrigin.BeginTime);
    }

    private void Bloom()
    {
        StopBloom();
        DoubleAnimationUsingKeyFrames Track()
        {
            var value = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(.48), FillBehavior = FillBehavior.Stop };
            value.KeyFrames.Add(new LinearDoubleKeyFrame(.72, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            value.KeyFrames.Add(new LinearDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.24))));
            value.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.48))));
            Timeline.SetDesiredFrameRate(value, 30); return value;
        }
        _scaleX = (AnimationClock)Track().CreateClock(true); _scaleY = (AnimationClock)Track().CreateClock(true);
        _scaleY.Completed += (_, _) => StopBloom();
        _bloom.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _scaleX);
        _bloom.ApplyAnimationClock(ScaleTransform.ScaleYProperty, _scaleY);
    }

    private void StopSpin()
    {
        _spin?.Controller?.Remove(); _spin = null;
        _rotation.ApplyAnimationClock(RotateTransform.AngleProperty, null);
    }
    private void StopBloom()
    {
        _scaleX?.Controller?.Remove(); _scaleY?.Controller?.Remove(); _scaleX = _scaleY = null;
        _bloom.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null); _bloom.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
    }
    private void StopMotion() { StopSpin(); StopBloom(); }

    private static DrawingImage PaintOrbit(IReadOnlyDictionary<string, int> weights, bool animate)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 20, 20));
            var active = AiActivity.Providers.Where(p => weights.GetValueOrDefault(p) > 0).ToArray();
            if (active.Length > 0) dc.DrawEllipse(null, new Pen(UI.Brush(UI.Track), 1.3), new Point(10, 10), 9, 9);
            int total = Math.Max(1, active.Sum(p => weights[p]));
            double end = 0;
            for (int rank = 0; rank < active.Length; rank++)
            {
                string provider = active[rank];
                double start = animate ? (double)rank / active.Length : end;
                double span = animate ? Math.Min((active.Length > 1 ? .5 : .34) * weights[provider] / total, .9 / active.Length) : (double)weights[provider] / total;
                var color = ((SolidColorBrush)UI.Brush(UI.ColorFor(provider switch { "claude" => ProviderId.Claude, "deepseek" => ProviderId.DeepSeek, _ => ProviderId.Codex }))).Color;
                for (int step = 0; step < 24; step++)
                {
                    var ink = new SolidColorBrush(Color.FromArgb(animate ? (byte)(255 * (1 - step / 24d)) : (byte)191, color.R, color.G, color.B)); ink.Freeze();
                    Point At(double fraction) => new(10 + 9 * Math.Cos((-90 + fraction * 360) * Math.PI / 180), 10 + 9 * Math.Sin((-90 + fraction * 360) * Math.PI / 180));
                    var arc = new StreamGeometry();
                    using (var path = arc.Open())
                    {
                        path.BeginFigure(At(start + span * step / 24), false, false);
                        path.ArcTo(At(start + span * (step + 1.02) / 24), new Size(9, 9), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    arc.Freeze(); dc.DrawGeometry(null, new Pen(ink, 1.5), arc);
                }
                end = start + span;
            }
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}

internal sealed class AiStatusGlyph : FrameworkElement
{
    private string _symbol = "";
    private Brush _ink = Brushes.White;
    internal string Symbol { get => _symbol; set { _symbol = value; InvalidateVisual(); } }
    internal Brush Ink { get => _ink; set { _ink = value; InvalidateVisual(); } }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var pen = new Pen(Ink, 1.55) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (Symbol.EndsWith(".circle", StringComparison.Ordinal)) drawing.DrawEllipse(null, pen, new Point(12, 12), 9.3, 9.3);
        if (Paths.TryGetValue(Symbol, out var path)) drawing.DrawGeometry(Symbol == "sparkle" ? Ink : null, Symbol == "sparkle" ? null : pen, path);
        if (Symbol == "ellipsis.bubble") foreach (double x in new[] { 7.5, 12, 16.5 }) drawing.DrawEllipse(Ink, null, new Point(x, 10.8), 1, 1);
        drawing.Pop();
    }
    private static readonly IReadOnlyDictionary<string, Geometry> Paths = CreatePaths();
    private static IReadOnlyDictionary<string, Geometry> CreatePaths()
    {
        var paths = new Dictionary<string, string>
        {
            ["sparkle"] = "M12,2 C13.1,8.8 15.2,10.9 22,12 C15.2,13.1 13.1,15.2 12,22 C10.9,15.2 8.8,13.1 2,12 C8.8,10.9 10.9,8.8 12,2 Z",
            ["terminal"] = "M5,4.5 H19 Q21,4.5 21,6.5 V17.5 Q21,19.5 19,19.5 H5 Q3,19.5 3,17.5 V6.5 Q3,4.5 5,4.5 Z M7,9 L10,12 L7,15 M13.5,15 H17.5",
            ["text.bubble"] = "M5,3.5 H19 Q21,3.5 21,5.5 V15 Q21,17 19,17 H10 L5,21 V17 Q3,17 3,15 V5.5 Q3,3.5 5,3.5 Z M7,8 H17 M7,11.5 H14.5",
            ["ellipsis.bubble"] = "M5,3.5 H19 Q21,3.5 21,5.5 V15 Q21,17 19,17 H10 L5,21 V17 Q3,17 3,15 V5.5 Q3,3.5 5,3.5 Z",
            ["hourglass"] = "M6,2.5 H18 M6,21.5 H18 M7,2.5 V6 Q7,9 11,12 Q7,15 7,18 V21.5 M17,2.5 V6 Q17,9 13,12 Q17,15 17,18 V21.5 M9.5,6 H14.5 M9.5,18 H14.5",
            ["cloud"] = "M6,18.5 C-0.5,18.5 0,9.5 6,9 C6.4,2.5 15,1.5 17.3,8.5 C24,7.5 25,18.5 18,18.5 Z",
            ["arrow.triangle.branch"] = "M12,21 V13 M12,13 L4,3 M12,13 L20,3 M3,3 H7 M3,3 V7 M21,3 H17 M21,3 V7",
            ["moon"] = "M18.9,16.5 C10.6,18 6,11 9,3.3 C0,6.2 2,20.7 12,20.7 C15.2,20.7 17.5,19.2 18.9,16.5 Z",
            ["hand.raised"] = "M7.5,13 V6.5 Q7.5,4.5 9,4.5 Q10.5,4.5 10.5,6.5 V11 M10.5,7 V3.5 Q10.5,1.5 12,1.5 Q13.5,1.5 13.5,3.5 V11 M13.5,7 V4.7 Q13.5,2.7 15,2.7 Q16.5,2.7 16.5,4.7 V12 M16.5,8 Q16.5,6 18,6 Q19.5,6 19.5,8 V15 C19.5,23 9,24 6,18 L3,12.5 Q2,10.5 3.5,10 Q5,9.5 7.5,13 Z",
            ["checkmark.circle"] = "M7.5,12 L10.5,15 L16.8,8.7",
            ["pause.circle"] = "M9.2,8 V16 M14.8,8 V16",
            ["xmark.circle"] = "M8.5,8.5 L15.5,15.5 M15.5,8.5 L8.5,15.5"
        };
        return paths.ToDictionary(p => p.Key, p => { var geometry = Geometry.Parse(p.Value); geometry.Freeze(); return geometry; });
    }
}
