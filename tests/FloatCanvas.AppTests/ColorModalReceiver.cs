using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class ColorModalReceiver
{
    internal static void Run(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        int keys = 0, clicks = 0;
        var window = new Window { Title = "FloatCanvas Modal Background Receiver", Width = 1000, Height = 800,
            Left = 10, Top = 10, Content = new TextBox { Text = "Isolated background receiver", Margin = new Thickness(20) }, Background = Brushes.DarkSeaGreen };
        window.PreviewKeyDown += (_, _) => keys++;
        window.PreviewMouseDown += (_, _) => clicks++;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => File.WriteAllText(output, JsonSerializer.Serialize(new { keys, clicks }));
        timer.Start(); try { app.Run(window); } finally { timer.Stop(); }
    }
}
