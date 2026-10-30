using System.Reflection;
using System.Windows;
using System.Windows.Input;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasNumericPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var session = new EditorSession();
        session.Document.Canvas.Grid.FlatCellSize = session.Document.Canvas.Projection.GridCellSize;
        var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        try
        {
            var edit = new CanvasColorEdit(() => session, () => true, () => { }, () => { });
            window.BeginNumericGesture = edit.Begin;
            window.NumericGestureCompleted += commit => { edit.Complete(commit); window.RefreshAfterNumericGesture(session.Document.Canvas); };
            void Change(Action<CanvasSettings> update)
            {
                if (edit.IsActive) edit.Preview(update);
                else session.ExecuteSettings("numeric", document => update(document.Canvas));
            }
            window.GridWidthChanged += value => Change(settings => settings.Grid.Width = value);
            window.GridSizeChanged += value => Change(settings => settings.Grid.FlatCellSize = value);
            window.GridCountsChanged += (columns, rows) => Change(settings => { settings.Projection.Columns = columns; settings.Projection.Rows = rows; });
            window.PrepareForShow();
            var width = Field<NumericValueField>(window, "width");
            int previews = 0;
            window.GridWidthChanged += _ => previews++;
            width.BeginGesture(); width.Slider.Value = 7; width.Slider.Value = 7.5;
            check("canvas numeric movement publishes every preview before release", previews == 2 && width.IsInteracting);
            check("canvas width preview mutates the pending document and preview revision without adding undo", session.Document.Canvas.Grid.Width == 7.5 && session.PreviewRevision == 2 && session.IsTransactionActive && session.UndoCount == 0);
            width.EndGesture(true);
            check("canvas width release commits exactly one undo step", !session.IsTransactionActive && session.UndoCount == 1 && session.Undo() && session.Document.Canvas.Grid.Width == 1);
            window.Refresh(session.Document.Canvas); window.PrepareForShow();
            foreach (string name in new[] { "width", "gridSize", "columns", "rows" })
            {
                session.Document.Canvas.Projection.Enabled = name is "columns" or "rows";
                window.Refresh(session.Document.Canvas); window.PrepareForShow();
                var field = Field<NumericValueField>(window, name);
                double initial = field.Value; int undo = session.UndoCount;
                string before = System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas);
                check(name + " rail starts one pending settings edit", field.BeginGesture() && session.IsTransactionActive);
                for (int i = 1; i <= 100; i++) field.Slider.Value = field.Slider.Minimum + (field.Slider.Maximum - field.Slider.Minimum) * i / 100;
                check(name + " drag has live settings previews before mouse release", session.PreviewRevision > session.TransactionPreviewRevision && session.UndoCount == undo && System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas) != before);
                field.CancelGesture();
                check(name + " cancellation restores settings and leaves history unchanged", !session.IsTransactionActive && session.UndoCount == undo && System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas) == before && field.Value == initial);
                window.PrepareForShow();
                field.AnchorScrubber!.TryBegin(0); field.AnchorScrubber.Update(60);
                check(name + " icon drag also previews before release", session.IsTransactionActive && session.UndoCount == undo && System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas) != before);
                field.AnchorScrubber.End(true);
                check(name + " icon release commits one reversible settings action", !session.IsTransactionActive && session.UndoCount == undo + 1 && session.Undo() && System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas) == before);
                window.Refresh(session.Document.Canvas); window.PrepareForShow();
                field.BeginGesture(); field.Slider.Value = initial + 1; field.Slider.Value = initial; field.EndGesture(true);
                check(name + " returning to the starting value restores the live model without an undo entry", session.UndoCount == undo && System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas) == before);
            }
            foreach (var (name, typed) in new[] { ("columns", 64d), ("rows", 64d), ("width", 16d) })
            {
                window.Refresh(session.Document.Canvas); window.PrepareForShow();
                var field = Field<NumericValueField>(window, name);
                double ModelValue() => name == "columns" ? session.Document.Canvas.Projection.Columns : name == "rows" ? session.Document.Canvas.Projection.Rows : session.Document.Canvas.Grid.Width;
                string before = System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas);
                int undo = session.UndoCount;
                field.Input.Text = typed.ToString(System.Globalization.CultureInfo.InvariantCulture); SendEnter(field.Input);
                check(name + " typed value beyond the common rail still commits within its original hard range", ModelValue() == typed && field.Value == typed && field.Slider.Value == field.Slider.Maximum && session.UndoCount == undo + 1);
                window.Refresh(session.Document.Canvas); window.PrepareForShow();
                check(name + " refresh preserves the out-of-rail value without adding history", field.TryGetValue(out double actual) && actual == typed && ModelValue() == typed && session.UndoCount == undo + 1);
                field.AnchorScrubber!.TryBegin(0); field.AnchorScrubber.Update(60);
                check(name + " icon still previews beyond the narrowed rail", field.Value > typed && ModelValue() == field.Value && session.IsTransactionActive && session.UndoCount == undo + 1);
                field.AnchorScrubber.End(true);
                check(name + " out-of-rail icon drag remains one undo action", session.UndoCount == undo + 2 && session.Undo() && ModelValue() == typed);
                window.RefreshAfterNumericGesture(session.Document.Canvas);
                field.AnchorScrubber.TryBegin(0); field.AnchorScrubber.Update(60); field.AnchorScrubber.End(false);
                check(name + " cancelling beyond the rail restores exact typed value without history", field.Value == typed && ModelValue() == typed && session.UndoCount == undo + 1 && !session.IsTransactionActive);
                check(name + " typed out-of-rail value has an independent reversible history entry", session.Undo() && session.UndoCount == undo && System.Text.Json.JsonSerializer.Serialize(session.Document.Canvas) == before);
            }
            double guide = 1; window.GuideOpacityChanged += value => guide = value;
            var opacity = Field<NumericValueField>(window, "guideOpacity");
            opacity.BeginGesture(); opacity.Slider.Value = 45;
            check("reference opacity previews during drag without a document transaction", guide == .45 && !session.IsTransactionActive && session.UndoCount == 0);
            opacity.CancelGesture();
            check("cancelling reference opacity restores the live view without history", guide == 1 && session.UndoCount == 0);
            session.ExecuteSettings("previous width", document => document.Canvas.Grid.Width = 3);
            window.Refresh(session.Document.Canvas); window.PrepareForShow();
            width.BeginGesture(); width.Slider.Value = 7; session.Undo(); width.Slider.Value = 8; width.EndGesture(true);
            check("external undo during a numeric gesture prevents stale release from reapplying its draft", session.Document.Canvas.Grid.Width == 1 && session.UndoCount == 0 && session.RedoCount == 1 && width.Value == 1);
            width.Input.Text = "invalid";
            check("invalid canvas draft cannot open a hidden settings transaction", !width.BeginGesture() && !width.AnchorScrubber!.TryBegin(0) && !session.IsTransactionActive && session.UndoCount == 0);
        }
        finally { window.CloseForDispose(); }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SendEnter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.Enter)
    { RoutedEvent = Keyboard.KeyDownEvent });
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
