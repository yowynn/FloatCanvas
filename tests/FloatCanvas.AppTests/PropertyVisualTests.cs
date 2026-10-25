using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class PropertyVisualTests
{
    internal static void Run(Action<string, bool> check)
    {
        SwitchGeometry(check);
        foreach (var theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            foreach (double value in new[] { 0d, 50, 100 })
            {
                var field = new NumericValueField(0, 100) { Value = value };
                field.Measure(new(150, 34)); field.Arrange(new(0, 0, 150, 34)); field.UpdateLayout();
                var rail = (Border)field.Slider.Template.FindName("Rail", field.Slider);
                var track = (Track)field.Slider.Template.FindName("PART_Track", field.Slider);
                var fill = (Border)track.DecreaseRepeatButton.Template.FindName("Fill", track.DecreaseRepeatButton);
                double left = rail.TranslatePoint(new(), field).X;
                double right = rail.TranslatePoint(new(rail.ActualWidth, 0), field).X;
                double fillLeft = fill.TranslatePoint(new(), field).X;
                double fillEnd = fill.TranslatePoint(new(fill.ActualWidth, 0), field).X;
                double center = track.Thumb.TranslatePoint(new(track.Thumb.ActualWidth / 2, 0), field).X;
                check($"{theme} rail {value}% has symmetric insets and fill reaches the knob center", Math.Abs(left - (field.ActualWidth - right)) < .01
                    && Math.Abs(fillLeft - left) < .01 && Math.Abs(fillEnd - center) < .01);
            }
        }
        var session = new EditorSession();
        var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 50, Style = new() { LinkColors = true } };
        var second = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 50 };
        session.Add(first); session.Add(second); session.SetSelection([first.Id, second.Id]);
        var inspector = new InspectorWindow(() => session);
        T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        var toggle = Field<PropertySwitch>(inspector, "linkColors");
        check("mixed inspector color links use the common switch", toggle.IsChecked is null && ReferenceEquals(toggle.Style, Application.Current.Resources["CanvasPropertySwitch"]));
        typeof(PropertySwitch).GetMethod("OnToggle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, null);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); inspector.CompletePendingEdit();
        check("mixed switch action chooses on for every selected layer and is undoable", session.SelectedElements().All(e => e.Style.LinkColors)
            && session.Undo() && session.SelectedElements().Select(e => e.Style.LinkColors).Distinct().Count() == 2);
        var numbers = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields");
        var properties = Field<StackPanel>(inspector, "propertyPanel");
        check("first visible inspector section has no top divider", properties.Children.OfType<Border>().First(e => e.Visibility == Visibility.Visible).BorderThickness.Top == 0);
        check("every inspector geometry scrubber belongs to an embedded anchor", Field<List<NumericScrubber>>(inspector, "scrubbers").All(scrub => numbers.Values.Any(number => ReferenceEquals(Field<FrameworkElement>(scrub, "label"), number.DragAnchor))));
        var picker = new ColorPickerPanel(ColorRole.Foreground, new ColorLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FloatCanvas-visual-" + Guid.NewGuid().ToString("N"), "colors.json")));
        picker.Measure(new(660, 500)); picker.Arrange(new(0, 0, 660, 500)); picker.UpdateLayout();
        check("color hex and opacity editors share a 34 DIP height", Field<TextBox>(picker, "hex").ActualHeight == PropertyPanelLayout.ControlHeight && Field<NumericValueField>(picker, "alphaField").ActualHeight == PropertyPanelLayout.ControlHeight);
        check("color and inspector opacity use the identical icon", ((NumericPropertyIcon)numbers["Opacity"].DragAnchor.Child).Kind == NumericPropertyKind.Opacity && ((NumericPropertyIcon)Field<NumericValueField>(picker, "alphaField").DragAnchor.Child).Kind == NumericPropertyKind.Opacity);
        check("section and property typography remain distinct and shared", Field<TextBlock>(picker, "pinnedHeading").FontSize == PropertyPanelLayout.SectionFontSize && PropertyPanelLayout.SectionFontSize > PropertyPanelLayout.LabelFontSize);
    }

    internal static void SwitchGeometry(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string theme in new[] { "light", "dark" })
        foreach (bool focused in new[] { false, true })
        foreach (bool? state in new bool?[] { false, true, null })
        {
            AppTheme.Apply(theme);
            var control = new PropertySwitch { IsThreeState = true, IsChecked = state };
            var row = PropertyPanelLayout.BooleanRow("Link colors", control);
            row.Measure(new(440, 42)); row.Arrange(new(0, 0, 440, 42)); row.UpdateLayout();
            // Apply the actual focus trigger's target setters without taking desktop focus.
            if (focused)
                foreach (var trigger in control.Template.Triggers.OfType<Trigger>().Where(t => t.Property == UIElement.IsKeyboardFocusedProperty))
                foreach (Setter setter in trigger.Setters)
                    if (setter.TargetName is { } name && control.Template.FindName(name, control) is DependencyObject target)
                    {
                        if (setter.Value is DynamicResourceExtension resource && target is FrameworkElement element)
                            element.SetResourceReference(setter.Property, resource.ResourceKey);
                        else target.SetValue(setter.Property, setter.Value);
                    }
            row.UpdateLayout();
            var knob = (Ellipse)control.Template.FindName("Knob", control);
            var track = (Border)control.Template.FindName("Track", control);
            var bounds = knob.TranslatePoint(new(), control) - track.TranslatePoint(new(), control);
            Geometry? clip = VisualTreeHelper.GetClip(knob);
            check($"switch {theme} focus={focused} state={state?.ToString() ?? "mixed"} preserves the complete circular knob", knob.ActualWidth == knob.ActualHeight
                && bounds.Y >= 0 && bounds.Y + knob.ActualHeight <= track.ActualHeight
                && (clip is null || clip.Bounds.Contains(new Rect(knob.RenderSize))));
        }
    }
}
