using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;

internal static class LayersWindowTests
{
    public static void Run(Action<string, bool> assert)
    {
        bool allowed = true;
        var session = new EditorSession();
        CanvasElement a = new() { Kind = ElementKind.Rectangle, Width = 40, Height = 40 },
            b = new() { Kind = ElementKind.Ellipse, Width = 40, Height = 40 },
            c = new() { Kind = ElementKind.Triangle, Width = 40, Height = 40 };
        session.Add(a); session.Add(b); session.Add(c); session.SetSelection([a.Id]);
        var window = new LayersWindow(() => session, () => allowed);
        window.PrepareForShow();
        var list = (Controls.ListBox)Field(window, "layers");
        Controls.ListBoxItem Item(Guid id) => list.Items.Cast<Controls.ListBoxItem>().Single(item => (Guid)item.Tag == id);
        void Click(string key)
        {
            var menu = Item(session.Selection.First()).ContextMenu;
            menu.Items.OfType<Controls.MenuItem>().Single(item => Equals(item.Header, L.Get(key))).RaiseEvent(new Wpf.RoutedEventArgs(Controls.MenuItem.ClickEvent));
            window.PrepareForShow();
        }
        int before = session.UndoCount;
        allowed = false;
        foreach (var key in new[] { "Inspector.BringToFront", "Inspector.SendToBack", "Inspector.Duplicate", "Inspector.Delete", "Layers.Group", "Layers.Ungroup" }) Click(key);
        assert("busy guard covers every layer context command", session.UndoCount == before && session.Document.Elements.Select(e => e.Id).SequenceEqual([a.Id, b.Id, c.Id]));
        Invoke(window, "ReorderLayers", new Guid[] { a.Id }, c.Id, true);
        assert("busy guard blocks drag ordering", session.Document.Elements[0].Id == a.Id && session.UndoCount == before);
        allowed = true;
        Click("Inspector.BringToFront"); assert("layer front context command records one atomic step", session.Document.Elements[^1].Id == a.Id && session.UndoCount == ++before);
        Click("Inspector.SendToBack"); assert("layer back context command records one atomic step", session.Document.Elements[0].Id == a.Id && session.UndoCount == ++before);
        Click("Inspector.Duplicate"); var clone = session.SelectedElements().Single();
        assert("layer duplicate remains independently editable and records one step", clone.Id != a.Id && clone.Transform.OffsetX == 16 && session.Document.Elements.Count == 4 && session.UndoCount == ++before);
        Click("Inspector.Delete"); assert("layer delete context command records one step", session.Document.Elements.Count == 3 && session.Selection.Count == 0 && session.UndoCount == ++before);
        session.Undo(); window.PrepareForShow();
        assert("layer delete undo restores selection", session.Selection.Contains(clone.Id) && session.Document.Elements.Count == 4);
        session.SetSelection([a.Id, b.Id]); window.PrepareForShow();
        before = session.UndoCount; Click("Layers.Group");
        assert("layer grouping stores editable members in one history step", session.Document.Groups.Count == 1 && session.Document.Elements.Where(e => e.Id == a.Id || e.Id == b.Id).All(e => e.GroupId is not null) && session.UndoCount == before + 1);
        Click("Layers.Ungroup");
        assert("layer ungrouping preserves members and adds one history step", session.Document.Groups.Count == 0 && session.Document.Elements.Count == 4 && session.UndoCount == before + 2);

        session.SetSelection([a.Id]); window.PrepareForShow(); before = session.UndoCount;
        Invoke(window, "ReorderLayers", new Guid[] { a.Id }, c.Id, true); window.PrepareForShow();
        assert("drop above a displayed row places the layer in front in one history step", session.Document.Elements.FindIndex(e => e.Id == a.Id) == session.Document.Elements.FindIndex(e => e.Id == c.Id) + 1 && session.UndoCount == before + 1);
        before = session.UndoCount; Invoke(window, "ReorderLayers", new Guid[] { a.Id }, c.Id, true);
        assert("dropping at the existing position makes no history entry", session.UndoCount == before);
        Invoke(window, "ReorderLayers", new Guid[] { a.Id }, c.Id, false); window.PrepareForShow();
        assert("drop below a displayed row places the layer behind it", session.Document.Elements.FindIndex(e => e.Id == a.Id) + 1 == session.Document.Elements.FindIndex(e => e.Id == c.Id));
        var row = Item(a.Id); var grid = (Controls.Grid)row.Content;
        var visible = grid.Children.OfType<Primitives.ToggleButton>().First();
        var previousIcon = ((System.Windows.Shapes.Path)visible.Content).Data.ToString();
        visible.IsChecked = false; visible.RaiseEvent(new Wpf.RoutedEventArgs(Primitives.ButtonBase.ClickEvent));
        window.PrepareForShow();
        assert("eye icon toggles real layer visibility and its glyph while preserving selection", !session.Document.Elements.Single(e => e.Id == a.Id).Visible && session.Selection.SetEquals([a.Id]) &&
            ((System.Windows.Shapes.Path)visible.Content).Data.ToString() != previousIcon);
        var locked = grid.Children.OfType<Primitives.ToggleButton>().Last();
        locked.IsChecked = true; locked.RaiseEvent(new Wpf.RoutedEventArgs(Primitives.ButtonBase.ClickEvent));
        before = session.UndoCount; Invoke(window, "ReorderLayers", new Guid[] { a.Id }, c.Id, true);
        assert("locked layer cannot be reordered by its handle", session.Document.Elements.Single(e => e.Id == a.Id).Locked && session.UndoCount == before);
        assert("layer action checks create no native window", new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !window.IsVisible);
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
