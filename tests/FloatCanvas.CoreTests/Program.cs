using FloatCanvas.Core;
using System.Text.Json;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 1e-8, $"{message}: {actual} != {expected}");
if (args.Contains("--canvas-only")) { CanvasProjectionTests.Run(Check); Console.WriteLine($"PASS {checks} canvas core checks"); return; }
CanvasElement Box(double x = 0, double y = 0) => new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF0000FF, StrokeWidth = 2 } };

// Affine order, rotation, inversion and negative desktop coordinates.
var affine = Affine2D.Translation(-1920, 350).Compose(Affine2D.Rotation(37)).Compose(Affine2D.Scale(2, .75));
PointD original = new(24, 17), transformed = affine.TransformPoint(original), restored = affine.Inverse().TransformPoint(transformed);
Near(restored.X, original.X, "affine inverse X"); Near(restored.Y, original.Y, "affine inverse Y");
Near(Affine2D.Translation(10, 20).Compose(Affine2D.Scale(2, 3)).TransformPoint(new(2, 3)).X, 14, "composition order");
Check(!Affine2D.Scale(0, 1).TryInverse(out _), "singular matrix must not be invertible");
Near(new RectD(8, 9, -4, -5).Normalize().X, 4, "normalize reversed rect");
Check(new RectD(-100, -100, 200, 200).Contains(new(0, 0)), "negative desktop bounds");

// Every element shares the same transform contract; geometry never bakes transforms into paths.
foreach (ElementKind kind in Enum.GetValues<ElementKind>())
{
    var element = Box(); element.Kind = kind; element.Text = "浮绘\n编辑"; element.Number = 7;
    if (ElementGeometry.UsesPoints(kind)) element.Points = [new(0, 0), new(100, 60)];
    var session = new EditorSession(); session.Add(element);
    session.TransformSelection(affine);
    Check(session.Document.Elements[0].Transform == affine, $"{kind}: transform retained");
    Check(session.Document.Elements[0].Points.SequenceEqual(element.Points), $"{kind}: points unchanged");
    Check(session.Undo() && session.Document.Elements[0].Transform == Affine2D.Identity, $"{kind}: undo transform");
    Check(session.Redo() && session.Document.Elements[0].Transform == affine, $"{kind}: redo transform");
}

var rotatedBox = Box(); rotatedBox.Transform = Affine2D.Translation(300, 200).Compose(Affine2D.Rotation(45));
Check(ElementGeometry.HitTest(rotatedBox, rotatedBox.Transform.TransformPoint(new(50, 30))), "rotated filled rectangle center");
Check(!ElementGeometry.HitTest(rotatedBox, rotatedBox.Transform.TransformPoint(new(130, 30))), "rotated rectangle outside");
rotatedBox.Style.Fill = 0;
Check(!ElementGeometry.HitTest(rotatedBox, rotatedBox.Transform.TransformPoint(new(50, 30))), "unfilled shape interior must not steal hit");
Check(ElementGeometry.HitTest(rotatedBox, rotatedBox.Transform.TransformPoint(new(100, 30))), "rotated rectangle edge");
var line = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(0, 0), new(80, 40), new(20, 80)], Transform = affine };
Check(ElementGeometry.HitTest(line, affine.TransformPoint(new(40, 20))), "transformed freehand segment hit");
Check(!ElementGeometry.HitTest(line, affine.TransformPoint(new(120, 120))), "transformed freehand miss");
var stretchedLine = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(100, 0)], Transform = Affine2D.Scale(100, 1), Style = new() { StrokeWidth = 4 } };
Check(ElementGeometry.HitTest(stretchedLine, new(500, 1), 0), "stretched stroke inside");
Check(!ElementGeometry.HitTest(stretchedLine, new(500, 100), 0), "nonuniform scale must not inflate perpendicular hit width");
Check(!ElementGeometry.HitTest(stretchedLine, new(-500, 0), 4), "nonuniform cap tolerance uses physical distance");
Check(ElementGeometry.HitTest(stretchedLine, new(500, 5), 4), "hit tolerance measured in physical pixels");
stretchedLine.Points = [new(0, 0), new(100, 100)];
Check(!ElementGeometry.HitTest(stretchedLine, new(500, 50), 0), "nonuniform diagonal stroke excludes broad-phase false positive");
var ellipse = Box(); ellipse.Kind = ElementKind.Ellipse;
Check(!ElementGeometry.HitTest(ellipse, new(1, 1), 0), "ellipse corner outside");
Check(ElementGeometry.HitTest(ellipse, new(50, 30), 0), "ellipse center filled");
var triangle = Box(); triangle.Kind = ElementKind.Triangle;
Check(!ElementGeometry.HitTest(triangle, new(1, 1), 0), "triangle corner outside");
Check(ElementGeometry.HitTest(triangle, new(50, 45), 0), "triangle center filled");

