namespace FloatCanvas.App;

/// <summary>One source for toolbar legends, tooltips, menus and application key routing.</summary>
internal static class ToolShortcutCatalog
{
    internal sealed record Shortcut(EditorTool? Tool, string Hint, int VirtualKey);
    internal static IReadOnlyList<Shortcut> All { get; } = Array.AsReadOnly(new[]
    {
        new Shortcut(EditorTool.Select,"V",0x56),new(EditorTool.Eraser,"E",0x45),
        new(EditorTool.Freehand,"B",0x42),new(EditorTool.Highlighter,"H",0x48),
        new(EditorTool.Line,"L",0x4C),new(EditorTool.Rectangle,"R",0x52),new(EditorTool.Ellipse,"O",0x4F),
        new(EditorTool.Text,"T",0x54),new(EditorTool.Number,"N",0x4E),new(EditorTool.Symbol,"S",0x53),
        new(EditorTool.Emoji,"J",0x4A),new(null,"M",0x4D),new(EditorTool.Canvas,"C",0x43),
        new(EditorTool.Image,"I",0x49),new(EditorTool.CaptureRegion,"P",0x50)
    });
    internal const int MoreKey = 0x4D;
    internal const int StampLibraryKey = 0xBD;
    internal const int NewStampKey = 0xBB;
    internal static string Hint(EditorTool tool) => All.FirstOrDefault(item=>item.Tool==tool)?.Hint ?? "";
    internal static bool IsReserved(int virtualKey) => virtualKey is MoreKey or StampLibraryKey or NewStampKey;
    internal static bool TryGetTool(int virtualKey,out EditorTool tool)
    {
        var binding=All.FirstOrDefault(item=>item.VirtualKey==virtualKey);
        if(binding?.Tool is { } value){tool=value;return true;}
        tool=default;return false;
    }
}

internal static class ToolbarShortcutHint
{
    internal static (System.Windows.Controls.Grid Content, System.Windows.Controls.ContentPresenter Glyph,
        System.Windows.Controls.TextBlock Hint) CreateButtonContent(System.Windows.Controls.Button button,string text,bool lowerRow=false)
    {
        // Fill the rounded frame's actual inner size at each DPI; individual
        // toolbar actions must not add padding or their own hint offsets.
        button.Padding=new System.Windows.Thickness(0);
        button.HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch;
        button.VerticalContentAlignment=System.Windows.VerticalAlignment.Stretch;
        var content=new System.Windows.Controls.Grid();
        var glyph=new System.Windows.Controls.ContentPresenter {Content=button.Content,
            HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center};
        var hint=Create(text,lowerRow,button.BorderThickness);
        content.Children.Add(glyph);content.Children.Add(hint);
        return(content,glyph,hint);
    }

    internal static System.Windows.Controls.TextBlock Create(string text, bool lowerRow = false,System.Windows.Thickness frameInset=default)
    {
        var hint=new System.Windows.Controls.TextBlock {Text=text,FontSize=9,FontFamily=new System.Windows.Media.FontFamily("Consolas"),
            FontWeight=System.Windows.FontWeights.SemiBold,Width=10,Height=12,TextAlignment=System.Windows.TextAlignment.Center,
            LineHeight=12,LineStackingStrategy=System.Windows.LineStackingStrategy.BlockLineHeight,
            HorizontalAlignment=lowerRow?System.Windows.HorizontalAlignment.Left:System.Windows.HorizontalAlignment.Right,
            VerticalAlignment=lowerRow?System.Windows.VerticalAlignment.Bottom:System.Windows.VerticalAlignment.Top,
            RenderTransform=new System.Windows.Media.TranslateTransform(lowerRow?-5-frameInset.Left:5+frameInset.Right,
                lowerRow?frameInset.Bottom:-frameInset.Top),IsHitTestVisible=false};
        System.Windows.Media.TextOptions.SetTextFormattingMode(hint,System.Windows.Media.TextFormattingMode.Display);
        hint.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,"CanvasText");
        hint.SetResourceReference(MaskSurfaceProperty,"CanvasSurface");return hint;
    }

    private static readonly System.Windows.DependencyProperty MaskSurfaceProperty=System.Windows.DependencyProperty.RegisterAttached(
        "MaskSurface",typeof(System.Windows.Media.Brush),typeof(ToolbarShortcutHint),new System.Windows.PropertyMetadata(null,(target,args)=>
        {
            if(target is not System.Windows.Controls.TextBlock hint || args.NewValue is not System.Windows.Media.SolidColorBrush surface)return;
            // The ten-DIP text box straddles its frame. Only its middle six DIP
            // mask the edge; transparent ends preserve neighboring frames.
            var clear=System.Windows.Media.Colors.Transparent;
            var mask=new System.Windows.Media.LinearGradientBrush(new System.Windows.Media.GradientStopCollection
            {new(clear,0),new(clear,.2),new(surface.Color,.2),new(surface.Color,.8),new(clear,.8),new(clear,1)},
                new System.Windows.Point(0,0),new System.Windows.Point(1,0));
            mask.Freeze();hint.Background=mask;
        }));
}
