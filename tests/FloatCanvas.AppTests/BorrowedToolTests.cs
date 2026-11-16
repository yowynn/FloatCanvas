using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class BorrowedToolTests
{
    internal static void Run(Action<string,bool> check,DesktopSession desktop,EditorController editor)
    {
        const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Call(string method,params object?[] args)=>typeof(DesktopSession).GetMethod(method,hidden)!.Invoke(desktop,args);
        object? State()=>typeof(DesktopSession).GetField("_borrowedTool",hidden)!.GetValue(desktop);
        void Hold()=>State()!.GetType().GetField("Started",hidden)!.SetValue(State(),Environment.TickCount64-1000);
        void Press(int key)=>Call("OnCommandInput",new OverlayKeyEvent(key,0,false));
        void Release(int key)=>Call("ReleaseBorrowedTool",key);
        var original=editor.Parameters;var bindings=EditorCommandCatalog.Overrides;
        try
        {
            foreach(var command in EditorCommandCatalog.All.Where(c=>c.Tool is not null))
            {
                if(command.Tool==EditorTool.Crop)
                {
                    using var stream=System.Windows.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.PassThrough.png",UriKind.Relative))!.Stream;
                    using var bytes=new System.IO.MemoryStream();stream.CopyTo(bytes);
                    var image=new FloatCanvas.Core.CanvasElement {Kind=FloatCanvas.Core.ElementKind.Image,Width=100,Height=100,AssetId=editor.Session.Assets.Add(bytes.ToArray())};
                    editor.Session.Document.Elements.Add(image);editor.Session.SetSelection([image.Id]);
                }
                var baseline=original.Clone();baseline.Tool=EditorTool.Rectangle;baseline.Style.StrokeWidth=7;
                editor.ApplyParameters(baseline);
                string before=JsonSerializer.Serialize(editor.Parameters);
                EditorCommandCatalog.Overrides=new() {[command.Id]=[new(0x51)]};
                Press(0x51);
                check("hold enters tool "+command.Id,State() is not null && ToolPresetLibrary.Family(editor.Tool)==ToolPresetLibrary.Family(command.Tool!.Value));
                Hold();Release(0x51);
                check("hold restores complete parameters "+command.Id,State() is null && JsonSerializer.Serialize(editor.Parameters)==before);
                if(command.Tool==EditorTool.Crop) {editor.Session.SetSelection([]);editor.Session.Document.Elements.Clear();}
            }
            EditorCommandCatalog.Overrides=[];
            editor.Tool=EditorTool.Select;
            Press(0x42);Release(0x42);
            check("tap keeps new brush active",editor.Tool==EditorTool.Freehand && State() is null);
            editor.Tool=EditorTool.Select;
            Press(0x42);Hold();
            Call("CurrentGroupCanvasView");
            editor.ResumePresentedPicking();
            editor.Pointer(new(OverlayPointerKind.Down,new(30,30),OverlayMouseButton.Left,0));
            editor.Pointer(new(OverlayPointerKind.Move,new(90,90),OverlayMouseButton.Left,0));
            Release(0x42);
            check("release during borrowed drawing preserves its pending stroke",editor.Tool==EditorTool.Freehand && editor.Session.IsTransactionActive);
            editor.Pointer(new(OverlayPointerKind.Up,new(120,100),OverlayMouseButton.Left,0));
            Call("TryRestoreBorrowedTool");
            check("completed borrowed stroke restores selection and keeps its layer",editor.Tool==EditorTool.Select && State() is null && editor.Session.Document.Elements.Count==1 && editor.Session.UndoCount==1);
            editor.Session.Undo();
            var toolbar=(System.Windows.Window)typeof(DesktopSession).GetField("_toolbar",hidden)!.GetValue(desktop)!;
            new System.Windows.Interop.WindowInteropHelper(toolbar).EnsureHandle();
            Press(0x54);Hold();
            Call("ShowTextEditor",null,new FloatCanvas.Core.PointD(40,40));
            Release(0x54);
            check("borrowed text waits for inline editing to finish",editor.Tool==EditorTool.Text && State() is not null);
            Call("FinishInlineText",false,false);Call("TryRestoreBorrowedTool");
            check("finishing inline text restores the original tool",editor.Tool==EditorTool.Select && State() is null);
            Press(0x42);Hold();Press(0x48);Hold();Release(0x42);
            check("older key release cannot override newer tool",editor.Tool==EditorTool.Highlighter);
            Release(0x48);
            check("latest held tool restores original base tool",editor.Tool==EditorTool.Select && State() is null);
            Press(0x42);Hold();Call("CancelBorrowedTool");
            check("focus cancellation restores original tool",editor.Tool==EditorTool.Select && State() is null);
            Press(0x42);Hold();Call("ExecuteEditorCommand",EditorCommandCatalog.All.Single(c=>c.Id=="Interaction.Cancel"));
            check("Escape ends an idle borrowed tool without leaving editing",editor.Tool==EditorTool.Select && State() is null);
            Press(0x42);Hold();Call("ActivateTool",EditorTool.Eraser);Release(0x42);
            check("explicit toolbar switch supersedes borrowed tool",editor.Tool==EditorTool.Eraser && State() is null);
            EditorCommandCatalog.Overrides=new() {["Tool.Crop"]=[new(0x51)]};Press(0x51);
            check("unavailable crop cannot start temporary tool",editor.Tool==EditorTool.Eraser && State() is null);
            EditorCommandCatalog.Overrides=new() {["Tool.Freehand"]=[new(0x51),new(0x57)]};
            Press(0x57);Hold();Release(0x57);
            check("alternate binding supports the same hold semantics",editor.Tool==EditorTool.Eraser && State() is null);
        }
        finally {Call("CancelBorrowedTool");EditorCommandCatalog.Overrides=bindings;editor.ApplyParameters(original);}
    }
}