// Selection resolves topmost unlocked visible element and group order is stable.
CanvasElement a = Box(), b = Box(), c = Box();
var editor = new EditorSession(new() { Elements = [a, b, c] });
Check(editor.HitTest(new(30, 30))!.Id == c.Id, "topmost hit"); c.Locked = true;
Check(editor.HitTest(new(30, 30))!.Id == b.Id, "locked skipped"); b.Visible = false;
Check(editor.HitTest(new(30, 30))!.Id == a.Id, "hidden skipped"); b.Visible = true; c.Locked = false;
editor.SetSelection([a.Id, b.Id]); editor.ReorderSelection(LayerOrder.Forward);
Check(editor.Document.Elements.Select(e => e.Id).SequenceEqual([c.Id, a.Id, b.Id]), "multi-selection moves forward as a stable block");
editor.Undo(); editor.ReorderSelection(LayerOrder.Back);
Check(editor.Document.Elements.Select(e => e.Id).SequenceEqual([a.Id, b.Id, c.Id]), "send back preserves internal order");
editor.DuplicateSelection();
Check(editor.Document.Elements.Count == 5 && editor.Selection.Count == 2, "duplicate group");
Check(editor.Document.Elements.Select(e => e.Id).Distinct().Count() == 5, "clones receive unique IDs");
editor.DeleteSelection(); Check(editor.Document.Elements.Count == 3, "delete selected clones"); editor.Undo(); Check(editor.Document.Elements.Count == 5, "undo deletion restores group");

// Cancel restores geometry and selection; exceptions roll back the complete gesture.
editor.SetSelection([a.Id]); var before = editor.Document.Clone();
editor.BeginTransaction("drag"); editor.ApplySelectionTransform(Affine2D.Translation(50, 20)); editor.Document.Elements.Clear(); editor.Selection.Clear(); editor.Cancel();
Check(JsonSerializer.Serialize(editor.Document) == JsonSerializer.Serialize(before), "cancel restores document");
Check(editor.Selection.SetEquals([a.Id]), "cancel restores selection");
int previousUndo = editor.UndoCount;
try { editor.Execute("failing action", d => { d.Elements.Clear(); throw new InvalidDataException(); }); }
catch (InvalidDataException) { }
Check(editor.Document.Elements.Count == before.Elements.Count && previousUndo == editor.UndoCount, "failed operation is atomic");
editor.BeginTransaction("eraser stroke"); editor.Document.Elements.RemoveRange(0, 2); editor.Commit();
Check(editor.UndoCount == previousUndo + 1, "multiple erase removals one history step"); editor.Undo(); Check(editor.Document.Elements.Count == 5, "undo eraser stroke");
int afterUndo = editor.UndoCount, afterRedo = editor.RedoCount;
editor.Execute("zero move", _ => editor.ApplySelectionTransform(Affine2D.Identity));
Check(editor.UndoCount == afterUndo && editor.RedoCount == afterRedo, "no-op preserves history and redo branch");

// Screenshot branches contain whole geometry, own every mutable object, and share immutable assets.
var assetBytes = new byte[] { 1, 2, 3, 4, 5 };
var assets = new AssetLibrary(); string assetId = assets.Add(assetBytes); assetBytes[0] = 9;
Check(assets.Get(assetId).Data.Span[0] == 1, "asset input copied once to immutable resource");
Check(assets.Add([1, 2, 3, 4, 5]) == assetId && assets.Count == 1, "identical assets deduplicated by hash");
var image = new CanvasElement { Kind = ElementKind.Image, AssetId = assetId, Width = 200, Height = 200, Transform = Affine2D.Translation(-50, -50), Effects = [new() { Kind = ImageEffectKind.Redact, Bounds = new(.2, .3, .1, .2) }] };
var partialLine = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(-100, 0), new(200, 100)] };
var hidden = Box(); hidden.Visible = false;
var source = new SceneDocument { Elements = [image, partialLine, Box(400, 400), hidden], Viewport = new(-1920, 0, 3840, 1080), NextNumber = 9 };
var capture = source.CreateCapture(new(10, 10, 100, 80), assetId);
Check(capture.Kind == DocumentKind.FrozenCapture && capture.Elements.Count == 2, "capture intersecting objects only");
Check(capture.Viewport == new RectD(0, 0, 100, 80) && capture.Background!.AssetId == assetId, "capture background and normalized viewport");
Check(capture.Elements[0].Id != image.Id && capture.Elements[0].Effects[0].Id != image.Effects[0].Id, "capture unique object and effect IDs");
Check(capture.Elements[1].Points.SequenceEqual(partialLine.Points), "capture does not cut geometry at crop");
Near(capture.Elements[0].Transform.OffsetX, -60, "capture coordinate translation");
capture.Elements[0].Effects[0].Amount = 80; capture.Elements[1].Points.Clear();
Check(image.Effects[0].Amount == 12 && partialLine.Points.Count == 2, "capture editing isolated from live source");

