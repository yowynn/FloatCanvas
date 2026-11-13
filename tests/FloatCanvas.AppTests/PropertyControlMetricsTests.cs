using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class PropertyControlMetricsTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1000, 700)) };
        settings.Projection.Enabled = true; CanvasGeometryMigration.UpgradeLegacy(settings);
        var canvas = new CanvasToolOptions(CanvasPresetLibrary.OfficialOnly()); canvas.Refresh(settings); canvas.PrepareForShow();
        Layout(canvas, 440);
        var controls = new FrameworkElement[] { Field<SegmentedChoice>(canvas,"projection"), Field<Button>(canvas,"fourPoints"),
            Field<NumericValueField>(canvas,"planeWidth"), Field<CanvasPinSelector>(canvas,"pinSelector"),
            Field<FrameworkElement>(canvas,"projectionGridCalibration"), Field<NumericValueField>(canvas,"gridSize"),
            Field<NumericValueField>(canvas,"gridOriginX"), Field<Button>(canvas,"rectificationPreview"), Field<Button>(canvas,"reset") };
        foreach(var item in controls) Console.WriteLine($"METRIC canvas {item.GetType().Name} top={Bounds(item,canvas).Top:0.##} height={item.ActualHeight:0.##}");
        for(int i=1;i<controls.Length;i++) Console.WriteLine($"GAP canvas {i} = {Bounds(controls[i],canvas).Top-Bounds(controls[i-1],canvas).Bottom:0.##}");
        check("canvas regular controls share one 34 DIP height", controls.Where(item=>item is not CanvasPinSelector).All(item=>Math.Abs(item.ActualHeight-34)<.1));
        check("pin selector occupies exactly three control units", Math.Abs(Field<CanvasPinSelector>(canvas,"pinSelector").ActualHeight-102)<.1);
        for(int i=1;i<controls.Length;i++) check("canvas adjacent rows share an 8 DIP gap " + i, Math.Abs(Bounds(controls[i],canvas).Top-Bounds(controls[i-1],canvas).Bottom-8)<.1);
        foreach(var tool in new[]{EditorTool.Freehand,EditorTool.Rectangle,EditorTool.Text,EditorTool.Presentation,EditorTool.PointerPresentation})
        {
            var controller=new EditorController(new()) {Tool=tool};
            if(tool==EditorTool.PointerPresentation)
            {
                var parameters=controller.Parameters; parameters.PointerPresentation = new() {Enabled=true,TrailEnabled=true}; controller.ApplyParameters(parameters);
            }
            var panel=new ToolOptionsPanel(controller,new ToolPresetLibrary(),ColorLibrary.Shared); panel.PrepareForShow(); Layout(panel,699);
            var fields=Descendants(panel).OfType<NumericValueField>().Where(item=>item.ActualHeight>0 && item.Visibility==Visibility.Visible).ToArray();
            check(tool+" numeric controls retain one unit",fields.Length>0 && fields.All(item=>Math.Abs(item.ActualHeight-34)<.1));
            if(tool==EditorTool.Rectangle)
            {
                var rows=Field<Dictionary<string,FrameworkElement>>(panel,"rows");
                var numbers=Field<Dictionary<string,NumericValueField>>(panel,"numericFields");
                Console.WriteLine($"METRIC rectangle size-row={rows["Size"].ActualHeight}, field={numbers["DefaultWidth"].ActualHeight}, pair child margin={rows["DefaultWidth"].Margin}");
                check("paired numeric row has no duplicated vertical padding",Math.Abs(rows["Size"].ActualHeight-34)<.1);
            }
        }
        foreach(bool withAction in new[]{false,true})
        {
            var first=new NumericValueField(0,100); var second=new NumericValueField(0,100);
            var pair=PropertyPanelLayout.Pair(first,second,withAction?new AspectRatioButton():null);
            Layout(pair,200);
            var bounds=Bounds(first,pair);var next=Bounds(second,pair);
            check("narrow pair preserves spacing and full height "+withAction,Math.Abs(first.ActualHeight-34)<.1 && Math.Abs(second.ActualHeight-34)<.1 && Math.Abs(next.Top-bounds.Bottom-(withAction?50:8))<.1);
        }
    }
    private static void Layout(FrameworkElement item,double width) {item.Measure(new(width,double.PositiveInfinity));item.Arrange(new Rect(0,0,width,item.DesiredSize.Height));item.UpdateLayout();}
    private static Rect Bounds(FrameworkElement item,Visual parent)=>item.TransformToAncestor(parent).TransformBounds(new Rect(item.RenderSize));
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){yield return child;foreach(var node in Descendants(child))yield return node;}}
}
