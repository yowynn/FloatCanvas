using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

internal static class InspectorLiveTests
{
    internal static void Run(Action<string, bool> check)
    {
        var session = new EditorSession();
        session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60 });
        var first = session.Selection.Single();
        session.Add(new() { Kind = ElementKind.Text, Text = "Other", Width = 180, Height = 30 });
        var second = session.Selection.Single();
        session.SetSelection([first]);
        var window = new InspectorWindow(() => session);
        using var surface = new InspectorTestSurface(window);
        var fields = Field<Dictionary<string, Controls.TextBox>>(window, "fields");
        var containers = Field<Dictionary<string, Wpf.FrameworkElement>>(window, "fieldContainers");
        int undo = session.UndoCount;
        fields["W"].Text = "1"; fields["W"].Text = "12"; fields["W"].Text = "125";
        check("typing previews every valid value without per-keystroke history", session.Document.Elements[0].Width == 125 && session.UndoCount == undo);
        fields["W"].Text = "-";
        check("incomplete numeric text keeps the last valid preview and input", session.Document.Elements[0].Width == 125 && fields["W"].Text == "-");
        session.SetSelection([second]);
        check("selection notification commits prior edit and replaces invalid old input", session.UndoCount == undo + 1 && fields["W"].Text == "180" && fields["Text"].Text == "Other" && !session.IsTransactionActive);
        check("one undo restores the complete typing burst", session.Undo() && session.Document.Elements[0].Width == 100);
        session.SetSelection([second]);
        fields["FontSize"].Text = "40";
        check("dependent text height refreshes while font size remains editable", double.Parse(fields["H"].Text) == session.Document.Elements[1].Height && fields["FontSize"].Text == "40");
        window.CompletePendingEdit();
        session.SetSelection([first, second]);
        check("multiple selection is explicitly labeled and shows only common fields", Field<Controls.TextBlock>(window, "selectionLabel").Visibility == Wpf.Visibility.Visible &&
            Field<Controls.TextBlock>(window, "selectionLabel").Text.Contains("2") && containers["Opacity"].Visibility == Wpf.Visibility.Visible &&
            containers["Stroke"].Visibility == Wpf.Visibility.Visible && containers["Fill"].Visibility == Wpf.Visibility.Collapsed && containers["Text"].Visibility == Wpf.Visibility.Collapsed && containers["FontSize"].Visibility == Wpf.Visibility.Collapsed);
        check("different selected numeric values are represented as mixed blank input", fields["W"].Text == "");
        undo = session.UndoCount;
        fields["Opacity"].Text = "42";
        check("a shared property immediately updates every selected layer", session.Document.Elements.All(e => e.Style.Opacity == .42));
        Invoke(window, "CompleteEdit");
        check("multi-layer edit has one undo entry", session.UndoCount == undo + 1 && session.Undo() && session.Document.Elements.All(e => e.Style.Opacity == 1));
        session.SetSelection([first, second]);
        var before = session.Document.Elements.Select(e => ElementGeometry.Bounds(e).Left).ToArray();
        check("common numeric label starts a multi-layer scrub", (bool)Invoke(window, "BeginScrub", "X")!);
        Invoke(window, "UpdateScrub", "X", 15d); Invoke(window, "UpdateScrub", "X", 30d); Invoke(window, "FinishScrub", true);
        check("multi-layer scrub applies one relative delta per layer", session.Document.Elements.Select((e, i) => Math.Abs(ElementGeometry.Bounds(e).Left - before[i] - 30) < 1e-8).All(v => v));
        check("one undo restores the whole multi-layer scrub", session.Undo() && session.Document.Elements.Select((e, i) => Math.Abs(ElementGeometry.Bounds(e).Left - before[i]) < 1e-8).All(v => v));
        session.Execute("different colors", _ => session.Document.Elements[1].Style.Stroke = 0xFFABCDEF);
        var colors = Field<Dictionary<string, ColorField>>(window, "colorFields");
        var firstColor = session.Document.Elements[0].Style.Stroke;
        check("mixed paint is explicitly marked", colors["Stroke"].IsMixed);
        var mixedColors = session.Document.Elements.Select(e => e.Style.Stroke).ToArray();
        var mixedPicker = Field<ColorPickerPanel>(colors["Stroke"], "picker");
        mixedPicker.Value = colors["Stroke"].Value;
        mixedPicker.PrepareForShow();
        typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(colors["Stroke"], true);
        typeof(ColorField).GetField("draftCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(colors["Stroke"], false);
        colors["Stroke"].ClosePicker();
        check("closing an unchanged mixed picker preserves each selected color", colors["Stroke"].IsMixed && session.Document.Elements.Select(e => e.Style.Stroke).SequenceEqual(mixedColors));
        Invoke(mixedPicker, "Apply");
        check("an explicit unchanged picker choice unifies a mixed selection", session.Document.Elements.All(e => e.Style.Stroke == firstColor) && !colors["Stroke"].IsMixed);
        window.CompletePendingEdit();
        session.Execute("restore mixed colors", _ => session.Document.Elements[1].Style.Stroke = 0xFFABCDEF);
        colors["Stroke"].SetUserValue(firstColor);
        check("choosing the first layer's color still unifies a mixed selection", session.Document.Elements.All(e => e.Style.Stroke == firstColor) && !colors["Stroke"].IsMixed);
        window.CompletePendingEdit();
        session.SetSelection([first]); fields["X"].Text = "25";
        PumpIdle();
        check("idle typing commits automatically without a confirmation action", !session.IsTransactionActive && Math.Abs(ElementGeometry.Bounds(session.Document.Elements[0]).Left - 25) < 1e-8);
        session.SetSelection([first]);
        session.Execute("lock", _ => session.Document.Elements[0].Locked = true);
        session.SetSelection([first, second]);
        check("selection with no jointly editable fields has an empty property list", Field<Controls.StackPanel>(window, "propertyPanel").Visibility == Wpf.Visibility.Collapsed);
        session.SetSelection([second]);
        session.Add(new() { Kind = ElementKind.Text, Text = "Different font", Style = new ElementStyle { FontFamily = "Arial" } });
        var third = session.Selection.Single();
        session.SetSelection([second, third]);
        check("mixed font families clear the visible font picker", Field<Controls.ComboBox>(window, "fontPicker").SelectedIndex == -1);
        session.SetSelection([second]);
        var commandSession = (DesktopSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        Set(commandSession, "_editor", new EditorController(session)); Set(commandSession, "_inspector", window);
        Set(commandSession, "_canvasCalibration", new CanvasCalibrationController());
        fields["W"].Text = "245";
        bool commandSeesEdit = false;
        ((Task)Invoke(commandSession, "RunCommand", (Func<Task>)(() => { commandSeesEdit = !session.IsTransactionActive && Math.Abs(ElementGeometry.Bounds(session.Document.Elements.Single(e => e.Id == second), includeStroke: false).Width - 245) < 1e-8; return Task.CompletedTask; }))!).GetAwaiter().GetResult();
        check("async command wrapper commits live properties before cancelling tool gestures", commandSeesEdit);
        fields["W"].Text = "255";
        bool uiSeesEdit = false;
        Invoke(commandSession, "RunUiAction", (Action)(() => { uiSeesEdit = !session.IsTransactionActive && Math.Abs(ElementGeometry.Bounds(session.Document.Elements.Single(e => e.Id == second), includeStroke: false).Width - 255) < 1e-8; }));
        check("UI command wrapper commits live properties before mutation", uiSeesEdit);
        session.SetSelection([]);
        check("empty selection collapses the property list without synchronizing hidden fields", containers.Values.All(container => container.Visibility == Wpf.Visibility.Collapsed) && Field<Controls.StackPanel>(window, "propertyPanel").Visibility == Wpf.Visibility.Collapsed);
    }

    private static void PumpIdle()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
