using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

/// <summary>Explicit, bounded, offscreen lifecycle stress. Never instantiated by the default benchmarks.</summary>
internal static class StabilityProbe
{
    private const int Width = 768, Height = 512, ImageSize = 256, ImagesPerRound = 3;
    private const int WarmupRounds = 10, CheckpointEvery = 10, HistoryCountLimit = 8;
    private const long MiB = 1024L * 1024;
    private const long HistoryByteLimit = 4 * MiB, ImageCacheLimit = 4 * MiB;
    // One visible flattened tile is Width * Height * 4; reserve exactly two tiles, not an unbounded cache.
    private const long RasterCacheLimit = Width * Height * 4L * 2;
    // Process budgets are regression tripwires, not attributed Skia counters or application RAM limits.
    private const long ManagedGrowthLimit = 32 * MiB, PrivateTailGrowthLimit = 96 * MiB;
    private const long PrivatePeakGrowthLimit = 128 * MiB, HandleTailGrowthLimit = 16, HandlePeakGrowthLimit = 32;
    private const double PrivateSlopeLimitBytesPerRound = 256 * 1024;

    public static int Run(string[] args)
    {
        int rounds = IntegerArgument(args, "--stability-rounds", 120);
        if (rounds is < 60 or > 2000 || rounds % CheckpointEvery != 0)
            throw new ArgumentException("--stability-rounds must be a multiple of 10 in [60, 2000].");
        string output = OutputPath(args);
        // Even a custom report location cannot redirect a probe into a user profile or existing project.
        string work = Path.Combine(Path.GetDirectoryName(output)!, "stability-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var started = DateTimeOffset.Now;
        var timer = Stopwatch.StartNew();
        var evidence = new Evidence();
        string? failure = null;
        string? incomplete = null;
        TrendResult? trend = null;
        MemorySample? disposedSample = null;
        Console.WriteLine($"Offscreen stability: {WarmupRounds} warm-up + {rounds} measured rounds. No windows, hooks, capture or user profile access.");
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This Windows resource/atomic-replace probe requires Windows.");
            Exercise(rounds, work, evidence);
            disposedSample = CollectSample(rounds, "all-owners-disposed");
            trend = AssessTrend(evidence.Checkpoints);
        }
        catch (ProbeIncompleteException ex) { incomplete = ex.Message; Console.Error.WriteLine(incomplete); }
        catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
        timer.Stop();
        bool passed = failure is null && incomplete is null && trend?.Passed == true && evidence.CompletedRounds == rounds;
        var report = new
        {
            schemaVersion = 1, probe = "bounded-offscreen-stability", startedAt = started, completedAt = DateTimeOffset.Now,
            durationSeconds = timer.Elapsed.TotalSeconds, passed, functionalChecksPassed = failure is null && incomplete is null,
            failure, fixtureIncomplete = incomplete, processId = Environment.ProcessId, workDirectory = work,
            environment = new { os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount,
                workDrive = DriveEnvironment(work) },
            workload = new { warmupRounds = WarmupRounds, requestedMeasuredRounds = rounds, completedMeasuredRounds = evidence.CompletedRounds,
                viewport = new { width = Width, height = Height }, imagesPerRound = ImagesPerRound, imageSize = ImageSize,
                reusableOwners = "One EditorSession/AssetLibrary, SceneRenderer, OverlaySceneCache and CPU SKSurface across every warm-up and measured round.",
                cleanup = "Retain assets required by undo/redo; verify undo pixels; explicitly replace the empty document to clear history; then prune, invalidate raster tiles and evict decoded/effect images.",
                storageEveryRounds = CheckpointEvery, explicitRecoveryDirectory = Path.Combine(work, "recovery"),
                noWindows = true, noInputHooks = true, noDesktopCapture = true, noUserProfile = true },
            ownershipBudgets = new { historyItems = HistoryCountLimit, historyBytes = HistoryByteLimit,
                rendererImageBytes = ImageCacheLimit, rasterBytes = RasterCacheLimit,
                rasterBudgetBasis = "768 * 512 * 4 bytes * 2 tiles; actual static scene requires one tile.",
                imageBudgetBasis = "Three 256-square BGRA images plus three equally sized effect outputs require at most 1,572,864 bytes before eviction, below the 4 MiB cache budget." },
            evidence.Counters, evidence.Checks, evidence.Peaks, evidence.Checkpoints, evidence.StorageRejections, disposedSample, trend,
            metricDefinitions = new
            {
                checkpoints = "After complete per-round release and two blocking full GCs separated by pending finalizers; long-lived renderer/surface still exist at normal checkpoints.",
                rendererOwnedBytes = "Production decoded/effect SKImage cache byte counter; rasterOwnedBytes is OverlaySceneCache's SKImage tile byte counter. These must reach zero after every round.",
                processMemory = "Private/working-set include runtime, managed/native allocators and OS effects; they cannot independently identify Skia allocations. HandleCount is a process OS-handle count, not an SKObject count.",
                managedBytes = "GC.GetTotalMemory(false) immediately after forced collections; heap/fragmentation describe the most recent GC.",
                thresholds = "Fixed before execution, not enlarged after a failure. Post-warm-up process trend budgets detect regression; they are not application memory promises. Working set is recorded only, because OS paging changes it independently."
            },
            limitations = new[]
            {
                "A bounded synthetic lifecycle run is not an hours-long session, every user asset, a complete native-allocation census, UI responsiveness or a game/fullscreen test.",
                "No real desktop, app profile, input, clipboard, display topology or capture device is touched.",
                "An open-reader replacement may be rejected by the filesystem. Only AccessDenied/sharing errors with exact prior-byte preservation, no temporary files and a successful unlocked retry are accepted and separately reported; no universal concurrent-replacement guarantee is inferred.",
                "Atomic-write checks cover concurrent project readers, a Windows destination lock, pre-cancellation and corrupt-current recovery; they do not simulate power loss or force-kill the writer mid-flush.",
                "Only the explicit --stability branch runs this workload; default benchmark/test duration is unchanged."
            }
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Stability {(incomplete is not null ? "INCOMPLETE" : passed ? "PASS" : "FAIL")}: {evidence.Checks.Values.Sum()} actual assertions, {evidence.CompletedRounds}/{rounds} measured rounds; report {output}");
        return passed ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exercise(int rounds, string work, Evidence evidence)
    {
        var assets = new AssetLibrary();
        var session = new EditorSession(EmptyDocument(), assets, HistoryCountLimit, HistoryByteLimit);
        using var renderer = new SceneRenderer { CacheByteLimit = ImageCacheLimit };
        using var cache = new OverlaySceneCache(renderer) { RasterCacheByteLimit = RasterCacheLimit };
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not allocate the isolated CPU target.");
        var recovery = new RecoveryStore(Path.Combine(work, "recovery"));
        for (int index = 1; index <= WarmupRounds + rounds; index++)
        {
            int measured = index - WarmupRounds;
            Round(index, measured, work, session, renderer, cache, surface, recovery, evidence);
            if (measured >= 0 && measured % CheckpointEvery == 0)
            {
                evidence.Checkpoints.Add(CollectSample(measured, measured == 0 ? "warm-baseline" : "released-owners-alive"));
                Console.WriteLine($"  checkpoint {measured}/{rounds}: assets/cache/history released; private {evidence.Checkpoints[^1].PrivateBytes / MiB} MiB");
            }
            if (measured > 0) evidence.CompletedRounds = measured;
        }
        cache.Dispose(); renderer.Dispose();
        evidence.Require(cache.CachedRasterBytes == 0 && renderer.CachedImageBytes == 0, "disposed_cache_ownership_zero");
    }

    // Keeping local byte arrays/documents inside a non-inlined call excludes JIT local lifetime from the checkpoint samples.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Round(int index, int measured, string work, EditorSession session, SceneRenderer renderer,
        OverlaySceneCache cache, SKSurface surface, RecoveryStore recovery, Evidence evidence)
    {
        renderer.CacheByteLimit = ImageCacheLimit;
        var ids = new List<string>();
        for (int image = 0; image < ImagesPerRound; image++)
        {
            byte[] original = GeneratedImage(index * ImagesPerRound + image);
            var normalized = AssetImport.Normalize(original);
            evidence.Require(normalized.Width == ImageSize && normalized.Height == ImageSize, "normalized_dimensions");
            string id = session.Assets.Add(normalized.PngBytes);
            evidence.Require(session.Assets.Add(normalized.PngBytes) == id, "content_addressed_deduplication");
            ids.Add(id); evidence.Counters.ImportedImages++; evidence.Counters.NormalizedBytes += normalized.PngBytes.LongLength;
        }
        session.Execute("Import isolated fixture", doc =>
        {
            for (int i = 0; i < ids.Count; i++) doc.Elements.Add(new CanvasElement
            {
                Kind = ElementKind.Image, AssetId = ids[i], Width = ImageSize, Height = ImageSize,
                Transform = Affine2D.Translation(20 + i * 210, 30 + i * 40),
                Effects = [new ImageEffect { Kind = (ImageEffectKind)(index % 3), Bounds = new(0.2, 0.2, 0.45, 0.45), Amount = 10 }]
            });
            doc.Elements.Add(new CanvasElement { Kind = ElementKind.Freehand,
                Points = Enumerable.Range(0, 1200).Select(p => new PointD(15 + p * 0.55, 390 + Math.Sin(p * 0.035) * 42)).ToList(),
                Style = new() { Stroke = 0xFF2674DC, StrokeWidth = 5 } });
            doc.Elements.Add(new CanvasElement { Kind = ElementKind.Text, Text = $"资源 round {index} · 编辑",
                Width = 440, Height = 48, Transform = Affine2D.Translation(30, 450), Style = new() { FontSize = 24 } });
            doc.LayerOpacity = index % 2 == 0 ? 0.65 : 1;
        });
        evidence.Require(session.Assets.Count == ImagesPerRound, "deduplicated_import_count");
        // Exceed the count limit with real edits. The image still belongs to the current scene even after its import entry expires.
        for (int move = 0; move < HistoryCountLimit * 3; move++)
        {
            session.Execute("Move fixture", doc => doc.Elements[0].Transform = Affine2D.Translation(1, 0).Compose(doc.Elements[0].Transform));
            ObserveOwnership(session, renderer, cache, evidence);
        }
        evidence.Require(session.UndoCount == HistoryCountLimit && session.RedoCount == 0, "history_oldest_entries_evicted");
        string sceneHash = JsonHash(session.Document);
        long decodes = renderer.ImageDecodeCount;
        string pixels = DrawHash(surface, cache, session, out int visible);
        evidence.Require(visible > 10000 && renderer.ImageDecodeCount >= decodes + ImagesPerRound, "new_images_really_decoded_and_visible");
        evidence.Require(renderer.CachedImageBytes == ImagesPerRound * ImageSize * ImageSize * 4L * 2 && cache.CachedRasterBytes == Width * Height * 4L,
            "known_decoded_effect_and_raster_ownership_accounted");
        ObserveOwnership(session, renderer, cache, evidence);
        long builds = cache.BuildCount, hits = cache.HitCount;
        string stablePixels = DrawHash(surface, cache, session, out _);
        evidence.Require(stablePixels == pixels && cache.BuildCount == builds && cache.HitCount > hits, "static_raster_cache_reused_with_identical_pixels");
        cache.RasterCacheByteLimit = Width * Height * 4L - 1;
        renderer.CacheByteLimit = ImageSize * ImageSize * 4L;
        string constrainedPixels = DrawHash(surface, cache, session, out _);
        evidence.Require(constrainedPixels == pixels && cache.CachedRasterBytes == 0 && renderer.CachedImageBytes <= renderer.CacheByteLimit,
            "active_scene_small_budget_evicts_without_pixel_change");
        renderer.CacheByteLimit = ImageCacheLimit; cache.RasterCacheByteLimit = RasterCacheLimit;
        if (index % CheckpointEvery == 0)
        {
            ExerciseByteBudget(session, evidence);
            StorageCycle(index, work, session, recovery, evidence).GetAwaiter().GetResult();
        }
        session.SelectAll(); session.DeleteSelection();
        session.Assets.Prune(session.ReferencedAssetsIncludingHistory());
        evidence.Require(session.Document.Elements.Count == 0 && session.Assets.Count == ImagesPerRound, "deleted_images_retained_by_undo_history");
        DrawHash(surface, cache, session, out visible);
        evidence.Require(visible == 0, "deleted_scene_is_transparent");
        evidence.Require(session.Undo(), "delete_can_undo");
        string undoPixels = DrawHash(surface, cache, session, out visible);
        evidence.Require(undoPixels == pixels && JsonHash(session.Document) == sceneHash && visible > 10000, "undo_restores_exact_scene_and_pixels");
        evidence.Require(session.Redo(), "delete_can_redo");
        DrawHash(surface, cache, session, out visible);
        evidence.Require(visible == 0, "redo_deletion_is_transparent");
        // This explicit new-document boundary intentionally discards undo/redo before pruning their resources.
        session.ReplaceDocument(EmptyDocument());
        session.Assets.Prune(session.ReferencedAssetsIncludingHistory());
        cache.Invalidate(); renderer.CacheByteLimit = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, session.Document, session.Assets);
        }
        evidence.Require(session.UndoCount == 0 && session.RedoCount == 0 && session.HistoryBytes == 0, "explicit_history_clear_zero");
        evidence.Require(session.Assets.Count == 0 && session.Assets.TotalBytes == 0 && !session.ReferencedAssetsIncludingHistory().Any(), "unreferenced_assets_pruned_to_zero");
        evidence.Require(renderer.CachedImageBytes == 0 && cache.CachedRasterBytes == 0, "decoded_effect_and_raster_caches_released_to_zero");
        evidence.Counters.CleanedRounds++;
        if (measured > 0) evidence.Counters.MeasuredImages += ImagesPerRound;
    }

    private static void ExerciseByteBudget(EditorSession source, Evidence evidence)
    {
        // The count cap cannot cause eviction here: fewer than 64 commits are submitted. A 1 MiB
        // total history budget includes the three shared incompressible image resources and snapshots.
        const int edits = 48;
        const long budget = MiB;
        var bounded = new EditorSession(source.Document.Clone(), source.Assets, 64, budget);
        for (int i = 0; i < edits; i++)
        {
            bounded.Execute("Byte-budget edit", doc => doc.Elements[0].Transform = Affine2D.Translation(1, 0).Compose(doc.Elements[0].Transform));
            evidence.Require(bounded.HistoryBytes <= budget && bounded.UndoCount + bounded.RedoCount <= 64, "byte_limited_history_stays_within_budget");
        }
        evidence.Require(bounded.UndoCount > 0 && bounded.UndoCount < edits, "byte_budget_alone_evicts_oldest_history");
        string latest = JsonHash(bounded.Document);
        evidence.Require(bounded.Undo() && JsonHash(bounded.Document) != latest && bounded.Redo() && JsonHash(bounded.Document) == latest,
            "byte_eviction_keeps_recent_undo_redo_usable");
        evidence.Counters.ByteBudgetEvictedEntries += edits - bounded.UndoCount;
        bounded.ReplaceDocument(EmptyDocument());
        evidence.Require(bounded.HistoryBytes == 0 && bounded.UndoCount == 0 && bounded.RedoCount == 0, "byte_limited_history_explicit_clear_zero");
    }

    private static async Task StorageCycle(int generation, string work, EditorSession session, RecoveryStore recovery, Evidence evidence)
    {
        var store = new ProjectStore();
        string projectPath = Path.Combine(work, "rotating-project.floatcanvas");
        SceneDocument committed = session.CreateCommittedSnapshot();
        session.BeginTransaction("Uncommitted preview must not recover");
        session.Document.Elements[0].Transform = Affine2D.Translation(100, 0).Compose(session.Document.Elements[0].Transform);
        SceneDocument snapshot = session.CreateCommittedSnapshot();
        evidence.Require(JsonHash(snapshot) == JsonHash(committed) && JsonHash(snapshot) != JsonHash(session.Document), "committed_snapshot_excludes_active_preview");
        session.Cancel();
        await store.SaveAsync(projectPath, snapshot, session.Assets);
        var loaded = await store.LoadAsync(projectPath);
        VerifyProject(loaded.Document, loaded.Assets, snapshot, session.Assets, evidence);
        var expected = Enumerable.Range(1, 4).Select(n => { var doc = snapshot.Clone(); doc.NextNumber = generation * 10 + n; return doc; }).ToArray();
        int readerObservations = 0, overlappingReads = 0;
        Task? pendingSave = null;
        bool writesComplete = false;
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSavePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowedHashes = expected.Append(snapshot).Select(JsonHash).ToHashSet(StringComparer.Ordinal);
        // Production readers share delete access, so replacing the ZIP must yield an old or new complete generation.
        Task reader = Task.Run(async () =>
        {
            for (int observation = 0; observation < 128 && !Volatile.Read(ref writesComplete); observation++)
            {
                bool pendingAtStart = Volatile.Read(ref pendingSave) is { IsCompleted: false };
                var read = await store.LoadAsync(projectPath);
                evidence.Require(allowedHashes.Contains(JsonHash(read.Document)), "concurrent_reader_observes_complete_known_generation");
                VerifyAssetBytes(read.Assets, snapshot.ReferencedAssets, session.Assets, evidence);
                Interlocked.Increment(ref readerObservations);
                if (pendingAtStart || Volatile.Read(ref pendingSave) is { IsCompleted: false }) Interlocked.Increment(ref overlappingReads);
                firstRead.TrySetResult();
                if (observation == 0) await firstSavePublished.Task;
            }
        });
        Exception? writerFailure = null, readerFailure = null;
        int completedWrites = 0;
        string beforeAttemptHash = FileHash(projectPath);
        try
        {
            await Task.WhenAny(firstRead.Task, reader);
            if (reader.IsFaulted) await reader;
            foreach (var doc in expected)
            {
                beforeAttemptHash = FileHash(projectPath);
                Task save = store.SaveAsync(projectPath, doc, session.Assets);
                Volatile.Write(ref pendingSave, save);
                firstSavePublished.TrySetResult();
                await save;
                completedWrites++;
            }
        }
        catch (Exception ex) { writerFailure = ex; }
        finally
        {
            Volatile.Write(ref pendingSave, null); Volatile.Write(ref writesComplete, true);
            firstSavePublished.TrySetResult();
            try { await reader; }
            catch (Exception ex) { readerFailure = ex; }
        }
        if (readerFailure is not null)
        {
            if (writerFailure is not null && !ReferenceEquals(writerFailure, readerFailure))
                throw new AggregateException("Project writer and concurrent reader both failed.", writerFailure, readerFailure);
            ExceptionDispatchInfo.Capture(readerFailure).Throw();
        }
        if (writerFailure is not null && !IsAccessOrSharingRejection(writerFailure)) ExceptionDispatchInfo.Capture(writerFailure).Throw();
        if (overlappingReads == 0)
            throw new ProbeIncompleteException("The fixture observed no project read overlapping a pending save task; atomic-replace acceptance is incomplete, not a storage failure.");
        evidence.Counters.ConcurrentProjectReaderObservations += readerObservations;
        evidence.Counters.ConcurrentProjectReads += overlappingReads;
        evidence.Counters.ConcurrentProjectSavesSucceeded += completedWrites;
        if (writerFailure is not null)
        {
            string afterRejectedHash = FileHash(projectPath);
            evidence.Require(afterRejectedHash == beforeAttemptHash, "reader_rejected_replace_preserves_exact_prior_bytes");
            evidence.Require(!Directory.EnumerateFiles(work, "*.tmp", SearchOption.AllDirectories).Any(), "reader_rejected_replace_cleans_temporary_file");
            // The reader task and all of its streams have now ended. Exactly one retry of the
            // failed generation is required; further generations are ordinary unlocked writes.
            await store.SaveAsync(projectPath, expected[completedWrites], session.Assets);
            var retried = await store.LoadAsync(projectPath);
            VerifyProject(retried.Document, retried.Assets, expected[completedWrites], session.Assets, evidence);
            evidence.StorageRejections.Add(RejectionEvidence("reader-open-replace", generation, writerFailure, beforeAttemptHash, afterRejectedHash));
            evidence.Counters.ConcurrentProjectSaveRejections++;
            evidence.Counters.UnlockedRetriesSucceeded++;
            foreach (var doc in expected.Skip(completedWrites + 1)) await store.SaveAsync(projectPath, doc, session.Assets);
        }
        loaded = await store.LoadAsync(projectPath);
        VerifyProject(loaded.Document, loaded.Assets, expected[^1], session.Assets, evidence);
        string beforeLock = FileHash(projectPath);
        Exception? lockRejection = null;
        using (var destinationLock = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.SaveAsync(projectPath, snapshot, session.Assets); }
            catch (Exception ex) when (IsAccessOrSharingRejection(ex)) { lockRejection = ex; }
            evidence.Require(lockRejection is not null && FileHash(projectPath) == beforeLock, "locked_atomic_project_replace_preserves_prior_bytes");
        }
        evidence.Require(!Directory.EnumerateFiles(work, "*.tmp", SearchOption.AllDirectories).Any(), "failed_project_replace_cleans_own_temporary_file");
        string afterLockHash = FileHash(projectPath);
        await store.SaveAsync(projectPath, snapshot, session.Assets);
        loaded = await store.LoadAsync(projectPath);
        VerifyProject(loaded.Document, loaded.Assets, snapshot, session.Assets, evidence);
        evidence.StorageRejections.Add(RejectionEvidence("intentional-destination-lock", generation,
            lockRejection ?? throw new InvalidOperationException("Expected destination lock rejection was not observed."), beforeLock, afterLockHash));
        evidence.Counters.IntentionalLockRejections++;
        evidence.Counters.UnlockedRetriesSucceeded++;
        var states = Enumerable.Range(1, 3).Select(n => new WorkspaceState
        {
            LiveDocument = WithNumber(snapshot, generation * 10 + n),
            CaptureDocument = snapshot.CreateCapture(new(0, 0, Width, Height), snapshot.ReferencedAssets.First()),
            CaptureOrigin = new(-generation, generation)
        }).ToArray();
        // Submit without awaiting each save: the real recovery gate must preserve submission order.
        await Task.WhenAll(states.Select(state => recovery.SaveAsync(state, session.Assets)));
        var recovered = await recovery.LoadAsync() ?? throw new InvalidDataException("Recovery unexpectedly empty.");
        evidence.Require(!recovered.FromBackup && JsonHash(recovered.State) == JsonHash(states[^1]), "queued_recovery_writes_keep_submission_order");
        VerifyAssetBytes(recovered.Assets, snapshot.ReferencedAssets, session.Assets, evidence);
        string currentHash = FileHash(recovery.CurrentPath), backupHash = FileHash(recovery.BackupPath);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool cancellationObserved = false;
            try { await recovery.SaveAsync(states[0], session.Assets, canceled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            evidence.Require(cancellationObserved && FileHash(recovery.CurrentPath) == currentHash && FileHash(recovery.BackupPath) == backupHash,
                "precanceled_recovery_preserves_both_generations");
        }
        // Deliberate damage is confined to the probe-owned current generation, never a user file.
        await File.WriteAllBytesAsync(recovery.CurrentPath, [0x46, 0x43, 0x00, 0xFF]);
        string corruptHash = FileHash(recovery.CurrentPath);
        recovered = await recovery.LoadAsync() ?? throw new InvalidDataException("Valid backup was lost.");
        evidence.Require(recovered.FromBackup && JsonHash(recovered.State) == JsonHash(states[^2]), "corrupt_current_recovers_exact_previous_complete_workspace");
        evidence.Require(FileHash(recovery.CurrentPath) == corruptHash && FileHash(recovery.BackupPath) == backupHash, "recovery_read_does_not_rewrite_evidence");
        await recovery.SaveAsync(states[^1], session.Assets);
        evidence.Require(FileHash(recovery.BackupPath) == backupHash, "corrupt_current_not_promoted_over_valid_backup");
        recovered = await recovery.LoadAsync() ?? throw new InvalidDataException("Saved recovery is missing.");
        evidence.Require(!recovered.FromBackup && JsonHash(recovered.State) == JsonHash(states[^1]), "recovery_resumes_after_corrupt_generation");
        evidence.Require(!Directory.EnumerateFiles(work, "*.tmp", SearchOption.AllDirectories).Any(), "storage_cycle_leaves_no_temporary_files");
        evidence.Counters.StorageCycles++;
    }

    private static void VerifyProject(SceneDocument actual, AssetLibrary actualAssets, SceneDocument expected, AssetLibrary expectedAssets, Evidence evidence)
    {
        evidence.Require(JsonHash(actual) == JsonHash(expected), "project_roundtrip_preserves_complete_scene");
        VerifyAssetBytes(actualAssets, expected.ReferencedAssets, expectedAssets, evidence);
    }
    private static void VerifyAssetBytes(AssetLibrary actual, IEnumerable<string> expectedIds, AssetLibrary expected, Evidence evidence)
    {
        var ids = expectedIds.ToArray();
        evidence.Require(actual.Count == ids.Length && ids.All(id => actual.TryGet(id, out var value) && value.Data.Span.SequenceEqual(expected.Get(id).Data.Span)),
            "stored_resources_match_original_bytes");
    }
    private static SceneDocument WithNumber(SceneDocument source, int number) { var result = source.Clone(); result.NextNumber = number; return result; }
    private static SceneDocument EmptyDocument() => new() { Viewport = new(0, 0, Width, Height) };

    private static void ObserveOwnership(EditorSession session, SceneRenderer renderer, OverlaySceneCache cache, Evidence evidence)
    {
        evidence.Require(session.UndoCount + session.RedoCount <= HistoryCountLimit && session.HistoryBytes <= HistoryByteLimit, "history_within_configured_budget");
        evidence.Require(renderer.CachedImageBytes <= ImageCacheLimit && cache.CachedRasterBytes <= RasterCacheLimit, "render_caches_within_configured_budget");
        evidence.Peaks.AssetBytes = Math.Max(evidence.Peaks.AssetBytes, session.Assets.TotalBytes);
        evidence.Peaks.HistoryBytes = Math.Max(evidence.Peaks.HistoryBytes, session.HistoryBytes);
        evidence.Peaks.HistoryEntries = Math.Max(evidence.Peaks.HistoryEntries, session.UndoCount + session.RedoCount);
        evidence.Peaks.RendererOwnedImageBytes = Math.Max(evidence.Peaks.RendererOwnedImageBytes, renderer.CachedImageBytes);
        evidence.Peaks.RasterOwnedBytes = Math.Max(evidence.Peaks.RasterOwnedBytes, cache.CachedRasterBytes);
    }
    private static string DrawHash(SKSurface surface, OverlaySceneCache cache, EditorSession session, out int visible)
    {
        surface.Canvas.Clear(SKColors.Transparent);
        cache.DrawOnClearedRasterTarget(surface, session.Document, session.Assets, session.SceneRevision, session.TransactionId);
        surface.Canvas.Flush();
        using var pixels = surface.PeekPixels() ?? throw new InvalidOperationException("Cannot read probe raster.");
        var span = pixels.GetPixelSpan(); visible = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++) if (span[y * pixels.RowBytes + x * 4 + 3] != 0) visible++;
        return Convert.ToHexStringLower(SHA256.HashData(span));
    }
    private static byte[] GeneratedImage(int seed)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(ImageSize, ImageSize, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var pixels = bitmap.PeekPixels();
        var span = pixels.GetPixelSpan(); uint state = (uint)seed + 0x9E3779B9;
        for (int y = 0; y < ImageSize; y++) for (int x = 0; x < ImageSize; x++)
        {
            int at = y * pixels.RowBytes + x * 4;
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            bool transparent = x < 8 || y < 8 || x >= ImageSize - 8 || y >= ImageSize - 8;
            span[at] = transparent ? (byte)0 : (byte)state;
            span[at + 1] = transparent ? (byte)0 : (byte)(state >> 8);
            span[at + 2] = transparent ? (byte)0 : (byte)(state >> 16);
            span[at + 3] = transparent ? (byte)0 : (byte)255;
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Cannot encode generated fixture.");
        return data.ToArray();
    }
    private static MemorySample CollectSample(int round, string phase)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new(round, phase, DateTimeOffset.Now, GC.GetTotalMemory(false), gc.HeapSizeBytes, gc.FragmentedBytes,
            process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount);
    }
    private static TrendResult AssessTrend(IReadOnlyList<MemorySample> samples)
    {
        var baseline = samples[0];
        long maxManagedGrowth = samples.Max(s => s.ManagedBytes) - baseline.ManagedBytes;
        long maxPrivateGrowth = samples.Max(s => s.PrivateBytes) - baseline.PrivateBytes;
        long privateTailGrowth = Median(samples.TakeLast(3).Select(s => s.PrivateBytes)) - baseline.PrivateBytes;
        long maxHandleGrowth = samples.Max(s => s.HandleCount) - baseline.HandleCount;
        long handleTailGrowth = Median(samples.TakeLast(3).Select(s => (long)s.HandleCount)) - baseline.HandleCount;
        double meanX = samples.Average(s => s.Round), meanY = samples.Average(s => (double)s.PrivateBytes);
        double slope = samples.Sum(s => (s.Round - meanX) * (s.PrivateBytes - meanY)) / samples.Sum(s => Math.Pow(s.Round - meanX, 2));
        var checks = new Dictionary<string, bool>
        {
            ["managed_post_gc_peak_growth"] = maxManagedGrowth <= ManagedGrowthLimit,
            ["private_post_gc_tail_growth"] = privateTailGrowth <= PrivateTailGrowthLimit,
            ["private_post_gc_peak_growth"] = maxPrivateGrowth <= PrivatePeakGrowthLimit,
            ["private_linear_slope"] = slope <= PrivateSlopeLimitBytesPerRound,
            ["handle_tail_growth"] = handleTailGrowth <= HandleTailGrowthLimit,
            ["handle_peak_growth"] = maxHandleGrowth <= HandlePeakGrowthLimit
        };
        return new(checks.Values.All(p => p), checks,
            new { maxManagedGrowth, managedGrowthLimit = ManagedGrowthLimit, privateTailGrowth, privateTailGrowthLimit = PrivateTailGrowthLimit,
                maxPrivateGrowth, privatePeakGrowthLimit = PrivatePeakGrowthLimit, privateSlopeBytesPerRound = slope, privateSlopeLimitBytesPerRound = PrivateSlopeLimitBytesPerRound,
                handleTailGrowth, handleTailGrowthLimit = HandleTailGrowthLimit, maxHandleGrowth, handlePeakGrowthLimit = HandlePeakGrowthLimit });
    }
    private static long Median(IEnumerable<long> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
    private static string JsonHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static bool IsAccessOrSharingRejection(Exception ex) => ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xFFFF) is 5 or 32 or 33;
    private static object RejectionEvidence(string stage, int generation, Exception exception, string before, string after) => new
    {
        stage, generation, exceptionType = exception.GetType().FullName,
        hresult = $"0x{unchecked((uint)exception.HResult):X8}", win32Error = exception.HResult & 0xFFFF,
        beforeSha256 = before, afterSha256 = after, priorBytesPreserved = before == after,
        noTemporaryFiles = true, unlockedRetrySucceeded = true
    };
    private static object DriveEnvironment(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return new { root = drive.Name, type = drive.DriveType.ToString(), format = drive.DriveFormat };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new { queryError = ex.GetType().Name + ": " + ex.Message }; }
    }
    private static int IntegerArgument(string[] args, string name, int fallback)
    {
        int at = Array.IndexOf(args, name);
        return at < 0 ? fallback : at + 1 < args.Length && int.TryParse(args[at + 1], out int number) ? number : throw new ArgumentException($"{name} requires an integer.");
    }
    private static string OutputPath(string[] args)
    {
        int at = Array.IndexOf(args, "--output");
        string root = Program.RepositoryRoot();
        string path = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : Path.Combine(root, "artifacts", "e2e", "stability-probe.json");
        bool contained = new[] { "artifacts", ".local" }.Any(name => path.StartsWith(Path.Combine(root, name) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!contained) throw new ArgumentException("Stability output must remain beneath this repository's artifacts or .local directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path;
    }
    private sealed class Evidence
    {
        private readonly object checkGate = new();
        public Dictionary<string, int> Checks { get; } = new(StringComparer.Ordinal);
        public Counters Counters { get; } = new();
        public Peaks Peaks { get; } = new();
        public List<MemorySample> Checkpoints { get; } = [];
        public List<object> StorageRejections { get; } = [];
        public int CompletedRounds { get; set; }
        public void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Stability assertion failed: " + name);
            lock (checkGate) Checks[name] = Checks.GetValueOrDefault(name) + 1;
        }
    }
    private sealed class Counters
    {
        public int ImportedImages { get; set; }
        public int MeasuredImages { get; set; }
        public long NormalizedBytes { get; set; }
        public int CleanedRounds { get; set; }
        public int StorageCycles { get; set; }
        public int ConcurrentProjectReads { get; set; }
        public int ConcurrentProjectReaderObservations { get; set; }
        public int ByteBudgetEvictedEntries { get; set; }
        public int ConcurrentProjectSavesSucceeded { get; set; }
        public int ConcurrentProjectSaveRejections { get; set; }
        public int IntentionalLockRejections { get; set; }
        public int UnlockedRetriesSucceeded { get; set; }
    }
    private sealed class Peaks
    {
        public long AssetBytes { get; set; }
        public long HistoryBytes { get; set; }
        public int HistoryEntries { get; set; }
        public long RendererOwnedImageBytes { get; set; }
        public long RasterOwnedBytes { get; set; }
    }
    private sealed record MemorySample(int Round, string Phase, DateTimeOffset Time, long ManagedBytes, long ManagedHeapBytes,
        long FragmentedBytes, long PrivateBytes, long WorkingSetBytes, int HandleCount);
    private sealed record TrendResult(bool Passed, Dictionary<string, bool> Checks, object Measurements);
    private sealed class ProbeIncompleteException(string message) : Exception(message);
}
