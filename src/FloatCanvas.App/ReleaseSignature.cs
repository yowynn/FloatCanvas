using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FloatCanvas.App;

internal sealed record ReleaseTrustKey(string KeyId, string PublicKeyPem);
internal sealed record ReleaseTrust(ReleaseTrustKey[] Keys, string[] AuthenticodeSha256)
{
    internal static ReleaseTrust Installed { get; } = ReadInstalled();
    private static ReleaseTrust ReadInstalled()
    {
        using var resource = typeof(ReleaseTrust).Assembly.GetManifestResourceStream("FloatCanvas.ReleaseTrust.json")
            ?? throw new InvalidDataException("The installed release trust configuration is missing.");
        return JsonSerializer.Deserialize<ReleaseTrust>(resource)
            ?? throw new InvalidDataException("The installed release trust configuration is empty.");
    }
}

internal sealed record SignedReleaseManifest(string KeyId, string Payload, string Signature);
internal sealed record ReleaseSignaturePayload(int SchemaVersion, string Product, string Version, string RuntimeIdentifier, string FileName, long Size, string Sha256);

/// <summary>Verifies publisher identity separately from GitHub transport and its same-source digest.</summary>
internal static class ReleaseSignature
{
    internal static ReleaseSignaturePayload Verify(byte[] document, ReleaseTrust trust, string version, string fileName, long size)
    {
        try
        {
            if (document.Length is 0 or > 32768) throw new InvalidDataException("The signed release manifest is missing or too large.");
            var envelope = JsonSerializer.Deserialize<SignedReleaseManifest>(document)
                ?? throw new InvalidDataException("The signed release manifest is empty.");
            var matches = (trust.Keys ?? Array.Empty<ReleaseTrustKey>()).Where(k => k.KeyId == envelope.KeyId).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("This release signing key is not trusted by the installed application.");
            byte[] payload = Convert.FromBase64String(envelope.Payload), signature = Convert.FromBase64String(envelope.Signature);
            using var rsa = RSA.Create(); rsa.ImportFromPem(matches[0].PublicKeyPem);
            if (rsa.KeySize < 3072 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("The publisher's release signature is invalid. No application files were changed.");
            var value = JsonSerializer.Deserialize<ReleaseSignaturePayload>(payload)
                ?? throw new InvalidDataException("The signed release payload is empty.");
            if (value.SchemaVersion != 1 || value.Product != "FloatCanvas" || value.Version != version ||
                value.RuntimeIdentifier != "win-x64" || value.FileName != fileName || value.Size != size ||
                value.Sha256 is null || value.Sha256.Length != 64 || value.Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("The signed release identity does not match this package, version, architecture or size.");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("The publisher's release signature could not be verified.", ex); }
    }
}
