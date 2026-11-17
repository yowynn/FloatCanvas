using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class InspectorGeometryVisibilityTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application();
        var session = new EditorSession();
        var inspector = new InspectorWindow(() => session);
        var panel = (StackPanel)typeof(InspectorWindow).GetField("propertyPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!;
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.Text, ElementKind.Ellipse })
        {
            session.Add(new() { Kind = kind, Width = 120, Height = 80 });
            session.Add(new() { Kind = kind, Width = 80, Height = 60 });
            session.SetSelection(session.Document.Elements.TakeLast(2).Select(element => element.Id));
            inspector.PrepareForShow();
            var labels = new[] { "Property.Position", "Property.Size" }.Select(L.Get).ToHashSet();
            var rows = panel.Children.OfType<Grid>().Where(row => row.Children.OfType<TextBlock>().Any(label => labels.Contains(label.Text))).ToArray();
            check($"{kind}: duplicate position and size rows collapse with their controls", rows.Length == 2 && rows.All(row => row.Visibility == Visibility.Collapsed));
            var heading = panel.Children.OfType<Border>().Single(border => border.Child is TextBlock text && text.Text == L.Get("Inspector.GeometrySection"));
            check($"{kind}: empty geometry section heading collapses", heading.Visibility == Visibility.Collapsed);
            session.SetSelection([session.Document.Elements.Last().Id]);
            inspector.PrepareForShow();
            check($"{kind}: single selection restores geometry controls and heading", rows.All(row => row.Visibility == Visibility.Visible) && heading.Visibility == Visibility.Visible);
        }
    }
}
