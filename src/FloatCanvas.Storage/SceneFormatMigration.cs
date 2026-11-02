using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Storage;

/// <summary>Upgrades a source-validated, owned document without recalibrating its content.</summary>
internal static class SceneFormatMigration
{
    internal static bool Supports(int version) => version is >= 1 and <= SceneDocument.CurrentVersion;
    internal static bool SupportsPrefab(int version) => version is >= 11 and <= SceneDocument.CurrentVersion;

    internal static void Upgrade(SceneDocument document)
    {
        if (!Supports(document.Version)) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        // v8 made the saved reference and pin explicit. Never recalculate the homography.
        if (document.Version <= 7) CanvasCalibration.UpgradeLegacy(document.Canvas.Projection);
        // Legacy appearance readers resolve role and typography semantics before publication.
        // v9 already records the independent plane/grid. Running the old geometry
        // migration for v9 would replace intentionally independent source dimensions.
        if (document.Version <= 8) CanvasGeometryMigration.UpgradeLegacy(document.Canvas);
        // v1-v9 groups were ownership metadata over the original flat draw order.
        // Null ledgers and identity placement preserve A/B/C even when A/C form a group.
        if (document.Version <= 9)
        {
            document.RootOrder = null;
            foreach (var group in document.Groups)
            {
                group.Transform = Affine2D.Identity; group.ElevationScale = 1; group.Visible = true; group.Locked = false;
                group.OrderMode = GroupOrderMode.LegacyPassThrough; group.ChildOrder = null;
            }
        }
        AppearanceMigration.NormalizeDocument(document);
        // v16 introduces document root spaces. Missing earlier assignments mean
        // exactly Main; preserve the existing matrix, draw ledger and local geometry.
        // v13 adds Presentation, v14 adds optional window-source descriptors, and
        // v15 adds typed keyboard display styles and owned layouts. Missing earlier
        // fields deserialize as Log with a null (standard keyboard) layout; never
        // infer a new visual style or materialize 104 keys into legacy documents.
        // Neither migration alters earlier placement, palette, typography,
        // presentation configuration or the v11 resource identity.
        // v17 adds optional independent preview headers. Missing metadata stays
        // neutral; copying the enclosing project's perspective would change legacy
        // prefab editing semantics and leak host state into reusable content.
        document.Version = SceneDocument.CurrentVersion;
    }
    internal static void Upgrade(PrefabPackage package)
    {
        if (!SupportsPrefab(package.Version)) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", package.Version));
        foreach (var definition in package.Definitions) AppearanceMigration.NormalizeGraph(definition.Graph);
        package.Version = SceneDocument.CurrentVersion;
    }
}
