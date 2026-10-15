using System.IO;
using System.Text.Json;

namespace FloatCanvas.MouseTests;

internal static class SdkFormatCheck
{
    // Pure structure/constant validation: never constructs Application, windows, devices, hooks or input.
    internal static int Run(string sdkDumpPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(sdkDumpPath));
        JsonElement sdk = document.RootElement;
        JsonElement managed = JsonSerializer.SerializeToElement(DirectInputNative.LayoutEvidence());
        var checks = new List<object>(); bool all = true;
        void Check(string name, bool pass) { checks.Add(new { Name = name, Passed = pass }); all &= pass; }
        foreach (string key in new[] { "DataFormatBytes", "ObjectFormatBytes", "ObjectsPointerOffset", "MouseStateBytes", "CapabilitiesBytes" })
            Check(key, sdk.GetProperty(key).GetInt32() == managed.GetProperty(key).GetInt32());
        Check("HeaderFlagsDataSizeCount", sdk.GetProperty("Flags").GetUInt32() == 2 && sdk.GetProperty("DataSize").GetUInt32() == 20 && sdk.GetProperty("Count").GetInt32() == 11);
        DirectInputNative.MouseObjectDefinition[] definitions = DirectInputNative.StandardMouseObjects();
        JsonElement[] expected = sdk.GetProperty("Objects").EnumerateArray().ToArray();
        Check("ObjectCount", expected.Length == definitions.Length);
        for (int i = 0; i < Math.Min(definitions.Length, expected.Length); i++)
        {
            var actual = definitions[i]; var reference = expected[i];
            string? guid = reference.GetProperty("Guid").ValueKind == JsonValueKind.Null ? null : reference.GetProperty("Guid").GetString();
            Check($"Object{i}", reference.GetProperty("Offset").GetUInt32() == actual.Offset && reference.GetProperty("Type").GetUInt32() == actual.Type &&
                reference.GetProperty("Flags").GetUInt32() == actual.Flags && string.Equals(guid, actual.Guid, StringComparison.OrdinalIgnoreCase));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { TestOnly = true, NoWindowsDevicesHooksOrInput = true, SdkDump = Path.GetFullPath(sdkDumpPath),
            Passed = all, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        return all ? 0 : 1;
    }
}
