using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private RectD ConversionTargetScreen => new(0, 0, _host.PrimaryMonitor.Bounds.Width, _host.PrimaryMonitor.Bounds.Height);
    private CanvasResolutionChoice ChooseCanvasResolution(SceneDocument document)
    {
        var target = ConversionTargetScreen;
        if (!CanvasResolutionConversion.NeedsConversion(document, target)) return CanvasResolutionChoice.Preserve;
        var dialog = CanvasResolutionDialog.Create(_toolbar, CanvasAuthoringReference.FromSavedDocument(document), target);
        SessionWindowCoordinator.ShowModal(dialog.Window); return dialog.Result();
    }

    private string ConvertedSuggestedPath(string? sourcePath)
    {
        string name = sourcePath is null ? L.Get("Project.FilePrefix") : System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        var target = ConversionTargetScreen;
        string file = $"{name}-{target.Width:0}x{target.Height:0}.floatcanvas";
        return sourcePath is null ? file : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, file);
    }

    private async Task ConvertCurrentCanvasResolutionAsync()
    {
        if (DocumentOperationBusy || Active.IsReadOnly || !Active.CanInsertInEditScope || !ReferenceEquals(Active, _live) || HasPrefabEditContext ||
            Active.EditScopeId is not null && _groupCanvasMode != GroupCanvasMode.RootProject || Active.IsTransactionActive) return;
        if (!CanvasResolutionConversion.NeedsConversion(Active.Document, ConversionTargetScreen)) return;
        var session = Active; long version = session.ContentVersion;
        if (ChooseCanvasResolution(session.Document) != CanvasResolutionChoice.Convert) return;
        if (!ReferenceEquals(Active, session) || session.ContentVersion != version || session.IsTransactionActive) return;
        _documentBusy = true;
        try
        {
            var source = session.Document.Clone(); string? sourcePath = FileState(session).Path;
            if (sourcePath is not null) NormalizePrefabLinkPaths(source, sourcePath);
            var target = ConversionTargetScreen;
            var candidate = await Task.Run(() => CanvasResolutionConversion.Prepare(source, target).Document, _documentLifetime.Token);
            SceneValidation.Validate(candidate);
            if (!ReferenceEquals(Active, session) || session.ContentVersion != version || ConversionTargetScreen != target) return;
            await FlushRecoveryAsync(); await ArchiveWorkspaceAsync();
            FinishInlineText(); _editor.Cancel(); ForgetWindowSourceBindings(session);
            _live = new(candidate, _assets); _documentFiles.Remove(session);
            _documentFiles[_live] = new() { SuggestedSaveAsPath = ConvertedSuggestedPath(sourcePath) };
            SynchronizeLiveDisplayLayout(); _editor.ReplaceSession(_live, _liveOrigin);
            _documentRecoveryFingerprint = null; RecoveryHistory.WorkspaceRestored();
            await RecoveryHistory.EnterWorkspaceAsync(SnapshotWorkspace(), _documentLifetime.Token);
            UpdateStatus(L.Get("Canvas.ConvertedWorkingCopy")); RequestRender();
        }
        catch (Exception error) when (error is not OperationCanceledException) { ShowError(error.Message); }
        finally { _documentBusy = false; ScheduleRecovery(); }
    }
}
