using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FloatCanvas.App;

internal static class ReleaseSignatureTestData
{
    // Ephemeral process memory only; never a production signing identity.
    private static readonly RSA Key = RSA.Create(3072);
    internal static readonly ReleaseTrust Trust = new(new[] { new ReleaseTrustKey("ephemeral-test", Key.ExportSubjectPublicKeyInfoPem()) }, Array.Empty<string>());
    internal static ReleaseSignaturePayload Payload(byte[] data, string version = "1.0.0") =>
        new(1, "FloatCanvas", version, "win-x64", $"FloatCanvas-{version}-win-x64.zip", data.Length, Convert.ToHexString(SHA256.HashData(data)));
    internal static byte[] Sign(ReleaseSignaturePayload value, string keyId = "ephemeral-test")
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedReleaseManifest(keyId, Convert.ToBase64String(bytes), Convert.ToBase64String(Key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    }
    internal static byte[] Sign(byte[] data) => Sign(Payload(data));
}

internal static class ReleaseSignatureTests
{
    internal static void Run(Action<string, bool> check)
    {
        byte[] archive = { 1, 2, 3, 4 };
        var payload = ReleaseSignatureTestData.Payload(archive);
        var document = ReleaseSignatureTestData.Sign(payload);
        check("release publisher RSA-PSS signature authenticates exact payload", Verify(document).Sha256 == payload.Sha256);
        Reject("unknown signing KeyId rejected", ReleaseSignatureTestData.Sign(payload, "unknown"));
        Reject("revoked key is absent from trusted key list", document, new ReleaseTrust(Array.Empty<ReleaseTrustKey>(), Array.Empty<string>()));
        var envelope = JsonSerializer.Deserialize<SignedReleaseManifest>(document)!;
        byte[] signature = Convert.FromBase64String(envelope.Signature); signature[0] ^= 1;
        Reject("tampered detached signature rejected", JsonSerializer.SerializeToUtf8Bytes(envelope with { Signature = Convert.ToBase64String(signature) }));
        Reject("tampered payload with intact signature rejected", JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload with { Sha256 = new string('0', 64) })) }));
        foreach (var value in new[] { payload with { Version = "2.0.0" }, payload with { RuntimeIdentifier = "win-arm64" }, payload with { FileName = "different.zip" }, payload with { Size = 9 }, payload with { Product = "Other" }, payload with { SchemaVersion = 2 } })
            Reject("valid signature cannot authorize mismatched release identity " + value, ReleaseSignatureTestData.Sign(value));
        Reject("malformed envelope rejected", "{}"u8.ToArray());
        Reject("missing signature rejected", Array.Empty<byte>());
        check("installed trust never includes ephemeral test key", ReleaseTrust.Installed.Keys.All(k => k.KeyId != "ephemeral-test"));

        ReleaseSignaturePayload Verify(byte[] bytes, ReleaseTrust? trust = null) => ReleaseSignature.Verify(bytes, trust ?? ReleaseSignatureTestData.Trust, "1.0.0", payload.FileName, archive.Length);
        void Reject(string label, byte[] bytes, ReleaseTrust? trust = null)
        { try { Verify(bytes, trust); check(label, false); } catch (InvalidDataException) { check(label, true); } }
    }
}
