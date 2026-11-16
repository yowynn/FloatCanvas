namespace FloatCanvas.App;

/// <summary>One source for toolbar legends, tooltips, menus and application key routing.</summary>
internal static class ToolShortcutCatalog
{
    internal sealed record Shortcut(EditorTool? Tool, string Hint, int VirtualKey);
    internal static IReadOnlyList<Shortcut> All => EditorCommandCatalog.All
        .Where(c=>c.Tool is not null || c.Id is "Image.Import" or "Tools.More")
        .Where(c=>EditorCommandCatalog.Bindings(c).Length>0)
        .Select(c=>new Shortcut(c.Tool ?? (c.Id=="Image.Import"?EditorTool.Image:null),EditorCommandCatalog.Hint(c.Id),
            EditorCommandCatalog.Bindings(c).FirstOrDefault().Key)).ToArray();
    internal const int MoreKey = 0x4D;
    internal const int StampLibraryKey = 0x77;
    internal const int NewStampKey = 0x4B;
    internal static string Hint(EditorTool tool) => EditorCommandCatalog.Hint(tool == EditorTool.Image ? "Image.Import" : EditorCommandCatalog.ToolId(tool));
    internal static bool IsReserved(int virtualKey) => virtualKey is MoreKey or StampLibraryKey or NewStampKey;
    internal static bool TryGetTool(int virtualKey,out EditorTool tool)
    {
        var command=EditorCommandCatalog.Resolve(virtualKey,FloatCanvas.Windows.OverlayModifiers.None);
        if(command?.Tool is { } value){tool=value;return true;}
        if(command?.Id=="Image.Import") { tool=EditorTool.Image;return true; }
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
            UseLayoutRounding=true,
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
