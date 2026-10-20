using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
using Threading = System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

/// <summary>
/// Full production CreateToolbarV2 content, rendered detached and in a hidden native DPI host.
/// It never registers hotkeys, enters editing, shows a window, or injects input.
/// </summary>
internal static class ToolbarAcceptanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Wpf.Application.Current is not null) throw new InvalidOperationException("Run --toolbar2-only in a dedicated process.");
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_TOOLBAR2_PREVIEW_DIR") ?? Path.Combine(Path.GetTempPath(),"FloatCanvas-ToolbarAcceptance",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string profile=Path.Combine(output,"profile-"+Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile",profile,"--primary-screen"]);
        new UserSettings {Theme="light",Language="zh-CN"}.Save();
        var app=new Wpf.Application {ShutdownMode=Wpf.ShutdownMode.OnExplicitShutdown};
        var context=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new Threading.DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? session=null; OverlayHost? host=null; Wpf.Window? toolbar=null; Wpf.FrameworkElement? shell=null;
        nint[] handles=[];
        try
        {
            // The internal hidden-host seam is deliberately reached through reflection; AppTests
            // does not need to broaden the platform assembly's public/internal API.
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),BindingFlags.Instance|BindingFlags.NonPublic,null,
                [OverlayMonitorScope.PrimaryScreen,false],null)!;
            session=new DesktopSession(app,host);
            Field<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
            toolbar=Field<Wpf.Window>(session,"_toolbar"); shell=(Wpf.FrameworkElement)toolbar.Content;
            handles=host.Diagnostics.SelectMany(pair=>new[]{pair.DisplayHandle,pair.InputHandle}).Append(host.ControlHandle).ToArray();
            toolbar.Content=null;
            var body=Field<Controls.Border>(session,"_toolbarExpandedContent");
            var grid=(Controls.Grid)body.Child;
            var editor=Field<EditorController>(session,"_editor");
            var library=Field<StampLibrary>(session,"_stampLibrary");
            var shelf=Field<StampShelfControl>(session,"_stampShelf");
            var display=Field<ToolbarStatusDisplay>(session,"_toolbarStatusDisplay");
            var status=Field<Controls.TextBlock>(session,"_status");
            var allButtons=grid.Children.OfType<Controls.Button>().ToArray();
            var buttons=allButtons.Where(button=>Controls.Grid.GetRow(button)==0).ToArray();
            var tools=Field<Dictionary<EditorTool,Controls.Button>>(session,"_toolbarToolButtons");

            var saved=Task.Run(async()=>
            {
                await library.LoadAsync();
                var source=new EditorSession(new SceneDocument {Elements=[new CanvasElement {Kind=ElementKind.Rectangle,Width=80,Height=40,Style=new ElementStyle {Fill=0xFF2383E2,StrokeWidth=0}}]});
                source.SetSelection(source.Document.Elements.Select(element=>element.Id));
                var first=await library.SaveSelectionAsync(source,"示例印章一");
                await library.PinAsync(2,first.Id);
                var second=await library.SaveSelectionAsync(source,"示例印章二");
                var loaded=await library.LoadStampAsync(second.Id);
                return(first,second,loaded);
            }).GetAwaiter().GetResult();
            check("saving a preset atomically fills the first empty slot and preserves existing assignments",library.Pins[0]==saved.first.Id && library.Pins[1]==saved.second.Id && library.Pins[2]==saved.first.Id);
            var target=new EditorSession(new SceneDocument {Elements=[new CanvasElement {Kind=ElementKind.Ellipse,Width=10,Height=10}]});
            target.SelectAll(); int before=target.UndoCount;
            StampLibrary.Place(saved.loaded,target,new PointD(200,200));
            check("placing a stamp preserves prior objects and commits one insertion",target.Document.Elements.Count==2 && target.UndoCount==before+1);
            check("placing a stamp leaves no selected transform frame",target.Selection.Count==0);
            shelf.Refresh(); Invoke(session,"RefreshToolbarState"); PumpToIdle(app.Dispatcher); Measure(shell);

            CheckEqualRowInsets(check,body,grid,display,"production toolbar",1);
            check("production toolbar preserves two 26 DIP rows separated by two DIP",grid.RowDefinitions.Select(row=>row.Height.Value).SequenceEqual(new[]{26d,2d,26d}));
            check("production toolbar shares 30 DIP columns around tool and action separators",grid.ColumnDefinitions.Count==32 && grid.ColumnDefinitions.Select((column,index)=>(column,index)).All(pair=>pair.column.Width.IsAbsolute && pair.column.Width.Value==(pair.index is 2 or 13 or 16 or 20 or 23?10:30)));
            check("production upper row retains every action around its dedicated separators",buttons.Length==25 && buttons.Select(Controls.Grid.GetColumn).Order().SequenceEqual(Enumerable.Range(0,32).Where(column=>column is not (2 or 13 or 16 or 20 or 23 or 28 or 29))) && !buttons.Any(button=>Equals(button.Tag,"tool:Blur")));
            check("compact 60 DIP tool body keeps its upper edge and the 100 DIP mascot anchor",Near(body.ActualHeight,60) && Near(body.TranslatePoint(new Wpf.Point(),shell).Y,39) && ToolbarLauncher.ArtworkSize==100 && Near(Field<ToolbarLauncher>(session,"_toolbarLauncher").TranslatePoint(new Wpf.Point(100,100),shell).Y,100));
            check("production lower row puts stamp actions under Select and Eraser",allButtons.Any(button=>Equals(button.Tag,"manage-stamps")&&Controls.Grid.GetRow(button)==2&&Controls.Grid.GetColumn(button)==0) && allButtons.Any(button=>Equals(button.Tag,"save-stamp")&&Controls.Grid.GetRow(button)==2&&Controls.Grid.GetColumn(button)==1));
            check("production lower row keeps its stamp actions and fills the remaining width with LCD",Controls.Grid.GetRow(shelf)==2 && Controls.Grid.GetColumn(shelf)==3 && Controls.Grid.GetColumnSpan(shelf)==10 && Controls.Grid.GetRow(display)==2 && Controls.Grid.GetColumn(display)==14 && Controls.Grid.GetColumnSpan(display)==18 && allButtons.Where(button=>Controls.Grid.GetRow(button)==2).Select(Controls.Grid.GetColumn).Order().SequenceEqual(new[]{0,1}));
            var billboard=allButtons.Single(button=>Equals(button.Tag,"billboard"));
            check("placement entry sits immediately right of canvas in the upper row and is unavailable without perspective",Controls.Grid.GetColumn(billboard)==15&&Controls.Grid.GetRow(billboard)==0&&!billboard.IsEnabled&&!editor.BillboardMode&&Controls.ToolTipService.GetShowOnDisabled(billboard));
            var surfaceArtwork=Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString();
            editor.Session.Document.Canvas.Projection=CanvasCalibration.CreateInitial(new(0,0,1200,800),10,8);
            editor.Session.Document.Canvas.Projection.Enabled=true;Invoke(session,"RefreshToolbarState");Measure(shell);
            var canvasAction=buttons.Single(button=>Equals(button.Tag,"canvas"));
            check("enabled perspective changes the Canvas artwork while its unselected tool keeps the ordinary style",billboard.IsEnabled&&tools[EditorTool.Canvas]==canvasAction&&canvasAction.Background==app.Resources["CanvasSurface"]&&canvasAction.BorderBrush==Media.Brushes.Transparent&&Descendants(canvasAction).OfType<Shapes.Path>().Single().Stroke==app.Resources["CanvasText"]);
            var parametersBefore=System.Text.Json.JsonSerializer.Serialize(editor.Parameters);
            billboard.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
            Measure(shell);
            check("billboard toggle changes its actual artwork and active style without changing tool parameters",editor.BillboardMode&&Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString()!=surfaceArtwork&&billboard.Background==app.Resources["CanvasHiddenSurface"]&&billboard.BorderBrush==app.Resources["CanvasHiddenBorder"]&&Descendants(billboard).OfType<Shapes.Path>().Single().Stroke==app.Resources["CanvasHiddenText"]&&System.Text.Json.JsonSerializer.Serialize(editor.Parameters)==parametersBefore);
            var placementMenu=Field<Controls.ContextMenu>(session,"_toolbarMenu");
            placementMenu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));
            var modeMenu=placementMenu.Items.OfType<Controls.MenuItem>().SelectMany(item=>item.Items.OfType<Controls.MenuItem>()).Single(item=>Equals(item.Tag,"billboard"));
            check("main menu reflects the global billboard toggle and its actual artwork",modeMenu.IsEnabled&&modeMenu.IsChecked&&Descendants((Wpf.DependencyObject)modeMenu.Icon).OfType<Shapes.Path>().Single().Data.ToString()==Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString());
            check("placement menu and toolbar share their tool name, current mode and complete usage hint",Equals(modeMenu.Header,L.Get("Billboard.Toggle"))&&Equals(modeMenu.ToolTip,billboard.ToolTip)&&Controls.ToolTipService.GetShowOnDisabled(modeMenu));
            var import=buttons.Single(button=>Equals(button.Tag,"import"));
            check("image import uses the shared full-width menu artwork",Descendants(import).OfType<Shapes.Path>().Single().Data.ToString()==Media.Geometry.Parse(CommandGlyphs.Image).ToString()&&Media.Geometry.Parse(CommandGlyphs.Image).Bounds.Width>=14);
            check("placement toolbar and menu use a heavier stroke while adjacent icons keep their standard weight",
                Descendants(billboard).OfType<Shapes.Path>().Single().StrokeThickness==1.8&&
                Descendants((Wpf.DependencyObject)modeMenu.Icon).OfType<Shapes.Path>().Single().StrokeThickness==1.8&&
                Descendants(import).OfType<Shapes.Path>().Single().StrokeThickness==1.5&&
                Descendants(canvasAction).OfType<Shapes.Path>().Single().StrokeThickness==1.5);
            var plane=Media.PathGeometry.CreateFromGeometry(Media.Geometry.Parse(CommandGlyphs.Surface));
            var placementPen=new Media.Pen(Media.Brushes.Black,1.8) {StartLineCap=Media.PenLineCap.Round,EndLineCap=Media.PenLineCap.Round,LineJoin=Media.PenLineJoin.Round};
            check("placement artwork fits the icon canvas including strokes",new Wpf.Rect(0,0,16,16).Contains(plane.GetRenderBounds(placementPen))&&
                new Wpf.Rect(0,0,16,16).Contains(Media.Geometry.Parse(CommandGlyphs.Billboard).GetRenderBounds(placementPen)));
            editor.Session.Document.Canvas.Projection.Enabled=false;Invoke(session,"RefreshToolbarState");Measure(shell);
            check("disabling perspective restores the surface artwork and leaves Canvas unselected",!editor.BillboardMode&&!billboard.IsEnabled&&Descendants(billboard).OfType<Shapes.Path>().Single().Data.ToString()==surfaceArtwork&&billboard.Background==app.Resources["CanvasButton"]&&canvasAction.Background==app.Resources["CanvasSurface"]&&canvasAction.BorderBrush==Media.Brushes.Transparent);
            canvasAction.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));Measure(shell);
            check("Canvas joins the mutually exclusive selected tool frames without opening settings on first activation",editor.Tool==EditorTool.Canvas&&canvasAction.BorderBrush==app.Resources["CanvasAccent"]&&tools[EditorTool.Freehand].BorderBrush==Media.Brushes.Transparent&&Field<CanvasSettingsWindow?>(session,"_canvasSettingsWindow") is null);
            Invoke(session,"ActivateTool",EditorTool.Freehand);Measure(shell);
            check("returning to Freehand restores its saved style and releases the Canvas frame",editor.Tool==EditorTool.Freehand&&tools[EditorTool.Freehand].BorderBrush==app.Resources["CanvasAccent"]&&canvasAction.BorderBrush==Media.Brushes.Transparent);
            check("upper twelve tool order matches the requested Select Eraser then ten drawing entries",buttons.Where(button=>Controls.Grid.GetColumn(button)<13).OrderBy(Controls.Grid.GetColumn).Select(button=>button.Tag?.ToString()).SequenceEqual(new[]{"tool:Select","tool:Eraser","tool:Freehand","tool:Highlighter","tool:Line","tool:Rectangle","tool:Ellipse","tool:Text","tool:Number","tool:Symbol","tool:Emoji","more"}));
            var opacity=Field<Controls.Slider>(session,"_opacitySlider");
            check("opacity occupies exactly two tool columns and added actions remain directly available",Controls.Grid.GetColumn(opacity)==28&&Controls.Grid.GetColumnSpan(opacity)==2&&opacity.Width+opacity.Margin.Left+opacity.Margin.Right==60&&buttons.Any(button=>Equals(button.Tag,"clear"))&&buttons.Any(button=>Equals(button.Tag,"visibility")));
            check("trailing actions follow canvas and placement, images, inspector/layers, then independent new and clear controls",buttons.Where(button=>Controls.Grid.GetColumn(button)>=14).OrderBy(Controls.Grid.GetColumn).Select(button=>button.Tag?.ToString()).SequenceEqual(new[]{"canvas","billboard","import","capture","crop","properties","layers","undo","redo","new-canvas","clear","visibility","done"}));
            var more=buttons.Single(button=>Equals(button.Tag,"more"));
            check("More is enabled and owns its reserved single-letter command",more.IsEnabled&&ToolShortcutCatalog.IsReserved(0x4D)&&!ToolShortcutCatalog.TryGetTool(0x4D,out _)&&more.ToolTip?.ToString()==L.Get("Feedback4.Toolbar.More")+" · M");
            check("main keyboard minus and plus are independent stamp commands",ToolShortcutCatalog.StampLibraryKey==0xBD&&ToolShortcutCatalog.NewStampKey==0xBB&&ToolShortcutCatalog.IsReserved(0xBD)&&ToolShortcutCatalog.IsReserved(0xBB)&&!ToolShortcutCatalog.IsReserved(0x6B)&&!ToolShortcutCatalog.IsReserved(0x6D));
            check("toolbar controls retain keyboard input without focus frames or focus-only tooltips",allButtons.All(button=>button.FocusVisualStyle is null&&Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(button)==false)&&opacity.FocusVisualStyle is null&&Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(opacity)==false&&Controls.ToolTipService.GetShowsToolTipOnKeyboardFocus(Field<ToolbarLauncher>(session,"_toolbarLauncher"))==false);
            var clearAction=buttons.Single(button=>Equals(button.Tag,"clear"));
            check("clear broom uses danger red beside the gray new-canvas action",clearAction.Background==app.Resources["CanvasDanger"]&&buttons.Single(button=>Equals(button.Tag,"new-canvas")).Background==app.Resources["CanvasButton"]&&Controls.Grid.GetColumn(clearAction)==Controls.Grid.GetColumn(buttons.Single(button=>Equals(button.Tag,"new-canvas")))+1);
            var slotButtons=((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().ToArray();
            for(int index=0;index<10;index++)
            {
                var top=buttons.Single(button=>Controls.Grid.GetColumn(button)==index+3);
                double topCenter=top.TranslatePoint(new Wpf.Point(top.ActualWidth/2,0),grid).X;
                double slotCenter=slotButtons[index].TranslatePoint(new Wpf.Point(slotButtons[index].ActualWidth/2,0),grid).X;
                check("production column "+(index+1)+" has one shared upper/lower center",Near(topCenter,slotCenter));
            }
            foreach(var button in buttons)
            {
                string name=button.Tag?.ToString() ?? "button";
                check(name+" is a 26 DIP square inside its 30 DIP column",Near(button.ActualWidth,26) && Near(button.ActualHeight,26) && button.Margin==new Wpf.Thickness(2,0,2,0));
                if(button.Content is not Wpf.FrameworkElement content)throw new InvalidOperationException(name+" has no visual glyph.");
                var glyph=content is Controls.Grid hintGrid?hintGrid.Children.OfType<Controls.ContentPresenter>().Single():content;
                var bound=Media.VisualTreeHelper.GetDescendantBounds(glyph);
                if(bound.IsEmpty)bound=new Wpf.Rect(content.RenderSize);
                var actual=glyph.TransformToAncestor(button).TransformBounds(bound);
                check(name+" glyph fits without clipping",actual.Left>=-.05 && actual.Top>=-.05 && actual.Right<=button.ActualWidth+.05 && actual.Bottom<=button.ActualHeight+.05);
            }
            check("straight-line entry now presents the actual current style sample",Glyph(tools[EditorTool.Line]) is Controls.Image {Source:Imaging.BitmapSource,Width:16,Height:16});
            var done=buttons.Single(button=>Equals(button.Tag,"done"));
            var mark=Vertices(Descendants(done).OfType<Shapes.Path>().Single().Data);
            check("done entry is a check mark rather than the pass-through cursor",mark.SequenceEqual(new[]{new Wpf.Point(2,8),new Wpf.Point(6,12),new Wpf.Point(14,3)}));
            check("save-stamp and stamp-management are independent lower-row entries",allButtons.Any(button=>Equals(button.Tag,"save-stamp")) && allButtons.Any(button=>Equals(button.Tag,"manage-stamps")));
            var imageMenu=more.ContextMenu;
            check("More owns only general canvas blur and mosaic",ReferenceEquals(tools[EditorTool.Blur],more)&&imageMenu is not null && imageMenu.Items.OfType<Controls.MenuItem>().Select(item=>item.Header?.ToString()).SequenceEqual(new[]{L.Get("Tool.Blur"),L.Get("Tool.Pixelate")}));
            var cropButton=buttons.Single(button=>Equals(button.Tag,"crop"));
            check("disabled crop retains a hover tooltip",Controls.ToolTipService.GetShowOnDisabled(cropButton) && cropButton.ToolTip?.ToString()==L.Get("Tool.Crop"));
            var cropMenus=Field<List<Controls.MenuItem>>(session,"_cropMenuItems");
            check("image crop follows import and capture and is disabled consistently without a target",Controls.Grid.GetColumn(cropButton)==Controls.Grid.GetColumn(buttons.Single(button=>Equals(button.Tag,"capture")))+1 &&
                ReferenceEquals(tools[EditorTool.Crop],cropButton) && !cropButton.IsEnabled && cropMenus.Count>0 && cropMenus.All(item=>!item.IsEnabled));
            var cropTarget=new CanvasElement {Kind=ElementKind.Image,Width=100,Height=80,AssetId=new string('a',64)};
            editor.Session.Document.Elements.Add(cropTarget);editor.Session.SetSelection([cropTarget.Id]);Invoke(session,"RefreshToolbarState");
            check("selecting one editable image enables the crop button and all menu entries",cropButton.IsEnabled && cropMenus.All(item=>item.IsEnabled));
            Invoke(session,"ActivateTool",EditorTool.Crop);
            check("the dedicated crop action activates its own tool family",editor.Tool==EditorTool.Crop);
            cropTarget.Locked=true;editor.Session.NotifyChanged();Invoke(session,"RefreshToolbarState");
            check("locking the selected image disables all crop entries and exits the crop tool",!cropButton.IsEnabled && cropMenus.All(item=>!item.IsEnabled) && editor.Tool==EditorTool.Select);
            Invoke(session,"ActivateTool",EditorTool.Crop);
            check("an inactive crop menu or keyboard activation cannot bypass eligibility",editor.Tool==EditorTool.Select);
            editor.Session.ReplaceDocument(new SceneDocument());editor.Tool=EditorTool.Freehand;Invoke(session,"RefreshToolbarState");
            var manage=allButtons.Single(button=>Equals(button.Tag,"manage-stamps"));var saveStamp=allButtons.Single(button=>Equals(button.Tag,"save-stamp"));
            check("idle stamp-library launcher shares New Stamp's normal gray button background",((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)saveStamp.Background).Color && ((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasButton"]).Color);
            var newStampVertices=Vertices(Descendants(saveStamp).OfType<Shapes.Path>().Single().Data);
            check("stamp buttons explain minus and plus while New Stamp presents an explicit plus",manage.ToolTip?.ToString()==L.Get("Stamp.LibraryTitle")+" · -"&&saveStamp.ToolTip?.ToString()==L.Get("Feedback3.Toolbar.NewStamp")+" · +"&&newStampVertices.Contains(new Wpf.Point(12.5,2.5))&&newStampVertices.Contains(new Wpf.Point(12.5,7.5))&&newStampVertices.Contains(new Wpf.Point(10,5))&&newStampVertices.Contains(new Wpf.Point(15,5)));

            var parameters=editor.Parameters; parameters.Tool=EditorTool.Symbol; parameters.Symbol=SymbolKind.Heart;parameters.Style.Stroke=0xFFDC4F78;
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Measure(shell);
            var firstSymbol=(Controls.Image)Glyph(tools[EditorTool.Symbol]);string heart=Fingerprint((Imaging.BitmapSource)firstSymbol.Source);
            parameters=editor.Parameters;parameters.Symbol=SymbolKind.Hexagon;parameters.Style.Stroke=0xFF168F69;
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Measure(shell);
            var nextSymbol=(Controls.Image)Glyph(tools[EditorTool.Symbol]);
            check("production symbol button replaces its shape and current paint color",heart!=Fingerprint((Imaging.BitmapSource)nextSymbol.Source) && ContainsColor((Imaging.BitmapSource)nextSymbol.Source,Media.Color.FromRgb(0x16,0x8F,0x69)));
            var oldEmoji=((Controls.Image)Glyph(tools[EditorTool.Emoji])).Source;
            parameters=editor.Parameters;parameters.Tool=EditorTool.Emoji;parameters.Emoji="🔥";
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Measure(shell);
            var nextEmoji=(Controls.Image)Glyph(tools[EditorTool.Emoji]);
            check("production emoji button replaces the actual image with the chosen emoji",!ReferenceEquals(oldEmoji,nextEmoji.Source) && ReferenceEquals(nextEmoji.Source,EmojiArtwork.Image("🔥")) && nextEmoji.Width==16 && nextEmoji.Height==16);
            string configuredSymbol=Fingerprint((Imaging.BitmapSource)nextSymbol.Source);
            Invoke(session,"ActivateTool",EditorTool.Freehand);Measure(shell);
            var rememberedSymbol=(Controls.Image)Glyph(tools[EditorTool.Symbol]);
            check("returning to Freehand preserves the most recent symbol shape and paint",Fingerprint((Imaging.BitmapSource)rememberedSymbol.Source)==configuredSymbol && ContainsColor((Imaging.BitmapSource)rememberedSymbol.Source,Media.Color.FromRgb(0x16,0x8F,0x69)));
            check("returning to Freehand preserves the most recent emoji image",ReferenceEquals(((Controls.Image)Glyph(tools[EditorTool.Emoji])).Source,EmojiArtwork.Image("🔥")));
            var hints=Field<List<Controls.TextBlock>>(session,"_toolbarShortcutHintLabels");
            check("all thirteen tools share single-letter corner legends and stamp commands add minus/plus",hints.Count==15&&hints.Take(13).Select(hint=>hint.Text).SequenceEqual(ToolShortcutCatalog.All.Select(binding=>binding.Hint))&&hints.Skip(13).Select(hint=>hint.Text).SequenceEqual(new[]{"-","+"}));
            foreach(var button in buttons.Where(button=>Controls.Grid.GetColumn(button)<13))
            {
                var hint=((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single();
                var origin=hint.TranslatePoint(new Wpf.Point(),button);
                check(button.Tag+" legend is centered on the right edge and top-aligned",Near(origin.X+hint.ActualWidth/2,button.ActualWidth)&&Near(origin.Y,0));
            }
            foreach(var button in slotButtons.Concat(new[]{manage,saveStamp}))
            {
                var hint=((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single();
                var origin=hint.TranslatePoint(new Wpf.Point(),button);
                // Slot content is centered in the wider 30-DIP hit cell; its visible frame is 26 DIP.
                double left=slotButtons.Contains(button)?2:0;
                check("lower "+hint.Text+" legend is centered on its left frame edge and bottom-aligned",Near(origin.X+hint.ActualWidth/2,left)&&Near(origin.Y+hint.ActualHeight,26));
            }
            var settings=Field<UserSettings>(session,"_settings");settings.ShowToolShortcutHints=false;Invoke(session,"RefreshToolShortcutHints");
            shelf.Refresh();Measure(shell);
            check("hiding shortcut legends also hides lower digits after a shelf rebuild",hints.All(hint=>hint.Visibility==Wpf.Visibility.Collapsed)&&((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single().Visibility==Wpf.Visibility.Collapsed)&&tools[EditorTool.Number]!=tools[EditorTool.Text]);
            settings.ShowToolShortcutHints=true;Invoke(session,"RefreshToolShortcutHints");
            check("restoring shortcut legends also restores every current slot digit",((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single().Visibility==Wpf.Visibility.Visible));
            check("unselected tools have no idle outline",tools.Where(pair=>pair.Key!=EditorTool.Freehand).All(pair=>pair.Value.BorderBrush is Media.SolidColorBrush brush&&brush.Color.A==0));
            Invoke(session,"SaveStampSelection");
            check("save-stamp with no selection explains the requirement without creating an unseen dialog",status.Text==L.Get("Stamp.NoSelection") && Field<StampLibraryWindow?>(session,"_stampWindow") is null);

            string longStatus=string.Concat(Enumerable.Repeat("这是用于验收省略与悬停滚动的长状态 · long toolbar status / ",8));
            Invoke(session,"UpdateStatus",longStatus);display.Refresh();Measure(shell);
            check("LCD keeps long content while trimming its extended layout",status.Text==longStatus && status.TextTrimming==Wpf.TextTrimming.CharacterEllipsis && double.IsNaN(status.Width) && Near(display.ActualWidth,476) && display.ClipToBounds);
            display.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice,Environment.TickCount) {RoutedEvent=Input.Mouse.MouseEnterEvent});
            var scroll=(Media.TranslateTransform)status.RenderTransform;
            check("LCD's actual hover handler begins a clipped scrolling animation",scroll.HasAnimatedProperties && status.TextTrimming==Wpf.TextTrimming.None && double.IsFinite(status.Width) && status.Width>display.ActualWidth);
            display.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice,Environment.TickCount) {RoutedEvent=Input.Mouse.MouseLeaveEvent});
            check("LCD's actual leave handler cancels animation and restores ellipsis",!scroll.HasAnimatedProperties && scroll.X==0 && status.TextTrimming==Wpf.TextTrimming.CharacterEllipsis && double.IsNaN(status.Width));

            Set(session,"_selectedStampId",(Guid?)saved.second.Id);Set(session,"_selectedStampFromSlot",true);Set(session,"_selectedStampRevision",saved.second.Revision);Set(session,"_selectedStamp",saved.loaded);
            editor.Tool=EditorTool.Stamp;Invoke(session,"RefreshToolbarState");
            check("a bound selected stamp retains stamp mode",editor.Tool==EditorTool.Stamp);
            check("slot selection highlights its slot and leaves the library launcher unselected",Field<Guid?>(session,"_toolbarActiveStamp")==saved.second.Id && Wpf.Automation.AutomationProperties.GetItemStatus(manage)=="");
            Set(session,"_selectedStampFromSlot",false);Invoke(session,"RefreshToolbarState");PumpToIdle(app.Dispatcher);
            check("library selection highlights its launcher and clears every slot highlight",Field<Guid?>(session,"_toolbarActiveStamp") is null && Wpf.Automation.AutomationProperties.GetItemStatus(manage)==L.Get("ToolbarV2.Selected") && manage.ToolTip is StampPreviewTooltip);
            check("selected library launcher replaces its glyph with a static image",Glyph(manage) is Controls.Image {Source:Imaging.BitmapSource {IsFrozen:true}});
            foreach(string theme in new[]{"light","dark"})
            {
                toolbar.Content=shell;AppTheme.Apply(theme);PumpToIdle(app.Dispatcher);toolbar.Content=null;Measure(shell);
                check(theme+" active library launcher uses the same transparent preview and blue frame as a stamp slot",((Media.SolidColorBrush)manage.Background).Color.A==0 && ((Media.SolidColorBrush)manage.BorderBrush).Color==((Media.SolidColorBrush)app.Resources["CanvasAccent"]).Color && Glyph(manage) is Controls.Image {Width:20,Height:20});
                check(theme+" active library launcher retains the common action-button hover and press template",ReferenceEquals(manage.Template,saveStamp.Template));
                Save(shell,Path.Combine(output,"stamp-library-active-"+theme+".png"),1.5);
            }
            Set(session,"_selectedStampFromSlot",true);Invoke(session,"RefreshToolbarState");
            check("leaving the library stamp restores its normal trigger glyph and gray background",ReferenceEquals(Glyph(manage),Field<object>(session,"_stampLibraryDefaultGlyph")) && ((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)saveStamp.Background).Color && ((Media.SolidColorBrush)manage.BorderBrush).Color.A==0 && manage.ToolTip?.ToString()==L.Get("Stamp.LibraryTitle")+" · -");
            Task.Run(()=>library.PinAsync(1,null)).GetAwaiter().GetResult();PumpToIdle(app.Dispatcher);
            check("unbinding the active last slot restores Select through the library notification",editor.Tool==EditorTool.Select && Field<Guid?>(session,"_selectedStampId") is null);

            parameters=editor.Parameters;parameters.Tool=EditorTool.Symbol;parameters.Symbol=SymbolKind.Heart;parameters.Style.Stroke=0xFF2383E2;parameters.Emoji="😀";
            editor.ApplyParameters(parameters);Invoke(session,"RefreshToolbarState");Invoke(session,"UpdateStatus","示例 · 心形 / 自定义印章");
            shelf.Refresh();PumpToIdle(app.Dispatcher);
            foreach(string theme in new[]{"light","dark"})
            {
                // Application resource changes traverse registered Window trees. Keep
                // the production owner connection while changing theme, then detach
                // only for rendering without ever showing its native window.
                toolbar.Content=shell;
                AppTheme.Apply(theme);PumpToIdle(app.Dispatcher);
                toolbar.Content=null;Measure(shell);
                var expected=((Media.SolidColorBrush)app.Resources["CanvasSurface"]).Color;
                check(theme+" idle library launcher follows the normal button background after theme changes",((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)saveStamp.Background).Color && ((Media.SolidColorBrush)manage.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasButton"]).Color);
                check(theme+" production body resolves the actual current theme surface",((Media.SolidColorBrush)body.Background).Color==expected);
                check(theme+" LCD follows its dedicated surface and readable text theme",((Media.SolidColorBrush)display.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasLcdSurface"]).Color&&((Media.SolidColorBrush)status.Foreground).Color==((Media.SolidColorBrush)app.Resources["CanvasLcdText"]).Color);
                RenderVisibilityStates(session,host,shell,theme,output);
                RenderPlacementStates(check,session,editor,billboard,import,placementMenu,modeMenu,shell,theme,output);
                var bitmap=Save(shell,Path.Combine(output,"production-toolbar-"+theme+".png"),1.5);
                var legend=hints.Single(value=>value.Text=="R");
                check(theme+" small shortcut lettering has strong contrast against its masking surface",Contrast(((Media.SolidColorBrush)legend.Foreground).Color,expected)>=7);
                foreach(double scale in new[]{1d,1.5,2d})
                {
                    var hint=hints.Single(value=>value.Text=="R");
                    int expectedInk=ReferenceInk(hint,scale);
                    int actualInk=LegendInk(shell,hint,scale);
                    Console.WriteLine($"LEGEND {theme} {scale:P0}: actual={actualInk}, reference={expectedInk}");
                    check($"{theme} {scale:P0} rectangle legend retains its complete rendered letter",expectedInk>5&&actualInk>=expectedInk*.8);
                    check($"{theme} {scale:P0} upper shortcut mask never erases its neighboring selected frame",NeighborUnchanged(shell,hint,tools[EditorTool.Ellipse],Controls.Control.BorderBrushProperty,true,scale));
                    var currentSlots=((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().ToArray();
                    var slotGrid=(Controls.Grid)currentSlots[1].Content;
                    var priorFrame=((Controls.Grid)currentSlots[0].Content).Children.OfType<Controls.Border>().Single();
                    check($"{theme} {scale:P0} lower shortcut mask never erases its previous slot frame",NeighborUnchanged(shell,slotGrid.Children.OfType<Controls.TextBlock>().Single(),priorFrame,Controls.Border.BorderBrushProperty,false,scale));
                }
                var sample=body.TranslatePoint(new Wpf.Point(body.ActualWidth-3,body.ActualHeight/2),shell);
                var actual=ColorAt(bitmap,(int)(sample.X*1.5),(int)(sample.Y*1.5));
                check(theme+" full production preview paints that surface in actual pixels",Math.Abs(actual.R-expected.R)<=2 && Math.Abs(actual.G-expected.G)<=2 && Math.Abs(actual.B-expected.B)<=2 && actual.A==255);
            }
            CheckNativeDpiLegends(check,shell,hints,tools,shelf,output);
            check("full-toolbar fixture never enters editing or shows canvas/input/tool windows",!host.IsEditing && !host.IsVisible && host.Diagnostics.All(pair=>!pair.InputVisible && !pair.DisplayVisible) && app.Windows.Cast<Wpf.Window>().All(window=>!window.IsVisible));
        }
        finally
        {
            if(toolbar is not null && shell is not null)toolbar.Content=shell;
            session?.Dispose();if(session is null)host?.Dispose();
            SynchronizationContext.SetSynchronizationContext(context);
            app.Shutdown();
        }
        check("full-toolbar fixture destroys every native monitor/control HWND",handles.All(handle=>!IsWindow(handle)));
        Console.WriteLine("Full production toolbar evidence: "+output);
    }

    private static bool Near(double a,double b)=>Math.Abs(a-b)<.05;
    private static void RenderPlacementStates(Action<string,bool> check,DesktopSession session,EditorController editor,
        Controls.Button placement,Controls.Button import,Controls.ContextMenu menu,Controls.MenuItem modeMenu,
        Wpf.FrameworkElement shell,string theme,string output)
    {
        string language=L.Preference;bool perspective=editor.Session.Document.Canvas.Projection.Enabled,mode=editor.BillboardMode;
        try
        {
            foreach(string locale in new[]{"zh-CN","en"})
            {
                L.Initialize(locale);
                var row=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal};
                row.SetResourceReference(Controls.Panel.BackgroundProperty,"CanvasSurface");
                foreach(bool billboard in new[]{false,true})
                {
                    editor.Session.Document.Canvas.Projection.Enabled=true;editor.BillboardMode=billboard;
                    menu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));Measure(shell);
                    string tooltip=(string)placement.ToolTip;
                    string[] lines=tooltip.Split('\n');
                    check($"{locale} {theme} {billboard} placement hint contains only its title and currently selected mode",
                        lines.Length==2&&lines[0]==L.Get("Billboard.Toggle")&&lines[1]==L.Format("Billboard.CurrentMode",L.Get(billboard?"Billboard.BillboardMode":"Billboard.DefaultMode"))&&Equals(modeMenu.ToolTip,tooltip)&&
                        System.Windows.Automation.AutomationProperties.GetItemStatus(placement)==L.Get(billboard?"Billboard.BillboardMode":"Billboard.DefaultMode"));
                    var card=new Controls.StackPanel {Width=340,Margin=new Wpf.Thickness(14)};
                    var icons=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal,Margin=new Wpf.Thickness(0,0,0,12)};
                    icons.Children.Add(new Controls.Image {Source=ControlPixels(placement),Width=52,Height=52,Stretch=Media.Stretch.Uniform});
                    icons.Children.Add(new Controls.Image {Source=ControlPixels(import),Width=52,Height=52,Margin=new Wpf.Thickness(14,0,0,0),Stretch=Media.Stretch.Uniform});
                    card.Children.Add(icons);
                    var text=new Controls.TextBlock {Text=tooltip,TextWrapping=Wpf.TextWrapping.Wrap,FontSize=14,LineHeight=22};
                    text.SetResourceReference(Controls.TextBlock.ForegroundProperty,"CanvasText");card.Children.Add(text);row.Children.Add(card);
                }
                Measure(row);Save(row,Path.Combine(output,$"placement-tooltip-{locale}-{theme}.png"),1.5);
            }
        }
        finally
        {
            L.Initialize(language);editor.Session.Document.Canvas.Projection.Enabled=perspective;editor.BillboardMode=mode;
            menu.RaiseEvent(new Wpf.RoutedEventArgs(Controls.ContextMenu.OpenedEvent));Measure(shell);
        }
        Imaging.BitmapSource ControlPixels(Controls.Button button)
        {
            var point=button.TranslatePoint(new Wpf.Point(),shell);
            var pixels=Raster(shell,4);
            return new Imaging.CroppedBitmap(pixels,new Wpf.Int32Rect((int)Math.Round(point.X*4),(int)Math.Round(point.Y*4),
                (int)Math.Round(button.ActualWidth*4),(int)Math.Round(button.ActualHeight*4)));
        }
    }
    private static void RenderVisibilityStates(DesktopSession session,OverlayHost host,
        Wpf.FrameworkElement shell,string theme,string output)
    {
        var visibleProperty=typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!;
        var original=host.IsVisible;
        try
        {
            foreach(bool visible in new[]{true,false})
            {
                // Project each host state through production refresh without showing
                // a native canvas or pretending to test the actual visibility command.
                visibleProperty.SetValue(host,visible);Invoke(session,"RefreshToolbarState");Measure(shell);
                string state=visible?"visible":"hidden";
                Save(shell,Path.Combine(output,"production-toolbar-"+theme+"-canvas-"+state+".png"),1.5);
            }
        }
        finally {visibleProperty.SetValue(host,original);Invoke(session,"RefreshToolbarState");Measure(shell);}
    }
    private static void CheckEqualRowInsets(Action<string,bool> check,Controls.Border body,Controls.Grid grid,
        ToolbarStatusDisplay display,string name,double scale)
    {
        var upper=grid.Children.OfType<Controls.Button>().First(button=>Controls.Grid.GetRow(button)==0);
        var lower=grid.Children.OfType<Controls.Button>().First(button=>Controls.Grid.GetRow(button)==2);
        double top=upper.TranslatePoint(new Wpf.Point(),body).Y;
        double upperBottom=upper.TranslatePoint(new Wpf.Point(0,upper.ActualHeight),body).Y;
        double lowerTop=lower.TranslatePoint(new Wpf.Point(),body).Y;
        double bottom=body.ActualHeight-lower.TranslatePoint(new Wpf.Point(0,lower.ActualHeight),body).Y;
        double middle=lowerTop-upperBottom;
        double topInset=top-Math.Round(body.BorderThickness.Top*scale)/scale;
        double bottomInset=bottom-Math.Round(body.BorderThickness.Bottom*scale)/scale;
        Console.WriteLine($"ROW SPACING {name}: inner top={topInset*scale:F2}px middle={middle*scale:F2}px inner bottom={bottomInset*scale:F2}px");
        double tolerance=scale==1?.05:1.01/scale;
        check(name+" gives both rows equal two-DIP upper middle and lower inner spacing",Math.Abs(topInset-2)<=tolerance&&Math.Abs(middle-2)<=tolerance&&Math.Abs(bottomInset-2)<=tolerance);
        double lcdTop=display.TranslatePoint(new Wpf.Point(),body).Y;
        double lcdBottom=display.TranslatePoint(new Wpf.Point(0,display.ActualHeight),body).Y;
        double lowerBottom=body.ActualHeight-bottom;
        var upperChrome=(Controls.Border)upper.Template.FindName("Chrome",upper);
        var lowerChrome=(Controls.Border)lower.Template.FindName("Chrome",lower);
        check(name+" aligns LCD and lower frames with matching small button corners",Math.Abs(lcdTop-lowerTop)<=.05&&Math.Abs(lcdBottom-lowerBottom)<=.05&&display.CornerRadius==new Wpf.CornerRadius(3)&&upperChrome.CornerRadius==display.CornerRadius&&lowerChrome.CornerRadius==display.CornerRadius);
    }
    private static double Contrast(Media.Color first,Media.Color second)
    {
        static double Channel(byte value){double x=value/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
        static double Light(Media.Color c)=>.2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B);
        double a=Light(first),b=Light(second);return(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
    private static bool NeighborUnchanged(Wpf.FrameworkElement shell,Controls.TextBlock hint,Wpf.FrameworkElement neighbor,Wpf.DependencyProperty stroke,bool leftEdge,double scale)
    {
        if(!hint.IsDescendantOf(shell)||!neighbor.IsDescendantOf(shell))throw new InvalidOperationException("A legend pixel probe must use the current production visual tree.");
        var old=neighbor.GetValue(stroke);neighbor.SetResourceReference(stroke,"CanvasAccent");
        var point=neighbor.TranslatePoint(new Wpf.Point(),shell);Measure(shell);var before=Raster(shell,scale);
        hint.Visibility=Wpf.Visibility.Hidden;Measure(shell);var after=Raster(shell,scale);
        hint.Visibility=Wpf.Visibility.Visible;neighbor.SetValue(stroke,old);Measure(shell);
        int left=(int)Math.Floor((point.X+(leftEdge?0:neighbor.ActualWidth-2))*scale),top=(int)Math.Ceiling((point.Y+(leftEdge?6:14))*scale);
        return DifferentPixels(before,after,new Wpf.Int32Rect(left,top,(int)Math.Ceiling(2*scale),(int)Math.Floor(8*scale)))==0;
    }
    private static void CheckNativeDpiLegends(Action<string,bool> check,Wpf.FrameworkElement shell,
        IReadOnlyList<Controls.TextBlock> hints,Dictionary<EditorTool,Controls.Button> tools,StampShelfControl shelf,string output)
    {
        // RTB resolution alone does not change WPF layout DPI. A hidden native
        // source and the production Window's rounding policy reproduce the
        // fractional inner size of a 26-DIP button with its one-DIP border.
        const double scale=1.5;
        var root=new Controls.Border {Child=shell,UseLayoutRounding=true,SnapsToDevicePixels=true};
        nint priorContext=SetThreadDpiAwarenessContext(-4),handle=0;
        try
        {
            using var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("FloatCanvas toolbar DPI regression")
                {Width=1600,Height=180,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000000});
            handle=source.Handle;source.RootVisual=root;
            // Set the actual layout DPI explicitly so CI also covers 150% on
            // machines whose physical monitor happens to use another scale.
            Media.VisualTreeHelper.SetRootDpi(root,new Wpf.DpiScale(scale,scale));Measure(root);
            check("native toolbar DPI probe lays out at 150% without showing its HWND",IsWindow(handle)&&!IsWindowVisible(handle)&&Near(Media.VisualTreeHelper.GetDpi(shell).DpiScaleX,scale));
            var body=shell is Controls.Grid surface?surface.Children.OfType<Controls.Border>().Single():throw new InvalidOperationException("Toolbar surface has no body.");
            var grid=(Controls.Grid)body.Child;
            var display=grid.Children.OfType<ToolbarStatusDisplay>().Single();
            CheckEqualRowInsets(check,body,grid,display,"native 150% toolbar",scale);
            var measured=new List<(string Text,int Expected,int Actual)>();
            foreach(var hint in hints)
            {
                int expected=ReferenceInk(hint,scale,layoutDpi:true),actual=LegendInk(shell,hint,scale);
                Console.WriteLine($"NATIVE LEGEND {hint.Text}: actual={actual}, reference={expected}");
                measured.Add((hint.Text,expected,actual));
            }
            foreach(var result in measured)check("150% native toolbar retains the complete "+result.Text+" shortcut legend",result.Expected>0&&result.Actual>=result.Expected*.8);
            check("150% native upper mask preserves its neighboring selected frame",NeighborUnchanged(shell,hints.Single(hint=>hint.Text=="R"),tools[EditorTool.Ellipse],Controls.Control.BorderBrushProperty,true,scale));
            var slots=((Controls.StackPanel)shelf.Content).Children.OfType<Controls.Button>().ToArray();
            var current=(Controls.Grid)slots[1].Content;
            var previous=((Controls.Grid)slots[0].Content).Children.OfType<Controls.Border>().Single();
            check("150% native lower mask preserves its previous slot frame",NeighborUnchanged(shell,current.Children.OfType<Controls.TextBlock>().Single(),previous,Controls.Border.BorderBrushProperty,false,scale));
            Save(shell,Path.Combine(output,"production-toolbar-native-150.png"),scale);
            source.RootVisual=null;
        }
        finally
        {
            root.Child=null;
            if(priorContext!=0)SetThreadDpiAwarenessContext(priorContext);
        }
        check("native toolbar DPI probe destroys its hidden HWND",handle!=0&&!IsWindow(handle));
    }
    private static int ReferenceInk(Controls.TextBlock hint,double scale,bool layoutDpi=false)
    {
        var reference=new Controls.TextBlock {Text=hint.Text,Width=hint.Width,Height=hint.Height,
            FontFamily=hint.FontFamily,FontSize=hint.FontSize,FontWeight=hint.FontWeight,
            LineHeight=hint.LineHeight,LineStackingStrategy=hint.LineStackingStrategy,
            TextAlignment=hint.TextAlignment,Foreground=Media.Brushes.Black,Background=Media.Brushes.White};
        Media.TextOptions.SetTextFormattingMode(reference,Media.TextOptions.GetTextFormattingMode(hint));
        if(layoutDpi)
        {
            Media.VisualTreeHelper.SetRootDpi(reference,new Wpf.DpiScale(scale,scale));reference.UseLayoutRounding=true;
            reference.Foreground=hint.Foreground;reference.Background=(Media.Brush)Wpf.Application.Current.FindResource("CanvasSurface");
        }
        Measure(reference);var before=Raster(reference,scale);reference.Text="";Measure(reference);
        return DifferentPixels(before,Raster(reference,scale),new Wpf.Int32Rect(0,0,before.PixelWidth,before.PixelHeight));
    }
    private static int LegendInk(Wpf.FrameworkElement shell,Controls.TextBlock hint,double scale)
    {
        var point=hint.TranslatePoint(new Wpf.Point(),shell);var before=Raster(shell,scale);
        string text=hint.Text;hint.Text="";Measure(shell);var after=Raster(shell,scale);hint.Text=text;Measure(shell);
        int left=Math.Max(0,(int)Math.Floor(point.X*scale)),top=Math.Max(0,(int)Math.Floor(point.Y*scale));
        int right=Math.Min(before.PixelWidth,(int)Math.Ceiling((point.X+hint.ActualWidth)*scale));
        int bottom=Math.Min(before.PixelHeight,(int)Math.Ceiling((point.Y+hint.ActualHeight)*scale));
        return DifferentPixels(before,after,new Wpf.Int32Rect(left,top,right-left,bottom-top));
    }
    private static int DifferentPixels(Imaging.BitmapSource first,Imaging.BitmapSource second,Wpf.Int32Rect area)
    {
        int stride=area.Width*4;var a=new byte[stride*area.Height];var b=new byte[a.Length];
        first.CopyPixels(area,a,stride,0);second.CopyPixels(area,b,stride,0);int count=0;
        for(int i=0;i<a.Length;i+=4)if(Math.Abs(a[i]-b[i])+Math.Abs(a[i+1]-b[i+1])+Math.Abs(a[i+2]-b[i+2])>24)count++;
        return count;
    }
    private static Imaging.RenderTargetBitmap Raster(Wpf.FrameworkElement element,double scale)
    {var bitmap=new Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,Media.PixelFormats.Pbgra32);bitmap.Render(element);return bitmap;}
    private static Wpf.UIElement Glyph(Controls.Button button)=>((Controls.Grid)button.Content).Children.OfType<Controls.ContentPresenter>().Single().Content as Wpf.UIElement ?? throw new InvalidOperationException("Missing tool glyph.");
    private static string Fingerprint(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));}
    private static bool ContainsColor(Imaging.BitmapSource bitmap,Media.Color color)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);for(int i=0;i<bytes.Length;i+=4)if(bytes[i+3]>250&&Math.Abs(bytes[i+2]-color.R)<4&&Math.Abs(bytes[i+1]-color.G)<4&&Math.Abs(bytes[i]-color.B)<4)return true;return false;}
    private static T Field<T>(object source,string name)=>(T)source.GetType().GetField(name,Private)!.GetValue(source)!;
    private static void Set(object source,string name,object? value)=>source.GetType().GetField(name,Private)!.SetValue(source,value);
    private static void Invoke(object source,string name,params object[] arguments)
    {
        try{source.GetType().GetMethod(name,Private)!.Invoke(source,arguments);}
        catch(TargetInvocationException ex)when(ex.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();}
    }
    private static void Measure(Wpf.FrameworkElement element)
    {element.Measure(new Wpf.Size(double.PositiveInfinity,double.PositiveInfinity));element.Arrange(new Wpf.Rect(new Wpf.Point(),element.DesiredSize));element.UpdateLayout();}
    private static void PumpToIdle(Threading.Dispatcher dispatcher)
    {
        if(dispatcher.HasShutdownStarted)return;
        var frame=new Threading.DispatcherFrame();dispatcher.BeginInvoke(Threading.DispatcherPriority.ContextIdle,()=>frame.Continue=false);Threading.Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        for(int index=0;index<Media.VisualTreeHelper.GetChildrenCount(root);index++)
        {var child=Media.VisualTreeHelper.GetChild(root,index);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static Wpf.Point[] Vertices(Media.Geometry geometry)=>geometry.GetFlattenedPathGeometry().Figures.SelectMany(figure=>new[]{figure.StartPoint}.Concat(figure.Segments.SelectMany(segment=>segment switch
    {Media.LineSegment line=>new[]{line.Point},Media.PolyLineSegment poly=>poly.Points.AsEnumerable(),_=>throw new InvalidOperationException("Expected a flattened line path.")}))).ToArray();
    private static Imaging.RenderTargetBitmap Save(Wpf.FrameworkElement element,string path,double scale)
    {
        var pixels=new Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,Media.PixelFormats.Pbgra32);pixels.Render(element);
        var png=new Imaging.PngBitmapEncoder();png.Frames.Add(Imaging.BitmapFrame.Create(pixels));using var output=File.Create(path);png.Save(output);
        return pixels;
    }
    private static Media.Color ColorAt(Imaging.BitmapSource bitmap,int x,int y)
    {byte[] pixel=new byte[4];bitmap.CopyPixels(new Wpf.Int32Rect(x,y,1,1),pixel,4,0);return Media.Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);}
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
}
