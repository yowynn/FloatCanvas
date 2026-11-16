using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class GroupCanvasUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                foreach (int width in new[] { 280, 340 }) Verify(check, output, locale, theme, width);
                ResourceSources(check, locale + " " + theme);
                ReadOnly(check, locale + " " + theme);
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static void Verify(Action<string, bool> check, string? output, string locale, string theme, int width)
    {
        string prefix = $"{locale} {theme} {width} ";
        var element = new CanvasElement { Name = "Inside group", Kind = ElementKind.Rectangle, Width = 40, Height = 30 };
        var group = new CanvasGroup { Name = "Stamp A", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [element.Id] };
        element.GroupId = group.Id;
        var session = new EditorSession(new() { Elements = [element], Groups = [group], RootOrder = [group.Id] });
        session.SetEditScope(group.Id);
        var layers = new LayersWindow(() => session);
        using var host = new NativeHost(layers, width, 540);
        layers.PrepareForShow(); host.Layout();
        var site = Field<Border>(layers, "groupContextSite");
        var choice = Field<CanvasModeSwitch>(layers, "groupCanvasChoice");
        var export = Field<Button>(layers, "groupExport"); var detach = Field<Button>(layers, "groupDetach");
        check(prefix + "missing owner context hides the operation area", site.Visibility == Visibility.Collapsed && layers.CurrentGroupContext is null);
        int first = 0, replacement = 0, temporaryCalls = 0, rootCalls = 0;
        layers.SetGroupContext(new("Foreground", GroupStorageKind.Ordinary, null, GroupCanvasMode.RootProject, false,
            Temporary: () => temporaryCalls++, Root: () => rootCalls++, IsRootContext: true)); host.Layout();
        check(prefix + "root level keeps the view selector without exposing group storage or operations", site.Visibility == Visibility.Visible &&
            choice.Mode is GroupCanvasMode.RootProject && Field<TextBlock>(layers, "groupStorage").Visibility == Visibility.Collapsed &&
            export.Visibility == Visibility.Collapsed && detach.Visibility == Visibility.Collapsed && choice.IsEnabled);
        layers.SetGroupContext(null);
        var ordinary = new GroupCanvasContext("Stamp A", GroupStorageKind.Ordinary, null, GroupCanvasMode.Temporary, true, Export: () => first++);
        layers.SetGroupContext(ordinary with { IsEntered = false, Temporary = () => temporaryCalls++ });
        check(prefix + "a selected but unentered group has no operation card or exposed callbacks", site.Visibility == Visibility.Collapsed && layers.CurrentGroupContext is null && !choice.IsEnabled && !choice.IsEnabled);
        choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        export.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + "hidden unentered card cannot activate entry or group operations", temporaryCalls == 0 && first == 0 && layers.CurrentGroupContext is null && !choice.IsEnabled);
        temporaryCalls = 0;
        layers.SetGroupContext(ordinary); host.Layout();
        check(prefix + "ordinary group exposes export and identifies its storage", site.Visibility == Visibility.Visible && export.Visibility == Visibility.Visible && export.IsEnabled && detach.Visibility == Visibility.Collapsed && Field<TextBlock>(layers, "groupStorage").Text == L.Get("GroupCanvas.Ordinary"));
        export.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        layers.SetGroupContext(ordinary with { Export = () => replacement++ }); export.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + "same display state replaces operation callbacks", first == 1 && replacement == 1);
        string? observedName = null;
        layers.BeginRename(element.Id); layers.FindNode(element.Id)!.DraftName = "Edited before export";
        layers.SetGroupContext(ordinary with { Export = () => observedName = session.Document.Elements.Single().Name });
        export.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + "group operations commit an active layer rename before invoking callbacks", !layers.IsRenaming && observedName == "Edited before export");
        choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + "unavailable radio target cannot change the accepted mode", Equals(choice.Mode, GroupCanvasMode.Temporary) && !choice.IsEnabled);
        layers.SetGroupContext(ordinary with { Root = () => rootCalls++ });
        choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + "cancelled or pending canvas switch restores the authoritative mode", rootCalls == 1 && Equals(choice.Mode, GroupCanvasMode.Temporary) && layers.CurrentGroupContext?.Mode == GroupCanvasMode.Temporary);
        int history = session.UndoCount; var children = layers.VisibleNodes.Select(row => row.Id).ToArray();
        GroupCanvasContext? accepted = null;
        Action root = () => { rootCalls++; accepted = accepted! with { Mode = GroupCanvasMode.RootProject }; layers.SetGroupContext(accepted); };
        Action temporary = () => { temporaryCalls++; accepted = accepted! with { Mode = GroupCanvasMode.Temporary }; layers.SetGroupContext(accepted); };
        accepted = ordinary with { Root = root, Temporary = temporary };
        layers.SetGroupContext(accepted); choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
        check(prefix + "accepted root canvas switch preserves the entered child list and history", rootCalls == 2 && Equals(choice.Mode, GroupCanvasMode.RootProject) && session.EditScopeId == group.Id && session.UndoCount == history && children.SequenceEqual(layers.VisibleNodes.Select(row => row.Id)));
        check(prefix + "root mode explains full preview and current list scope", Field<TextBlock>(layers, "groupCanvasNotice").Text == L.Get("GroupCanvas.RootNotice"));
        choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + "temporary canvas switch uses current callback", temporaryCalls == 1 && Equals(choice.Mode, GroupCanvasMode.Temporary));

        foreach (var storage in Enum.GetValues<GroupStorageKind>())
        {
            string source = "C:/A long folder name/Shared art/Linked source with a long Unicode name 印章 ABCDEFGHIJKLMNOPQRSTUVWXYZ/Another nested directory/source.floatcanvas";
            int detached = 0;
            layers.SetEditContext(storage == GroupStorageKind.ExternalFile ? "Linked stamp" : null, storage == GroupStorageKind.ExternalFile ? () => { } : null, storage == GroupStorageKind.ExternalFile ? () => { } : null, storage == GroupStorageKind.ExternalFile);
            accepted = new("Stamp A 印章A with a longer descriptive name", storage, source, GroupCanvasMode.RootProject, true, temporary, root, () => replacement++, () => detached++);
            layers.SetGroupContext(accepted); host.Layout();
            check(prefix + storage + " correct group operations are visible", export.Visibility == (storage == GroupStorageKind.ExternalFile ? Visibility.Collapsed : Visibility.Visible) && detach.Visibility == (storage == GroupStorageKind.Ordinary ? Visibility.Collapsed : Visibility.Visible));
            var sourceViewport = Field<ScrollViewer>(layers, "groupSourceViewport"); var sourceText = Field<TextBlock>(layers, "groupSource");
            check(prefix + storage + " external path is available in compact status tooltip", sourceViewport.Visibility == Visibility.Collapsed &&
                (storage != GroupStorageKind.ExternalFile || sourceText.Text == source && Field<TextBlock>(layers, "groupStorage").ToolTip.ToString()!.Contains(source)));
            check(prefix + storage + " shared navigation has no separate complete or cancel controls", Field<WrapPanel>(layers, "editContext").Children.Count == 0);
            check(prefix + storage + " auxiliary canvases stay in a closed icon menu", Field<Button>(layers, "rootCanvasMenu").ContextMenu?.IsOpen == false);
            if (storage != GroupStorageKind.Ordinary) { detach.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); check(prefix + storage + " detach invokes the current operation once", detached == 1); }
            var controls = new FrameworkElement[] { site, choice, export, detach }.Where(control => control.Visibility == Visibility.Visible);
            check(prefix + storage + " group controls fit the narrow native viewport", controls.All(control => Within(control, host.Root)) && choice.ActualHeight >= 26 && Bounds(Field<ListBox>(layers, "layers"), host.Root).Height > 90);
            check(prefix + storage + " no thumbnail or separate preview button remains", !Descendants<Image>(site).Any() && !Descendants<Button>(site).Any(button => button.Content?.ToString() == L.Get("Prefab.PreviewAll")));
            if (output is not null)
            {
                Directory.CreateDirectory(output); host.Settle(); Save(host.Root, Path.Combine(output, $"group-context-{storage}-{locale}-{theme}-{width}.png"));
            }
        }
        layers.SetGroupContext(null); host.Layout();
        check(prefix + "leaving group context clears owner callbacks and hides controls", layers.CurrentGroupContext is null && site.Visibility == Visibility.Collapsed);
        check(prefix + "UI verification stays hidden without capture services", !layers.IsVisible && !IsWindowVisible(host.Handle));
        SessionWindowCoordinator.Close(layers);
    }

    private static void ResourceSources(Action<string, bool> check, string prefix)
    {
        var absolute = new PrefabDefinition { Name = "Absolute", Graph = new(), Source = Source("C:/Art/source.floatprefab", false) };
        var relative = new PrefabDefinition { Name = "Relative", Graph = new(), Source = Source("assets/source.floatcanvas", true) };
        var embedded = new PrefabDefinition { Name = "Embedded", Graph = new() };
        var owner = new EditorSession(new() { Definitions = [absolute, relative, embedded] });
        var resources = new PrefabResourcesWindow(() => owner); resources.Refresh();
        check(prefix + " resource summary preserves complete absolute source", resources.Rows.Single(row => row.Id == absolute.Id).Summary.Contains(L.Format("GroupCanvas.AbsoluteSource", absolute.Source!.Locator), StringComparison.Ordinal));
        check(prefix + " resource summary labels a locator relative to its project", resources.Rows.Single(row => row.Id == relative.Id).Summary.Contains(L.Format("GroupCanvas.RelativeSource", relative.Source!.Locator), StringComparison.Ordinal));
        check(prefix + " embedded resource summary does not invent an external path", !resources.Rows.Single(row => row.Id == embedded.Id).Linked && !resources.Rows.Single(row => row.Id == embedded.Id).Summary.Contains("source", StringComparison.Ordinal));
        SessionWindowCoordinator.Close(resources);
    }
    private static void ReadOnly(Action<string, bool> check, string prefix)
    {
        var element = new CanvasElement { Name = "Cached child", Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
        var group = new CanvasGroup { Name = "Cached group", Locked = true, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [element.Id] }; element.GroupId = group.Id;
        var session = new EditorSession(new() { Elements = [element], Groups = [group], RootOrder = [group.Id] }, readOnly: true);
        var window = new LayersWindow(() => session); using var host = new NativeHost(window, 280, 540); window.PrepareForShow(); host.Layout();
        var list = Field<ListBox>(window, "layers"); list.SelectedItem = window.FindNode(group.Id); host.Layout();
        var row = LayerTreeRowPresenter.Find(window.Container(group.Id))!;
        check(prefix + " offline snapshot still populates and selects its real group list", window.VisibleNodes.Single().Id == group.Id && session.Selection.SetEquals([group.Id]));
        check(prefix + " offline snapshot disables row mutation controls without an empty-group action", !row.Handle.IsEnabled && !row.VisibleButton.IsEnabled && !row.LockedButton.IsEnabled && !window.BeginRename(group.Id) && !window.BeginDrag(group.Id));
        row.EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.PrepareForShow(); host.Layout(); list.SelectedItem = window.FindNode(element.Id);
        window.SetLayerFlag(element.Id, true, false);
        check(prefix + " offline snapshot permits locked-group navigation and selection while mutations keep history unchanged", session.EditScopeId == group.Id && session.Selection.SetEquals([element.Id]) && window.VisibleNodes.Single().Id == element.Id && element.Visible && session.Document.Groups.Count == 1 && session.UndoCount == 0);
        int copied = 0, deleted = 0; window.CommandKey += key => { if (key == 0x43) copied++; if (key == 0x2e) deleted++; };
        var route = typeof(LayersWindow).GetMethod("RouteCommandKey", Hidden)!;
        route.Invoke(window, [0x43, System.Windows.Input.ModifierKeys.Control]); route.Invoke(window, [0x2e, System.Windows.Input.ModifierKeys.None]);
        check(prefix + " offline snapshot routes Copy but blocks data editing shortcuts", copied == 1 && deleted == 0 && session.Document.Elements.Count == 1 && session.UndoCount == 0);
        int detached = 0; window.SetGroupContext(new("Cached source", GroupStorageKind.ExternalFile, "C:/unavailable/source.floatcanvas", GroupCanvasMode.Temporary, true, Detach: () => detached++, IsReadOnly: true, IsOfflineSnapshot: true)); host.Layout();
        Field<Button>(window, "groupDetach").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(prefix + " offline snapshot explains its read-only state and keeps explicit detach available", Field<TextBlock>(window, "groupCanvasNotice").Text == L.Get("GroupCanvas.ReadOnlyNotice") && Field<Button>(window, "groupDetach").IsEnabled && detached == 1);
        SessionWindowCoordinator.Close(window);
    }
    private static ExternalPrefabSource Source(string locator, bool relative) => new() { Locator = locator, IsRelative = relative, SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.ProjectContent, LastAcceptedFileSha256 = new string('a', 64), LastAcceptedContentRevision = Guid.NewGuid() };
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static Rect Bounds(FrameworkElement control, FrameworkElement root) => control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
    private static bool Within(FrameworkElement control, FrameworkElement root) { var bounds = Bounds(control, root); return bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.1 && bounds.Top >= -.1 && bounds.Right <= root.ActualWidth + .1 && bounds.Bottom <= root.ActualHeight + .1; }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var item in Descendants<T>(child)) yield return item; } }
    private static void Save(FrameworkElement root, string path) { var dpi = VisualTreeHelper.GetDpi(root); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
    internal sealed class NativeHost : IDisposable
    {
        private readonly LayersWindow window; private readonly FrameworkElement content; private readonly HwndSource source; private readonly int width, height;
        internal Border Root { get; } internal nint Handle => source.Handle;
        internal NativeHost(LayersWindow window, int width, int height)
        {
            this.window = window; this.width = width; this.height = height; content = (FrameworkElement)window.Content; window.Content = null;
            var surface = new Grid(); surface.RowDefinitions.Add(new() { Height = new GridLength(AppWindowChrome.CaptionHeight) });
            surface.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            var headerActions = (FrameworkElement)window.Resources["CanvasWindowActions"];
            headerActions.HorizontalAlignment = HorizontalAlignment.Right; surface.Children.Add(headerActions);
            Grid.SetRow(content, 1); surface.Children.Add(content);
            Root = new Border { Width = width, Height = height, Child = surface }; Root.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); AppTheme.UsePopupPalette(Root);
            Root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
            source = new(new HwndSourceParameters("FloatCanvas group operation UI") { Width = width, Height = height, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 }); source.RootVisual = Root;
            var dpi = VisualTreeHelper.GetDpi(Root); SetWindowPos(source.Handle, 0, 0, 0, (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), 0x16); Layout();
        }
        internal void Layout() { Root.Measure(new Size(width, height)); Root.Arrange(new Rect(0, 0, width, height)); Root.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        internal void Settle()
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) => frame.Continue = false; timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Layout();
        }
        public void Dispose() { ((Grid)Root.Child).Children.Clear(); Root.Child = null; source.RootVisual = null; source.Dispose(); window.Content = content; }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