// Number deletion never renumbers other objects or decreases the next counter.
var numbering = new EditorSession();
numbering.Add(new() { Kind = ElementKind.Number, Number = 1 }); numbering.Add(new() { Kind = ElementKind.Number, Number = 8 });
numbering.DeleteSelection();
Check(numbering.Document.NextNumber == 9 && numbering.Document.Elements[0].Number == 1, "number sequence does not collapse after deletion");

// Both history stacks share one limit; geometry history references image IDs, never image bytes.
var history = new EditorSession(new() { Elements = [image.Clone()] }, assets, historyLimit: 3, historyByteLimit: 20_000);
for (int i = 0; i < 8; i++) history.Execute($"opacity {i}", d => d.LayerOpacity = i / 10.0);
Check(history.UndoCount == 3 && history.RedoCount == 0, "oldest operations discarded");
history.Undo(); history.Undo();
Check(history.UndoCount + history.RedoCount == 3 && history.HistoryBytes <= history.HistoryByteLimit, "undo and redo share history budget");
history.Execute("branch", d => d.LayerOpacity = .9);
Check(!history.CanRedo, "new edits clear redo branch");
Check(assets.Count == 1 && assets.TotalBytes == 5, "history never duplicates asset bytes");
var tinyBudget = new EditorSession(new() { Elements = [image.Clone()] }, assets, 100, 1);
tinyBudget.Execute("change", d => d.LayerOpacity = .3);
Check(!tinyBudget.CanUndo && tinyBudget.HistoryBytes == 0, "oversized operation discarded under budget");
var retained = new EditorSession(new() { Elements = [image.Clone()] }, assets);
retained.SelectAll(); retained.DeleteSelection();
Check(retained.ReferencedAssetsIncludingHistory().Contains(assetId), "deleted image retained while undo can restore it");
Check(retained.HistoryBytes >= assets.TotalBytes, "history budget counts referenced image bytes");
var hugeAssets = new AssetLibrary(); var hugeId = hugeAssets.Add(new byte[100_000]);
var hugeImage = image.Clone(); hugeImage.AssetId = hugeId;
var hugeHistory = new EditorSession(new() { Elements = [hugeImage] }, hugeAssets, 100, 5_000);
hugeHistory.SelectAll(); hugeHistory.DeleteSelection();
Check(!hugeHistory.CanUndo && !hugeHistory.ReferencedAssetsIncludingHistory().Contains(hugeId), "oversized deleted asset does not stay pinned by undo");
hugeAssets.Prune(hugeHistory.ReferencedAssetsIncludingHistory());
Check(hugeAssets.TotalBytes == 0, "unreferenced oversized resource collectible by shared library owner");

// Versioned models round-trip every element and do not serialize computed convenience properties.
var document = new SceneDocument { Background = image.Clone(), Elements = Enum.GetValues<ElementKind>().Select(kind => new CanvasElement { Kind = kind, Width = 123, Height = 45, Transform = affine, Text = "浮绘\nsecond line", AssetId = assetId, ImageCrop = new(.1, .2, .7, .6), Points = [new(-1, 2), new(5, 8)], Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.1, .2, .3, .4) }] }).ToList() };
string serialized = JsonSerializer.Serialize(document);
var roundTrip = JsonSerializer.Deserialize<SceneDocument>(serialized)!;
Check(serialized == JsonSerializer.Serialize(roundTrip), "scene exact JSON round-trip");
Check(!serialized.Contains("ReferencedAssets") && !serialized.Contains("MaximumScale"), "computed model properties excluded from format");
ArrowHeadTests.Run(Check);
ArrowHeadTests.CommittedSnapshots(Check);
EditablePresetTests.Run(Check);
SceneRevisionTests.Run(Check);
CanvasProjectionTests.Run(Check);
Console.WriteLine($"PASS {checks} core checks");
