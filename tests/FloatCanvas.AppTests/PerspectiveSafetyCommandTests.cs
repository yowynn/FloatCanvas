using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Production command and property paths, without creating HWNDs, hooks or desktop input.</summary>
internal static class PerspectiveSafetyCommandTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SelectionTransforms(check);
        InspectorScrubs(check);
        KeyboardCommands(check);
    }

    private static SceneDocument Scene(params CanvasElement[] elements)
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
        return new() { Viewport = new(0, -400, 1000, 1000), Elements = elements.ToList(),
            Canvas = new() { Projection = new() { Enabled = true, Columns = 100, Rows = 100, CellSize = 1, Matrix = matrix,
                Quad = EditorController.Corners(new RectD(0, 0, 100, 100)).Select(matrix.TransformPoint).ToList() } } };
    }

    private static CanvasElement Box(double y, double height = 50) => new()
    {
        Kind = ElementKind.Rectangle, Width = 100, Height = height,
        Transform = Affine2D.Translation(100, y), Style = new() { StrokeWidth = 0 }
    };

    private static void SelectionTransforms(Action<string, bool> check)
    {
        foreach (bool billboard in new[] { false, true })
        {
            var first = Box(100); var near = Box(3749.5);
            if (billboard) { near.IsBillboard = true; near.BillboardAnchor = new(200, 3799.5); }
            var group = new CanvasGroup { Name = "Safety command group" };
            first.GroupId = near.GroupId = group.Id;
            var document = Scene(first, near); document.Groups.Add(group);
            var session = new EditorSession(document); session.SetSelection([first.Id, near.Id]);
            string before = JsonSerializer.Serialize(document); var selection = session.Selection.ToHashSet();
            check($"command fixture begins with safe {(billboard ? "billboard" : "planar")} candidates",
                document.Elements.All(e => CanvasMapping.Create(document).IsElementSafe(e)));
            bool rejected = false;
            try { session.TransformSelection(Affine2D.Translation(0, 1)); }
            catch (InvalidOperationException error) when (error.Message == L.Get("Perspective.InputOutsidePlane")) { rejected = true; }
            check($"unsafe {(billboard ? "billboard" : "planar")} keyboard transform rejects the whole group atomically",
                rejected && JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals(selection) &&
                !session.IsTransactionActive && session.UndoCount == 0);

            session.BeginTransaction("live transform safety"); rejected = false;
            try { session.ApplySelectionTransform(Affine2D.Translation(0, 1)); }
            catch (InvalidOperationException error) when (error.Message == L.Get("Perspective.InputOutsidePlane")) { rejected = true; }
            check("live selection transform validates every candidate before changing any member", rejected &&
                session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before);
            session.Cancel();
            session.TransformSelection(Affine2D.Translation(0, -20));
            check("safe selection movement remains one reversible operation", session.UndoCount == 1 &&
                session.Document.Elements.All(e => CanvasMapping.Create(session.Document).IsElementSafe(e)) &&
                session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals(selection));
        }
    }

    private static void InspectorScrubs(Action<string, bool> check)
    {
        foreach (string key in new[] { "Y", "H", "StrokeWidth" })
        {
            var safe = Box(100, 100); var near = Box(3650, 100);
            var state = new EditorSession(Scene(safe, near)); state.SetSelection([safe.Id, near.Id]);
            var inspector = new InspectorWindow(() => state);
            Field<CheckBox>(inspector, "keepAspect").IsChecked = false;
            var label = ((Grid)Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers")[key])
                .Children.OfType<TextBlock>().Single();
            string before = JsonSerializer.Serialize(state.Document); var selected = state.Selection.ToHashSet();
            check(key + " safety scrub begins without native input", (bool)Invoke(inspector, "BeginScrub", key)!);
            double accepted = key == "StrokeWidth" ? 100 : 25;
            double rejected = key == "StrokeWidth" ? 1200 : 150;
            Invoke(inspector, "UpdateScrub", key, accepted);
            string preview = JsonSerializer.Serialize(state.Document);
            check(key + " scrub previews safe complete candidates", preview != before && state.UndoCount == 0 && state.IsTransactionActive);
            Invoke(inspector, "UpdateScrub", key, rejected);
            check(key + " unsafe scrub keeps every selected member at the previous safe preview and shows forbidden cursor",
                JsonSerializer.Serialize(state.Document) == preview && label.Cursor == Cursors.No &&
                Field<TextBlock>(inspector, "status").Text == L.Get("Perspective.InputOutsidePlane"));
            Invoke(inspector, "FinishScrub", true);
            check(key + " unsafe scrub release restores the full down state and retained selection", !state.IsTransactionActive &&
                JsonSerializer.Serialize(state.Document) == before && state.Selection.SetEquals(selected) && state.UndoCount == 0 &&
                label.Cursor == Cursors.SizeWE && Field<TextBlock>(inspector, "status").Text == L.Get("Perspective.InputOutsidePlane"));

            Invoke(inspector, "BeginScrub", key);
            Invoke(inspector, "UpdateScrub", key, rejected);
            Invoke(inspector, "UpdateScrub", key, accepted);
            check(key + " returning to safety clears the forbidden cursor and message during the same scrub",
                label.Cursor == Cursors.SizeWE && Field<TextBlock>(inspector, "status").Text.Length == 0);
            Invoke(inspector, "FinishScrub", true);
            check(key + " returned-safe scrub commits once and undoes exactly", state.UndoCount == 1 &&
                state.Document.Elements.All(e => CanvasMapping.Create(state.Document).IsElementSafe(e)) && state.Undo() &&
                JsonSerializer.Serialize(state.Document) == before);
            check(key + " scrub regression never creates or shows an HWND", !inspector.IsVisible && new WindowInteropHelper(inspector).Handle == 0);
            inspector.Close();
        }
    }

    private static void KeyboardCommands(Action<string, bool> check)
    {
        var originalKeys = new byte[256]; bool saved = GetKeyboardState(originalKeys);
        check("safety shortcut fixture can preserve its thread keyboard state", saved);
        if (!saved) return;
        try
        {
            foreach (bool shift in new[] { false, true })
            {
                var source = Box(shift ? 3745 : 3749.5);
                var state = new EditorSession(Scene(source)); state.SetSelection([source.Id]);
                var desktop = Desktop(state); string before = JsonSerializer.Serialize(state.Document);
                SetModifiers(shift: shift);
                bool leaked = false;
                try { Invoke(desktop, "OnKey", 0x28); } catch (TargetInvocationException) { leaked = true; }
                check($"{(shift ? "Shift+Down" : "Down")} rejects a complete unsafe candidate without leaking from OnKey", !leaked &&
                    JsonSerializer.Serialize(state.Document) == before && state.UndoCount == 0 && !state.IsTransactionActive &&
                    Field<TextBlock>(desktop, "_status").Text == L.Get("Perspective.InputOutsidePlane"));
                Invoke(desktop, "OnKey", 0x26);
                check("safe arrow command continues normally after a rejected move", state.UndoCount == 1 &&
                    state.Undo() && JsonSerializer.Serialize(state.Document) == before);
            }

            var duplicate = Box(3740); var duplicateState = new EditorSession(Scene(duplicate)); duplicateState.SetSelection([duplicate.Id]);
            var duplicateDesktop = Desktop(duplicateState); string duplicateBefore = JsonSerializer.Serialize(duplicateState.Document);
            SetModifiers(control: true); bool duplicateLeaked = false;
            try { Invoke(duplicateDesktop, "OnKey", 0x44); } catch (TargetInvocationException) { duplicateLeaked = true; }
            check("Ctrl+D safety rejection stays in OnKey and reports inability to create", !duplicateLeaked &&
                JsonSerializer.Serialize(duplicateState.Document) == duplicateBefore && duplicateState.Selection.SetEquals([duplicate.Id]) &&
                duplicateState.UndoCount == 0 && !duplicateState.IsTransactionActive &&
                Field<TextBlock>(duplicateDesktop, "_status").Text == L.Get("Perspective.InputOutsidePlane"));
            var safeDuplicate = Box(100); duplicateState.ReplaceDocument(Scene(safeDuplicate)); duplicateState.SetSelection([safeDuplicate.Id]);
            Invoke(duplicateDesktop, "OnKey", 0x44);
            check("Ctrl+D still inserts one safe editable duplicate after rejection", duplicateState.Document.Elements.Count == 2 && duplicateState.UndoCount == 1);
        }
        finally { check("safety shortcut fixture restores its thread keyboard state", SetKeyboardState(originalKeys)); }
    }

    private static DesktopSession Desktop(EditorSession state)
    {
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        Set(host, "<IsVisible>k__BackingField", true);
        Set(desktop, "_host", host); Set(desktop, "_editor", new EditorController(state));
        Set(desktop, "_canvasCalibration", new CanvasCalibrationController()); Set(desktop, "_status", new TextBlock());
        return desktop;
    }
    private static void SetModifiers(bool control = false, bool shift = false)
    {
        var keys = new byte[256];
        if (control) keys[0x11] = keys[0xA2] = 0x80;
        if (shift) keys[0x10] = keys[0xA0] = 0x80;
        if (!SetKeyboardState(keys)) throw new InvalidOperationException("Unable to set this test thread's keyboard state.");
    }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] keys);
}
