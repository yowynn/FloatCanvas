using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class BillboardInspectorTests
{
    internal static void Run(Action<string,bool> check)
    {
        var session=new EditorSession(new SceneDocument {Viewport=new(0,0,1000,800)});
        session.Document.Canvas.Projection=CanvasCalibration.CreateInitial(session.Document.Viewport,10,8);
        session.Document.Canvas.Projection.Enabled=true;
        session.Add(new CanvasElement {Kind=ElementKind.Rectangle,Width=100,Height=60,Transform=Affine2D.Translation(200,200),Style=new(){StrokeWidth=5}});
        var inspector=new InspectorWindow(()=>session); inspector.PrepareForShow();
        try
        {
            var fields=Field<Dictionary<string,TextBox>>(inspector,"fields");
            var containers=Field<Dictionary<string,FrameworkElement>>(inspector,"fieldContainers");
            var picker=Field<ComboBox>(inspector,"placement");
            check("perspective inspector exposes surface placement without an elevation field",picker.SelectedIndex==0&&containers["Placement"].Visibility==Visibility.Visible&&containers["BillboardHeight"].Visibility==Visibility.Collapsed);
            int before=session.UndoCount;picker.SelectedIndex=1;inspector.CompletePendingEdit();inspector.PrepareForShow();
            var e=session.Document.Elements.Single();var anchor=e.BillboardAnchor;
            check("placement conversion is one undoable edit preserving logical size and style",e.IsBillboard&&e.Width==100&&e.Height==60&&e.Style.StrokeWidth==5&&e.BillboardHeight==0&&session.UndoCount==before+1&&containers["BillboardHeight"].Visibility==Visibility.Visible);
            check("billboard X and Y expose its ground anchor",Math.Abs(double.Parse(fields["X"].Text)-anchor.X)<.001&&Math.Abs(double.Parse(fields["Y"].Text)-anchor.Y)<.001);
            fields["BillboardHeight"].Text="-20";inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("signed elevation edits leave ground anchor and logical dimensions unchanged",e.BillboardHeight==-20&&e.BillboardAnchor==anchor&&e.Width==100&&e.Height==60);
            fields["X"].Text="400";inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("billboard X moves the ground anchor without altering elevation",e.BillboardAnchor==new PointD(400,anchor.Y)&&e.BillboardHeight==-20);
            fields["Angle"].Text="45";inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("own-angle property edit preserves anchor and elevation",Math.Abs(Math.Atan2(e.Transform.M21,e.Transform.M11)*180/Math.PI-45)<1e-8&&e.BillboardAnchor==new PointD(400,anchor.Y)&&e.BillboardHeight==-20);
            fields["W"].Text="200";inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("billboard dimensions remain logical under own rotation",Math.Abs(e.Width-200)<1e-8&&e.Height==60&&e.BillboardAnchor==new PointD(400,anchor.Y)&&e.Style.StrokeWidth==5);
            session.Document.Canvas.Projection.Enabled=false;session.NotifyChanged();inspector.PrepareForShow();
            check("turning perspective off hides placement controls without clearing billboard data",containers["Placement"].Visibility==Visibility.Collapsed&&containers["BillboardHeight"].Visibility==Visibility.Collapsed&&e.IsBillboard&&e.BillboardHeight==-20);
            session.Document.Canvas.Projection.Enabled=true;session.NotifyChanged();inspector.PrepareForShow();
            check("turning perspective back on restores the placement controls",containers["Placement"].Visibility==Visibility.Visible&&containers["BillboardHeight"].Visibility==Visibility.Visible&&picker.SelectedIndex==1);
            picker.SelectedIndex=0;inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("conversion back to surface preserves content and logical dimensions",!e.IsBillboard&&Math.Abs(e.Width-200)<1e-8&&e.Height==60&&e.Style.StrokeWidth==5);
            check("undo restores billboard metadata",session.Undo()&&session.Document.Elements.Single().IsBillboard&&session.Document.Elements.Single().BillboardHeight==-20);
            var text=new CanvasElement {Kind=ElementKind.Text,Text="Billboard",Width=120,Height=24,IsBillboard=true,BillboardAnchor=new(300,350),BillboardHeight=40,Style=new(){FontSize=20}};
            session.Add(text);text=session.Document.Elements.Single(item=>item.Id==text.Id);inspector.PrepareForShow();var mapping=CanvasMapping.Create(session.Document);
            var center=mapping.ElementToScreen(text).TransformPoint(ElementGeometry.LocalBounds(text).Center);
            fields["Text"].Text="Billboard\ntext changes\nheight";inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("billboard property text edits keep ground anchor and screen center while remeasuring geometry",text.Height>24&&text.BillboardAnchor==new PointD(300,350)&&text.BillboardHeight==40&&mapping.ElementToScreen(text).TransformPoint(ElementGeometry.LocalBounds(text).Center).DistanceTo(center)<1e-8);
            fields["W"].Text="240";inspector.CompletePendingEdit();inspector.PrepareForShow();
            check("billboard text-frame resize preserves fixed center anchor and elevation",text.Width==240&&text.TextFrameFixed&&text.BillboardAnchor==new PointD(300,350)&&text.BillboardHeight==40&&mapping.ElementToScreen(text).TransformPoint(ElementGeometry.LocalBounds(text).Center).DistanceTo(center)<1e-8);
            check("billboard inspector tests create no native window",new System.Windows.Interop.WindowInteropHelper(inspector).Handle==0);
        }
        finally{inspector.Close();}
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
}
