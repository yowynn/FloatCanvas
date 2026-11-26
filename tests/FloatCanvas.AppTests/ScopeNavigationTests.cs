using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class ScopeNavigationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OverlayContextMenu.CloseAll();
        string language = L.Preference;
        if (output is not null) Directory.CreateDirectory(output);
        try
        {
            L.Initialize("en"); AppTheme.Apply("light");
            Model(check);
            InvalidHistory(check);
            SharedDesktopHistory(check);
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            foreach (int width in new[] { 280, 340 })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                LayersAndHeader(check, output, locale, theme, width);
            }
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); }
    }

    private static void Model(Action<string, bool> check)
    {
        var history = new ScopeNavigationHistory(); var tree = new object();
        bool exists = true, allowed = true; int oldCalls = 0, newCalls = 0;
        var entries = Enumerable.Range(0, 6).Select(i => new ScopeNavigationEntry(i.ToString(), "Level " + i,
            i == 0 ? ScopeNavigation.RootGlyph : LayersWindow.Folder, () => oldCalls++,
            Exists: i == 4 ? () => exists : null, CanNavigate: () => allowed)).ToArray();
        var deepest = history.Update(tree, entries);
        check("history model records a five-level branch", deepest.CurrentIndex == 5 && deepest.Entries.Select(e => e.Key).SequenceEqual(entries.Select(e => e.Key)));
        var current = entries.Take(4).ToArray(); current[3] = current[3] with { Name = "Renamed current", Navigate = () => newCalls++ };
        var returned = history.Update(tree, current);
        returned.Entries[3].Navigate();
        check("history model going up retains the full future and refreshes current callbacks", returned.CurrentIndex == 3 && returned.Entries.Count == 6 &&
            returned.Entries[3].DisplayName == "Renamed current" && newCalls == 1 && oldCalls == 0 && deepest.CurrentIndex == 5 && deepest.Entries[3].Name == "Level 3");
        allowed = false; var disabled = history.Update(tree, current);
        check("temporary command guards retain history instead of pruning it", disabled.Entries.Count == 6 && !disabled.Entries[5].Available);
        allowed = true; exists = false;
        var pruned = history.Update(tree, current);
        check("history model stops at the first missing ancestor even when a later entry exists", pruned.CurrentIndex == 3 && pruned.Entries.Count == 4);
        exists = true; history.Update(tree, entries);
        var fork = history.Update(tree, current.Append(new("4-prime", "New fourth", LayersWindow.Folder, () => { })).ToArray());
        check("history model entering a sibling replaces the old suffix", fork.CurrentIndex == 4 && fork.Entries.Select(e => e.Key).SequenceEqual(new[] { "0", "1", "2", "3", "4-prime" }));
        var reset = history.Update(new object(), entries.Take(1).ToArray());
        check("history model changing the owning tree resets the explored branch", reset.CurrentIndex == 0 && reset.Entries.Count == 1);

        int modeCalls = 0; bool canToggle = true;
        var menu = ScopeNavigation.Menu(returned, L.Get("GroupCanvas.RootProject"), () => modeCalls++, () => canToggle);
        var modeItem = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "scope-mode"));
        canToggle = false; Click(modeItem);
        check("mode command rechecks a guard changed after menu creation", modeCalls == 0);
        canToggle = true; Click(modeItem);
        check("mode command invokes its callback once when allowed", modeCalls == 1);
    }

    private static void LayersAndHeader(Action<string, bool> check, string? output, string locale, string theme, int width)
    {
        string prefix = $"scope {locale}/{theme}/{width} ";
        var scene = new NestedScene(locale); var session = scene.Session;
        var layers = new LayersWindow(() => session);
        using var host = new GroupCanvasUiTests.NativeHost(layers, width, 540);
        int modeCalls = 0; var mode = GroupCanvasMode.RootProject;
        void Context() => layers.SetGroupContext(new(scene.Names[2], GroupStorageKind.Ordinary, null, mode, true,
            Temporary: () => { modeCalls++; mode = GroupCanvasMode.Temporary; }, Root: () => { modeCalls++; mode = GroupCanvasMode.RootProject; }));
        void Refresh() { Context(); layers.PrepareForShow(); host.Layout(); }
        Refresh();
        check(prefix + "shallow overflow is mounted but collapsed", Overflow(layers).Visibility == Visibility.Collapsed && Crumbs(layers).Length == 1);
        foreach (var id in scene.Ids) { layers.EnterGroup(id); Refresh(); }
        string authored = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        long revision = session.SceneRevision;
        check(prefix + "real group entry reaches five nested levels", session.EditScopeId == scene.Ids[4] && Path(layers).CurrentIndex == 5 && Crumbs(layers).Length == 6);
        Click(Crumbs(layers)[3]); Refresh();
        var branch = Path(layers); var menu = Menu(layers);
        check(prefix + "return to three retains the complete explored branch and selects the child left", session.EditScopeId == scene.Ids[2] &&
            session.Selection.SetEquals([scene.Ids[3]]) && branch.CurrentIndex == 3 && branch.Entries.Count == 6 && Crumbs(layers).Length == 6);
        VerifyMenu(check, prefix, menu, branch);

        // NativeHost is hidden; only the menu's own native surface opens. Exercise
        // both ordinary ownership and an installed overlay scope without OS input.
        foreach (bool overlay in new[] { false, true })
        {
            using var scope = overlay ? new OverlayWindowScope(host.Handle, () => true) : null;
            var current = Crumbs(layers)[branch.CurrentIndex];
            var actual = (OverlayContextMenu)current.ContextMenu!;
            try
            {
                Click(current); host.Settle();
                check(prefix + "current breadcrumb opens its full menu overlay=" + overlay, actual.IsOpen && actual.Items.OfType<MenuItem>().Count() == 7);
                Click(Entries(actual)[3]); host.Layout();
                check(prefix + "clicking current closes menu without navigating overlay=" + overlay, !actual.IsOpen && session.EditScopeId == scene.Ids[2] && session.Selection.SetEquals([scene.Ids[3]]));
            }
            finally { actual.IsOpen = false; }
        }

        var file = new BoundFileIndicator(() => { }, () => { }, () => { }, () => true, () => true);
        file.Refresh(System.IO.Path.Combine("B:\\", "Scope preview.floatcanvas"), true, true);
        var header = new RootCanvasToolbarHeader(file, () => { }, () => true) { Margin = new(0) };
        int toggles = 0;
        var description = Description(branch, L.Get("GroupCanvas.RootProject"), () => toggles++);
        header.SetScope(description, true); Layout(header, width, ToolbarUpperRow.ExtensionHeight);
        var headerMenu = (OverlayContextMenu)Field<Button>(header, "scopeButton").ContextMenu!;
        var labels = Descendants<TextBlock>(header).Select(t => t.Text).ToArray();
        check(prefix + "file strip shows only the active path and keeps future scopes in its menu", header.ActualHeight == ToolbarUpperRow.ExtensionHeight &&
            !labels.Any(t => t.Contains(scene.Names[3], StringComparison.Ordinal) || t.Contains(scene.Names[4], StringComparison.Ordinal)) &&
            Field<TextBlock>(header, "current").Text.Contains(scene.Names[2], StringComparison.Ordinal) &&
            Entries(headerMenu).Select(i => (string)i.Tag).SequenceEqual(branch.Entries.Select(e => e.Key)));
        check(prefix + "mode is a separate badge and never part of the current layer name", Field<TextBlock>(header,"current").Text == scene.Names[2] &&
            Field<TextBlock>(header,"modeLabel").Visibility == Visibility.Collapsed && Field<Border>(header,"modeBadge").ActualWidth <= 26 && Field<Border>(header,"modeBadge").Child is StackPanel &&
            Field<Border>(header,"modeBadge").BorderThickness == new Thickness(1));
        VerifyMenu(check, prefix + "file strip ", headerMenu, branch);
        VerifyMenu(check, prefix + "file overflow ", (OverlayContextMenu)Field<Button>(header, "overflow").ContextMenu!, branch);
        Click(headerMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "scope-mode")));
        check(prefix + "file menu mode command uses its owner callback", toggles == 1);

        if (output is not null)
        {
            host.Settle(); Save(host.Root, System.IO.Path.Combine(output, $"scope-layers-{locale}-{theme}-Narrow{width}.png"));
            // Render the menu into a standalone surface after its styles, templates,
            // and glyphs have been realized by the WPF dispatcher.
            SaveMenu(menu, System.IO.Path.Combine(output, $"scope-menu-{locale}-{theme}-Narrow{width}.png"), width);
            Layout(header, width, ToolbarUpperRow.ExtensionHeight); Settle();
            Save(header, System.IO.Path.Combine(output, $"scope-file-{locale}-{theme}-Narrow{width}.png"));
        }

        Click(Entries(Menu(layers))[5]); Refresh();
        check(prefix + "history menu can navigate forward directly to five", session.EditScopeId == scene.Ids[4] && Path(layers).CurrentIndex == 5);
        Click(Crumbs(layers)[3]); Refresh();
        Click(Menu(layers).Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "scope-mode"))); Refresh();
        check(prefix + "mode is a separate partition and switching preserves the branch", modeCalls == 1 && mode == GroupCanvasMode.Temporary &&
            Path(layers).CurrentIndex == 3 && Path(layers).Entries.Count == 6 && session.EditScopeId == scene.Ids[2]);
        VerifyMenu(check, prefix + "independent ", Menu(layers), Path(layers));
        layers.EnterGroup(scene.Alternate); Refresh();
        var replaced = Path(layers);
        check(prefix + "entering a new fourth removes old four and five from breadcrumbs and menu", session.EditScopeId == scene.Alternate && replaced.CurrentIndex == 4 &&
            replaced.Entries.Count == 5 && replaced.Entries[^1].LocalScope == scene.Alternate &&
            !Entries(Menu(layers)).Any(i => Equals(i.Tag, scene.Ids[3].ToString()) || Equals(i.Tag, scene.Ids[4].ToString())) && Crumbs(layers).Length == 5);
        check(prefix + "navigation and mode leave author content transforms and undo untouched", JsonSerializer.Serialize(session.Document) == authored && session.UndoCount == undo && session.SceneRevision == revision);
    }

    private static void InvalidHistory(Action<string, bool> check)
    {
        foreach (string change in new[] { "root", "document", "session", "delete", "reparent" })
        {
            var scene = new NestedScene("en"); var session = scene.Session;
            var layers = new LayersWindow(() => session);
            session.SetEditScope(scene.Ids[4]); layers.PrepareForShow();
            session.SetEditScope(scene.Ids[2]); layers.PrepareForShow();
            var oldEntry = Path(layers).Entries[4]; var stale = Entries(Menu(layers))[4];
            switch (change)
            {
                case "root": session.SetRootCanvas(RootCanvasKind.Foreground); break;
                case "document": session.ReplaceDocument(session.Document.Clone()); session.SetEditScope(scene.Ids[2]); break;
                case "session": session = new(session.Document.Clone()); session.SetEditScope(scene.Ids[2]); break;
                case "delete": session.SetSelection([scene.Ids[3]]); session.DeleteSelection(); break;
                case "reparent": session.MoveNodes([scene.Ids[3]], scene.Ids[1]); break;
            }
            var scope = session.EditScopeId; int undo = session.UndoCount; string authored = JsonSerializer.Serialize(session.Document);
            Click(stale);
            check("stale " + change + " menu callback rechecks its captured address", !oldEntry.Available && session.EditScopeId == scope && session.UndoCount == undo && JsonSerializer.Serialize(session.Document) == authored);
            layers.PrepareForShow(); var path = Path(layers);
            check("history prunes expired " + change + " suffix", path.Entries.Count == (change == "root" ? 1 : 4) && path.CurrentIndex == (change == "root" ? 0 : 3));
            if (change == "root")
            {
                check("foreground history uses its root glyph", path.Entries[0].Glyph == ScopeNavigation.RootGlyphFor(RootCanvasKind.Foreground) && path.Entries[0].Glyph != ScopeNavigation.RootGlyph);
                session.SetRootCanvas(RootCanvasKind.Background); layers.PrepareForShow();
                check("background history uses its own root glyph", Path(layers).Entries[0].Glyph == ScopeNavigation.RootGlyphFor(RootCanvasKind.Background) &&
                    Path(layers).Entries[0].Glyph != ScopeNavigation.RootGlyphFor(RootCanvasKind.Foreground));
                session.SetRootCanvas(RootCanvasKind.Main); layers.PrepareForShow();
                check("returning to the old root does not resurrect expired history", Path(layers).Entries.Count == 1 && Path(layers).Entries[0].Glyph == ScopeNavigation.RootGlyph);
            }
        }

        var guarded = new NestedScene("en"); bool allowed = true;
        var window = new LayersWindow(() => guarded.Session, () => allowed);
        guarded.Session.SetEditScope(guarded.Ids[4]); window.PrepareForShow();
        guarded.Session.SetEditScope(guarded.Ids[2]); window.PrepareForShow();
        var future = Entries(Menu(window))[5]; allowed = false; Click(future);
        check("stale enabled history command honors the live mutation guard", guarded.Session.EditScopeId == guarded.Ids[2] && guarded.Session.UndoCount == 0);
        allowed = true; guarded.Session.SetNodeLocked(guarded.Ids[3], true); window.PrepareForShow();
        check("locked future remains available for read-only browsing", Path(window).Entries.Count == 6 && Entries(Menu(window))[5].IsEnabled);
        guarded.Session.SetNodeLocked(guarded.Ids[3], false); window.PrepareForShow();
        check("unlocking restores forward navigation without losing history", Entries(Menu(window))[5].IsEnabled && Path(window).Entries.Count == 6);
    }

    private static void SharedDesktopHistory(Action<string, bool> check)
    {
        var scene = new NestedScene("en"); var owner = scene.Session;
        var inner = new CanvasGroup { Name = "Shared inner", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var outer = new CanvasGroup { Name = "Shared outer", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [inner.Id] }; inner.ParentId = outer.Id;
        var definition = new PrefabDefinition { Name = "Shared definition", Graph = new() { Groups = [outer, inner], RootOrder = [outer.Id] } };
        new SceneGraph(definition.Graph).InsertElement(new() { Kind = ElementKind.Rectangle, Width = 8, Height = 8 }, inner.Id, worldCoordinates: false);
        var instance = new PrefabInstance { Name = "Shared boundary", DefinitionId = definition.Id, ParentId = scene.Ids[2] };
        owner.Document.Definitions.Add(definition); new SceneGraph(owner.Document).InsertInstance(instance, scene.Ids[2]);
        using var fixture = new DesktopFixture(owner);
        owner.SetEditScope(scene.Ids[4]); fixture.Refresh();
        var history = Field<ScopeNavigationHistory>(fixture.Desktop, "_scopeNavigationHistory");
        owner.SetEditScope(scene.Ids[2]); fixture.Refresh();
        var description = fixture.Describe(); var path = description.Navigation!;
        check("desktop owner retains one complete history across scope refreshes", ReferenceEquals(history, Field<ScopeNavigationHistory>(fixture.Desktop, "_scopeNavigationHistory")) &&
            Path(fixture.Layers).Entries.Select(e => e.Key).SequenceEqual(path.Entries.Select(e => e.Key)) && Path(fixture.Layers).CurrentIndex == 3 && path.Entries.Count == 6);
        // Refresh once supplies exactly one description to both production surfaces.
        fixture.Refresh();
        var layerPath = Path(fixture.Layers);
        var headerMenu = (OverlayContextMenu)Field<Button>(fixture.Header, "scopeButton").ContextMenu!;
        check("desktop refresh supplies the same branch to the file strip and layer menu", Entries(headerMenu).Select(i => (string)i.Tag).SequenceEqual(layerPath.Entries.Select(e => e.Key)) &&
            Entries(headerMenu).Single(i => i.InputGestureText.StartsWith("✓", StringComparison.Ordinal)).Tag!.Equals(layerPath.Entries[3].Key));
        check("layers receive the owner's actual navigation path", ReferenceEquals(layerPath, Field<EditingScopeDescription>(fixture.Layers, "navigationDescription").Navigation));
        var futureItem = Entries(headerMenu)[5];
        string authored = JsonSerializer.Serialize(owner.Document); int undo = owner.UndoCount;
        // Set only the busy backing value: the property setter also refreshes
        // unrelated runtime services which this description-only fixture omits.
        Set(fixture.Desktop, "_documentBusyValue", true);
        try
        {
            Click(futureItem);
            Click(headerMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "scope-mode")));
            check("real owner history and mode callbacks recheck the live busy guard", !path.Entries[5].Available &&
                description.CanToggleMode?.Invoke() == false && owner.EditScopeId == scene.Ids[2] &&
                owner.UndoCount == undo && JsonSerializer.Serialize(owner.Document) == authored && Field<GroupCanvasMode>(fixture.Desktop, "_groupCanvasMode") == GroupCanvasMode.RootProject);
        }
        finally { Set(fixture.Desktop, "_documentBusyValue", false); }
        owner.SetNodeLocked(scene.Ids[3], true); fixture.Refresh();
        check("real owner permits locked future browsing without erasing history", Path(fixture.Layers).Entries[5].Available && Path(fixture.Layers).Entries.Count == 6);
        owner.SetNodeLocked(scene.Ids[3], false); fixture.Refresh();
        Set(fixture.Desktop, "_groupCanvasMode", GroupCanvasMode.Temporary); fixture.Refresh();
        check("desktop mode change shares history instead of changing its navigation root", Path(fixture.Layers).Entries.Count == 6 && Path(fixture.Layers).CurrentIndex == 3);

        var draft = owner.BeginPrefabEdit(definition.Id);
        var context = new PrefabEditContext(owner, draft, default, EditorTool.Select) { EnteredInstanceId = instance.Id };
        fixture.Enter(context); draft.Session.SetEditScope(inner.Id); fixture.Refresh();
        var shared = Path(fixture.Layers);
        check("desktop production addresses span owner groups shared boundary and local groups", shared.CurrentIndex == 6 && shared.Entries.Count == 7 &&
            shared.Entries[4].DisplayName == instance.Name && shared.Entries[4].Glyph == LayersWindow.Instance && !shared.Entries[3].Local &&
            shared.Entries[4].Local && shared.Entries[^1].LocalScope == inner.Id && shared.Entries.All(e => e.Available));
        var sharedMenu = (OverlayContextMenu)Field<Button>(fixture.Header, "scopeButton").ContextMenu!;
        check("file strip and layers agree on the complete shared boundary path", Entries(sharedMenu).Select(i => (string)i.Tag).SequenceEqual(shared.Entries.Select(e => e.Key)) &&
            Entries(Menu(fixture.Layers)).Select(i => (string)i.Tag).SequenceEqual(shared.Entries.Select(e => e.Key)));
        fixture.Leave(); fixture.Refresh();
        var returned = Path(fixture.Layers); var staleBoundary = returned.Entries[4];
        check("leaving a shared graph retains forward addresses without treating them as local drop targets", returned.CurrentIndex == 3 && returned.Entries.Count == 7 &&
            returned.Entries.Skip(4).All(e => e.Available && !e.Local) && Crumbs(fixture.Layers).Skip(4).All(b => b.Tag is null));
        owner.SetNodeLocked(instance.Id, true); fixture.Refresh();
        check("locked shared boundary keeps forward descendants browsable", Path(fixture.Layers).Entries.Count == 7 && Path(fixture.Layers).Entries.Skip(4).All(e => e.Available));
        owner.SetNodeLocked(instance.Id, false); fixture.Refresh();
        owner.MoveNodes([instance.Id], scene.Ids[1]); fixture.Refresh();
        check("reparented shared boundary expires its entire old descendant history", !staleBoundary.Available && Path(fixture.Layers).Entries.Count == 4);
        owner.MoveNodes([instance.Id], scene.Ids[2]);
        fixture.Enter(context); fixture.Refresh(); fixture.Leave(); fixture.Refresh();
        var deletedBoundary = Path(fixture.Layers).Entries[4];
        owner.SetSelection([instance.Id]); owner.DeleteSelection(); fixture.Refresh();
        check("deleted shared instance invalidates addresses even while its definition exists", owner.Document.Definitions.Any(d => d.Id == definition.Id) && !deletedBoundary.Available && Path(fixture.Layers).Entries.Count == 4);
    }

    private static void VerifyMenu(Action<string, bool> check, string prefix, OverlayContextMenu menu, ScopeNavigationPath path)
    {
        var items = Entries(menu); var current = items[path.CurrentIndex];
        check(prefix + "menu lists history ancestors and current in branch order", items.Select(i => (string)i.Tag).SequenceEqual(path.Entries.Select(e => e.Key)));
        check(prefix + "current has a check label and retains its real icon", current.InputGestureText.StartsWith("✓", StringComparison.Ordinal) &&
            AutomationProperties.GetItemStatus(current) == L.Get("ScopeNavigation.Current") && current.IsEnabled &&
            current.Icon is DependencyObject icon && Descendants<System.Windows.Shapes.Path>(icon).Any(p => p.Data is not null));
        check(prefix + "every entry keeps its icon and depth badge", items.Select((item, depth) => item.Icon is DependencyObject itemIcon &&
            Descendants<System.Windows.Shapes.Path>(itemIcon).Any(p => p.Data is not null) &&
            (depth == 0 || Descendants<TextBlock>(itemIcon).Any(t => t.Text == depth.ToString()))).All(value => value));
        check(prefix + "future entries have a distinct history style", items.Skip(path.CurrentIndex + 1).All(i =>
            i.Header is TextBlock { FontStyle: var style } && style == FontStyles.Italic && i.InputGestureText.StartsWith("↳", StringComparison.Ordinal) &&
            AutomationProperties.GetItemStatus(i) == L.Get("ScopeNavigation.History")) && current.Header is TextBlock { FontWeight: var weight } && weight == FontWeights.SemiBold);
        int modeIndex = menu.Items.IndexOf(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "scope-mode")));
        var mode = (MenuItem)menu.Items[modeIndex];
        check(prefix + "mode switch is first with a separator before navigation", modeIndex == 0 && menu.Items[1] is Separator && menu.Items.Count == items.Length + 2 &&
            mode.Header is string text && !text.Contains('\n') && mode.Icon is FrameworkElement);
        menu.ApplyTemplate(); menu.Measure(new Size(500,double.PositiveInfinity)); Layout(menu,500,menu.DesiredSize.Height); Settle();
        check(prefix + "depth badges preserve icon and label vertical centers", items.All(item =>
        {
            var icon = (Grid)item.Icon; var artwork = (FrameworkElement)icon.Children[0]; var label = (FrameworkElement)item.Header;
            return icon.ActualWidth == 18 && Math.Abs(artwork.TranslatePoint(new(artwork.ActualWidth/2,artwork.ActualHeight/2),item).Y -
                label.TranslatePoint(new(label.ActualWidth/2,label.ActualHeight/2),item).Y) < 1;
        }));
        var modeArtwork = (FrameworkElement)mode.Icon; var title = (ContentPresenter)mode.Template.FindName("Header",mode);
        check(prefix + "mode action uses the complete row instead of reserving the navigation status column", mode.Template.FindName("Header",mode) is ContentPresenter presenter &&
            Grid.GetColumnSpan(presenter) == 3 && mode.ActualHeight <= items[0].ActualHeight + 1);
        check(prefix + "mode icon aligns with its action title", Math.Abs(modeArtwork.TranslatePoint(new(modeArtwork.ActualWidth/2,modeArtwork.ActualHeight/2),mode).Y -
            title.TranslatePoint(new(title.ActualWidth/2,title.ActualHeight/2),mode).Y) < 1.5);
    }

    private sealed class NestedScene
    {
        internal EditorSession Session { get; }
        internal Guid[] Ids { get; }
        internal string[] Names { get; }
        internal Guid Alternate { get; }
        internal NestedScene(string locale)
        {
            var document = new SceneDocument(); var graph = new SceneGraph(document); Guid? parent = null;
            Names = Enumerable.Range(1, 5).Select(n => locale == "zh-CN" ? "嵌套画布第" + n + "层" : "Nested canvas level " + n).ToArray();
            Ids = Names.Select((name, n) =>
            {
                var group = graph.InsertGroup(new() { Name = name, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [],
                    Transform = Affine2D.Translation(n * 7, n * 3).Compose(Affine2D.Rotation(n * 4)) }, parent);
                parent = group.Id; return group.Id;
            }).ToArray();
            Alternate = graph.InsertGroup(new() { Name = locale == "zh-CN" ? "新的第四层" : "New level four", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }, Ids[2]).Id;
            graph.InsertElement(new() { Kind = ElementKind.Rectangle, Width = 8, Height = 8 }, Ids[^1], worldCoordinates: false);
            graph.InsertElement(new() { Kind = ElementKind.Rectangle, Width = 8, Height = 8 }, Alternate, worldCoordinates: false);
            Session = new(document);
        }
    }

    // Use the production description/address/refresh methods with real sessions and
    // controls. Constructor-only rendering, tray, profile, and hotkey services are
    // outside this fixture; it never invokes navigation/publishing runtime commands.
    private sealed class DesktopFixture : IDisposable
    {
        internal DesktopSession Desktop { get; } = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        internal EditorController Editor { get; }
        internal LayersWindow Layers { get; }
        internal RootCanvasToolbarHeader Header { get; }
        private readonly EditorSession owner;
        internal DesktopFixture(EditorSession owner)
        {
            this.owner = owner; Editor = new(owner); Set(Desktop, "_editor", Editor);
            Set(Desktop,"_groupCanvasMode",GroupCanvasMode.RootProject);
            Layers = new(() => Editor.Session); Set(Desktop, "_layers", Layers);
            var file = new BoundFileIndicator(() => { }, () => { }, () => { }, () => true, () => true);
            Header = new(file, () => { }, () => true); Set(Desktop, "_rootCanvasHeader", Header);
        }
        internal EditingScopeDescription Describe() => (EditingScopeDescription)Call(Desktop, "DescribeEditingScope")!;
        internal void Refresh() { Layers.PrepareForShow(); Call(Desktop, "RefreshEditingScope"); }
        internal void Enter(PrefabEditContext context) { Set(Desktop, "_prefabEditContext", context); Editor.ReplaceSession(context.Session, default); }
        internal void Leave() { Set(Desktop, "_prefabEditContext", null); Editor.ReplaceSession(owner, default); }
        public void Dispose() { Editor.DetachSession(); Layers.Close(); }
    }

    private static EditingScopeDescription Description(ScopeNavigationPath path, string mode, Action toggle) => new(
        string.Join(" › ", path.Entries.Take(path.CurrentIndex + 1).Select(e => e.DisplayName)), mode, "",
        path.Entries.Take(path.CurrentIndex).Select(e => (e.DisplayName, e.Navigate)).ToArray(), path.Entries[path.CurrentIndex].DisplayName,
        path, toggle, () => true);
    private static ScopeNavigationPath Path(LayersWindow window) => Field<ScopeNavigationPath>(window, "navigationPath");
    private static Button[] Crumbs(LayersWindow window) => Field<StackPanel>(window, "breadcrumbs").Children.OfType<Button>().Where(b => !Equals(b.Tag, "overflow")).ToArray();
    private static Button Overflow(LayersWindow window) => Field<StackPanel>(window, "breadcrumbs").Children.OfType<Button>().Single(b => Equals(b.Tag, "overflow"));
    private static OverlayContextMenu Menu(LayersWindow window) => (OverlayContextMenu)Overflow(window).ContextMenu!;
    private static MenuItem[] Entries(OverlayContextMenu menu) => menu.Items.OfType<MenuItem>().Where(i => !Equals(i.Tag, "scope-mode")).ToArray();
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
    private static object? Call(object owner, string name) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, null);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Settle()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void SaveMenu(OverlayContextMenu menu, string output, int width)
    {
        // This menu has not been opened; render it without stealing its placement
        // target or creating a second parent for any existing menu item.
        menu.ApplyTemplate(); menu.Measure(new Size(width, double.PositiveInfinity));
        Layout(menu, width, menu.DesiredSize.Height); Settle(); Save(menu, output);
    }
    private static void Save(FrameworkElement root, string output)
    {
        Settle(); root.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(output); encoder.Save(stream);
    }
}
