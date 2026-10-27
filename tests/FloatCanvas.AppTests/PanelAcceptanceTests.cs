using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PanelAcceptanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var results = new List<(string Name, bool Passed)>();
        void Observe(string name, bool passed) { results.Add((name, passed)); Console.WriteLine($"PANEL_OBSERVE {name}: {passed}"); }
        var font = ToolOptionPreviews.FontPicker();
        Observe("font picker supports direct text editing", font.IsEditable && !font.IsTextSearchEnabled);
        Observe("font choices retain preview fonts and readable names without creating controls", font.Items.Count > 0 &&
            font.Items.Cast<object>().All(item => item is FontPickerControl.FontChoice { Name.Length: > 0, DisplayName.Length: > 0, Family: not null }));
        if (font is FontPickerControl searchable)
        {
            int allFonts = searchable.Items.Count, changes = 0;
            ToolOptionPreviews.SelectFont(searchable, "Arial");
            searchable.FontCommitted += _ => changes++;
            searchable.ApplySearch("Segoe");
            Observe("font search narrows choices without applying a font", searchable.Items.Count > 0 && searchable.Items.Count < allFonts && changes == 0 && searchable.Text == "Segoe");
            searchable.ShowAllFonts();
            Observe("the full font view clears its filter without applying a draft", searchable.Items.Count == allFonts && changes == 0);
            searchable.ApplyTemplate();
            if (searchable.Template.FindName("PART_EditableTextBox", searchable) is TextBox input)
            {
                input.Text = "Arial";
                int beforePause = searchable.Items.Count;
                Pump(350);
                Observe("programmatic text on a detached font editor cannot schedule filtering or a popup", beforePause == allFonts && searchable.Items.Count == allFonts && !searchable.IsDropDownOpen);
            }
            else Observe("programmatic text on a detached font editor cannot schedule filtering or a popup", false);
            using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.FontPickerKeys") { Width = 10, Height = 10, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000) });
            searchable.ApplySearch("Segoe UI");
            typeof(FontPickerControl).GetMethod("OnPreviewKeyDown", Private)!.Invoke(searchable, [new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }]);
            Observe("font Enter applies the matching candidate exactly once", changes == 1 && ToolOptionPreviews.SelectedFontName(searchable) == "Segoe UI");
            searchable.ApplySearch("Arial");
            typeof(FontPickerControl).GetMethod("OnPreviewKeyDown", Private)!.Invoke(searchable, [new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }]);
            Observe("font Escape restores the committed family without another apply", changes == 1 && searchable.Text == "Segoe UI" && searchable.Items.Count == allFonts);
        }
        var pin = ActionIconButton.Create(PanelActionIcon.Pin, "pin", () => { });
        ActionIconButton.UpdatePin(pin, false);
        double idleSize = pin.Width;
        ActionIconButton.UpdatePin(pin, true);
        Observe("active pin grows visually without changing its header allocation", pin.Width == idleSize && pin.RenderTransform is ScaleTransform scale && scale.ScaleX > 1 && scale.ScaleY > 1);
        Observe("header actions have room for active pin outlines", pin.Margin.Left + pin.Margin.Right >= 6);
        var canvas = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        try
        {
            canvas.ApplyTemplate();
            var close = (Button)canvas.Template.FindName("Close", canvas);
            var headerPin = Field<Button>(canvas, "pin");
            Observe("idle pin and close share the same caption size and spacing", headerPin.Width == close.Width && headerPin.Height == close.Height && headerPin.Margin == close.Margin);
            var settings = new CanvasSettings(); settings.Projection.Enabled = true; canvas.Refresh(settings);
            canvas.SetReferenceMonitors([new MonitorInfo("main", new(0,0,1920,1080), new(0,0,1920,1040),96,96,true)], null);
            canvas.PrepareForShow();
            Observe("single-monitor reset becomes a plain button without a dropdown region", Field<Button>(canvas,"resetChoices").Visibility == Visibility.Collapsed);
            var content = (FrameworkElement)canvas.Content;
            content.Measure(new(canvas.Width - 2, 700)); content.Arrange(new(0,0,canvas.Width - 2,700)); content.UpdateLayout();
            var action = (FrameworkElement)Field<Button>(canvas,"fourPoints").Parent;
            double initial = action.ActualWidth;
            var container = (FrameworkElement)action.Parent;
            Console.WriteLine($"PANEL_ACTION_WIDTH initial={initial} container={container.ActualWidth}");
            Observe("canvas actions initially fill their parameter container", Math.Abs(initial - container.ActualWidth) < 2);
            var projection = Field<SegmentedChoice>(canvas, "projection");
            var projectionRow = (Grid)projection.Parent;
            var group = Field<PropertyChangeRegion>(canvas, "projectionOptions");
            var projectionBody = (StackPanel)group.Child;
            var background = Field<ColorField>(canvas, "background");
            var backgroundRow = (Grid)background.Parent;
            Observe("canvas conditional rows preserve the ungrouped label alignment", Math.Abs(projectionRow.TranslatePoint(new(0,0),content).X - backgroundRow.TranslatePoint(new(0,0),content).X) < 1 && group.Margin.Left + projectionBody.Margin.Left == 0);
            Observe("perspective options have a transient cue outside the triggering switch", group.CueStrength == 0 && !ReferenceEquals(projectionRow.Parent, projectionBody) && ReferenceEquals(container.Parent, projectionBody));
            content.Measure(new(canvas.Width + 198, 700)); content.Arrange(new(0,0,canvas.Width + 198,700)); content.UpdateLayout();
            Observe("canvas action width remains fixed and centered after widening", Math.Abs(action.ActualWidth - initial) < 1 && action.HorizontalAlignment == HorizontalAlignment.Center);
        }
        finally { canvas.CloseForDispose(); }
        var session = new EditorSession();
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 80 };
        session.Add(shape);
        var inspector = new InspectorWindow(() => session);
        try
        {
            inspector.Refresh();
            inspector.PrepareForShow();
            var linked = Field<StackPanel>(inspector, "linkedColorsGroup");
            var region = Field<PropertyChangeRegion>(inspector, "linkedColorsRegion");
            var colors = (StackPanel)region.Child;
            var fields = Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers");
            Observe("inspector link switch precedes the colors it controls", ReferenceEquals(linked.Children[0], fields["LinkColors"]) && ReferenceEquals(linked.Children[1], region) && colors.Children.Contains(fields["Stroke"]) && colors.Children.Contains(fields["Fill"]));
            Observe("inspector color cue keeps labels flush without a permanent group", region.CueStrength == 0 && region.Margin.Left + colors.Margin.Left == 0);
            session.Selection.Clear(); inspector.Refresh(); inspector.PrepareForShow();
            Observe("inspector hides its color rows when no supported fields remain", linked.Visibility == Visibility.Collapsed);
        }
        finally { inspector.Close(); }
        foreach (var result in results) check(result.Name, result.Passed);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
}
