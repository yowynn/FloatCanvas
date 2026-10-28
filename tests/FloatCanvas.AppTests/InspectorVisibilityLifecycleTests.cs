using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class InspectorVisibilityLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var originalForeground = GetForegroundWindow();
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "visible", Width = 100, Height = 40,
            Style = new() { FontFamily = "Arial" } };
        var current = new EditorSession(new() { Elements = [text] }); current.SelectAll();
        int sessionReads = 0;
        var inspector = new InspectorWindow(() => { sessionReads++; return current; })
        {
            Left = -20000, Top = -20000, Width = 420, Height = 500,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0
        };
        bool activated = false; inspector.Activated += (_, _) => activated = true;
        var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
        var containers = Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers");
        var font = Field<ComboBox>(inspector, "fontPicker");
        var colors = Field<Dictionary<string, ColorField>>(inspector, "colorFields");
        var results = new List<(string Name, bool Passed)>();
        nint handle = 0;
        try
        {
            inspector.Show(); Pump(); handle = new WindowInteropHelper(inspector).Handle;
            results.Add(("inspector visibility regression uses an actual visible native window", inspector.IsVisible && IsWindow(handle) && fields["Text"].Text == "visible"));
            inspector.Hide(); Pump();
            int hiddenTextUpdates = 0; fields["Text"].TextChanged += (_, _) => hiddenTextUpdates++;
            int readsBeforeHiddenUpdate = sessionReads;
            current.Execute("hidden text edit", document => document.Elements[0].Text = "changed while hidden");
            inspector.Refresh(); Pump();
            Console.WriteLine($"INSPECTOR_HIDDEN sessionReads={sessionReads - readsBeforeHiddenUpdate} textUpdates={hiddenTextUpdates}");
            results.Add(("hidden inspector ignores session and render refreshes without reading the session or updating fields", sessionReads == readsBeforeHiddenUpdate && hiddenTextUpdates == 0 && fields["Text"].Text == "visible"));
            inspector.Show(); Pump();
            results.Add(("reopening inspector synchronizes changes made while hidden", fields["Text"].Text == "changed while hidden"));

            var picture = new CanvasElement { Kind = ElementKind.LiveBlur, Width = 80, Height = 60,
                Style = new() { FontFamily = "Arial", Fill = 0xFF123456 }, Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1) }] };
            current.Add(picture); Pump();
            string hiddenFont = fields["FontFamily"].Text, hiddenText = fields["Text"].Text;
            uint hiddenStroke = colors["Stroke"].Value, hiddenFill = colors["Fill"].Value;
            int fontChanges = 0, textChanges = 0, colorChanges = 0;
            fields["FontFamily"].TextChanged += (_, _) => fontChanges++;
            fields["Text"].TextChanged += (_, _) => textChanges++;
            var strokeProperty = DependencyPropertyDescriptor.FromProperty(ColorField.ValueProperty, typeof(ColorField));
            var fillProperty = DependencyPropertyDescriptor.FromProperty(ColorField.ValueProperty, typeof(ColorField));
            EventHandler countColor = (_, _) => colorChanges++;
            strokeProperty.AddValueChanged(colors["Stroke"], countColor); fillProperty.AddValueChanged(colors["Fill"], countColor);
            try
            {
                current.Execute("effect metadata changes", document =>
                {
                    var image = document.Elements.Single(element => element.Id == picture.Id);
                    image.Style.FontFamily = "Times New Roman"; image.Text = "hidden text metadata";
                    image.Style.Stroke = 0xFFABCDEF; image.Style.Fill = 0xFF654321;
                });
                inspector.Refresh(); Pump();
                Console.WriteLine($"INSPECTOR_COLLAPSED fontUpdates={fontChanges} textUpdates={textChanges} colorUpdates={colorChanges}");
                results.Add(("visible inspector does not synchronize collapsed font text and paint rows",
                    containers["FontFamily"].Visibility == Visibility.Collapsed && containers["Stroke"].Visibility == Visibility.Collapsed &&
                    fontChanges == 0 && textChanges == 0 && colorChanges == 0 && fields["FontFamily"].Text == hiddenFont &&
                    fields["Text"].Text == hiddenText && colors["Stroke"].Value == hiddenStroke && colors["Fill"].Value == hiddenFill));
            }
            finally
            {
                strokeProperty.RemoveValueChanged(colors["Stroke"], countColor); fillProperty.RemoveValueChanged(colors["Fill"], countColor);
            }
            current.Add(new() { Kind = ElementKind.Rectangle, Width = 90, Height = 60,
                Style = new() { Stroke = 0xFF112233, Fill = 0xFF778899, LinkColors = true } }); Pump();
            var link = Field<SegmentedChoice>(inspector, "linkColors");
            link.SelectedValue = false;
            results.Add(("revealing a dependent paint row synchronizes its latest model value", containers["Fill"].Visibility == Visibility.Visible && colors["Fill"].Value == 0xFF778899));
            inspector.CompletePendingEdit();
            inspector.Hide(); Pump();
            var replacement = new CanvasElement { Kind = ElementKind.Text, Text = "replacement session", Width = 140, Height = 60,
                Style = new() { FontFamily = "Segoe UI", Stroke = 0xFF334455 } };
            current = new(new() { Elements = [replacement] }); current.SelectAll();
            inspector.Refresh(); inspector.Show(); Pump();
            results.Add(("reopened inspector binds the current session selection and newly visible rows",
                fields["Text"].Text == "replacement session" && fields["FontFamily"].Text == "Segoe UI" &&
                ToolOptionPreviews.SelectedFontName(font) == "Segoe UI" && colors["Stroke"].Value == 0xFF334455));
            int undo = current.UndoCount;
            fields["Text"].Text = "finish before hiding";
            inspector.Hide(); Pump();
            results.Add(("hiding inspector commits its pending edit once without retaining a transaction",
                current.Document.Elements[0].Text == "finish before hiding" && current.UndoCount == undo + 1 && !current.IsTransactionActive));
            inspector.Show(); Pump();
            results.Add(("reopening after a pending edit retains the committed value", fields["Text"].Text == "finish before hiding"));
            Console.WriteLine($"INSPECTOR_VISIBILITY native={handle} foregroundBefore={originalForeground} foregroundAfter={GetForegroundWindow()} activated={activated}");
        }
        finally { inspector.Hide(); if (handle != 0) HwndSource.FromHwnd(handle)?.Dispose(); Pump(); }
        results.Add(("inspector offscreen visibility checks never activate its window or leak its HWND", !activated && GetForegroundWindow() != handle && handle != 0 && !IsWindow(handle)));
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
}
