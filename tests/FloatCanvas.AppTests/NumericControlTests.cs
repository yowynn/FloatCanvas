using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

/// <summary>Real field and panel lifecycles, measured and rendered without desktop input or user profiles.</summary>
internal static class NumericControlTests
{
    public static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        string preference = L.Preference;
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-numeric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            NumericScrubberTests.Run(check);
            AppTheme.Apply("light");
            PropertyVisualTests.Run(check);
            FieldGestures(check);
            InspectorGestures(check);
            ToolGestures(root, check);
            AspectRatios(root, check);
            OverflowLabels(check);
            var canvas = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
            canvas.PrepareForShow();
            var canvasWidth = Field<NumericValueField>(canvas, "width");
            Measure(canvasWidth, 120, 34); canvasWidth.BeginGesture(); canvasWidth.UpdateRail(60);
            double previewWidth = canvasWidth.Value; canvas.Refresh(new(), false);
            check("canvas repaint preserves the active label or rail preview", canvasWidth.IsInteracting && canvasWidth.Value == previewWidth);
            canvasWidth.EndGesture(true); canvas.CloseForDispose();
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                Layout(root, language + "-" + theme, check, output);
            }
        }
        finally { L.Initialize(preference); Directory.Delete(root, true); }
    }

    private static void FieldGestures(Action<string, bool> check)
    {
        var field = new NumericValueField(-10000, 10000, 0, 100) { Value = 50 };
        var label = new Controls.TextBlock { Text = "Width" }; field.SetLabel(label); var scrubber = field.AnchorScrubber!;
        check("property label remains passive while its embedded anchor owns dragging", !label.Focusable && label.Cursor is null && field.DragAnchor.Cursor == Wpf.Input.Cursors.SizeWE);
        int commits = 0; field.SliderValueCommitted += () => commits++;
        scrubber.TryBegin(0); scrubber.Update(3); scrubber.End(true);
        check("numeric click tolerance leaves value and history unchanged", field.Value == 50 && commits == 0);
        for (int i = 0; i < 10; i++) { scrubber.TryBegin(0); scrubber.Update(10); scrubber.End(true); }
        double fine = field.Value - 50; field.Value = 50;
        scrubber.TryBegin(0); scrubber.Update(100); scrubber.End(true);
        check("ten short drags remain finer than one equally long total drag", field.Value - 50 > fine && commits == 11);
        field.Input.Text = "500";
        check("typed values beyond common range retain their value", field.Value == 500 && field.Slider.Value == 100);
        scrubber.TryBegin(0); scrubber.Update(1000); scrubber.End(true);
        check("label dragging can leave the common range", field.Value > 500 && field.Slider.Value == 100);
        field.Value = 50; Measure(field, 110, 34);
        int before = commits;
        field.BeginGesture(); field.UpdateRail(4); field.UpdateRail(field.Slider.ActualWidth - 4);
        check("rail previews endpoints without committing each movement", field.Value == 100 && commits == before);
        field.EndGesture(true);
        check("range gesture commits only once", commits == before + 1);
        field.BeginGesture(); field.UpdateRail(4); field.CancelGesture();
        check("cancel restores the original range value without commit", field.Value == 100 && commits == before + 1 && !field.IsInteracting);
        field.Input.Text = "invalid";
        check("invalid draft blocks gestures without losing typed text", !field.BeginGesture() && !scrubber.TryBegin(0) && field.Input.Text == "invalid");
        field.Value = 12; field.BeginGesture(); field.UpdateRail(4); field.IsEnabled = false;
        check("disabling an active field cancels its draft", !field.IsInteracting && field.Value == 12);
        field.IsEnabled = true; field.Value = 9999; scrubber.TryBegin(0); scrubber.Update(1000); scrubber.End(true);
        check("free dragging still respects the property's hard limit", field.Value == 10000);
        var integer = new NumericValueField(1, 1000000, sliderMaximum: 100, integerOnly: true) { Value = 1 };
        integer.Input.Text = "1.5";
        check("integer fields reject fractional text", !integer.TryGetValue(out _) && integer.Value == 1);
        field.Unit = "px"; field.Value = 42;
        check("units remain outside editable numeric text and are exposed to accessibility", field.Input.Text == "42" && field.TryGetValue(out var pixels) && pixels == 42 && Wpf.Automation.AutomationProperties.GetHelpText(field.Input) == "px");
        check("range tooltip includes the actual unit", field.Slider.ToolTip.ToString()!.EndsWith(" px"));
        field.Value = 50; int starts = 0; double startValue = 0;
        field.GestureStarted += () => { starts++; startValue = field.Value; };
        field.Slider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = Wpf.UIElement.PreviewMouseLeftButtonDownEvent });
        check("empty-track mouse press reaches gesture start before mutating the value", starts == 1 && startValue == 50);
        field.CancelGesture();
        field.Value = 50; field.AnchorScrubber!.TryBegin(0); field.AnchorScrubber.Update(90);
        check("embedded drag anchor changes a value without entering its text or range", field.IsInteracting && field.Value > 50);
        field.AnchorScrubber.End(false);
        check("embedded anchor cancellation restores the original value", !field.IsInteracting && field.Value == 50);
        check("embedded anchors retain editing keys instead of drawing commands", !EditorShortcutPolicy.ShouldRoute(0x57, System.Windows.Input.ModifierKeys.None, field.DragAnchor));
        check("embedded anchors retain the global undo shortcut", EditorShortcutPolicy.ShouldRoute(0x5A, System.Windows.Input.ModifierKeys.Control, field.DragAnchor));
    }

    private static void InspectorGestures(Action<string, bool> check)
    {
        var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60 });
        var inspector = new InspectorWindow(() => session);
        inspector.PrepareForShow();
        var fields = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields");
        var field = fields["Opacity"]; Measure(field, 110, 34);
        string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        field.BeginGesture(); field.UpdateRail(30); field.UpdateRail(50);
        check("inspector rail previews in a single pending transaction", session.IsTransactionActive && session.UndoCount == undo && JsonSerializer.Serialize(session.Document) != before);
        field.EndGesture(true);
        check("inspector rail releases as one undo step", !session.IsTransactionActive && session.UndoCount == undo + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        inspector.Refresh(); inspector.PrepareForShow(); field.BeginGesture(); field.UpdateRail(20); field.CancelGesture();
        check("inspector cancel restores model and undo history", !session.IsTransactionActive && session.UndoCount == undo && JsonSerializer.Serialize(session.Document) == before);
        field.BeginGesture(); field.UpdateRail(30); inspector.CompletePendingEdit();
        check("leaving inspector cancels unfinished numeric gestures", !field.IsInteracting && !session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before);
        var scrubber = field.AnchorScrubber!;
        scrubber.TryBegin(0); scrubber.Update(-50); scrubber.End(true);
        check("generic inspector label drag uses the same undo boundary", session.UndoCount == undo + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        var first = session.Document.Elements[0].Id;
        var other = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 40 }; session.Add(other);
        session.SetSelection([first]); inspector.Refresh(); inspector.PrepareForShow(); before = JsonSerializer.Serialize(session.Document);
        field.BeginGesture(); field.UpdateRail(20);
        // The real pointer/command transfer completes the editor before choosing
        // another layer. A detached hidden panel does not observe the session.
        inspector.CompletePendingEdit(); session.SetSelection([other.Id]); inspector.PrepareForShow();
        check("selection transfer cancels preview and prepares the newly selected layer", !field.IsInteracting && !session.IsTransactionActive && session.Selection.SetEquals([other.Id]) && JsonSerializer.Serialize(session.Document) == before);
        check("inspector numeric checks remain detached", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 && !inspector.IsVisible);
    }

    private static void ToolGestures(string root, Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Number };
        var panel = Panel(root, controller);
        var fields = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
        var counter = fields["NextNumber"]; Measure(counter, 120, 34);
        int original = session.Document.NextNumber, undo = session.UndoCount;
        counter.BeginGesture(); counter.UpdateRail(40); counter.UpdateRail(60);
        check("next number rail does not generate history for each preview", session.IsTransactionActive && session.UndoCount == undo);
        counter.EndGesture(true);
        check("next number range gesture is one reversible edit", session.UndoCount == undo + 1 && session.Undo() && session.Document.NextNumber == original);
        panel.Refresh(); panel.PrepareForShow(); counter.BeginGesture(); counter.UpdateRail(70); counter.CancelGesture();
        check("next number cancellation restores counter and history", session.Document.NextNumber == original && session.UndoCount == undo && !session.IsTransactionActive);
        controller.Tool = EditorTool.Rectangle; panel.Refresh(); panel.PrepareForShow();
        var width = fields["DefaultWidth"]; Measure(width, 120, 34); double originalWidth = controller.Parameters.DefaultWidth;
        width.BeginGesture(); width.UpdateRail(60); width.CancelGesture();
        check("tool numeric cancellation restores parameters without document history", controller.Parameters.DefaultWidth == originalWidth && session.UndoCount == undo);
        width.BeginGesture(); width.UpdateRail(70); panel.Refresh(); panel.PrepareForShow();
        check("tool refresh cancels active gestures without stale preview state", !width.IsInteracting && controller.Parameters.DefaultWidth == originalWidth);
        width.BeginGesture();
        fields["StrokeWidth"].Input.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, fields["StrokeWidth"].Input, fields["DefaultWidth"].Input)
            { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
        check("focus leaving another numeric input does not cancel the new gesture", width.IsInteracting);
        width.CancelGesture();
        // DesktopSession refreshes the options panel synchronously when a tool
        // parameter changes. Prepare explicitly simulates that visible-state
        // synchronization for this detached fixture.
        panel.ParametersChanged += panel.PrepareForShow;
        width.BeginGesture(); width.UpdateRail(50); width.UpdateRail(80);
        check("tool rail survives the runtime parameter-refresh feedback loop", width.IsInteracting && width.Value == controller.Parameters.DefaultWidth && width.Value != originalWidth);
        width.EndGesture(true);
        var drag = width.AnchorScrubber!;
        double beforeDrag = width.Value;
        drag.TryBegin(0); drag.Update(30); drag.Update(90);
        check("tool label survives multiple runtime parameter-refresh callbacks", drag.IsActive && width.IsInteracting && width.Value > beforeDrag);
        drag.End(false);
        check("tool feedback loop cancellation restores the gesture's original value", !width.IsInteracting && controller.Parameters.DefaultWidth == beforeDrag && width.Value == beforeDrag);
    }

    private static void Layout(string root, string name, Action<string, bool> check, string? output)
    {
        var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 320, Height = 180 });
        var inspector = new InspectorWindow(() => session);
        inspector.PrepareForShow();
        var properties = Field<Controls.StackPanel>(inspector, "propertyPanel");
        Measure((Wpf.FrameworkElement)inspector.Content, inspector.Width - 2, 650);
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
        var panel = Panel(root, controller); Measure(panel, ToolOptionsPanel.PreferredWidth, 620);
        var grid = Field<Controls.Grid>(panel, "parameterGrid");
        check(name + $" property and tool columns share the same default usable width ({properties.ActualWidth:0.##}/{grid.ActualWidth:0.##})", Math.Abs(properties.ActualWidth - PropertyPanelLayout.ContentWidth) < 1 && Math.Abs(grid.ActualWidth - properties.ActualWidth) < 1);
        var rows = Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows");
        var canvas = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        canvas.Refresh(new(), false); canvas.PrepareForShow(); Measure((Wpf.FrameworkElement)canvas.Content, canvas.Width - 2, 540);
        var canvasWidth = Field<NumericValueField>(canvas, "width");
        var canvasBody = (Wpf.FrameworkElement)((Wpf.FrameworkElement)canvasWidth.Parent).Parent;
        check(name + " canvas parameter column uses the shared width", Math.Abs(canvasBody.ActualWidth - properties.ActualWidth) < 1);
        check(name + " width and height share the same row", Controls.Grid.GetRow(rows["DefaultWidth"]) == Controls.Grid.GetRow(rows["DefaultHeight"]));
        var captions = Field<Dictionary<string, Controls.TextBlock>>(panel, "captions");
        var aspect = Field<AspectRatioButton>(panel, "aspect");
        var left = rows["DefaultWidth"].TranslatePoint(new(rows["DefaultWidth"].ActualWidth, 0), panel).X;
        var right = rows["DefaultHeight"].TranslatePoint(new(), panel).X;
        var chain = aspect.TranslatePoint(new(), panel).X;
        check(name + " chain has its own gap between dimensions", right - left >= PropertyPanelLayout.PairGap - .01 && chain >= left && chain + aspect.ActualWidth <= right);
        check(name + " unrelated corner and stroke properties use distinct full rows", Controls.Grid.GetRow(rows["CornerRadius"]) != Controls.Grid.GetRow(rows["StrokeWidth"]) && rows["CornerRadius"].ActualWidth == grid.ActualWidth);
        var labelWidths = new[] { "StrokeWidth", "CornerRadius", "Foreground", "StrokePattern" }.Select(key => ((Controls.Grid)rows[key]).ColumnDefinitions[0].ActualWidth).ToArray();
        check(name + " numeric, color and dropdown values share the panel label column within its cap", labelWidths.All(width => Math.Abs(width - labelWidths[0]) < .01 && width <= PropertyPanelLayout.LabelWidth + PropertyPanelLayout.LabelGap));
        check(name + " common rectangle labels fit without scrolling", new[] { "StrokeWidth", "CornerRadius", "StrokePattern" }.All(key => captions[key] is PropertyLabel label && label.OverflowWidth < 1) && ((Controls.Grid)rows["Size"]).Children.OfType<PropertyLabel>().Single().OverflowWidth < 1);
        var inspectorRows = Field<Dictionary<string, Wpf.FrameworkElement>>(inspector, "fieldContainers");
        var inspectorNumbers = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields");
        double Edge(string key, bool end = false) => inspectorNumbers[key].TranslatePoint(new(end ? inspectorNumbers[key].ActualWidth : 0, 0), properties).X;
        double pixel = 1 / Media.VisualTreeHelper.GetDpi(properties).DpiScaleX;
        check(name + " position and size have equal input widths within pixel rounding and aligned edges", new[] { "X", "Y", "W", "H" }.All(key => Math.Abs(inspectorNumbers[key].ActualWidth - inspectorNumbers["W"].ActualWidth) <= pixel + .01) && Math.Abs(Edge("X") - Edge("W")) < .01 && Math.Abs(Edge("Y") - Edge("H")) < .01);
        check(name + " empty XY gap equals the WH chain gap", Math.Abs(Edge("Y") - Edge("X", true) - PropertyPanelLayout.PairGap) < .01 && Math.Abs(Edge("H") - Edge("W", true) - PropertyPanelLayout.PairGap) < .01);
        var toggle = Field<Controls.CheckBox>(panel, "linkColors");
        var toggleRow = (Controls.Grid)rows["LinkColors"];
        check(name + " boolean names can cross the label column while the switch stays at the right edge", toggleRow.Children.OfType<Controls.TextBlock>().Single().ActualWidth > PropertyPanelLayout.LabelWidth && Math.Abs(toggle.TranslatePoint(new(toggle.ActualWidth, 0), grid).X - grid.ActualWidth) < .01);
        var groupLabel = ((Controls.Grid)rows["Size"]).Children.OfType<Controls.TextBlock>().Single();
        var switchLabel = toggleRow.Children.OfType<Controls.TextBlock>().Single();
        check(name + " grouped and boolean property labels share typography", groupLabel.FontSize == PropertyPanelLayout.LabelFontSize && switchLabel.FontSize == groupLabel.FontSize && switchLabel.FontFamily.Equals(groupLabel.FontFamily) && switchLabel.FontWeight == groupLabel.FontWeight);
        var fieldHeight = rows["DefaultWidth"].ActualHeight;
        check(name + " numeric color dropdown and switch rows share the standard height (" + string.Join(", ", new[] { "StrokeWidth", "Foreground", "StrokePattern", "Size", "LinkColors" }.Select(key => key + "=" + rows[key].ActualHeight)) + ")", new[] { "StrokeWidth", "Foreground", "StrokePattern", "Size", "LinkColors" }.All(key => Math.Abs(rows[key].ActualHeight - PropertyPanelLayout.ControlHeight) < .01) && fieldHeight == PropertyPanelLayout.ControlHeight);
        check(name + " rectangle does not leak hidden text or arrow rows", new[] { "FontFamily", "FontSize", "TextAlignment", "Number", "ArrowHeadStyle", "StartArrowHead" }.All(key => inspectorRows[key].Visibility == Wpf.Visibility.Collapsed));
        var fields = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
        check(name + " numeric fields carry their actual unit", fields["DefaultWidth"].Unit == "px" && fields["Opacity"].Unit == "%" && fields["NextNumber"].Unit == "#" && fields["NumberStep"].Unit == L.Get("Numeric.Unit.Step"));
        foreach (var field in new[] { fields["DefaultWidth"], fields["DefaultHeight"] })
        {
            var inputBottom = field.Input.TranslatePoint(new(0, field.Input.ActualHeight), field).Y;
            var sliderTop = field.Slider.TranslatePoint(new(), field).Y;
            check(name + " anchor and text have separate hit areas", field.DragAnchor.ActualWidth == 24 && field.Input.TranslatePoint(new(), field).X >= field.DragAnchor.TranslatePoint(new(24, 0), field).X);
            check(name + $" text and rail have separate hit areas at paired width ({field.Input.ActualWidth:0.#}/{field.Slider.ActualWidth:0.#}; {inputBottom:0.#}/{sliderTop:0.#}; h={field.ActualHeight:0.#})", field.Input.ActualWidth >= 70 && field.Slider.ActualWidth >= 58 && inputBottom <= sliderTop + .01 && Math.Abs(field.ActualHeight - 34) < .01);
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            Render(panel, Path.Combine(output, name + "-tools.png"), 144);
            Render((Wpf.FrameworkElement)inspector.Content, Path.Combine(output, name + "-inspector.png"), 144);
            Render((Wpf.FrameworkElement)canvas.Content, Path.Combine(output, name + "-canvas.png"), 144);
            var settings = new AppSettingsWindow();
            var general = settings.AddSection(L.Get("Settings.General"), L.Get("Settings.GeneralHint"));
            AppSettingsWindow.AddSetting(general, L.Get("Settings.Theme"), new Controls.ComboBox { ItemsSource = new[] { L.Get("Settings.SystemTheme"), L.Get("Settings.LightTheme"), L.Get("Settings.DarkTheme") }, SelectedIndex = 1, MinHeight = 34 });
            AppSettingsWindow.AddSetting(general, L.Get("Settings.Language"), new Controls.ComboBox { ItemsSource = new[] { "简体中文", "English" }, SelectedIndex = name.StartsWith("en") ? 1 : 0, MinHeight = 34 }, L.Get("Settings.LanguageRestartHint"));
            AppSettingsWindow.AddSetting(general, L.Get("Feedback3.Toolbar.ShowShortcutHints"), new Controls.CheckBox { IsChecked = true });
            Measure((Wpf.FrameworkElement)settings.Content, settings.Width - 2, 590);
            Render((Wpf.FrameworkElement)settings.Content, Path.Combine(output, name + "-settings-component.png"), 144);
            var colors = new ColorPickerPanel(ColorRole.Foreground, new ColorLibrary(Path.Combine(root, "colors-preview.json"))) { Value = 0xC0EF4444 };
            colors.PrepareForShow();
            Measure(colors, 660, 500); Render(colors, Path.Combine(output, name + "-colors.png"), 144);
        }
        panel.Width = 320; Measure(panel, 320, 620);
        check(name + " tool minimum keeps related dimensions usable without stacking its columns", panel.ActualWidth >= PropertyPanelLayout.TwoColumnMinimumWidth - 2 && Controls.Grid.GetRow(rows["DefaultWidth"]) == Controls.Grid.GetRow(rows["DefaultHeight"]) && fields["DefaultWidth"].ActualWidth >= 100);
        var narrow = new NumericValueField(0, 100) { Value = 42, Unit = "px" }; Measure(narrow, 72, 34);
        check(name + " numeric control fits a 72 DIP slot", narrow.Input.ActualWidth <= 72 && narrow.Slider.ActualWidth >= 50 && narrow.ActualHeight == 34);
        canvas.CloseForDispose();
    }

    private static void AspectRatios(string root, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle, DefaultWidth = 160, DefaultHeight = 100 };
        var panel = Panel(root, controller); panel.ParametersChanged += panel.Refresh;
        using var visibleToolPanel = new VisiblePanelHost(panel);
        check("live tool ratio fixture is visible without foreground activation", panel.IsVisible && !visibleToolPanel.OwnsForeground);
        var numbers = Field<Dictionary<string, NumericValueField>>(panel, "numericFields");
        var chain = Field<AspectRatioButton>(panel, "aspect");
        chain.IsChecked = true; chain.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        numbers["DefaultWidth"].Input.Text = "320";
        check("tool chain links typed width to height", controller.ConstrainAspect && controller.DefaultWidth == 320 && controller.DefaultHeight == 200 && numbers["DefaultHeight"].Value == 200);
        numbers["DefaultHeight"].Input.Text = "150";
        check("tool chain links typed height back to width", controller.DefaultHeight == 150 && controller.DefaultWidth == 240 && numbers["DefaultWidth"].Value == 240);
        var width = numbers["DefaultWidth"]; Measure(width, 120, 34);
        string before = JsonSerializer.Serialize(controller.Parameters);
        var drag = width.AnchorScrubber!; drag.TryBegin(0); drag.Update(90);
        check("tool paired value updates during name dragging", width.IsInteracting && Math.Abs(controller.DefaultWidth / controller.DefaultHeight - 1.6) < 1e-9 && Math.Abs(numbers["DefaultHeight"].Value - controller.DefaultHeight) < .001);
        drag.End(false);
        check("cancelling tool size drag restores both dimensions and lock", JsonSerializer.Serialize(controller.Parameters) == before && chain.IsChecked == true);
        width.BeginGesture(); width.UpdateRail(80); width.EndGesture(true);
        check("tool rail preserves a locked non-square ratio", Math.Abs(controller.DefaultWidth / controller.DefaultHeight - 1.6) < 1e-9);
        string path = Path.Combine(root, "aspect-presets.json");
        var saved = new ToolPresetLibrary(path).Save("Linked rectangle", controller.Parameters);
        var loaded = new ToolPresetLibrary(path).User.Single(preset => preset.Id == saved.Id).Parameters;
        check("personal preset round trip preserves lock and linked dimensions", loaded.ConstrainAspect && loaded.DefaultWidth == controller.DefaultWidth && loaded.DefaultHeight == controller.DefaultHeight);
        chain.IsChecked = false; chain.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        double height = controller.DefaultHeight; width.Input.Text = "500";
        check("unlocked tool width leaves height independent", !controller.ConstrainAspect && controller.DefaultWidth == 500 && controller.DefaultHeight == height);

        var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 160, Height = 100 });
        var inspector = new InspectorWindow(() => session);
        inspector.PrepareForShow();
        using var visibleInspector = new VisibleWindowHost(inspector);
        check("live inspector ratio fixture is visible without foreground activation", inspector.IsVisible && !visibleInspector.OwnsForeground);
        var fields = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields");
        var lockButton = Field<AspectRatioButton>(inspector, "keepAspect");
        lockButton.IsChecked = true; lockButton.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent)); inspector.CompletePendingEdit();
        check("inspector chain writes the serializable layer property", JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(session.Document.Elements[0]))!.ConstrainAspect == true);
        check("inspector chain toggle is undoable", session.Undo() && session.Document.Elements[0].ConstrainAspect is null);
        session.Redo(); inspector.Refresh(); inspector.PrepareForShow(); int undo = session.UndoCount;
        fields["W"].Input.Text = "320";
        check("inspector typed width synchronizes height immediately", fields["H"].Value == 200 && session.Document.Elements[0].Height == 200);
        inspector.CompletePendingEdit();
        check("linked inspector resize is one undo step", session.UndoCount == undo + 1 && session.Undo() && session.Document.Elements[0].Width == 160 && session.Document.Elements[0].Height == 100);
        inspector.Refresh();
        inspector.PrepareForShow();
        Invoke(inspector, "BeginScrub", "W"); Invoke(inspector, "UpdateScrub", "W", 32d);
        check("inspector name drag updates the companion field before release", fields["W"].Value == 192 && fields["H"].Value == 120);
        Invoke(inspector, "FinishScrub", false);
        check("inspector name cancellation restores both dimensions", fields["W"].Value == 160 && fields["H"].Value == 100);
        Measure(fields["H"], 120, 34); fields["H"].BeginGesture(); fields["H"].UpdateRail(60);
        check("inspector rail synchronizes both dimensions during preview", Math.Abs(fields["W"].Value / fields["H"].Value - 1.6) < .0001);
        fields["H"].CancelGesture();
        check("inspector rail cancellation restores dimensions and lock", fields["W"].Value == 160 && fields["H"].Value == 100 && lockButton.IsChecked == true);
        lockButton.IsChecked = false; lockButton.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent)); inspector.CompletePendingEdit();
        fields["W"].Input.Text = "200"; inspector.CompletePendingEdit();
        check("unlocked inspector resizing preserves the other axis", session.Document.Elements[0].Width == 200 && session.Document.Elements[0].Height == 100 && session.Document.Elements[0].ConstrainAspect == false);
        var anchor = Field<List<NumericScrubber>>(inspector, "scrubbers").Single(scrub => ReferenceEquals(Field<Wpf.FrameworkElement>(scrub, "label"), fields["W"].DragAnchor));
        int anchorUndo = session.UndoCount; anchor.TryBegin(0); anchor.Update(50); anchor.Update(90); anchor.End(true);
        check("inspector embedded anchor changes geometry in one undo step", session.Document.Elements[0].Width > 200 && session.UndoCount == anchorUndo + 1);
        check("inspector embedded anchor undo restores width", session.Undo() && session.Document.Elements[0].Width == 200);
        inspector.Refresh(); inspector.PrepareForShow(); anchor.TryBegin(0); anchor.Update(90); anchor.End(false);
        check("inspector embedded anchor cancellation restores geometry", session.Document.Elements[0].Width == 200 && !session.IsTransactionActive);
    }

    private static void OverflowLabels(Action<string, bool> check)
    {
        var shortLabel = new PropertyLabel("Width"); Measure(shortLabel, PropertyPanelLayout.LabelWidth, 20); shortLabel.StartScroll();
        check("fitting label remains still on hover", shortLabel.OverflowWidth == 0 && !shortLabel.IsScrolling);
        var label = new PropertyLabel("A longer translated property name"); Measure(label, PropertyPanelLayout.LabelWidth, 20);
        double width = label.ActualWidth; label.StartScroll();
        check("overflow scroll changes only text, keeping the hit area fixed", label.OverflowWidth > 0 && label.ActualWidth == width && (!Wpf.SystemParameters.ClientAreaAnimation || label.IsScrolling) && label.RenderTransform.Value.IsIdentity);
        label.StopScroll();
        check("stopped labels return to an ellipsis and retain their full accessible name", !label.IsScrolling && label.TextTrimming == Wpf.TextTrimming.CharacterEllipsis && Wpf.Automation.AutomationProperties.GetName(label) == label.Text);
    }

    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static ToolOptionsPanel Panel(string root, EditorController controller)
    {
        var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(root, "presets.json")), new ColorLibrary(Path.Combine(root, "colors.json")));
        panel.PrepareForShow(); return panel;
    }
    private sealed class VisiblePanelHost : IDisposable
    {
        private readonly System.Windows.Interop.HwndSource source;
        internal VisiblePanelHost(Wpf.FrameworkElement panel)
        {
            panel.Opacity = 0; panel.IsHitTestVisible = false;
            source = new(new System.Windows.Interop.HwndSourceParameters("FloatCanvas.NumericToolRatio")
            { PositionX = -10000, PositionY = -10000, Width = 820, Height = 650, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0 });
            source.RootVisual = panel; ShowWindow(source.Handle, 4);
            Measure(panel, ToolOptionsPanel.PreferredWidth, 620);
        }
        internal bool OwnsForeground { get { GetWindowThreadProcessId(GetForegroundWindow(), out uint process); return process == Environment.ProcessId; } }
        public void Dispose() { source.RootVisual = null; source.Dispose(); }
    }
    private sealed class VisibleWindowHost : IDisposable
    {
        private readonly Wpf.Window window;
        internal VisibleWindowHost(Wpf.Window window)
        {
            this.window = window;
            window.ShowActivated = false; window.Opacity = 0; window.IsHitTestVisible = false;
            window.WindowStartupLocation = Wpf.WindowStartupLocation.Manual; window.Left = window.Top = -10000;
            window.SourceInitialized += (_, _) =>
            {
                nint handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                SetWindowLongPtrW(handle, -20, GetWindowLongPtrW(handle, -20) | 0x080000A0);
            };
            window.Show(); window.UpdateLayout();
        }
        internal bool OwnsForeground { get { GetWindowThreadProcessId(GetForegroundWindow(), out uint process); return process == Environment.ProcessId; } }
        public void Dispose()
        {
            System.ComponentModel.CancelEventHandler permitClose = (_, args) => args.Cancel = false;
            window.Closing += permitClose;
            try { window.Close(); }
            finally { window.Closing -= permitClose; }
        }
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Measure(Wpf.FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private static void Render(Wpf.FrameworkElement element, string path, double dpi)
    {
        double width = element.ActualWidth + element.Margin.Left + element.Margin.Right;
        double height = element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        var bitmap = new Imaging.RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96), (int)Math.Ceiling(height * dpi / 96), dpi, dpi, Media.PixelFormats.Pbgra32);
        var background = new Media.DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle((Media.Brush)Wpf.Application.Current.Resources["CanvasSurface"], null, new(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(element); var encoder = new Imaging.PngBitmapEncoder(); encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
