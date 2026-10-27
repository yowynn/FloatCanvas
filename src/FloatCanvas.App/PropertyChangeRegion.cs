using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>A transient cue for one contiguous block of changing property rows.</summary>
internal sealed class PropertyChangeRegion : Decorator
{
    internal const double HighlightInset = 10;
    private static readonly DependencyProperty CueStrengthProperty = DependencyProperty.Register(
        nameof(CueStrength), typeof(double), typeof(PropertyChangeRegion), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly StackPanel body;
    private readonly DispatcherTimer clearCue = new() { Interval = TimeSpan.FromSeconds(1) };
    private int transition;

    internal double CueStrength => (double)GetValue(CueStrengthProperty);

    internal PropertyChangeRegion(StackPanel body)
    {
        this.body = body;
        Margin = new(-HighlightInset, 0, -HighlightInset, 0);
        body.Margin = new(HighlightInset, 0, HighlightInset, 0);
        Child = body; ClipToBounds = true;
        clearCue.Tick += (_, _) => Reset();
        Unloaded += (_, _) => Reset();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Reset(); };
    }

    // Exchange outer margin for content margin, keeping the property alignment
    // while allowing the cue to paint into the existing window inset.
    internal static ScrollViewer ReserveHighlightSpace(ScrollViewer scroll)
    {
        var margin = scroll.Margin;
        var content = (FrameworkElement)scroll.Content;
        var inset = content.Margin;
        scroll.Margin = new(margin.Left - HighlightInset, margin.Top, margin.Right - HighlightInset, margin.Bottom);
        content.Margin = new(inset.Left + HighlightInset, inset.Top, inset.Right + HighlightInset, inset.Bottom);
        return scroll;
    }

    internal void Change(Action update)
    {
        double previousHeight = ActualHeight;
        var previousRows = VisibleRows();
        Reset();
        update();
        if (!IsVisible || ActualWidth <= 0 || previousRows.SequenceEqual(VisibleRows())) return;

        body.Measure(new Size(ActualWidth, double.PositiveInfinity));
        double nextHeight = body.DesiredSize.Height;
        if (nextHeight <= 0) return;
        int version = transition;
        if (SystemParameters.ClientAreaAnimation)
        {
            Height = previousHeight;
            var resize = new DoubleAnimation(previousHeight, nextHeight, TimeSpan.FromMilliseconds(210))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            resize.Completed += (_, _) =>
            {
                if (version != transition) return;
                BeginAnimation(HeightProperty, null); ClearValue(HeightProperty);
            };
            BeginAnimation(HeightProperty, resize);
            var cue = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            cue.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            cue.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
            cue.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
            BeginAnimation(CueStrengthProperty, cue);
        }
        else SetValue(CueStrengthProperty, 1d);
        clearCue.Start();
    }

    internal void Reset()
    {
        transition++; clearCue.Stop();
        BeginAnimation(HeightProperty, null); ClearValue(HeightProperty);
        BeginAnimation(CueStrengthProperty, null); SetValue(CueStrengthProperty, 0d);
    }

    private FrameworkElement[] VisibleRows() => body.Children.OfType<FrameworkElement>()
        .Where(row => row.Visibility == Visibility.Visible).ToArray();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (CueStrength <= 0 || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var accent = TryFindResource("CanvasAccent") as Brush ?? SystemColors.HighlightBrush;
        drawingContext.PushOpacity(CueStrength * .14);
        drawingContext.DrawRoundedRectangle(accent, null, new Rect(RenderSize), 6, 6);
        drawingContext.Pop();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}
