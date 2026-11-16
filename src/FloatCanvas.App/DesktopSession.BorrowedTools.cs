using FloatCanvas.Core;
using System.Windows.Threading;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed class BorrowedTool(int key,ToolParameters previous,bool sameTool,EditorSession session)
    {
        internal readonly int Key=key;
        internal readonly ToolParameters Previous=previous;
        internal readonly bool SameTool=sameTool;
        internal readonly EditorSession Session=session;
        internal long Started=Environment.TickCount64;
        internal bool Used,Released;
    }
    private BorrowedTool? _borrowedTool;
    private DispatcherTimer? _borrowedToolRestoreTimer;
    private bool _changingBorrowedTool;

    private bool BorrowedToolBusy => _editor.HasPointerGesture || Active.IsTransactionActive ||
        _editor.HasPendingBillboardOperation || _inlineText is not null || DocumentOperationBusy ||
        _canvasCalibration?.IsDragging==true || _canvasCalibration?.IsPlacing==true ||
        _canvasGridScale?.IsDragging==true;

    private void BeginBorrowedTool(EditorCommand command,int key)
    {
        if(command.Tool is not {} tool || BorrowedToolBusy || !CanExecuteEditorCommand(command) ||
            tool==EditorTool.Canvas && !CanEditDisplayedCanvas) return;
        // A newer tool key takes ownership; releasing an older key cannot undo it.
        CancelBorrowedTool();
        var previous=_editor.Parameters;
        bool same=ToolFamily(previous.Tool)==ToolFamily(tool) && AliasMatches(tool);
        _changingBorrowedTool=true;
        try
        {
            ActivateToolCore(tool,showOptions:false);
            _borrowedTool=new(key,previous,same,Active);
        }
        finally {_changingBorrowedTool=false;}
        RequestRender();
    }

    private void ReleaseBorrowedTool(int key)
    {
        if(_borrowedTool is not {} state || state.Key!=key)return;
        if(!state.Used && Environment.TickCount64-state.Started<250)
        {
            ForgetBorrowedTool();
            if(state.SameTool) SelectTool(_editor.Tool);
            return;
        }
        state.Released=true;
        TryRestoreBorrowedTool();
        if(_borrowedTool is null)return;
        // Text editing, multi-click paths and asynchronous capture may outlive key-up.
        // Poll only while restoration is pending, never throughout a held press.
        _borrowedToolRestoreTimer??=new DispatcherTimer(TimeSpan.FromMilliseconds(30),DispatcherPriority.Background,
            (_,_)=>TryRestoreBorrowedTool(),Dispatcher.CurrentDispatcher);
        _borrowedToolRestoreTimer.Start();
    }

    private void ForgetBorrowedTool()
    { _borrowedTool=null;_borrowedToolRestoreTimer?.Stop(); }

    private void TryRestoreBorrowedTool()
    {
        if(_changingBorrowedTool || _borrowedTool is not {Released:true} state)return;
        if(_disposed || !ReferenceEquals(state.Session,Active)) {ForgetBorrowedTool();return;}
        if(BorrowedToolBusy)return;
        ForgetBorrowedTool();_changingBorrowedTool=true;
        try
        {
            if(_editor.Tool!=EditorTool.Canvas) _toolbarToolMemory[ToolFamily(_editor.Tool)]=_editor.Parameters;
            EndCanvasCalibration();
            _editor.ApplyParameters(state.Previous);
            UpdateStatus(ToolName(_editor.Tool));
            SynchronizeCanvasTool();RefreshToolbarState();RequestRender();
        }
        finally {_changingBorrowedTool=false;}
    }

    private void CancelBorrowedTool()
    {
        if(_changingBorrowedTool || _borrowedTool is not {} state)return;
        state.Used=true;state.Released=true;
        _changingBorrowedTool=true;
        try {_editor.Cancel();EndCanvasCalibration();}
        finally {_changingBorrowedTool=false;}
        ReleaseBorrowedTool(state.Key);
    }
}
