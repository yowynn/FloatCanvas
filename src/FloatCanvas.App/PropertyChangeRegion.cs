using System.Windows;
using System.Windows.Automation;
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

    internal void Change(Action update) => ChangeTogether(update, this);

    internal static void ChangeTogether(Action update, params PropertyChangeRegion[] regions)
    {
        var before = regions.Select(region => VisibleChildren(region.body).ToArray()).ToArray();
        update();
        var changed = regions.Where((region, i) => !before[i].SequenceEqual(VisibleChildren(region.body))).ToArray();
        foreach (var region in changed)
        {
            // A parent already paints one continuous cue over its nested rows.
            if (changed.Any(other => other != region && other.IsAncestorOf(region))) region.Reset();
            else region.CueChangedProperties();
        }
    }

    private void CueChangedProperties()
    {
        Reset();
        if (!IsVisible || ActualWidth <= 0) return;

        body.Measure(new Size(ActualWidth, double.PositiveInfinity));
        if (body.DesiredSize.Height <= 0) return;
        // Layout belongs entirely to the new state. Animate only the cue so
        // collapsed rows never leave an empty, highlighted intermediate frame.
        InvalidateMeasure();
        if (SystemParameters.ClientAreaAnimation)
        {
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
        clearCue.Stop();
        BeginAnimation(CueStrengthProperty, null); SetValue(CueStrengthProperty, 0d);
    }

    private readonly record struct PropertyIdentity(object Key, string? Role, string? Caption);

    // Stop at the row label. Changes to a value or a dropdown's selected item
    // leave the identity of that property intact.
    private static IEnumerable<PropertyIdentity> VisibleChildren(Panel group)
    {
        foreach (var row in group.Children.OfType<FrameworkElement>())
        {
            if (row.Visibility != Visibility.Visible) continue;
            var caption = AutomationProperties.GetLabeledBy(row) as TextBlock ?? row as PropertyLabel;
            string key = AutomationProperties.GetAutomationId(row);
            yield return new(string.IsNullOrEmpty(key) ? (object)row : key, caption is null ? null : AutomationProperties.GetAutomationId(caption), caption?.Text);
            if (caption is null && row is Panel children)
                foreach (var item in VisibleChildren(children)) yield return item;
        }
    }

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
