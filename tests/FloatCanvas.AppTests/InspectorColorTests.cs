using System.IO;
using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Automation = System.Windows.Automation;
using Input = System.Windows.Input;
using Primitives = System.Windows.Controls.Primitives;
using L = FloatCanvas.Localization.AppText;

/// <summary>Color choices update the scene immediately and participate in coherent history.</summary>
internal static class InspectorColorTests
{
    internal static void Run(Action<string, bool> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-InspectorColors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                bool allowed = true;
                var library = new ColorLibrary(Path.Combine(directory, language + ".json"));
                var session = new EditorSession();
                session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60,
                    Style = new() { Stroke = 0xFF123456, Fill = 0x408090A0 } });
                Guid originalId = session.Document.Elements.Single().Id;
                var inspector = new InspectorWindow(() => session, () => allowed, library);
                using var surface = new InspectorTestSurface(inspector);
                var colors = Field<Dictionary<string, ColorField>>(inspector, "colorFields");
                var fields = Field<Dictionary<string, Controls.TextBox>>(inspector, "fields");
                check(language + " properties use shared paint and effect colors without raw paint inputs", colors.Keys.Order().SequenceEqual(["EffectColor", "Fill", "Stroke"]) &&
                    colors["Stroke"].Role == ColorRole.Foreground && colors["Fill"].Role == ColorRole.Background &&
                    !fields.ContainsKey("Stroke") && !fields.ContainsKey("Fill") &&
                    colors["Stroke"].Value == 0xFF123456 && colors["Fill"].Value == 0x408090A0);
                check(language + " color fields keep paint labels and exact accessible ARGB", colors.Where(pair => pair.Key != "EffectColor").All(pair =>
                    Automation.AutomationProperties.GetLabeledBy(pair.Value) is Controls.TextBlock caption && !string.IsNullOrWhiteSpace(caption.Text) &&
                    Automation.AutomationProperties.GetName(pair.Value).Contains(ColorLibrary.ToHtml(pair.Value.Value))));
                int undo = session.UndoCount; string before = JsonSerializer.Serialize(session.Document);
                colors["Stroke"].SetUserValue(0x80112233); colors["Fill"].SetUserValue(0x20445566);
                fields["X"].Text = "35";
                inspector.Refresh();
                check(language + " color choices immediately preview exact ARGB and position", session.Document.Elements.Single().Style.Stroke == 0x80112233 &&
                    session.Document.Elements.Single().Style.Fill == 0x20445566 && session.UndoCount == undo && fields["X"].Text == "35");
                Invoke(inspector, "Apply");
                var applied = session.Document.Elements.Single();
                check(language + " paint and position burst commits one undo step", applied.Style.Stroke == 0x80112233 &&
                    applied.Style.Fill == 0x20445566 && Math.Abs(ElementGeometry.Bounds(applied).Left - 35) < 1e-8 && session.UndoCount == undo + 1);
                check(language + " undo restores previous paint and geometry together", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
                check(language + " undo notification synchronizes swatches without manual refresh", colors["Stroke"].Value == 0xFF123456 && colors["Fill"].Value == 0x408090A0);
                allowed = false; colors["Stroke"].SetUserValue(0xFFABCDEF);
                check(language + " busy color choice cannot mutate the document", JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo);
                allowed = true; session.SetSelection([]);
                check(language + " changing selection collapses unavailable property inputs", Field<Controls.StackPanel>(inspector, "propertyPanel").Visibility == Wpf.Visibility.Collapsed && session.Selection.Count == 0);
                session.SetSelection([originalId]);
                int beforeReload = session.UndoCount;
                ColorField? requested = null; inspector.EyedropperRequested += field => requested = field;
                var picker = Field<ColorPickerPanel>(colors["Fill"], "picker");
                var eyedropper = Descendants(picker).OfType<Controls.Button>().Single(button => Automation.AutomationProperties.GetName(button) == L.Get("Color.Eyedropper"));
                eyedropper.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
                check(language + " eyedropper request forwards the exact target field without mutation", ReferenceEquals(requested, colors["Fill"]) &&
                    JsonSerializer.Serialize(session.Document) == before && session.UndoCount == beforeReload);
                requested!.SetUserValue(0x7F102030); inspector.Refresh();
                check(language + " sampled colors apply immediately", colors["Fill"].Value == 0x7F102030 &&
                    session.Document.Elements.Single().Style.Fill == 0x7F102030 && library.History.Contains(0x7F102030u));
                var closedEscape = new Input.KeyEventArgs(Input.Keyboard.PrimaryDevice, new DetachedPresentationSource(), 0, Input.Key.Escape)
                    { RoutedEvent = Input.Keyboard.PreviewKeyDownEvent };
                ((Wpf.UIElement)colors["Fill"].PickerRoot).RaiseEvent(closedEscape);
                check(language + " a closed color picker leaves Escape available to its parent and preserves sampled paint", !closedEscape.Handled &&
                    !colors["Fill"].IsPickerOpen && colors["Fill"].Value == 0x7F102030 && session.Document.Elements.Single().Style.Fill == 0x7F102030);
                colors["Fill"].OpenPicker();
                check(language + " the color Escape fixture opens the real secondary picker", colors["Fill"].IsPickerOpen);
                var escape = new Input.KeyEventArgs(Input.Keyboard.PrimaryDevice, new DetachedPresentationSource(), 0, Input.Key.Escape)
                    { RoutedEvent = Input.Keyboard.PreviewKeyDownEvent };
                ((Wpf.UIElement)colors["Fill"].PickerRoot).RaiseEvent(escape);
                check(language + " open picker Escape is consumed without losing sampled paint", escape.Handled && !colors["Fill"].IsPickerOpen &&
                    colors["Fill"].Value == 0x7F102030 && session.Document.Elements.Single().Style.Fill == 0x7F102030);
                check(language + " closed picker does not consume a parent's next Escape", !colors["Fill"].TryClosePicker());
                Invoke(inspector, "Apply");
                check(language + " inspector color checks use a visible offscreen native surface", new System.Windows.Interop.WindowInteropHelper(inspector).Handle != 0 && inspector.IsVisible && inspector.Left < -10000 && !inspector.ShowActivated);
            }
        }
        finally
        {
            L.Initialize(preference);
            Directory.Delete(directory, true);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject parent)
    {
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(parent).OfType<Wpf.DependencyObject>())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }

    private sealed class DetachedPresentationSource : Wpf.PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
