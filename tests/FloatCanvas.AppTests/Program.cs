using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using System.IO;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--overlay-probe-exit"]) return 73;
        if (args is ["--overlay-probe-wait"]) { Thread.Sleep(10000); return 0; }
        try
        {
            if (string.Equals(Path.GetFileName(Environment.ProcessPath), "FloatCanvas.SafetyPreview.exe", StringComparison.OrdinalIgnoreCase)) args = ["--canvas-safety-dialog-preview"];
            ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-AppTests", Guid.NewGuid().ToString("N"))]);
            FloatCanvas.Localization.AppText.Initialize("zh-CN");
            if (args.Contains("--overlay-protection-only")) { OverlayProtectionTests.Run(Assert); return 0; }
            if (args.Contains("--overlay-protection-runtime")) { OverlayProtectionRuntimeTests.Run(Assert); return 0; }
            if (args.Contains("--system-overlay-production")) return SystemOverlayProof.Production();
            if (args.Contains("--system-overlay-performance")) return SystemOverlayProof.Production(measurePerformance:true);
            if (args.Contains("--system-overlay-native-dialog")) return SystemOverlayProof.Production(measurePerformance:true,nativeDialogOnly:true);
            if (args.Contains("--system-overlay-native-dialog-interaction")) return SystemOverlayProof.Production(measurePerformance:true,nativeDialogOnly:true);
            if (args.Contains("--system-overlay-document-dialog")) return SystemOverlayProof.Production(measurePerformance:true,nativeDialogOnly:true);
            if (args.Contains("--system-overlay-document-dialog-interaction")) return SystemOverlayProof.Production(measurePerformance:true,nativeDialogOnly:true);
            if (args.Contains("--system-overlay-aux-only")) { SystemOverlayProof.AuxiliaryGate(); return 0; }
            if (args.Contains("--system-overlay-popup-only")) { SystemOverlayProof.PopupGate(); return 0; }
            if (args.Contains("--system-overlay-ime-focus-only")) { SystemOverlayProof.ImeFocusGate(); return 0; }
            if (args.Contains("--system-overlay-options-focus-only")) { SystemOverlayProof.OptionsFocusGate(); return 0; }
            if (args.Contains("--system-overlay-window-contract")) { SystemOverlayProof.WindowContract(); return 0; }
            if (args.Contains("--system-overlay-receiver")) { SystemOverlayProof.Receiver(); return 0; }
            if (args.Contains("--relative-input-receiver")) { RelativeInputReceiver.Run(); return 0; }
            if (args.Contains("--system-overlay-confinement-observation")) { SystemOverlayProof.ConfinementObservation(); return 0; }
            if (args.Contains("--projection-hover-runtime-preview")) { ProjectionHoverRuntimeFixture.Run(Assert); return 0; }
            if (args.Contains("--projection-hover-only")) { ProjectionUnsafeHoverTests.Run(Assert); RetainedBillboardInteractionTests.Run(Assert); return 0; }
            if (args.Contains("--canvas-tool-only")) { CanvasToolInteractionTests.Run(Assert); return 0; }
            if (args.Contains("--canvas-safety-dialog-runtime-only")) { CanvasSafetyDialogRuntimeTests.Run(Assert); return 0; }
            if (args.Contains("--canvas-safety-dialog-preview")) { CanvasSafetyDialogRuntimeTests.RunPreview(Assert); return 0; }
            if (args.Contains("--canvas-safety-resolution-only")) { CanvasSafetyResolutionTests.Run(Assert); return 0; }
            if (args.Contains("--native-window-progress-only")) { NativeWindowProgressTests.Run(Assert); return 0; }
            if (args.Contains("--perspective-safety-only")) { PerspectiveSafetyInputTests.Run(Assert); CanvasSafetyResolutionTests.Run(Assert); GeometrySafetySnapshotTests.Run(Assert); PerspectiveSafetyCommandTests.Run(Assert); return 0; }
            if (args.Contains("--startup-dialog-only")) { StartupDialogTests.Run(Assert); return 0; }
            if (args.Contains("--project-open-dispatch-only")) { ProjectOpenDispatchTests.Run(Assert); return 0; }
            if (args.Contains("--depth-occlusion-ui-only")) { DepthOcclusionUiRuntimeTests.Run(Assert); return 0; }
            if (args.Contains("--depth-occlusion-only")) { DepthOcclusionSettingsTests.Run(Assert); DepthOcclusionInteractionTests.Run(Assert); return 0; }
            if (args.Contains("--billboard-only")) { BillboardControllerTests.Run(Assert); return 0; }
            if (args.Contains("--billboard-creation-only")) { BillboardCreationTests.Run(Assert); return 0; }
            if (args.Contains("--billboard-audit-only")) { BillboardInteractionAuditTests.Run(Assert); return 0; }
            if (args.Contains("--billboard-ui-runtime-only")) { BillboardUiRuntimeTests.Run(Assert); return 0; }
            if (args.Contains("--billboard-canvas-runtime-only")) { BillboardCanvasRuntimeTests.Run(Assert); return 0; }
            if (args.FirstOrDefault(a=>a.StartsWith("--round2-ui-")) is { } round2) { Round2UiTests.Run(Assert,round2[12..]); return 0; }
            if (args.Contains("--round2-canvas-only")) {Round2CanvasTests.Run(Assert);return 0;}
            if (args.Contains("--preview4-canvas-only")) { Preview4CanvasTests.Run(Assert); return 0; }
            if (args.Contains("--preview4-settings-only")) { Preview4SettingsTests.Run(Assert); LauncherGestureStateTests.Run(Assert); return 0; }
            if (args.Contains("--preview4-menu-only")) { Preview4MenuTests.Run(Assert, true); LauncherMenuTests.Run(Assert); return 0; }
            if (args.Contains("--move-into-view-only")) { MoveIntoViewTests.Run(Assert); return 0; }
            if (args.Contains("--move-into-view-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "move-into-view"); return 0; }
            if (args.Contains("--round2-stamps-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "round2-stamps"); return 0; }
            if (args.Contains("--preview4-menu-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "preview4-menu"); return 0; }
            if (args.Contains("--preview4-settings-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "preview4-settings"); return 0; }
            if (args.Contains("--preview4-properties-only")) { Preview4PropertiesTests.Run(Assert); return 0; }
            if (args.Contains("--canvas-commands-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "canvas-commands"); return 0; }
            if (args.Contains("--shortcut-matrix-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "shortcut-matrix"); return 0; }
            if (args.Contains("--shortcut-surfaces-only")) { ShortcutSurfaceRoutingTests.Run(Assert); return 0; }
            if (args.Contains("--aux-shortcuts-only")) { AuxiliaryShortcutRoutingTests.Run(Assert); return 0; }
            if (args.Contains("--aux-shortcut-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "aux-shortcut-runtime"); return 0; }
            if (args.Contains("--input-method-ui-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "input-method-ui"); return 0; }
            if (args.Contains("--temporary-move-controller-only")) { TemporaryMoveControllerTests.Run(Assert); return 0; }
            if (args.Contains("--selection-shortcut-regression-only")) { SelectionShortcutRegressionTests.Run(Assert); return 0; }
            if (args.Contains("--properties-click-sequence-only")) { PropertiesClickSequenceTests.Run(Assert); return 0; }
            if (args.Contains("--preview4-shift-only")) { Preview4ShiftTests.Run(Assert); return 0; }
            if (args.Contains("--preview4-drag-only")) { Preview4DragTests.Run(Assert); return 0; }
            if (args.Contains("--layer-drag-dpi-only")) { LayerDragDpiTests.Run(Assert); return 0; }
            if (args.Contains("--preview4-drag-runtime-only")) { Preview4DragRuntimeFixture.Run(Assert); return 0; }
            if (args.Contains("--preview4-shift-runtime-only")) { InlineTextRuntimeFixture.Run(Assert,shifts:true); return 0; }
            if (args.Contains("--preview4-properties-runtime-only")) { InlineTextRuntimeFixture.Run(Assert,properties:true); return 0; }
            if (args.Contains("--inline-text-only")) { Preview4InlineTextTests.Run(Assert); return 0; }
            if (args.Contains("--inline-text-runtime-only")) { InlineTextRuntimeFixture.Run(Assert); return 0; }
            if (args.Contains("--inline-text-preview")) { InlineTextRuntimeFixture.Run(Assert,true); return 0; }
            if (args.Contains("--preview4-colors-only")) { Preview4ColorTests.Run(Assert); return 0; }
            if (args.Contains("--preview4-colors-runtime-only")) { Preview4ColorRuntimeFixture.Run(Assert); return 0; }
            if (args.Contains("--monitor-scope-only")) { MonitorScopeSettingsTests.Run(Assert); MonitorScopeRuntimeTests.Run(Assert); return 0; }
            if (args.Contains("--canvas-presets-only")) { CanvasPresetTests.Run(Assert); CanvasSettingsControlTests.Run(Assert); return 0; }
            if (args.Contains("--stamp-shelf-only")) { Task.Run(() => StampShelfOrderingTests.RunAsync(Assert)).GetAwaiter().GetResult(); StampLibraryUiTests.Run(Assert); return 0; }
            if (args.Contains("--stamp-shelf-native-geometry-only")) { StampLibraryUiTests.RunNativeGeometry(Assert); return 0; }
            if (args.Contains("--shell-integration-only")) { ShellIntegrationTests.Run(Assert); return 0; }
            if (args.Contains("--shell-runtime-only")) { ShellRuntimeFixture.Run(Assert); return 0; }
            if (args.Contains("--launcher-menu-only")) { LauncherMenuTests.Run(Assert); RetiredToolTests.Run(Assert); return 0; }
            if (args.Contains("--launcher-tab-runtime-only")) { NativeInputRoutingFixture.Run(Assert, "launcher-tab"); return 0; }
            if (args.Contains("--canvas-projection-preview")) { CanvasProjectionRuntimeFixture.Run(Assert, true); return 0; }
            if (args.Contains("--canvas-projection-runtime-only")) { CanvasProjectionRuntimeFixture.Run(Assert); return 0; }
            if (args.Contains("--perspective-input-only")) { PerspectiveInputTests.Run(Assert); CanvasCalibrationTests.Run(Assert); PerspectiveSafetyInputTests.Run(Assert); CanvasSafetyResolutionTests.Run(Assert); GeometrySafetySnapshotTests.Run(Assert); PerspectiveSafetyCommandTests.Run(Assert); return 0; }
            if (args.Contains("--session-capture-background-helper")) { SessionCaptureRoutingFixture.RunBackgroundHelper(args); return 0; }
            if (args.Contains("--focus-external-helper")) { FocusSessionRoutingFixture.RunExternalHelper(args); return 0; }
            if (args.Contains("--focus-session-only")) { FocusSessionRoutingFixture.Run(Assert); return 0; }
            if (args.Contains("--capture-selection-only")) { CaptureSelectionTests.Run(Assert); return 0; }
            if (args.Contains("--image-tools-only")) { ImageToolEligibilityTests.Run(Assert); ImageToolTargetTests.Run(Assert); WorkspaceToolControllerTests.Run(Assert); ImageEffectsAndCrop(); DocumentRegions(); return 0; }
            if (args.Contains("--image-drop-reader-only")) { ImageDropDataReaderTests.Run(Assert); return 0; }
            if (args.Contains("--gpu-settings-only")) { GpuSettingsTests.Run(Assert); LocalizationTests.Run(Assert); return 0; }
            if (args.Contains("--animation-integration-only")) { AnimatedImageIntegrationTests.Run(Assert); return 0; }
            if (args.Contains("--snapshot-image-only")) { SnapshotImageTests.Run(Assert); return 0; }
            if (args.Contains("--snapshot-coordinates-only")) { SnapshotCoordinateTests.Run(Assert); return 0; }
            if (args.Contains("--diagnostics-only")) { ApplicationDiagnosticsTests.Run(Assert); return 0; }
            if (args.Contains("--windows-compatibility-only")) { LegacyWindowsEffectsTests.Run(Assert); return 0; }
            if (args.Contains("--tool-state-store-only")) { ToolStateStoreTests.Run(Assert); return 0; }
            if (args.Contains("--tool-state-only")) { ToolStateStoreTests.Run(Assert); ToolWorkingStateTests.Run(Assert); return 0; }
            if (args.Contains("--clear-canvas-only")) { NewCanvasButtonTests.Run(Assert); return 0; }
            if (args.Contains("--updates-client-only")) { GitHubUpdateClientTests.Run(Assert); return 0; }
            if (args.Contains("--updates-recovery-only")) { UpdateRecoveryTests.Run(Assert); return 0; }
            if (args.Contains("--update-installer-only")) { UpdateInstallerTests.Run(Assert); return 0; }
            if (args.Contains("--update-window-only")) { AppUpdateWindowTests.Run(Assert); return 0; }
            if (args.Contains("--preset-isolation-only")) { PresetIsolationTests.Run(Assert); return 0; }
            if (args.Contains("--palette-layout-only")) { PaletteLayoutTests.Run(Assert); return 0; }
            if (args.Contains("--feedback4-geometry-only")) { Feedback4GeometryTests.Run(Assert); return 0; }
            if (args.Contains("--feedback4-panels-only")) { NativeInputRoutingFixture.Run(Assert, "feedback4-panels"); return 0; }
            if (args.Contains("--feedback4-workspace-only")) { NativeInputRoutingFixture.Run(Assert, "feedback4-workspace"); return 0; }
            if (args.Contains("--live-effect-stamp-only")) { Task.Run(() => LiveEffectStampTests.RunAsync(Assert)).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--tool-options-keyboard-only")) { ToolOptionsKeyboardRouteTests.Run(Assert); return 0; }
            if (args.Contains("--tile-interaction-only")) { TileInteractionTests.Run(Assert); TileDragReorderTests.Run(Assert); return 0; }
            if (args.Contains("--tile-drag-only")) { TileDragReorderTests.Run(Assert); return 0; }
            if (args.Contains("--feedback4-options-only")) { Feedback4OptionsTests.Run(Assert); return 0; }
            if (args.Contains("--settings-window-only")) { SettingsWindowFixture.Run(Assert); return 0; }
            if (args.Contains("--corner-client-only")) { CornerClientTests.Run(Assert); return 0; }
            if (args.Contains("--lcd-only")) { LcdPaletteTests.Run(Assert); return 0; }
            if (args.Contains("--stamp-library-ui-only")) { StampLibraryUiTests.Run(Assert); return 0; }
            if (args.Contains("--inspector-only")) { InspectorTests.Run(Assert); return 0; }
            if (args.Contains("--inspector-validation-only")) { InspectorValidationLifecycleTests.Run(Assert,windowLifecycle:true); return 0; }
            if (args.Contains("--inspector3-only")) { InspectorFeedbackTests.Run(Assert); return 0; }
            if (args.Contains("--tool-icons-only")) { ToolParameterIconTests.Run(Assert); return 0; }
            if (args.Contains("--feedback-options-only")) { OptionsFeedbackTests.Run(Assert); return 0; }
            if (args.Contains("--colors3-only")) { Feedback3ColorTests.Run(Assert); return 0; }
            if (args.Contains("--number-bounds-only")) { NumberBoundsTests.Run(Assert); return 0; }
            if (args.Contains("--native-input-routing-only")) { NativeInputRoutingFixture.Run(Assert); return 0; }
            if (args.Contains("--stamp-selection-only")) { StampSelectionIntegrationTests.Run(Assert); return 0; }
            if (args.Contains("--toolbar2-only")) { ToolbarAcceptanceTests.Run(Assert); return 0; }
            if (args.Contains("--creation-selection-only")) { CreationSelectionTests.Run(Assert); return 0; }
            if (args.Contains("--click-placement-threshold-only")) { ClickPlacementThresholdTests.Run(Assert); return 0; }
            if (args.Contains("--drag-sized-objects-only")) { DragSizedObjectsTests.Run(Assert); return 0; }
            if (args.Contains("--stamp-preview-only")) { StampPreviewTests.Run(Assert); return 0; }
            if (args.Contains("--billboard-stamp-preview-only")) { BillboardStampPreviewTests.Run(Assert); return 0; }
            if (args.Contains("--emoji-only")) { EmojiTests.Run(Assert); return 0; }
            if (args.Contains("--layer-names-only")) { LayerRenameTests.Run(Assert); return 0; }
            if (args.Contains("--welcome-only")) { WelcomeSessionTests.Run(Assert); HotkeyHintTests.Run(Assert); return 0; }
            if (args.Contains("--session-lifecycle-only")) { SessionLifecycleFixture.Run(Assert); return 0; }
            if (args.Contains("--window-layout-only")) { WindowLayoutFixture.Run(Assert); return 0; }
            if (args.Contains("--icons-only")) { AppIconTests.Run(Assert); return 0; }
            if (args.Contains("--toolbar-only")) { LauncherGestureStateTests.Run(Assert); ToolbarInteractionTests.Run(Assert); return 0; }
            if (args.Contains("--window-chrome-only")) { WindowChromeTests.Run(Assert); return 0; }
            if (args.Contains("--panel-ui-only"))
            {
                WindowChromeTests.Run(Assert); InspectorTests.Run(Assert); ThemeTests.Run(Assert); return 0;
            }
            if (args.Contains("--recovery-only")) { WorkspaceRecoveryHistoryTests.RunAsync(Assert).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--workspace-preview-only")) { WorkspacePreviewTests.RunAsync(Assert).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--clipboard-only")) { ObjectClipboardTests.Run(Assert); return 0; }
            if (args.Contains("--tool-options-input-only")) { ToolOptionsInputFixture.Run(Assert); return 0; }
            if (args.Contains("--tool-options-only")) { ToolOptionsPanelTests.Run(Assert); return 0; }
            if (args.Contains("--tool-compatibility-only")) { ToolCompatibilityTests.Run(Assert); return 0; }
            if (args.Contains("--presets-only")) { ToolPresetAndColorTests.Run(Assert); return 0; }
            if (args.Contains("--stamps-only")) { Task.Run(() => StampLibraryTests.RunAsync(Assert)).GetAwaiter().GetResult(); return 0; }
            LocalizationTests.Run((name, condition) => Assert(name, condition));
            DepthOcclusionSettingsTests.Run(Assert); DepthOcclusionInteractionTests.Run(Assert);
            Round2CanvasTests.Run(Assert);
            foreach(string group in new[]{"colors","welcome","pins","logo","text"})Round2UiTests.Run(Assert,group);
            Preview4CanvasTests.Run(Assert);
            Preview4InlineTextTests.Run(Assert);
            Preview4PropertiesTests.Run(Assert);
            TemporaryMoveControllerTests.Run(Assert);
            PropertiesClickSequenceTests.Run(Assert);
            ShortcutSurfaceRoutingTests.Run(Assert);
            AuxiliaryShortcutRoutingTests.Run(Assert);
            Preview4ShiftTests.Run(Assert);
            Preview4DragTests.Run(Assert);
            Preview4SettingsTests.Run(Assert);
            Preview4MenuTests.Run(Assert);
            Preview4ColorTests.Run(Assert);
            CanvasPresetTests.Run(Assert);
            Task.Run(() => StampShelfOrderingTests.RunAsync(Assert)).GetAwaiter().GetResult();
            ShellIntegrationTests.Run(Assert);
            OverlayProtectionTests.Run(Assert);
            LauncherMenuTests.Run(Assert); RetiredToolTests.Run(Assert);
            BillboardControllerTests.Run(Assert); BillboardCreationTests.Run(Assert);
            PerspectiveInputTests.Run(Assert); CanvasCalibrationTests.Run(Assert);
            PerspectiveSafetyInputTests.Run(Assert); CanvasSafetyResolutionTests.Run(Assert);
            ProjectionUnsafeHoverTests.Run(Assert);
            CanvasToolInteractionTests.Run(Assert);
            RetainedBillboardInteractionTests.Run(Assert);
            GeometrySafetySnapshotTests.Run(Assert); PerspectiveSafetyCommandTests.Run(Assert);
            GpuSettingsTests.Run(Assert);
            ApplicationDiagnosticsTests.Run(Assert);
            NewCanvasButtonTests.Run(Assert);
            WelcomeSessionTests.Run(Assert);
            GitHubUpdateClientTests.Run(Assert);
            UpdateInstallerTests.Run(Assert);
            UpdateRecoveryTests.Run(Assert);
            AppUpdateWindowTests.Run(Assert);
            LayerRenameTests.Run(Assert);
            EmojiTests.Run(Assert);
            CreationSelectionTests.Run(Assert);
            ClickPlacementThresholdTests.Run(Assert);
            DragSizedObjectsTests.Run(Assert);
            CaptureSelectionTests.Run(Assert);
            SnapshotImageTests.Run(Assert);
            SnapshotCoordinateTests.Run(Assert);
            Feedback4GeometryTests.Run(Assert);
            StampPreviewTests.Run(Assert);
            BillboardStampPreviewTests.Run(Assert);
            HotkeyHintTests.Run((name, condition) => Assert(name, condition));
            InlineTextEditSafetyTests.Run((name, condition) => Assert(name, condition));
            CaptureViewportPlacementTests.Run((name, condition) => Assert(name, condition));
            MoveIntoViewTests.Run((name, condition) => Assert(name, condition));
            ImageToolTargetTests.Run((name, condition) => Assert(name, condition));
            WorkspaceToolControllerTests.Run(Assert);
            ToolCompatibilityTests.Run(Assert);
            ImageDropTests.Run((name, condition) => Assert(name, condition));
            ImageDropDataReaderTests.Run((name, condition) => Assert(name, condition));
            AnimatedImageIntegrationTests.Run(Assert);
            ObjectClipboardTests.Run(Assert);
            LauncherGestureStateTests.Run(Assert);
            ToolbarInteractionTests.Run(Assert);
            ToolPresetAndColorTests.Run(Assert);
            ToolStateStoreTests.Run(Assert);
            PresetIsolationTests.Run(Assert);
            ToolOptionsPanelTests.Run(Assert);
            Feedback4OptionsTests.Run(Assert);
            PaletteLayoutTests.Run(Assert);
            ToolOptionsKeyboardRouteTests.Run(Assert);
            Task.Run(() => StampLibraryTests.RunAsync(Assert)).GetAwaiter().GetResult();
            Task.Run(() => LiveEffectStampTests.RunAsync(Assert)).GetAwaiter().GetResult();
            Task.Run(() => StampPlacementRequestTests.RunAsync(Assert)).GetAwaiter().GetResult();
            EncodedPngImportTests.Run((name, condition) => Assert(name, condition));
            CreationTools(); SelectionAndTransforms(); CancellationAndReplacement(); EraserTransactions();
            ImageToolEligibilityTests.Run(Assert); ImageEffectsAndCrop(); DocumentRegions(); TextDoubleClick(); RasterPreviewRanges();
            PointerNoOpTests.Run((name, condition) => Assert(name, condition));
            InspectorTests.Run((name, condition) => Assert(name, condition));
            AppIconTests.Run(Assert);
            WindowChromeTests.Run(Assert);
            WorkspaceRecoveryHistoryTests.RunAsync(Assert).GetAwaiter().GetResult();
            WorkspacePreviewTests.RunAsync(Assert).GetAwaiter().GetResult();
            ThemeTests.Run((name, condition) => Assert(name, condition));
            MonitorScopeSettingsTests.Run((name, condition) => Assert(name, condition));
            Console.WriteLine($"PASS {checks} controller/session/render/inspector checks. No native windows shown or input injected."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CreationTools()
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow,
                     EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle })
        {
            var session = Session(); var controller = new EditorController(session) { Tool = tool };
            Gesture(controller, new(20, 25), new(120, 85));
            var created = session.Document.Elements.Single();
            Assert(tool + " creates correct element", tool == EditorTool.Triangle
                ? created.Kind == ElementKind.Symbol && created.Symbol == SymbolKind.Triangle : created.Kind.ToString() == tool.ToString());
            Assert(tool + " commits one undo step with the tool-specific selection policy", session.UndoCount == 1 && !session.IsTransactionActive &&
                (tool is EditorTool.Freehand or EditorTool.Highlighter ? session.Selection.Count == 0 : session.Selection.Contains(created.Id)));
            using var renderer = new SceneRenderer(); using var bitmap = Decode(renderer.ExportPng(session.Document, session.Assets, true));
            Assert(tool + " produces actual PNG pixels", CountPixels(bitmap) > 40);
            Assert(tool + " undo/redo preserves editable object", session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Single().Kind == created.Kind);
        }
        var numbered = Session(); var numbers = new EditorController(numbered) { Tool = EditorTool.Number };
        Down(numbers, new(60, 60)); Up(numbers, new(60, 60)); Down(numbers, new(120, 60)); Up(numbers, new(120, 60));
        Assert("number tool advances monotonically", numbered.Document.Elements.Select(e => e.Number).SequenceEqual([1, 2]) && numbered.Document.NextNumber == 3);
        var controllerText = new EditorController(Session()) { Tool = EditorTool.Text };
        int editRequests = 0; PointD? point = null;
        controllerText.EditText += (element, p) => { if (element is null) { editRequests++; point = p; } };
        Down(controllerText, new(25, 35)); Up(controllerText, new(25, 35));
        Assert("text tool requests editor without empty transaction", editRequests == 1 && point == new PointD(25, 35) && !controllerText.Session.IsTransactionActive);
        var imageController = new EditorController(Session()) { Tool = EditorTool.Image }; int messages = 0;
        imageController.Message += _ => messages++; Down(imageController, new(10, 10));
        Assert("image tool gives import action guidance", messages == 1 && imageController.Session.Document.Elements.Count == 0);
        var highlighter = Session(); var pen = new EditorController(highlighter) { Tool = EditorTool.Highlighter }; pen.Style.Opacity = .8;
        Gesture(pen, new(20, 30), new(120, 30));
        using var r = new SceneRenderer(); using var png = Decode(r.ExportPng(highlighter.Document, highlighter.Assets, true));
        Assert("highlighter opacity applied once", Math.Abs(png.GetPixel(60, 30).Alpha - 71) <= 2 && highlighter.Document.Elements[0].Style.Opacity == .8);
    }

    private static void SelectionAndTransforms()
    {
        var session = Session(); var shape = Rectangle(80, 60, new(50, 70)); session.Add(shape);
        var controller = new EditorController(session) { Tool = EditorTool.Select };
        var before = session.Document.Elements.Single().Transform;
        Gesture(controller, new(90, 100), new(110, 115));
        var moved = session.Document.Elements.Single();
        Assert("selection drag moves by document pixels", moved.Transform.OffsetX == before.OffsetX + 20 && moved.Transform.OffsetY == before.OffsetY + 15);
        int undo = session.UndoCount;
        var bounds = session.SelectionBounds()!.Value; var center = bounds.Center;
        Gesture(controller, new(center.X, bounds.Top - 28), new(center.X + 100, center.Y), OverlayModifiers.Shift);
        var rotated = session.Document.Elements.Single();
        double angle = Math.Atan2(rotated.Transform.M21, rotated.Transform.M11) * 180 / Math.PI;
        Assert("rotation handle snaps 90 degrees", Math.Abs(angle - 90) < 1e-8 && session.UndoCount == undo + 1);
        Assert("rotation leaves element center fixed", rotated.Transform.TransformPoint(new(40, 30)).DistanceTo(center) < 1e-8);
        session.Undo(); var beforeResize = session.Document.Elements.Single().Transform; bounds = session.SelectionBounds()!.Value; center = bounds.Center;
        Gesture(controller, new(bounds.Right, bounds.Bottom), center + (new PointD(bounds.Right, bounds.Bottom) - center) * 2);
        var resized = session.Document.Elements.Single();
        Assert("shape resize changes vector dimensions without scaling its stroke matrix", Math.Abs(resized.Transform.M11 - 1) < 1e-8 && Math.Abs(resized.Transform.M22 - 1) < 1e-8 && resized.Width == 160 && resized.Height == 120);
        Assert("resize remains undoable as one operation", session.Undo() && session.Document.Elements.Single().Transform == beforeResize);

        // Nonuniform resize after rotation must preserve perpendicular axes instead of introducing a shear.
        session.UpdateSelection(e => e.Transform = Affine2D.Translation(150, 90).Compose(Affine2D.Rotation(30)));
        bounds = session.SelectionBounds()!.Value; center = bounds.Center;
        Gesture(controller, new(bounds.Right, bounds.Bottom), new(bounds.Right + 35, bounds.Bottom + 10));
        var transform = session.Document.Elements.Single().Transform;
        Assert("rotated shape nonuniform resize preserves orthogonal local axes", Math.Abs(transform.M11 * transform.M12 + transform.M21 * transform.M22) < 1e-7);
        Assert("rotated shape resize preserves rotation", Math.Abs(Math.Atan2(transform.M21, transform.M11) * 180 / Math.PI - 30) < 1e-7);

        var lineSession = Session();
        var line = new CanvasElement { Kind = ElementKind.Arrow, Points = [new(0, 0), new(100, 0)], Transform = Affine2D.Translation(50, 100).Compose(Affine2D.Rotation(30)) };
        lineSession.Add(line); var lineController = new EditorController(lineSession) { Tool = EditorTool.Select };
        var oldEnd = line.Transform.TransformPoint(line.Points[^1]); var newEnd = oldEnd + new PointD(25, 40);
        Gesture(lineController, oldEnd, newEnd);
        var edited = lineSession.Document.Elements.Single();
        Assert("endpoint edit uses inverse element transform", edited.Transform.TransformPoint(edited.Points[^1]).DistanceTo(newEnd) < 1e-8 && edited.Transform == line.Transform);

        var multi = Session(); var first = Rectangle(40, 40, new(30, 50)); var second = Rectangle(40, 40, new(150, 50));
        multi.Add(first); multi.Add(second); multi.SetSelection([first.Id]);
        var multiselect = new EditorController(multi) { Tool = EditorTool.Select };
        Down(multiselect, new(170, 70), OverlayModifiers.Shift); Up(multiselect, new(170, 70), OverlayModifiers.Shift);
        Assert("Shift toggles a second selected object", multi.Selection.SetEquals([first.Id, second.Id]));
        var positions = multi.Document.Elements.Select(e => e.Transform).ToArray();
        Gesture(multiselect, new(50, 70), new(60, 85));
        Assert("multi-selection drag moves both objects", multi.Document.Elements.Select((e, i) => e.Transform.OffsetX == positions[i].OffsetX + 10 && e.Transform.OffsetY == positions[i].OffsetY + 15).All(v => v));
        multi.SetSelection([]); Gesture(multiselect, new(0, 0), new(240, 140));
        Assert("marquee selects intersecting visible objects", multi.Selection.SetEquals([first.Id, second.Id]));
        var groupBounds = multi.SelectionBounds()!.Value; var groupCenter = groupBounds.Center;
        Gesture(multiselect, new(groupBounds.Right, groupBounds.Bottom), groupCenter + (new PointD(groupBounds.Right, groupBounds.Bottom) - groupCenter) * 1.5);
        Assert("group resize preserves uniform aspect", multi.Document.Elements.All(e => Math.Abs(e.Transform.M11 - e.Transform.M22) < 1e-8));
        var imageSession = Session();
        imageSession.Add(new() { Kind = ElementKind.Image, Width = 100, Height = 60, Transform = Affine2D.Translation(80, 80) });
        var imageController = new EditorController(imageSession) { Tool = EditorTool.Select };
        var imageBounds = imageSession.SelectionBounds()!.Value; var imageCenter = imageBounds.Center;
        Gesture(imageController, new(imageBounds.Right, imageBounds.Bottom), new(imageBounds.Right + 40, imageBounds.Bottom + 15));
        var scaledImage = imageSession.Document.Elements.Single();
        Assert("image resize locks original aspect ratio", Near(scaledImage.Transform.M11, scaledImage.Transform.M22) && scaledImage.Transform.M11 > 1);
        imageSession.Undo(); imageBounds = imageSession.SelectionBounds()!.Value; imageCenter = imageBounds.Center;
        Gesture(imageController, new(imageBounds.Right, imageBounds.Bottom), imageCenter - (new PointD(imageBounds.Right, imageBounds.Bottom) - imageCenter));
        Assert("drag across resize center clamps instead of reflecting or growing", Near(imageSession.Document.Elements.Single().Transform.M11, .02) && Near(imageSession.Document.Elements.Single().Transform.M22, .02));
    }

    private static void CancellationAndReplacement()
    {
        var session = Session(); var controller = new EditorController(session);
        Down(controller, new(15, 15)); Move(controller, new(100, 100));
        Assert("pending draw visible before commit", session.Document.Elements.Count == 1 && session.IsTransactionActive);
        controller.Cancel();
        Assert("Escape cancellation restores document and history", session.Document.Elements.Count == 0 && session.UndoCount == 0 && !session.IsTransactionActive);
        Down(controller, new(15, 15)); Move(controller, new(100, 100)); controller.Tool = EditorTool.Select;
        Assert("tool switch cancels pending transaction", session.Document.Elements.Count == 0 && !session.IsTransactionActive);
        controller.Tool = EditorTool.Rectangle; Down(controller, new(15, 15)); Move(controller, new(60, 60));
        controller.Pointer(new(OverlayPointerKind.Cancel, new(0, 0), OverlayMouseButton.None, OverlayModifiers.None));
        Assert("native capture cancellation removes preview", session.Document.Elements.Count == 0 && controller.Marquee is null && controller.EffectPreviewPolygon is null);
        controller.Tool = EditorTool.Line; Down(controller, new(20, 20)); Move(controller, new(30, 30));
        var replacement = Session(); controller.ReplaceSession(replacement, new(1000, 500));
        Assert("session replacement cancels old gesture", !session.IsTransactionActive && session.Document.Elements.Count == 0 && ReferenceEquals(controller.Session, replacement));
        int changes = 0; controller.Changed += () => changes++;
        session.NotifyChanged(); Assert("old session events detached", changes == 0);
        replacement.NotifyChanged(); Assert("new session events subscribed exactly once", changes == 1);
        controller.Tool = EditorTool.Rectangle; Gesture(controller, new(1010, 520), new(1050, 550));
        Assert("new session applies screen origin", replacement.Document.Elements.Single().Transform.OffsetX == 10 && replacement.Document.Elements.Single().Transform.OffsetY == 20);
        Up(controller, new(1060, 560)); Assert("stray pointer up does not create a second operation", replacement.UndoCount == 1);
    }

    private static void EraserTransactions()
    {
        var session = Session(); var a = Rectangle(30, 30, new(20, 20)); var b = Rectangle(30, 30, new(100, 20)); var locked = Rectangle(30, 30, new(180, 20)); locked.Locked = true;
        session.Add(a); session.Add(b); session.Add(locked); int undo = session.UndoCount;
        var controller = new EditorController(session) { Tool = EditorTool.Eraser };
        Down(controller, new(30, 30)); Move(controller, new(110, 30)); Move(controller, new(190, 30)); Up(controller, new(200, 30));
        Assert("eraser affects unlocked objects in one transaction", session.Document.Elements.Count == 1 && session.Document.Elements[0].Id == locked.Id && session.UndoCount == undo + 1);
        session.Undo(); Assert("one undo restores entire eraser gesture", session.Document.Elements.Count == 3);
        Down(controller, new(30, 30)); Move(controller, new(110, 30)); controller.Cancel();
        Assert("cancel eraser restores all removed elements", session.Document.Elements.Count == 3 && !session.IsTransactionActive);
    }

    private static void ImageEffectsAndCrop()
    {
        // Frozen screenshot/image effects remain bound to their image. Live canvas
        // Pixelate has a separate desktop-region contract exercised below elsewhere.
        var session = Session(); session.Document.Kind = DocumentKind.FrozenCapture;
        using var source = new SKBitmap(100, 80);
        for (int y = 0; y < 80; y++) for (int x = 0; x < 100; x++) source.SetPixel(x, y, new((byte)(80 + x), (byte)(80 + y), 230));
        using var originalImage = SKImage.FromBitmap(source); using var data = originalImage.Encode(SKEncodedImageFormat.Png, 100);
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 80, AssetId = session.Assets.Add(data.ToArray()),
            Transform = Affine2D.Translation(180, 40).Compose(Affine2D.Rotation(30)), ImageCrop = new(.25, .2, .5, .6) };
        session.Add(image); var controller = new EditorController(session) { Tool = EditorTool.Blur };
        controller.Style.Stroke = 0xFF112233;
        PointD a = image.Transform.TransformPoint(new(20, 10)), b = image.Transform.TransformPoint(new(70, 50));
        Down(controller, a); Move(controller, b);
        var preview = controller.EffectPreviewPolygon!;
        Assert("rotated effect preview uses actual image-local corners", preview.Count == 4 && preview[0].DistanceTo(a) < 1e-8 && preview[2].DistanceTo(b) < 1e-8 && Math.Abs(preview[1].Y - preview[0].Y) > 10);
        Up(controller, b);
        var effect = session.Document.Elements.Single().Effects.Single();
        Assert("rotated cropped image effect maps to original normalized coordinates", Near(effect.Bounds.X, .35) && Near(effect.Bounds.Y, .275) && Near(effect.Bounds.Width, .25) && Near(effect.Bounds.Height, .3));
        // Existing saved solid-cover effects remain readable/renderable; new gestures use blur.
        effect.Kind = ImageEffectKind.Redact;
        using var renderer = new SceneRenderer();
        PointD center = image.Transform.TransformPoint(new(45, 30));
        using (var png = Decode(renderer.ExportPng(session.Document, session.Assets, true)))
        {
            var pixel = png.GetPixel((int)center.X, (int)center.Y);
            Assert("legacy saved solid effect still rasterizes its stored color on transformed target", pixel.Red == 17 && pixel.Green == 34 && pixel.Blue == 51);
        }
        controller.Tool = EditorTool.Crop; int undo = session.UndoCount;
        Gesture(controller, a, b);
        var cropped = session.Document.Elements.Single();
        Assert("image crop commits once and reduces local frame", cropped.Width == 50 && Near(cropped.Height, 40) && session.UndoCount == undo + 1);
        Assert("crop retains transformed source pixels at original world positions", cropped.Transform.TransformPoint(new(0, 0)).DistanceTo(a) < 1e-8 && cropped.Transform.TransformPoint(new(50, 40)).DistanceTo(b) < 1e-8);
        Assert("crop persists normalized source region", cropped.ImageCrop is { } crop && Near(crop.X, .35) && Near(crop.Y, .275) && Near(crop.Width, .25) && Near(crop.Height, .3));
        Assert("crop preserves effects in original image coordinates", cropped.Effects.Single().Bounds == effect.Bounds);
        using (var png = Decode(renderer.ExportPng(session.Document, session.Assets, true)))
            Assert("crop output retains previous target effect", png.GetPixel((int)center.X, (int)center.Y).Red == 17);
        session.Undo(); Assert("image crop undo restores prior frame and existing effect", session.Document.Elements.Single().Width == 100 && session.Document.Elements.Single().Effects.Count == 1);
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
        {
            controller.Tool = tool; Gesture(controller, a, b);
            Assert(tool + " binds effect to selected image", session.Document.Elements.Single().Effects.Last().Kind.ToString() == tool.ToString());
        }
        controller.Tool = EditorTool.Blur; Down(controller, a); Move(controller, b); controller.Cancel();
        Assert("cancel effect leaves saved effects unchanged", session.Document.Elements.Single().Effects.Count == 3 && controller.EffectPreviewPolygon is null);
        controller.Tool = EditorTool.Crop; Gesture(controller, a, b);
        var firstCrop = session.Document.Elements.Single();
        var nextA = firstCrop.Transform.TransformPoint(new(10, 10)); var nextB = firstCrop.Transform.TransformPoint(new(40, 30));
        Gesture(controller, nextA, nextB);
        var secondCrop = session.Document.Elements.Single();
        Assert("repeated crop composes source coordinates without re-encoding original", secondCrop.ImageCrop is { } repeated && Near(repeated.X, .4) && Near(repeated.Y, .35) && Near(repeated.Width, .15) && Near(repeated.Height, .15) && secondCrop.AssetId == image.AssetId && session.Assets.Count == 1);
        Assert("repeated rotated crop still preserves world position", secondCrop.Transform.TransformPoint(new(0, 0)).DistanceTo(nextA) < 1e-7 && Near(secondCrop.Width, 30) && Near(secondCrop.Height, 20));
        controller.Tool = EditorTool.Blur;
        Down(controller, nextA); Move(controller, nextB); controller.Tool = EditorTool.Select; Up(controller, nextB);
        Assert("switching effect tool cancels its pending region", secondCrop.Effects.Count == 3 && controller.EffectPreviewPolygon is null && !session.IsTransactionActive);
        var singular = Session(); singular.Document.Kind = DocumentKind.FrozenCapture; singular.Add(new() { Kind = ElementKind.Image, Width = 100, Height = 80, Transform = Affine2D.Scale(0, 1) });
        var invalidController = new EditorController(singular) { Tool = EditorTool.Blur }; int errors = 0; invalidController.Message += _ => errors++;
        Gesture(invalidController, new(0, 0), new(50, 50));
        Assert("singular image transforms rejected before effect gesture", errors == 1 && singular.Document.Elements[0].Effects.Count == 0 && invalidController.Marquee is null);
    }

    private static void DocumentRegions()
    {
        var session = Session(); var controller = new EditorController(session) { Tool = EditorTool.CaptureRegion, ScreenOrigin = new(1000, 500) };
        RectD? requested = null; controller.CaptureSelectionCompleted += selection => requested = selection.Bounds;
        Gesture(controller, new(1020, 530), new(1100, 600));
        Assert("capture region emits document coordinates without editing source", requested == new RectD(20, 30, 80, 70) && session.UndoCount == 0 && controller.Marquee is null);
        controller.ScreenOrigin = new(0, 0); controller.Tool = EditorTool.Crop; int messages = 0; controller.Message += _ => messages++;
        var original = session.Document.Viewport; Gesture(controller, new(20, 20), new(100, 100));
        Assert("live canvas crop without image returns to selection instead of shrinking desktop", controller.Tool == EditorTool.Select && session.Document.Viewport == original && session.UndoCount == 0);
        session.Document.Kind = DocumentKind.FrozenCapture;
        Gesture(controller, new(20, 30), new(120, 110));
        Assert("frozen screenshot background alone does not enable crop", controller.Tool == EditorTool.Select && session.Document.Viewport == original && session.UndoCount == 0);
        Gesture(controller, new(900, 900), new(950, 950));
        Assert("selection outside a frozen screenshot cannot crop its background", session.Document.Viewport == original && session.UndoCount == 0);
        controller.Tool = EditorTool.CaptureRegion; Down(controller, new(40, 40)); Move(controller, new(90, 90)); controller.Cancel(); requested = null; Up(controller, new(100, 100));
        Assert("cancelled region cannot trigger later capture", requested is null && controller.Marquee is null);
    }

    private static void TextDoubleClick()
    {
        var session = Session(); var text = new CanvasElement { Kind = ElementKind.Text, Text = "中文文字", Width = 160, Height = 70, Transform = Affine2D.Translation(50, 70) }; session.Add(text);
        var controller = new EditorController(session) { Tool = EditorTool.Select }; Guid? edited = null;
        controller.PropertiesRequested += _ => edited = session.Selection.SingleOrDefault();
        Down(controller, new(110, 100)); Move(controller, new(115, 105));
        controller.Pointer(new(OverlayPointerKind.DoubleClick, new(110, 100), OverlayMouseButton.Left, OverlayModifiers.None));
        Assert("text double click cancels transform before opening properties", edited == text.Id && !session.IsTransactionActive && session.Document.Elements.Single().Transform == text.Transform);
        var number = new CanvasElement { Kind = ElementKind.Number, Number = 5, Width = 40, Height = 40, Transform = Affine2D.Translation(250, 50) }; session.Add(number);
        controller.Pointer(new(OverlayPointerKind.DoubleClick, new(270, 70), OverlayMouseButton.Left, OverlayModifiers.None));
        Assert("number double click requests properties", edited == number.Id && !session.IsTransactionActive);
    }

    private static void RasterPreviewRanges()
    {
        var session = Session(); var controller = new EditorController(session);
        Assert("idle raster uses committed scene", controller.RasterPreview == (-1, -1, false));
        Down(controller, new(20, 20)); Move(controller, new(60, 60));
        Assert("new drawing alone is the dynamic span", controller.RasterPreview == (0, 0, false));
        Up(controller, new(60, 60));
        Assert("committed drawing leaves dynamic span", controller.RasterPreview == (-1, -1, false));
        session.Add(new() { Kind = ElementKind.Rectangle, Width = 40, Height = 40, Transform = Affine2D.Translation(120, 30) });
        session.Add(new() { Kind = ElementKind.Rectangle, Width = 40, Height = 40, Transform = Affine2D.Translation(220, 30) });
        session.SetSelection([session.Document.Elements[0].Id, session.Document.Elements[2].Id]);
        controller.Tool = EditorTool.Select; Down(controller, new(40, 40));
        Assert("noncontiguous selection keeps intervening z-order in dynamic span", controller.RasterPreview == (0, 2, false));
        controller.Cancel();
        controller.Tool = EditorTool.Eraser; Down(controller, new(30, 30));
        Assert("erasure bypasses raster reuse while list mutates", controller.RasterPreview.BypassCache);
        controller.Cancel();
        session.BeginTransaction("external preview"); session.NotifyChanged();
        Assert("unknown transactions bypass raster reuse", controller.RasterPreview.BypassCache);
        session.Cancel();
    }

    private static EditorSession Session() => new(new() { Viewport = new(0, 0, 500, 300) });
    private static CanvasElement Rectangle(double width, double height, PointD origin) => new() { Kind = ElementKind.Rectangle, Width = width, Height = height,
        Transform = Affine2D.Translation(origin.X, origin.Y), Style = new() { Fill = 0xFF2383E2, Stroke = 0xFF2383E2 } };
    private static void Gesture(EditorController c, PointD a, PointD b, OverlayModifiers modifiers = OverlayModifiers.None) { Down(c, a, modifiers); Move(c, b, modifiers); Up(c, b, modifiers); }
    private static void Down(EditorController c, PointD p, OverlayModifiers m = OverlayModifiers.None) => Send(c, OverlayPointerKind.Down, p, m);
    private static void Move(EditorController c, PointD p, OverlayModifiers m = OverlayModifiers.None) => Send(c, OverlayPointerKind.Move, p, m);
    private static void Up(EditorController c, PointD p, OverlayModifiers m = OverlayModifiers.None) => Send(c, OverlayPointerKind.Up, p, m);
    private static void Send(EditorController c, OverlayPointerKind kind, PointD p, OverlayModifiers m) => c.Pointer(new(kind, new(p.X, p.Y), OverlayMouseButton.Left, m));
    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png) ?? throw new Exception("PNG decode failed.");
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;
    private static int CountPixels(SKBitmap bitmap) { int count = 0; for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).Alpha > 0) count++; return count; }
    private static void Assert(string name, bool condition) { if (!condition) throw new InvalidOperationException("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
}
