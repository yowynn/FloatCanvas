using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Peers = System.Windows.Automation.Peers;

/// <summary>The property draft survives work in the independent layer window.</summary>
internal static class InspectorLayerPanelTests
{
    internal static void Run(Action<string, bool> check)
    {
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                var session = new EditorSession();
                session.Add(new() { Kind = ElementKind.Text, Text = "Original", Width = 120, Height = 40 });
                Guid selected = session.Document.Elements.Single().Id;
                var inspector = new InspectorWindow(() => session);
                var layerWindow = new LayersWindow(() => session);
                var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
                var layers = (Controls.ListBox)Field(layerWindow, "layers");
                fields["Text"].Text = "Unsubmitted\n保留草稿";
                string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
                check(language + " properties and layers are independent custom-chrome windows", !Descendants(inspector).OfType<Controls.ListBox>().Any() &&
                    Descendants(layerWindow).Contains(layers) && inspector.WindowStyle == Wpf.WindowStyle.None && layerWindow.WindowStyle == Wpf.WindowStyle.None);
                var row = (Controls.ListBoxItem)layers.Items[0];
                var toggles = Descendants(row).OfType<Primitives.ToggleButton>().ToArray();
                check(language + " layer row uses two accessible icon toggles and a drag handle", toggles.Length == 2 &&
                    toggles.All(toggle => Peers.FrameworkElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(Peers.PatternInterface.Toggle) is not null) &&
                    Descendants(row).OfType<Primitives.Thumb>().Count() == 1 && !Descendants(row).OfType<Controls.CheckBox>().Any());
                check(language + " layer commands live in the row context menu with shortcuts", row.ContextMenu.Items.OfType<Controls.MenuItem>().Count() == 7 &&
                    row.ContextMenu.Items.OfType<Controls.MenuItem>().All(item => item.Icon is not null) &&
                    row.ContextMenu.Items.OfType<Controls.MenuItem>().Any(item => item.InputGestureText == "Ctrl+G"));
                layerWindow.ApplyTemplate();
                var help = (Controls.Button)layerWindow.Template.FindName("Help", layerWindow);
                check(language + " layer help belongs to the shared header without an embedded body button", help.Visibility == Wpf.Visibility.Visible &&
                    Wpf.Automation.AutomationProperties.GetName(help) == L.Get("Layers.Help") &&
                    layerWindow.Resources["CanvasWindowHelpText"] is string text && text == L.Get("Layers.HelpWithRename") &&
                    !Descendants((Wpf.DependencyObject)layerWindow.Content).OfType<Controls.Button>().Any() && layerWindow.Icon is not null);
                layers.SelectedItems.Clear(); inspector.Refresh();
                check(language + " independent layer selection retains the pending property draft", fields["Text"].Text == "Unsubmitted\n保留草稿" && session.Selection.Count == 0);
                session.SetSelection([selected]); inspector.Refresh(); layerWindow.Refresh();
                check(language + " layer refresh changes no document content or history", JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo &&
                    fields["Text"].Text == "Unsubmitted\n保留草稿");
                check(language + " split panel tests create no native windows", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 &&
                    new System.Windows.Interop.WindowInteropHelper(layerWindow).Handle == 0 && !inspector.IsVisible && !layerWindow.IsVisible);
            }
        }
        finally { L.Initialize(preference); }
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject parent)
    {
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(parent).OfType<Wpf.DependencyObject>())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
