using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>One small, temporary OLE target while the desktop receives normal input.</summary>
internal sealed class DropImportWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    private readonly Button resume = new() { MinHeight = 32, Padding = new(10, 3, 10, 3), Margin = new(0, 0, 8, 0) };
    private readonly Button close = new() { MinHeight = 32, Padding = new(10, 3, 10, 3) };
    private bool busy, completed;
    internal event Action<string[]>? FilesDropped;
    internal event Action? ResumeRequested;

    internal DropImportWindow(string destination)
    {
        Title = L.Get("ImageDrop.Title"); Width = 360; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.ToolWindow;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; AllowDrop = true;
        FontSize = 14; FontFamily = new("Segoe UI, Microsoft YaHei UI");
        SetResourceReference(BackgroundProperty, "CanvasSurface");
        SetResourceReference(ForegroundProperty, "CanvasText");
        var panel = new StackPanel { Margin = new(16) };
        panel.Children.Add(new TextBlock { Text = L.Format("ImageDrop.Destination", destination), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = L.Get("ImageDrop.Hint"), TextWrapping = TextWrapping.Wrap, Margin = new(12) };
        var area = new Border { Child = hint, Background = Brushes.Transparent, BorderBrush = Brushes.Gray, BorderThickness = new(1), CornerRadius = new(6), Margin = new(0, 12, 0, 0) };
        panel.Children.Add(area); panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(resume); buttons.Children.Add(close); panel.Children.Add(buttons); Content = panel;
        resume.Click += (_, _) => ResumeRequested?.Invoke(); close.Click += (_, _) => Close();
        DragOver += (_, e) => { e.Effects = HandleDrag(e.Data, e.AllowedEffects, false); e.Handled = true; };
        Drop += (_, e) => { e.Effects = HandleDrag(e.Data, e.AllowedEffects, true); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        ShowFailure(L.Get("ImageDrop.Waiting"));
    }

    internal void SetBusy()
    {
        busy = true; AllowDrop = false; resume.IsEnabled = false; status.Text = L.Get("ImageDrop.Busy");
    }
    internal void ShowFailure(string message)
    {
        busy = false; AllowDrop = !completed; resume.IsEnabled = true; status.Text = message;
        resume.Content = L.Get(completed ? "ImageDrop.Continue" : "ImageDrop.Return");
        close.Content = L.Get(completed ? "ImageDrop.Close" : "ImageDrop.Cancel");
    }
    internal void Complete(int count)
    {
        completed = true; ShowFailure(L.Format("ImageDrop.Imported", count));
    }
    internal DragDropEffects HandleDrag(IDataObject data, DragDropEffects allowedEffects, bool dropping)
    {
        if (busy || completed) return DragDropEffects.None;
        try
        {
            if ((allowedEffects & DragDropEffects.Copy) != 0 && data.GetDataPresent(DataFormats.FileDrop) &&
                data.GetData(DataFormats.FileDrop) is string[] files && SupportsFiles(files))
            { if (dropping) FilesDropped?.Invoke(files); return DragDropEffects.Copy; }
            if (dropping) ShowFailure(L.Get("ImageDrop.Unsupported"));
        }
        catch (Exception ex) { ShowFailure(L.Format("ImageDrop.Failed", ex.Message)); }
        return DragDropEffects.None;
    }
    internal static bool SupportsFiles(string[] paths) => paths.Length > 0 && paths.All(path => !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp");
}
