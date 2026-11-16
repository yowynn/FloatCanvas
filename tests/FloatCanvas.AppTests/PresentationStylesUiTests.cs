using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class PresentationStylesUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        RequirePrivateDesktop();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string folder = output ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-layout-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        ApplicationPaths.Configure(["--profile", Path.Combine(folder, "profile")]); L.Initialize("en"); AppTheme.Apply("dark");
        var layer = new CanvasElement { Kind = ElementKind.Presentation, Width = 760, Height = 260, Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard } };
        var session = new EditorSession(new() { Elements = [layer] }); session.SetSelection([layer.Id]);
        var inspector = new InspectorWindow(() => session) { ShowActivated = false };
        Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); inspector.PrepareForShow(); inspector.Show(); Pump();
        var panel = Field<PresentationSettingsPanel>(inspector, "presentationSettings");
        string original = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        check("keyboard settings expose two primary modes and two recording appearances with standard numeric fields", Entry<SegmentedChoice>(panel, "DisplayStyle").Items.Count == 2 && Entry<SegmentedChoice>(panel, "LogStyle").Items.Count == 2 && Entry<NumericValueField>(panel, "KeyHoldMilliseconds").Input is not null && Entry<NumericValueField>(panel, "RecentChordLimit").Slider.IsSnapToTickEnabled);
        check("live keyboard shows layout editor while irrelevant log controls stay hidden", Rows(panel)["KeyboardLayout"].IsVisible && !Rows(panel)["KeyPanelAlignment"].IsVisible && !Rows(panel)["RecentChordLimit"].IsVisible && !Rows(panel)["KeyHoldMilliseconds"].IsVisible && !Rows(panel)["KeyDisplay"].IsVisible);
        string aId = PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0x41).Id;
        PresentationKeyboardLayout? published = null;
        Edit(panel, dialog =>
        {
            check("layout editor starts from a detached complete standard keyboard", dialog.Draft.Keys.Count == 104 && dialog.Draft.IsValid() && JsonSerializer.Serialize(session.Document) == original);
            check("all physical key captions fit on one line inside their actual preview key", Field<Dictionary<string, Thumb>>(dialog, "keyViews").Values.All(view =>
            {
                if (view.Template.FindName("KeyCaption", view) is not TextBlock caption || caption.TextWrapping != TextWrapping.NoWrap || string.IsNullOrWhiteSpace(caption.Text)) return false;
                var bounds = caption.TransformToAncestor(view).TransformBounds(new Rect(caption.RenderSize));
                var keyBounds = new Rect(new Point(), view.RenderSize); keyBounds.Inflate(.01, .01);
                return keyBounds.Contains(bounds);
            }));
            dialog.SelectKey(aId);
            Field<TextBox>(dialog, "label").Text = "Move";
            Toggle(Field<CheckBox>(dialog, "enabled"), false);
            var fields = Field<Dictionary<string, NumericValueField>>(dialog, "coordinates");
            fields["Width"].Input.Text = "1.5"; fields["Height"].Input.Text = "1.25";
            var thumb = Field<Dictionary<string, Thumb>>(dialog, "keyViews")[aId];
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(30, 15) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(30, 15, false) { RoutedEvent = Thumb.DragCompletedEvent });
            var edited = dialog.Draft.Keys.Single(key => key.Id == aId);
            check("real key thumb and standard size controls edit only the draft", edited.Label == "Move" && !edited.Enabled && edited.X == 2.75 && edited.Y == 4 && edited.Width == 1.5 && edited.Height == 1.25 && JsonSerializer.Serialize(session.Document) == original && session.UndoCount == undo);
            Save(dialog, Path.Combine(folder, "keyboard-layout-edited-en-dark.png"));
            dialog.Accept(); published = dialog.ResultLayout;
        });
        inspector.CompletePendingEdit();
        var stored = session.Document.Elements.Single().Presentation!.KeyboardLayout;
        check("actual Inspector layout Apply publishes one deep-cloned undoable property edit", stored is not null && session.UndoCount == undo + 1 && stored.Keys.Single(key => key.Id == aId) is { Label: "Move", Enabled: false, Width: 1.5 } && !ReferenceEquals(stored, published));
        published!.Keys[0].Label = "external mutation";
        check("accepted layout values cannot alias dialog output or runtime input", session.Document.Elements.Single().Presentation!.KeyboardLayout!.Keys[0].Label != "external mutation" && !JsonSerializer.Serialize(session.Document).Contains("PressedKeys", StringComparison.Ordinal));
        check("layout Apply supports exact undo and redo", session.Undo() && JsonSerializer.Serialize(session.Document) == original && session.Redo() && session.Document.Elements.Single().Presentation!.KeyboardLayout?.Keys.Single(key => key.Id == aId).Label == "Move");
        inspector.PrepareForShow(); string beforeCancel = JsonSerializer.Serialize(session.Document); int beforeCancelUndo = session.UndoCount;
        Edit(panel, dialog => { dialog.SelectKey(aId); Field<Dictionary<string, NumericValueField>>(dialog, "coordinates")["Width"].Input.Text = "3"; SessionWindowCoordinator.SetModalResult(dialog, false); });
        check("actual layout Cancel preserves source and undo history", JsonSerializer.Serialize(session.Document) == beforeCancel && session.UndoCount == beforeCancelUndo);
        Edit(panel, dialog => { dialog.ResetToDefault(); dialog.Accept(); }); inspector.CompletePendingEdit();
        check("restoring the standard layout stores null in one normal Inspector undo", session.Document.Elements.Single().Presentation!.KeyboardLayout is null && session.UndoCount == beforeCancelUndo + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == beforeCancel);
        inspector.PrepareForShow(); session.BeginTransaction("foreign layout edit"); string foreign = JsonSerializer.Serialize(session.Document);
        Entry<Button>(panel, "KeyboardLayout").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("layout editor cannot enter a foreign author transaction", session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == foreign && !app.Windows.OfType<KeyboardLayoutEditorWindow>().Any(window => window.IsVisible));
        session.Cancel(); inspector.PrepareForShow();
        var group = session.CreatePopulatedGroup(); session.SetSelection([layer.Id]); inspector.PrepareForShow(); string scoped = JsonSerializer.Serialize(session.Document); int scopedUndo = session.UndoCount;
        Edit(panel, dialog => { dialog.SelectKey(aId); Toggle(Field<CheckBox>(dialog, "enabled"), true); session.EnterScope(group); session.LeaveScope(); session.SetSelection([layer.Id]); dialog.Accept(); });
        check("a layout draft cannot publish after an author scope round trip", JsonSerializer.Serialize(session.Document) == scoped && session.UndoCount == scopedUndo);
        inspector.PrepareForShow(); Entry<SegmentedChoice>(panel, "DisplayStyle").SelectedValue = PresentationDisplayStyle.Log; inspector.CompletePendingEdit(); inspector.PrepareForShow();
        int numericUndo = session.UndoCount; var hold = Entry<NumericValueField>(panel, "KeyHoldMilliseconds");
        check("standard keyboard numeric rail can begin a scoped Inspector gesture", hold.BeginGesture());
        hold.Slider.Value = 1200; hold.Slider.Value = 1800;
        check("continuous keyboard numeric gesture retains one open author transaction", session.IsTransactionActive && session.UndoCount == numericUndo && !Field<DispatcherTimer>(inspector, "editTimer").IsEnabled);
        hold.EndGesture(true);
        check("keyboard numeric rail completes as one undo", session.UndoCount == numericUndo + 1 && session.Document.Elements.Single(element => element.Kind == ElementKind.Presentation).Presentation!.KeyHoldMilliseconds == 1800);
        hold.BeginGesture(); hold.Slider.Value = 500; hold.EndGesture(false);
        check("cancelled keyboard numeric gesture restores the prior value without another undo", session.UndoCount == numericUndo + 1 && session.Document.Elements.Single(element => element.Kind == ElementKind.Presentation).Presentation!.KeyHoldMilliseconds == 1800);
        SessionWindowCoordinator.Close(inspector);
        var controller = new EditorController(session); controller.ApplyParameters(new() { Tool = EditorTool.Presentation, Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard } });
        var toolPanel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(folder, "recipes.json")), new ColorLibrary(Path.Combine(folder, "colors.json"))); toolPanel.PrepareForShow();
        var toolSettings = Field<PresentationSettingsPanel>(toolPanel, "presentationSettings"); string authored = JsonSerializer.Serialize(session.Document); int toolUndo = session.UndoCount;
        Edit(toolSettings, dialog => { dialog.SelectKey(aId); Field<TextBox>(dialog, "label").Text = "Future A"; dialog.Accept(); });
        check("tool layout Apply changes only future keyboard layer parameters", controller.Parameters.Presentation!.KeyboardLayout?.Keys.Single(key => key.Id == aId).Label == "Future A" && JsonSerializer.Serialize(session.Document) == authored && session.UndoCount == toolUndo);
        var empty = new KeyboardLayoutEditorWindow(new() { Keys = [] }); empty.ResetToDefault(); empty.Accept();
        check("empty compatible layouts can be restored without a selection crash", empty.Accepted && empty.ResultLayout is null); SessionWindowCoordinator.Close(empty);
        check("keyboard layout UI stays on the private desktop without observing physical input", !PresentationDesktopAvailability.IsCurrentInputDesktop());
        Console.WriteLine("PRESENTATION_STYLES_UI " + folder);
    }
    private static void Edit(PresentationSettingsPanel panel, Action<KeyboardLayoutEditorWindow> edit)
    {
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var dialog = Application.Current.Windows.OfType<KeyboardLayoutEditorWindow>().Single(window => window.IsVisible);
            try { edit(dialog); }
            catch (Exception error) { failure = error; if (dialog.IsVisible) SessionWindowCoordinator.SetModalResult(dialog, false); }
        });
        Entry<Button>(panel, "KeyboardLayout").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (failure is not null) throw new InvalidOperationException("Modal layout fixture failed.", failure);
    }
    private static Dictionary<string, FrameworkElement> Rows(PresentationSettingsPanel panel) => Field<Dictionary<string, FrameworkElement>>(panel, "rows");
    private static T Entry<T>(PresentationSettingsPanel panel, string key) where T : FrameworkElement
    { var entries = Field<IDictionary>(panel, "entries"); var entry = entries[key]!; return (T)entry.GetType().GetProperty("Control")!.GetValue(entry)!; }
    private static void Toggle(CheckBox check, bool value) { check.IsChecked = value; check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private static void Save(Window window, string path)
    {
        window.UpdateLayout(); var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0); root.InvalidateArrange(); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.5), (int)Math.Ceiling(root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static void RequirePrivateDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --presentation-styles-ui-only.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
}
