using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Invisible TSF/IME input surface at the canvas caret; no text dialog or visible window chrome.</summary>
internal sealed class CanvasTextInput : IDisposable
{
    private readonly Window window;
    private readonly InlineTextEdit edit;
    private readonly Func<PointD> screenOrigin;
    private bool disposed;
    internal bool IsActive=>window.IsActive&&edit.Input.IsKeyboardFocusWithin;
    internal bool Owns(DependencyObject? source)=>source is not null&&ReferenceEquals(Window.GetWindow(source),window);
    internal event Action? Deactivated;
    internal event Action<bool>? CompleteRequested;
    internal event Action<int>? CommandKey;

    internal CanvasTextInput(Window owner,InlineTextEdit edit,Func<PointD> screenOrigin)
    {
        this.edit=edit;this.screenOrigin=screenOrigin;
        var input=edit.Input;
        InputMethod.SetIsInputMethodEnabled(input,true);
        input.Background=Brushes.Transparent;input.Foreground=Brushes.Transparent;input.CaretBrush=Brushes.Transparent;
        input.BorderThickness=new(0);input.Padding=new(0);input.SelectionBrush=Brushes.Transparent;
        window=new Window {Title="FloatCanvas text input",Owner=owner,Content=input,Width=400,Height=160,
            WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,Opacity=0,
            ShowInTaskbar=false,ShowActivated=false,ResizeMode=ResizeMode.NoResize,Topmost=true};
        window.SourceInitialized+=(_,_)=>((HwndSource)PresentationSource.FromVisual(window)).AddHook(Hook);
        window.Deactivated+=(_,_)=>Deactivated?.Invoke();
        window.PreviewKeyDown+=(_,e)=>
        {
            var key=e.Key==Key.System?e.SystemKey:e.Key;
            var modifiers=Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            int command = KeyInterop.VirtualKeyFromKey(key);
            if (EditorShortcutPolicy.CanRouteProjectCommandFromText(command, (OverlayModifiers)modifiers))
            { e.Handled = true; if (!AppInputPolicy.IsRepeat(e)) CommandKey?.Invoke(command); return; }
            if(key==Key.Escape){e.Handled=true;CompleteRequested?.Invoke(false);}
            else if(key==Key.Enter&&(modifiers&ModifierKeys.Shift)==0){e.Handled=true;CompleteRequested?.Invoke(true);}
            else if((modifiers&~ModifierKeys.Shift)==0&&edit.Navigate(key,(modifiers&ModifierKeys.Shift)!=0))e.Handled=true;
        };
        edit.Changed+=UpdatePlacement;
        SessionWindowCoordinator.TrackOwned(window);
        SessionWindowCoordinator.Show(window);
        UpdatePlacement();
    }
    private nint Hook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x84){handled=true;return -1;} // HTTRANSPARENT: all pointer editing stays on the canvas.
        return 0;
    }
    internal void Focus()
    {
        if(disposed||!edit.IsActive)return;
        SessionWindowCoordinator.Show(window,true);edit.Input.Focus();UpdatePlacement();
    }
    private void UpdatePlacement()
    {
        if(disposed||!edit.IsActive)return;
        var caret=edit.Caret;
        var matrix=edit.LocalToScreen;
        if(!matrix.TryTransformPoint(new(caret.X,caret.Y),out var position))return;
        var jacobian=matrix.Jacobian(new(caret.X,caret.Y));
        // Moving the carrier between monitors can synchronously change its DPI
        // and rearrange its content. Recompute once in the destination's layout.
        for(int pass=0;pass<2;pass++)
        {
            var dpi=VisualTreeHelper.GetDpi(window);
            double scale=Math.Sqrt(jacobian.M11*jacobian.M11+jacobian.M21*jacobian.M21)/dpi.DpiScaleX;
            edit.Input.FontSize=Math.Clamp(edit.Target.Style.FontSize*scale,1,ElementStyle.MaximumFontSize);
            edit.Input.FontFamily=new FontFamily(edit.Target.Style.FontFamily);
            edit.Input.Width=Math.Clamp(edit.Target.Width*scale,40,1000);
            window.UpdateLayout();
            Rect native=edit.Input.GetRectFromCharacterIndex(edit.Input.CaretIndex);
            // The TextBox can be narrower than its transparent carrier and is then
            // centered by WPF. TSF includes that content offset in its screen extent.
            var nativeInWindow=edit.Input.TranslatePoint(native.IsEmpty?new Point():native.TopLeft,window);
            var origin=screenOrigin();
            OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(window).Handle,
                new(position.X+origin.X-nativeInWindow.X*dpi.DpiScaleX,position.Y+origin.Y-nativeInWindow.Y*dpi.DpiScaleY));
            var destinationDpi=VisualTreeHelper.GetDpi(window);
            if(destinationDpi.DpiScaleX==dpi.DpiScaleX&&destinationDpi.DpiScaleY==dpi.DpiScaleY)break;
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        edit.Changed-=UpdatePlacement;window.Content=null;SessionWindowCoordinator.Close(window);
    }
}
