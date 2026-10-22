using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Cix.Exchange.RevolutX;

/// <summary>Ed25519 signing with BouncyCastle (pure managed, works on App Service Linux and Windows).</summary>
public sealed class BouncyCastleEd25519Signer : IEd25519Signer
{
    private readonly Ed25519PrivateKeyParameters _key;

    private BouncyCastleEd25519Signer(Ed25519PrivateKeyParameters key) => _key = key;

    /// <summary>Loads an unencrypted PKCS#8 PEM, as produced by <c>openssl genpkey -algorithm ed25519</c>.</summary>
    public static BouncyCastleEd25519Signer FromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        const string label = "PRIVATE KEY";
        const string header = "-----BEGIN " + label + "-----";
        const string footer = "-----END " + label + "-----";
        var start = pem.IndexOf(header, StringComparison.Ordinal);
        var end = pem.IndexOf(footer, StringComparison.Ordinal);
        if (start < 0 || end < start)
            throw new FormatException("Expected a PKCS#8 PEM private key (BEGIN PRIVATE KEY).");

        var base64 = pem[(start + header.Length)..end]
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        var key = PrivateKeyFactory.CreateKey(Convert.FromBase64String(base64)) as Ed25519PrivateKeyParameters
            ?? throw new FormatException("The PEM key is not an Ed25519 private key.");

        return new BouncyCastleEd25519Signer(key);
    }

    /// <summary>Creates a signer from a raw 32-byte seed (tests and tooling).</summary>
    public static BouncyCastleEd25519Signer FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != Ed25519PrivateKeyParameters.KeySize)
            throw new ArgumentException($"Seed must be {Ed25519PrivateKeyParameters.KeySize} bytes.", nameof(seed));

        return new BouncyCastleEd25519Signer(new Ed25519PrivateKeyParameters(seed.ToArray(), 0));
    }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        var bytes = message.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.GenerateSignature();
    }
}
