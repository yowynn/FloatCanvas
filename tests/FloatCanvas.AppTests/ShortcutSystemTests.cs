using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static class ShortcutSystemTests
{
    internal static void Run(Action<string,bool> check)
    {
        var saved=EditorCommandCatalog.Overrides;
        try
        {
            EditorCommandCatalog.Overrides=[];
            check("default shortcut catalog has no collision",EditorCommandCatalog.Validate([]) is null);
            check("stamp shortcuts use unmodified visible keys",EditorCommandCatalog.Resolve(0xC0,0)?.Id=="Stamp.Library"&&EditorCommandCatalog.Resolve(0xBB,0)?.Id=="Stamp.Create");
            check("punctuation legends use familiar keycaps",HotkeyDisplay.Format(new(0,0xC0))=="~"&&HotkeyDisplay.Format(new(0,0xBB))=="+"&&HotkeyDisplay.Format(new(0,0xBD))=="-");
            foreach(int key in new[]{0x43,0x44,0x56,0x4F,0x59})
                check("extra Shift does not inherit Ctrl command "+key,EditorCommandCatalog.Resolve(key,OverlayModifiers.Control|OverlayModifiers.Shift) is null);
            check("Ctrl Shift A is explicit deselect",EditorCommandCatalog.Resolve(0x41,OverlayModifiers.Control|OverlayModifiers.Shift)?.Id=="Edit.Deselect");
            check("Ctrl T has no accidental tool command",!EditorShortcutPolicy.IsCommand(0x54,OverlayModifiers.Control));
            check("F4 opens tool properties",EditorCommandCatalog.Resolve(0x73,0)?.Id=="Panel.Tools");
            foreach(var tool in Enum.GetValues<EditorTool>().Where(t=>t!=EditorTool.Image && !EditorCommandCatalog.IsRetired(EditorCommandCatalog.ToolId(t))))
                check("tool registered "+tool,EditorCommandCatalog.All.Any(c=>c.Tool==tool));
            check("image import is an action",EditorCommandCatalog.All.Single(c=>c.Id=="Image.Import").Tool is null);
            check("preset aliases have no standalone tool commands", !EditorCommandCatalog.All.Any(command => EditorCommandCatalog.IsRetired(command.Id)));
            check("stamp placement has no assignable command and retains library, creation and all ten slots",
                !EditorCommandCatalog.All.Any(command => command.Id == "Tool.Stamp") &&
                EditorCommandCatalog.All.Any(command => command.Id == "Stamp.Library") &&
                EditorCommandCatalog.All.Any(command => command.Id == "Stamp.Create") &&
                EditorCommandCatalog.All.Count(command => command.Id.StartsWith("Stamp.Slot", StringComparison.Ordinal)) == 10);
            var clear = new CommandChord(0x08, OverlayModifiers.Control | OverlayModifiers.Shift);
            check("the default clear chord is separate from capture Backspace and never repeats",
                EditorCommandCatalog.Resolve(clear.Key, clear.Modifiers) is { Id: "Edit.Clear", Repeat: false } &&
                EditorCommandCatalog.Resolve(clear.Key, OverlayModifiers.None)?.Id == "Capture.Back" &&
                !EditorShortcutPolicy.ShouldRoute(clear.Key, (ModifierKeys)clear.Modifiers, new TextBox()));
            var clearConflict = EditorCommandCatalog.ReconcileSavedBindings(
                new() { ["Edit.Copy"] = [clear] }, [], out var clearNotices);
            check("upgrading keeps a user's binding over the new clear default",
                clearConflict["Edit.Copy"].SequenceEqual([clear]) && clearConflict["Edit.Clear"].Length == 0 && clearNotices.Count == 1);
            check("optional globals contain only mouse master and capture", GlobalShortcutCatalog.Optional.Select(command => command.Id).SequenceEqual(new[] { "Global.Pointer", "Global.Capture" }));
            var settings = new UserSettings
            {
                CommandBindings = new() { ["Tool.Arrow"] = [new(0x51)], ["Tool.Stamp"] = [new(0x55)], ["Tool.Line"] = [new(0x4B)] },
                GlobalCommandBindings = new() { ["Global.Keyboard"] = new(3, 0x78), ["Global.HideAll"] = new(3, 0x79), ["Global.Capture"] = new(3, 0x7A) }
            };
            settings.RemoveRetiredShortcuts();
            check("retired settings do not discard unrelated custom bindings", settings.CommandBindings.Count == 1 && settings.CommandBindings.ContainsKey("Tool.Line") &&
                EditorCommandCatalog.Validate(settings.CommandBindings) is null && settings.GlobalCommandBindings.Count == 1 && settings.GlobalCommandBindings.ContainsKey("Global.Capture"));
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "shortcut-migration-" + Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(path, """{"CommandBindings":{"Tool.Arrow":[],"Tool.Line":[{"Key":75,"Modifiers":3},{"Key":85,"Modifiers":6}],"Tool.Freehand":[]},"GlobalCommandBindings":{"Global.Keyboard":{"Modifiers":3,"VirtualKey":120}},"FutureSetting":true}""");
            try
            {
                var storage = new CompatibleConfiguration(path);
                var options = new System.Text.Json.JsonSerializerOptions();
                var migrated = storage.Read<UserSettings>(options);
                migrated.RemoveRetiredShortcuts(); storage.Save(migrated, options);
                var json = System.IO.File.ReadAllText(path);
                check("saving removes obsolete bindings while retaining unknown settings", !json.Contains("Tool.Arrow") && !json.Contains("Global.Keyboard") && json.Contains("Tool.Line") && json.Contains("FutureSetting"));
                var loaded = new CompatibleConfiguration(path).Read<UserSettings>(options);
                EditorCommandCatalog.Overrides = loaded.CommandBindings;
                check("settings round-trip both custom chords and an intentional clear",
                    EditorCommandCatalog.Validate(loaded.CommandBindings) is null &&
                    EditorCommandCatalog.Resolve(0x4B, OverlayModifiers.Control | OverlayModifiers.Alt)?.Id == "Tool.Line" &&
                    EditorCommandCatalog.Resolve(0x55, OverlayModifiers.Control | OverlayModifiers.Shift)?.Id == "Tool.Line" &&
                    EditorCommandCatalog.Resolve(0x42, 0) is null);
            }
            finally { System.IO.File.Delete(path); }
            var oldProfile = new Dictionary<string, CommandChord[]>
            {
                ["Panel.Tools"] = [new(0xC0)], ["Edit.Copy"] = [new(0x76)],
                ["Edit.Undo"] = [new(0x20, OverlayModifiers.Control), new(0x55, OverlayModifiers.Control)]
            };
            EditorCommandCatalog.Overrides = EditorCommandCatalog.ReconcileSavedBindings(oldProfile,
                [KeyValuePair.Create(new CommandChord(0x76), "global fixture")], out var notices);
            check("upgrade keeps the user's binding when a new default collides",
                EditorCommandCatalog.Resolve(0xC0, 0)?.Id == "Panel.Tools" && EditorCommandCatalog.Hint("Stamp.Library") == "");
            check("upgrade skips only reserved or occupied chords and reports them",
                EditorCommandCatalog.Validate(EditorCommandCatalog.Overrides) is null &&
                EditorCommandCatalog.Resolve(0x55, OverlayModifiers.Control)?.Id == "Edit.Undo" &&
                EditorCommandCatalog.Resolve(0x76, 0) is null && notices.Count == 3);
            check("modified Space cannot be saved as an editor command",
                EditorCommandCatalog.Validate(new() { ["Edit.Undo"] = [new(0x20, OverlayModifiers.Control)] }) ==
                FloatCanvas.Localization.AppText.Get("Shortcuts.SpaceReserved"));
            EditorCommandCatalog.Overrides = [];
            check("shortcut help hides unbound entries", !AppHelp.ReadGuide(true).Contains(FloatCanvas.Localization.AppText.Get("Shortcuts.Unbound")));
            EditorCommandCatalog.Overrides = new() { ["Tool.Blur"] = [new(0x4B)] };
            check("a newly bound tool appears in help", AppHelp.ReadGuide(true).Contains("| " + ToolDisplayName.Get(EditorTool.Blur) + " | K |"));
            EditorCommandCatalog.Overrides = [];
            var custom=new Dictionary<string,CommandChord[]> { ["Edit.Undo"]=[new(0x55,OverlayModifiers.Control)] };
            check("unique custom binding accepted",EditorCommandCatalog.Validate(custom) is null);
            EditorCommandCatalog.Overrides=custom;
            check("old undo binding removed",EditorCommandCatalog.Resolve(0x5A,OverlayModifiers.Control) is null);
            check("popup history follows remapping",PresetHistoryKeyboardRoute.ShouldRoute(Key.U,ModifierKeys.Control,null));
            check("popup history ignores old key",!PresetHistoryKeyboardRoute.ShouldRoute(Key.Z,ModifierKeys.Control,null));
            custom["Edit.Copy"]=[new(0x55,OverlayModifiers.Control)];
            check("conflicting commands rejected",EditorCommandCatalog.Validate(custom) is not null);
            EditorCommandCatalog.Overrides=[];
            foreach(int key in new[]{0x09,0x20,0x0D})
                check("ordinary button forwards editor keys "+key,EditorShortcutPolicy.ShouldRoute(key,ModifierKeys.None,new Button()));
            check("canvas retains Space",EditorShortcutPolicy.ShouldRoute(0x20,ModifierKeys.None,null));
            var numeric = new NumericValueField(0, 100);
            check("canvas opacity shortcuts work from numeric sliders while plain numeric movement stays local",
                EditorShortcutPolicy.ShouldRoute(0x21, ModifierKeys.Control, numeric.Slider) &&
                EditorShortcutPolicy.ShouldRoute(0x22, ModifierKeys.Control, numeric.Slider) &&
                !EditorShortcutPolicy.ShouldRoute(0x27, ModifierKeys.None, numeric.Slider) &&
                !EditorShortcutPolicy.ShouldRoute(0x21, ModifierKeys.Control, new TextBox()));
            check("text retains shortcuts",!EditorShortcutPolicy.ShouldRoute(0x43,ModifierKeys.Control,new TextBox()));
            check("F6 does not navigate invisible focus",!EditorShortcutPolicy.ShouldRoute(0x75,ModifierKeys.None,new TextBox()));
            foreach(var chord in new[]{new CommandChord(0x51),new CommandChord(0x51,OverlayModifiers.Control|OverlayModifiers.Alt),new CommandChord(0x41,OverlayModifiers.Control)})
            {
                EditorCommandCatalog.Overrides=new() {["File.Save"]=[chord],["Edit.SelectAll"]=[]};
                check("custom file binding cannot steal text or AltGr "+chord,!EditorShortcutPolicy.ShouldRoute(chord.Key,(ModifierKeys)chord.Modifiers,new TextBox()));
                check("custom file binding still runs on canvas "+chord,EditorShortcutPolicy.ShouldRoute(chord.Key,(ModifierKeys)chord.Modifiers,null));
            }
            EditorCommandCatalog.Overrides=[];
            Gestures(check);
            NumericKeys(check);
        }
        finally { EditorCommandCatalog.Overrides=saved; }
    }

    private static void NumericKeys(Action<string,bool> check)
    {
        var field=new NumericValueField(0,100) {Value=20};int commits=0;
        using var modifiers = new ThreadModifierScope(ModifierKeys.None);
        field.SliderValueCommitted+=()=>commits++;
        void Key(Key key,bool down=true)=>field.Slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,new DetachedSource(),Environment.TickCount,key)
            {RoutedEvent=down?Keyboard.PreviewKeyDownEvent:Keyboard.PreviewKeyUpEvent});
        for(int i=0;i<5;i++) Key(System.Windows.Input.Key.Right);
        check($"held numeric arrows share one pending gesture (value={field.Value}, interacting={field.IsInteracting}, commits={commits})",
            field.Value==25 && field.IsInteracting && commits==0);
        Key(System.Windows.Input.Key.Right,false);
        check("numeric key release commits once",!field.IsInteracting && commits==1);
        Key(System.Windows.Input.Key.Left);Key(System.Windows.Input.Key.Escape);Key(System.Windows.Input.Key.Left,false);
        check("numeric Escape restores the value without committing",field.Value==25 && !field.IsInteracting && commits==1);
    }
    private sealed class DetachedSource:System.Windows.PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual {get;set;}
        public override bool IsDisposed=>false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore()=>null;
    }

    private static void Gestures(Action<string,bool> check)
    {
        static (EditorSession Session,EditorController Editor,Guid Id) Fixture()
        {
            var element=new CanvasElement {Kind=ElementKind.Rectangle,Width=100,Height=80,Transform=Affine2D.Translation(100,100),
                Style=new(){Background=0xff4488cc,StrokeWidth=0}};
            var session=new EditorSession(new SceneDocument {Viewport=new(0,0,800,600),Elements=[element]});
            session.SetSelection([element.Id]);
            return(session,new EditorController(session){Tool=EditorTool.Select},element.Id);
        }
        static void Pointer(EditorController editor,OverlayPointerKind kind,double x,double y,OverlayModifiers mods=0,OverlayMouseButton button=OverlayMouseButton.Left) =>
            editor.Pointer(new(kind,new(x,y),button,mods));
        {
            var (session,editor,id)=Fixture(); editor.Tool=EditorTool.Freehand;
            double width=editor.Style.StrokeWidth;
            editor.BeginTemporarySelection();
            Pointer(editor,OverlayPointerKind.Down,150,140);
            Pointer(editor,OverlayPointerKind.Move,180,160);
            editor.EndTemporarySelection();
            check("release temporary selection retains drag",editor.IsTemporaryMove && session.IsTransactionActive);
            Pointer(editor,OverlayPointerKind.Up,180,160);
            check("temporary selection moves object",Math.Abs(session.Document.Elements[0].Transform.OffsetX-130)<.01);
            check("temporary selection restores original parameters",editor.Tool==EditorTool.Freehand && editor.Style.StrokeWidth==width && !editor.IsBorrowingSelection);
            session.Undo();check("temporary move has one undo",Math.Abs(session.Document.Elements[0].Transform.OffsetX-100)<.01);
            editor.BeginTemporarySelection();Pointer(editor,OverlayPointerKind.Down,150,140);Pointer(editor,OverlayPointerKind.Move,175,140);
            editor.EndTemporarySelection(cancel:true);
            check("context cancellation rolls back borrowed drag",!session.IsTransactionActive && !editor.IsBorrowingSelection && Math.Abs(session.Document.Elements[0].Transform.OffsetX-100)<.01);
        }
        {
            var (session,editor,id)=Fixture();
            Pointer(editor,OverlayPointerKind.Down,150,140,OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Up,150,140,OverlayModifiers.Alt);
            check("Alt click does not duplicate",session.Document.Elements.Count==1 && !session.CanUndo);
            Pointer(editor,OverlayPointerKind.Down,150,140,OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Move,190,160,OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Up,190,160,OverlayModifiers.Alt);
            check("Alt drag creates copy and leaves original",session.Document.Elements.Count==2 && session.Document.Elements.Single(e=>e.Id==id).Transform.OffsetX==100);
            session.Undo();check("copy and move undo together",session.Document.Elements.Count==1 && !session.CanUndo);
            editor.BeginTemporarySelection();Pointer(editor,OverlayPointerKind.Down,150,140);Pointer(editor,OverlayPointerKind.Move,180,140);Pointer(editor,OverlayPointerKind.Up,180,140);
            Pointer(editor,OverlayPointerKind.Down,180,140,OverlayModifiers.Alt);Pointer(editor,OverlayPointerKind.Up,180,140,OverlayModifiers.Alt);
            check("second borrowed click does not inherit drag state",session.Document.Elements.Count==1);
            editor.EndTemporarySelection();
        }
        foreach(bool center in new[]{false,true})
        {
            var(session,editor,id)=Fixture();var mod=center?OverlayModifiers.Alt:OverlayModifiers.None;
            Pointer(editor,OverlayPointerKind.Down,200,180,mod);Pointer(editor,OverlayPointerKind.Move,240,220,mod);Pointer(editor,OverlayPointerKind.Up,240,220,mod);
            var bounds=ElementGeometry.Bounds(session.Document.Elements.Single(),false);
            check("resize keeps selected pivot "+center,center?bounds.Center.DistanceTo(new(150,140))<.01:Math.Abs(bounds.X-100)<.01 && Math.Abs(bounds.Y-100)<.01);
        }
        {
            var(session,editor,id)=Fixture();editor.Tool=EditorTool.PointerPresentation;
            check("pointer demonstration normally hides selection",!editor.ShouldShowSelection(0));
            editor.BeginTemporarySelection();
            check("borrowing from pointer demonstration exposes selection handles",editor.ShouldShowSelection(0));
            Pointer(editor,OverlayPointerKind.Down,200,180);Pointer(editor,OverlayPointerKind.Move,220,200);Pointer(editor,OverlayPointerKind.Up,220,200);
            check("borrowed pointer demonstration resizes with one history entry",session.UndoCount==1 && session.Document.Elements[0].Width>100);
            editor.EndTemporarySelection();
            check("release restores passive pointer demonstration",editor.Tool==EditorTool.PointerPresentation && !editor.ShouldShowSelection(0));
        }
        {
            var(session,editor,id)=Fixture();editor.Tool=EditorTool.Freehand;
            editor.BeginTemporarySelection();
            Pointer(editor,OverlayPointerKind.Down,150,140,OverlayModifiers.Shift);
            Pointer(editor,OverlayPointerKind.Up,150,140,OverlayModifiers.Shift);
            check("borrowed Shift click removes selection",session.Selection.Count==0);
            editor.EndTemporarySelection();
            int properties=0;editor.PropertiesRequested+=_=>properties++;
            editor.BeginTemporarySelection();Pointer(editor,OverlayPointerKind.DoubleClick,150,140);
            check("borrowed double click opens selection properties",properties==1 && editor.Tool==EditorTool.Freehand);
            editor.EndTemporarySelection();
            Pointer(editor,OverlayPointerKind.Down,400,300,OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Move,440,330,OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Up,440,330,OverlayModifiers.Alt);
            check("undefined Alt stroke does not create artwork",session.Document.Elements.Count==1 && !session.IsTransactionActive);
        }
        {
            var(session,editor,id)=Fixture();
            var other=new CanvasElement {Kind=ElementKind.Rectangle,Width=50,Height=40,Transform=Affine2D.Translation(300,100)};
            session.Add(other);session.SetSelection([id,other.Id]);
            Pointer(editor,OverlayPointerKind.Down,150,140,OverlayModifiers.Control|OverlayModifiers.Shift);
            Pointer(editor,OverlayPointerKind.Up,150,140,OverlayModifiers.Control|OverlayModifiers.Shift);
            check("Ctrl Shift subtracts only the deep target and retains other selection",session.Selection.SetEquals([other.Id]));
        }
        {
            var(session,editor,id)=Fixture();editor.Tool=EditorTool.Rectangle;
            Pointer(editor,OverlayPointerKind.Down,400,300,OverlayModifiers.Control|OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Move,440,330,OverlayModifiers.Control|OverlayModifiers.Alt);
            Pointer(editor,OverlayPointerKind.Up,440,330,OverlayModifiers.Control|OverlayModifiers.Alt);
            check("Alt creates a box around pointer anchor",ElementGeometry.Bounds(session.Document.Elements.Last(),false).Center.DistanceTo(new(400,300))<.01);
            Pointer(editor,OverlayPointerKind.Down,500,400);Pointer(editor,OverlayPointerKind.Down,500,400,button:OverlayMouseButton.Right);
            check("right cancellation retains drawing tool",editor.Tool==EditorTool.Rectangle && !session.IsTransactionActive);
        }
    }
}
