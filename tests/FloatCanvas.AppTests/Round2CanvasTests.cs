using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class Round2CanvasTests
{
    internal static void Run(Action<string,bool> check)
    {
        PointD[] corners=[new(120,100),new(480,100),new(450,280),new(140,280)];
        int count=0;
        foreach(var order in Permutations(corners))
        {
            check("unordered four corners map consistently "+(++count),CanvasCalibration.TryOrderCorners(order,out var sorted)&&sorted.SequenceEqual(corners));
            var document=new SceneDocument {Viewport=new(0,0,640,360)};
            document.Canvas.Projection=CanvasCalibration.CreateInitial(document.Viewport);
            var original=document.Canvas.Projection.Clone();
            var session=new EditorSession(document);var calibration=new CanvasCalibrationController();
            calibration.BeginPlacement(session);
            for(int i=0;i<3;i++)
            {
                calibration.Down(order[i]);calibration.Move(order[(i+1)%4]);
                check($"placement {count}/{i} keeps preview transient",session.UndoCount==0&&!session.IsTransactionActive&&calibration.PlacementPreview.Count==i+2);
            }
            check("placement commits valid fourth corner "+count,calibration.Down(order[3])&&!calibration.IsPlacing&&session.UndoCount==1&&session.Document.Canvas.Projection.Quad.SequenceEqual(corners));
            session.Undo();check("four points undo atomically "+count,session.Document.Canvas.Projection.Quad.SequenceEqual(original.Quad)&&session.Document.Canvas.Projection.Enabled==original.Enabled);
        }
        foreach(var invalid in new PointD[][] { [corners[0],corners[1],corners[2],corners[0]], [new(0,0),new(1,1),new(2,2),new(3,3)], [new(0,0),new(100,0),new(100,100),new(70,30)], [new(double.NaN,0),..corners.Take(3)] })
            check("invalid four-point geometry is rejected",!CanvasCalibration.TryOrderCorners(invalid,out _));
        var draft=new EditorSession(new SceneDocument {Viewport=new(0,0,640,360)});var controller=new CanvasCalibrationController();
        controller.BeginPlacement(draft);controller.Down(corners[0]);controller.Down(corners[1]);controller.Down(corners[2]);
        check("invalid fourth point retains three valid clicks",!controller.Down(new(350,150))&&controller.IsPlacing&&draft.UndoCount==0);
        controller.Escape();check("Escape discards the four-point preview without changing the canvas",!controller.IsActive&&draft.UndoCount==0&&!draft.Document.Canvas.Projection.Enabled);
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};AppTheme.Apply("light");
        var window=new CanvasSettingsWindow();
        try
        {
            window.Refresh(new CanvasSettings {Projection=new(){Enabled=true}});
            window.PrepareForShow();
            window.Measure(new(600,590));window.Arrange(new(0,0,600,590));window.UpdateLayout();
            var reset=Field<Button>(window,"reset");var four=Field<Button>(window,"fourPoints");var arrow=Field<Button>(window,"resetChoices");
            check("four-point placement and reset occupy separate fixed-width rows",four.Parent is Grid first&&reset.Parent is Grid second&&!ReferenceEquals(first,second)&&first.Width==CanvasSettingsWindow.PerspectiveActionsWidth&&second.Width==first.Width);
            check("four-point placement is the full-width primary action without a legacy finish button",((Grid)four.Parent).Children.Count==1&&Grid.GetColumn(four)==0&&double.IsNaN(four.Width)&&four.HorizontalAlignment==HorizontalAlignment.Stretch&&typeof(CanvasSettingsWindow).GetField("calibrate",BindingFlags.Instance|BindingFlags.NonPublic) is null);
            check("reset action and arrow are contiguous halves",reset.Margin==new Thickness(0)&&arrow.Margin==new Thickness(0)&&ReferenceEquals(reset.Parent,arrow.Parent));
            int requests=0;window.FourPointsRequested+=()=>requests++;four.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("four-point button invokes its dedicated action",requests==1);
            var actionRow=(Grid)four.Parent;var resetRow=(Grid)reset.Parent;
            window.Measure(new(900,590));window.Arrange(new(0,0,900,590));window.UpdateLayout();
            check("widening canvas settings keeps both action rows fixed",actionRow.Width==CanvasSettingsWindow.PerspectiveActionsWidth&&resetRow.Width==actionRow.Width);
        }finally{window.CloseForDispose();}
    }
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
    private static IEnumerable<PointD[]> Permutations(PointD[] points)=>points.Length==0?[Array.Empty<PointD>()]:points.SelectMany((p,i)=>Permutations(points.Where((_,j)=>j!=i).ToArray()).Select(tail=>new[]{p}.Concat(tail).ToArray()));
}
