using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private InlineTextEdit? _inlineText;
    private CanvasTextInput? _canvasTextInput;
    private DispatcherTimer? _inlineCaretTimer;
    private bool _inlineCaretVisible;
    private BillboardDepthLine? _inlineBillboardDepth;

    private void ShowTextEditor(CanvasElement? existing,PointD point)
    {
        var depth = existing is null ? _editor.ActiveBillboardDepthLine : null;
        FinishInlineText();_inspector?.CompletePendingEdit();_editor.Cancel();
        if(!CanAddCanvasContent())return;
        if(existing?.Kind==ElementKind.Number)
        {
            Active.SetSelection([existing.Id]);
            if(!_inspectorWanted)ToggleInspector();
            return;
        }
        var edit=new InlineTextEdit(Active,existing,point,_editor.Style,_renderer,depth);
        edit.PlacementRejected+=()=>UpdateStatus(FloatCanvas.Localization.AppText.Get("Perspective.InputOutsidePlane"));
        _inlineBillboardDepth = depth;
        _inlineText=edit;
        UpdateHiddenCanvasState();
        edit.Changed+=()=>
        {
            _inlineCaretVisible=true;
            UpdateHiddenCanvasState();
            _inlineCaretTimer?.Stop();_inlineCaretTimer?.Start();RequestRender();
        };
        edit.Finished+=()=>
        {
            if(!ReferenceEquals(_inlineText,edit))return;
            _inlineText=null;_inlineBillboardDepth=null;_inlineCaretTimer?.Stop();
            var input=_canvasTextInput;_canvasTextInput=null;input?.Dispose();
            UpdateHiddenCanvasState();
            RequestRender();
        };
        try
        {
            _canvasTextInput=new(_toolbar,edit,()=>_editor.ScreenOrigin);
            _canvasTextInput.CompleteRequested+=commit=>
            {
                FinishInlineText(commit,selectEdited:commit);
                if(!_disposed&&_host.IsEditing)_host.TryFocusEditingCanvas();
            };
            _canvasTextInput.CommandKey += key => { FinishInlineText(); OnKey(key); };
            _canvasTextInput.Deactivated+=()=>QueueInlineTextFocusCheck(edit);
            _inlineCaretVisible=true;
            if(_inlineCaretTimer is null)
            {
                _inlineCaretTimer=new() {Interval=TimeSpan.FromMilliseconds(530)};
                _inlineCaretTimer.Tick+=(_,_)=>{_inlineCaretVisible=!_inlineCaretVisible;RequestRender();};
            }
            _inlineCaretTimer.Start();UpdateStatus(FloatCanvas.Localization.AppText.Get("Text.InlineHint"));RequestRender();
        }
        catch {edit.Finish(commit:false);throw;}
    }
    private void FinishInlineText(bool commit=true,bool selectEdited=false)=>_inlineText?.Finish(commit,selectEdited);

    private void QueueInlineTextFocusCheck(InlineTextEdit edit)=>_app.Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>
    {
        if(!_disposed&&ReferenceEquals(_inlineText,edit)&&!edit.IsSelecting&&_canvasTextInput?.IsActive!=true&&
            !_host.IsInputMethodSwitchPending)
            FinishInlineText();
    });

    private bool HandleInlineTextPointer(OverlayPointerEvent pointer)
    {
        if(_inlineText is not {IsActive:true} edit)return false;
        var point=new PointD(pointer.Position.X-_editor.ScreenOrigin.X,pointer.Position.Y-_editor.ScreenOrigin.Y);
        bool down=pointer.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick;
        if(down&&(pointer.Button!=OverlayMouseButton.Left||(pointer.Modifiers&OverlayModifiers.Control)!=0||_editor.IsSelectionFrameHit(point)||!edit.Contains(point)))
        {FinishInlineText();return false;}
        if(pointer.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick or OverlayPointerKind.Move or OverlayPointerKind.Up)
        {
            edit.Pointer(point,down,pointer.Kind==OverlayPointerKind.Up,(pointer.Modifiers&OverlayModifiers.Shift)!=0,pointer.Kind==OverlayPointerKind.DoubleClick);
            if(pointer.Kind==OverlayPointerKind.Up)_canvasTextInput?.Focus();
        }
        else if(pointer.Kind==OverlayPointerKind.Cancel)
        {
            edit.Pointer(point,false,true);
            QueueInlineTextFocusCheck(edit);
        }
        return true;
    }

    private void DrawInlineText(SKCanvas canvas)
    {
        if(_inlineText is not {IsActive:true} edit)return;
        if(!CanvasMapping.Create(Active.Document).IsElementSafe(edit.Target))return;
        var matrix=edit.LocalToScreen;
        double padding=InlineTextEdit.FramePadding;
        var corners=EditorController.Corners(new(-padding,-padding,edit.Target.Width+padding*2,edit.Target.Height+padding*2)).Select(matrix.TransformPoint).ToArray();
        using var frame=new SKPaint {Color=new(35,131,226),IsAntialias=true,Style=SKPaintStyle.Stroke,StrokeWidth=1.5f};
        DrawScreenPolygon(canvas,corners,frame);
        canvas.Save();
        var transform=new SKMatrix((float)matrix.M11,(float)matrix.M12,(float)matrix.M13,(float)matrix.M21,(float)matrix.M22,
            (float)matrix.M23,(float)matrix.M31,(float)matrix.M32,(float)matrix.M33);
        canvas.Concat(in transform);
        using var selection=new SKPaint {Color=new(35,131,226,75),IsAntialias=true};
        foreach(var rectangle in edit.Geometry.Selection(edit.Input.SelectionStart,edit.Input.SelectionLength))
            canvas.DrawRect((float)rectangle.X,(float)rectangle.Y,(float)rectangle.Width,(float)rectangle.Height,selection);
        if(_inlineCaretVisible&&edit.Input.SelectionLength==0)
        {
            var caret=edit.Caret;
            var top=matrix.TransformPoint(new(caret.X,caret.Y));var bottom=matrix.TransformPoint(new(caret.X,caret.Bottom));
            canvas.Restore();canvas.Save();
            using var outline=new SKPaint {Color=SKColors.White,StrokeWidth=5,IsAntialias=true,StrokeCap=SKStrokeCap.Round};
            using var ink=new SKPaint {Color=new(24,36,48),StrokeWidth=3,IsAntialias=true,StrokeCap=SKStrokeCap.Round};
            canvas.DrawLine((float)top.X,(float)top.Y,(float)bottom.X,(float)bottom.Y,outline);
            canvas.DrawLine((float)top.X,(float)top.Y,(float)bottom.X,(float)bottom.Y,ink);
        }
        canvas.Restore();
    }
}
