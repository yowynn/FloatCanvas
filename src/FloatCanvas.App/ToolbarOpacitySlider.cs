using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>A toolbar-weight opacity ribbon with a hollow thumb and continuous pointer gestures.</summary>
internal sealed class ToolbarOpacitySlider : Slider
{
    private static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(ToolbarOpacitySlider),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty IsThumbHighlightedProperty = DependencyProperty.Register(nameof(IsThumbHighlighted), typeof(bool), typeof(ToolbarOpacitySlider),
        new PropertyMetadata(false));
    private Brush Ink => (Brush)GetValue(InkProperty);
    public bool IsThumbHighlighted => (bool)GetValue(IsThumbHighlightedProperty);
    private bool pointerActive, thumbPressed;
    private Point? pointerPosition;
    private double pointerOrigin, pointerValue, railStart, railTravel;
    internal bool IsPointerInteracting => pointerActive;
    internal event Action? PointerGestureStarted;
    internal event Action? PointerGestureCompleted;

    internal ToolbarOpacitySlider()
    {
        Minimum = 0; Maximum = Value = 1;
        SmallChange = .01; LargeChange = .1;
        Width = 56; Height = 26;
        Cursor = Cursors.Arrow;
        FocusVisualStyle = null;
        SetResourceReference(StyleProperty, typeof(Slider));
        SetResourceReference(InkProperty, "CanvasText");
        Template = (ControlTemplate)XamlReader.Parse(TemplateMarkup);
        IsEnabledChanged += (_, _) => { if (!IsEnabled) { EndPointerGesture(); pointerPosition = null; RefreshThumbHighlight(); } InvalidateVisual(); };
        Unloaded += (_, _) => { EndPointerGesture(); pointerPosition = null; RefreshThumbHighlight(); };
        LayoutUpdated += (_, _) => RefreshThumbHighlight();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdatePointerHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        UpdatePointerHover(e.GetPosition(this));
    }

    private void UpdatePointerHover(Point point)
    {
        pointerPosition = new Rect(RenderSize).Contains(point) ? point : null;
        RefreshThumbHighlight();
    }

    private void RefreshThumbHighlight()
    {
        // Dragging keeps the handle active; otherwise only its own bounds hover.
        bool highlighted = IsEnabled && (pointerActive || pointerPosition is Point point &&
            Template?.FindName("PART_Track", this) is Track { Thumb: { } thumb } &&
            new Rect(thumb.TranslatePoint(new(), this), thumb.RenderSize).Contains(point));
        if (IsThumbHighlighted != highlighted) SetValue(IsThumbHighlightedProperty, highlighted);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true; // Do not let the track's RepeatButtons start page stepping.
        StartPointerGesture(e.GetPosition(this), CaptureMouse);
    }

    internal void StartPointerGesture(Point point, Func<bool> capture)
    {
        UpdatePointerHover(point);
        if (pointerActive || !IsEnabled || !double.IsFinite(point.X)) return;
        ApplyTemplate();
        if (Template.FindName("PART_Track", this) is not Track { Thumb: { } thumb } track) return;
        var bounds = new Rect(thumb.TranslatePoint(new(), this), thumb.RenderSize);
        pointerOrigin = point.X; pointerValue = Value;
        railStart = track.TranslatePoint(new(thumb.ActualWidth / 2, 0), this).X;
        railTravel = Math.Max(1, track.ActualWidth - thumb.ActualWidth);
        thumbPressed = bounds.Contains(point);
        // Capture can synchronously route MouseMove. Prepare the mode and start
        // the transaction before either the initial jump or that reentrant move.
        pointerActive = true;
        PointerGestureStarted?.Invoke();
        Focus();
        if (!thumbPressed) UpdatePointer(point.X);
        RefreshThumbHighlight();
        if (!pointerActive) return;
        if (!capture()) EndPointerGesture();
        else if (!pointerActive && IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        UpdatePointerHover(e.GetPosition(this));
        if (!pointerActive) { base.OnPreviewMouseMove(e); return; }
        if (e.LeftButton != MouseButtonState.Pressed) EndPointerGesture();
        else UpdatePointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        UpdatePointerHover(e.GetPosition(this));
        if (!pointerActive) { base.OnPreviewMouseLeftButtonUp(e); return; }
        UpdatePointer(e.GetPosition(this).X);
        EndPointerGesture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        UpdatePointerHover(e.GetPosition(this));
        base.OnLostMouseCapture(e);
        if (!IsMouseCaptured) EndPointerGesture();
    }

    private void UpdatePointer(double x)
    {
        if (!pointerActive || !double.IsFinite(x)) return;
        double fraction = (x - railStart) / railTravel;
        if (IsDirectionReversed) fraction = 1 - fraction;
        double next = thumbPressed
            ? pointerValue + (IsDirectionReversed ? -1 : 1) * (x - pointerOrigin) / railTravel * (Maximum - Minimum)
            : Minimum + fraction * (Maximum - Minimum);
        SetCurrentValue(ValueProperty, Math.Clamp(next, Minimum, Maximum));
    }

    internal void EndPointerGesture()
    {
        if (!pointerActive) return;
        pointerActive = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        PointerGestureCompleted?.Invoke();
        UpdateLayout();
        RefreshThumbHighlight();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.PushOpacity(IsEnabled ? 1 : .45);
        var ribbon = new Rect((ActualWidth - 42) / 2, (ActualHeight - 8) / 2, 42, 8);
        drawing.PushClip(new RectangleGeometry(ribbon, 2, 2));
        for (int x = 0; x < 21; x++)
        {
            drawing.PushOpacity(.28 * (1 - x / 21d));
            for (int y = 0; y < 4; y++)
                if ((x + y) % 2 == 0) drawing.DrawRectangle(Ink, null, new(ribbon.Left + x * 2, ribbon.Top + y * 2, 2, 2));
            drawing.Pop();
        }
        drawing.PushOpacity(.6);
        drawing.PushOpacityMask(new LinearGradientBrush(Colors.Transparent, Colors.Black, 0));
        drawing.DrawRectangle(Ink, null, ribbon);
        drawing.Pop(); drawing.Pop(); drawing.Pop();
        ribbon.Inflate(-.7, -.7);
        drawing.DrawRoundedRectangle(null, new Pen(Ink, 1.4), ribbon, 1.3, 1.3);
        drawing.Pop();
    }

    private const string TemplateMarkup = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Background="Transparent">
            <Track x:Name="PART_Track" Width="49.9167" Height="26" Orientation="Horizontal"
                   Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                   Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                   IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
              <Track.DecreaseRepeatButton>
                <RepeatButton Focusable="False" IsTabStop="False" Command="{x:Static Slider.DecreaseLarge}">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Width="7.9167" Height="19" Focusable="False">
                  <Thumb.Template>
                    <ControlTemplate TargetType="Thumb">
                      <Grid>
                        <Border x:Name="Cutout" Margin="-.5" Background="{DynamicResource CanvasSurface}" CornerRadius="2.875"/>
                        <Border x:Name="Handle" Background="{DynamicResource CanvasSurface}" BorderBrush="{DynamicResource CanvasText}"
                                BorderThickness="1.4" CornerRadius="2.375"/>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <DataTrigger Binding="{Binding IsThumbHighlighted, RelativeSource={RelativeSource AncestorType=Slider}}" Value="True">
                          <Setter TargetName="Handle" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/>
                        </DataTrigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Focusable="False" IsTabStop="False" Command="{x:Static Slider.IncreaseLarge}">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="PART_Track" Property="Opacity" Value=".45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;
}
