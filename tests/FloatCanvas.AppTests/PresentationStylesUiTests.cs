using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
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
        KeyboardCoordinates(check);
        KeyboardMultiSelection(check,folder);
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
    private static void KeyboardMultiSelection(Action<string,bool> check,string folder)
    {
        var source=PresentationKeyboardLayout.CreateDefault();
        var dialog=new KeyboardLayoutEditorWindow(source);dialog.Show();Pump();
        var ids=source.Keys.Where(k=>k.VirtualKey is 0x1B or 0x44).Select(k=>k.Id).ToArray();
        var views=Field<Dictionary<string,Thumb>>(dialog,"keyViews");
        void Click(string id,ModifierKeys modifiers)
        {
            using var scope=new ThreadModifierScope(modifiers);
            views[id].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
        }
        void SendKey(Key key,ModifierKeys modifiers,UIElement target)
        {
            using var scope=new ThreadModifierScope(modifiers);
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog)!,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        }
        dialog.SelectKey(ids[0]);Click(ids[1],ModifierKeys.Control);
        check("Ctrl-click adds a key to the keyboard selection",dialog.SelectedKeyIds.SetEquals(ids));
        Click(ids[1],ModifierKeys.Shift);
        check("Shift-click removes a selected key without focus reselecting it",dialog.SelectedKeyIds.SetEquals([ids[0]]));
        Click(ids[1],ModifierKeys.Control);views[ids[0]].Focus();
        check("keyboard focusing a selected key preserves the multi-selection",dialog.SelectedKeyIds.Count==2);
        SendKey(Key.Space,ModifierKeys.None,views[ids[0]]);
        check("Space disables all selected keys without changing the source layout",dialog.Draft.Keys.Where(k=>ids.Contains(k.Id)).All(k=>!k.Enabled)&&source.Keys.All(k=>k.Enabled));
        dialog.SelectKey(ids[0]);dialog.ToggleSelectedEnabled();dialog.SelectKeys(ids);
        check("mixed selected enable state is shown explicitly",Field<CheckBox>(dialog,"enabled").IsChecked is null);
        Save(dialog,Path.Combine(folder,"keyboard-multiselect.png"));
        SendKey(Key.Space,ModifierKeys.None,views[ids[0]]);
        check("Space enables a mixed selection together",dialog.Draft.Keys.Where(k=>ids.Contains(k.Id)).All(k=>k.Enabled));
        SendKey(Key.A,ModifierKeys.Control,views[ids[0]]);
        check("Ctrl+A selects every keyboard key",dialog.SelectedKeyIds.Count==source.Keys.Count);
        dialog.SelectKeys(ids);
        var before=dialog.Draft;
        views[ids[0]].RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
        views[ids[0]].RaiseEvent(new DragDeltaEventArgs(30,15){RoutedEvent=Thumb.DragDeltaEvent});
        check("dragging selected keys preserves their spacing",dialog.Draft.Keys.Where(k=>ids.Contains(k.Id)).All(k=>k.X==before.Keys.Single(p=>p.Id==k.Id).X+1&&k.Y==before.Keys.Single(p=>p.Id==k.Id).Y+.5));
        views[ids[0]].RaiseEvent(new DragCompletedEventArgs(30,15,true){RoutedEvent=Thumb.DragCompletedEvent});
        check("cancelling a group drag restores all selected keys",JsonSerializer.Serialize(dialog.Draft)==JsonSerializer.Serialize(before));
        var label=Field<TextBox>(dialog,"label");dialog.SelectKey(ids[0]);label.Focus();
        var enabled=dialog.Draft.Keys.Single(k=>k.Id==ids[0]).Enabled;
        SendKey(Key.Space,ModifierKeys.None,label);
        check("Space in the label field never toggles keyboard keys",dialog.Draft.Keys.Single(k=>k.Id==ids[0]).Enabled==enabled);
        dialog.SelectKeys(ids);
        views[ids[1]].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.PreviewMouseDoubleClickEvent});
        check("double-click toggles only its key and leaves an explicit single selection",dialog.SelectedKeyIds.SetEquals([ids[1]]) && !dialog.Draft.Keys.Single(k=>k.Id==ids[1]).Enabled && dialog.Draft.Keys.Single(k=>k.Id==ids[0]).Enabled);
        dialog.Close();
    }

    private static void KeyboardCoordinates(Action<string,bool> check)
    {
        foreach (bool move in new[]{false,true})
        foreach (var pose in new[]{"rotated","projective","billboard"})
        {
            var layout=PresentationKeyboardLayout.CreateDefault();
            foreach(var key in layout.Keys) key.Enabled=key.VirtualKey is 0x1B or 0xA2 or 0xA3 or 0x7B or 0x44;
            var source=new CanvasElement { Kind=ElementKind.Presentation,Width=760,Height=260,Transform=Affine2D.Translation(110,75).Compose(Affine2D.Rotation(23)),Presentation=new(){DisplayStyle=PresentationDisplayStyle.Keyboard,KeyboardLayout=layout} };
            if(pose=="projective") source.ProjectivePlacement=new(1,0,0,0,1,0,.0001,.0002,1);
            if(pose=="billboard") {source.IsBillboard=true;source.BillboardAnchor=new(300,300);source.BillboardHeight=40;}
            var document=new SceneDocument {Viewport=new(0,0,1200,900),Elements=[source]};
            document.Canvas.Projection.Enabled=true;document.Canvas.Projection.Matrix=new(1,0,0,0,1,0,0,.0002,1);
            document.Canvas.Projection.Quad=[new(0,0),new(1200,0),new(1000,750),new(0,750)];
            var mapping=CanvasMapping.Create(document);
            var session=new EditorSession(document);session.SetSelection([source.Id]);
            var inspector=new InspectorWindow(()=>session);inspector.PrepareForShow();inspector.Show();Pump();
            var panel=Field<PresentationSettingsPanel>(inspector,"presentationSettings");
            var targetKey=layout.Keys.Single(k=>k.VirtualKey==(move?0x1B:0x44));
            var expected=KeyPoint(source,targetKey.X,targetKey.Y,mapping);
            var expectedEnd=KeyPoint(source,targetKey.X+1,targetKey.Y+1,mapping);
            var changed=layout.Clone();foreach(var key in changed.Keys)key.Enabled=key.VirtualKey==0x44;
            var d=changed.Keys.Single(k=>k.VirtualKey==0x44);if(move){d.X=targetKey.X;d.Y=targetKey.Y;}
            typeof(PresentationSettingsPanel).GetMethod("Commit",Hidden)!.Invoke(panel,["KeyboardLayout",changed]);inspector.CompletePendingEdit();
            var actual=session.Document.Elements.Single();
            Console.WriteLine($"KEYBOARD_ANCHOR pose={pose} move={move} distance={KeyPoint(actual,d.X,d.Y,mapping).DistanceTo(expected)} size={actual.Width},{actual.Height} undo={session.UndoCount}");
            check(pose+" keyboard layout coordinates stay aligned when "+(move?"D moves to ESC":"only D remains"),KeyPoint(actual,d.X,d.Y,mapping).DistanceTo(expected)<1e-6 && actual.Width<760 && actual.Height<260);
            check(pose+" keyboard key scale is preserved",KeyPoint(actual,d.X+1,d.Y+1,mapping).DistanceTo(expectedEnd)<1e-6);
            check("keyboard layout reframing is one undoable edit",session.UndoCount==1 && session.Undo() && session.Document.Elements.Single().Width==760 && session.Redo());
            inspector.Close();
        }
        static PointD KeyPoint(CanvasElement e,double x,double y,CanvasMapping mapping)
        {
            var keys=e.Presentation!.KeyboardLayout!.Keys.Where(k=>k.Enabled).ToArray();
            double left=keys.Min(k=>k.X),top=keys.Min(k=>k.Y),w=keys.Max(k=>k.X+k.Width)-left,h=keys.Max(k=>k.Y+k.Height)-top;
            double f=e.Style.FontSize,ratio=(w*f/.36+f*.7)/(h*f/.36+f*.7),width=ratio*e.Height;
            double pad=Math.Min(f*.35,Math.Min(width,e.Height)/8),unit=(e.Height-pad*2)/h;
            return mapping.ElementToScreen(e).TransformPoint(new((width/2-w*unit/2+(x-left)*unit)*e.Width/width,pad+(y-top)*unit));
        }
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
