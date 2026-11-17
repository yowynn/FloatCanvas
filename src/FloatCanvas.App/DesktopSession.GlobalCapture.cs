using FloatCanvas.Core;
using FloatCanvas.Windows;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private FrozenRegionCaptureWindow? _globalCaptureWindow;
    private Func<Task<CapturedDesktop>>? _captureGlobalDesktop = null;
    private Func<FrozenRegionCaptureWindow,bool?>? _selectGlobalRegion = null;

    private async Task CaptureGlobalRegionAsync()
    {
        if(_globalCapture is not null||DocumentOperationBusy||!Active.CanInsertInEditScope)return;
        _inspector?.CompletePendingEdit();
        _editor.Cancel();
        var state=new GlobalCaptureState(Active,_editor.CanvasOrigin,_editor.Parameters.Clone(),_host.IsVisible,_host.IsEditing);
        _globalCapture=state;
        bool inserted=false;
        try
        {
            _documentBusy=true;
            var view=CurrentGroupCanvasView();var scene=view.Document.Clone();var resources=ResourceSnapshot(scene);
            var viewing=EditingCanvasView;var mapping=MappingForGroupCanvasView(view,viewing);var origin=view.Origin;
            var editorMapping=_editor.CurrentCanvasMapping;var editorOrigin=_editor.CanvasOrigin;
            var parameters=ToolbarPreviewParameters(EditorTool.CaptureRegion).Clone();parameters.Tool=EditorTool.CaptureRegion;
            parameters.CaptureSource=CaptureSource.Background; // Frozen pixels already include the visible composition.
            var revision=Active.PreviewRevision;var scope=Active.EditScopeVersion;
            bool Current()=>!_disposed&&ReferenceEquals(_globalCapture,state)&&ReferenceEquals(Active,state.Session)&&Active.PreviewRevision==revision&&Active.EditScopeVersion==scope
                &&_editor.CurrentCanvasMapping.PlaneToScreen==editorMapping.PlaneToScreen&&_editor.CanvasOrigin==editorOrigin&&EditingCanvasView==viewing;
            var presentation=FreezePresentationCapture(Active);using var windows=FreezeWindowSourceCapture(Active);
            var animation=_renderer.AnimationTime;
            var desktop=await (_captureGlobalDesktop?.Invoke()??CaptureCleanDesktopAsync());
            if(!Current())return;
            var displays=_host.Monitors.Select(m=>new RectD(m.Bounds.Left-origin.X,m.Bounds.Top-origin.Y,m.Bounds.Width,m.Bounds.Height)).ToArray();
            var bounds=new RectD(desktop.Bounds.Left-origin.X,desktop.Bounds.Top-origin.Y,desktop.Bounds.Width,desktop.Bounds.Height);
            if(state.Visible)
            {
                var full=new CaptureSelection(CaptureShape.Rectangle,CaptureSource.BackgroundAndCanvas,bounds,[],0,0);
                var combined=await Task.Run(()=>SnapshotImageCommand.Render(scene,resources,full,desktop,displays,animation,origin,presentation,windows,viewing,preserveLayerOpacity:true));
                desktop=desktop with { PngBytes=combined.PngBytes };
            }
            if(!Current())return;
            var selector=new FrozenRegionCaptureWindow(desktop,scene,parameters,mapping,origin,_editor.BillboardMode,_editor.BillboardWorkflow);
            _globalCaptureWindow=selector;
            using(var transfer=_host.BeginNativeDialog())
            {
                if(_selectGlobalRegion is {} select)select(selector);else selector.ShowDialog();
            }
            _globalCaptureWindow=null;
            if(!Current()||selector.Selection is not {} selection)return;
            var raster=await Task.Run(()=>SnapshotImageCommand.Render(scene,resources,selection,desktop,displays,animation,origin));
            if(!Current()||!raster.HasVisiblePixels)return;
            // The authored tool is never changed. Only the completed capture enters editing.
            _documentBusy=false;
            if(!_host.IsEditing&&!SetEditing(true))return;
            if(!_host.IsVisible)_host.SetVisible(true);
            SnapshotImageCommand.Insert(Active,raster,mapping.PlaneToScreen,selection.BillboardDepthLine,selection.BillboardScreenFirst,mapping);
            inserted=true;ScheduleRecovery();
        }
        finally
        {
            _globalCaptureWindow=null;_documentBusy=false;
            CompleteGlobalCapture(inserted);RefreshToolbarState();RequestRender();
        }
    }
}
