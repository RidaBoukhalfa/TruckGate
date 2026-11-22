using System.Security.Cryptography;

namespace TruckGate.Crypto;

/// <summary>ECDSA P-256 / SHA-256 verification of raw r||s signatures (IEEE P1363).</summary>
public static class EcdsaP256
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>Returns false for any invalid key or signature; never throws for bad input.</summary>
    public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] rawSignature)
    {
        if (rawSignature.Length != TicketFields.SignatureLength) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (!IsP256(ecdsa)) return false;
            return ecdsa.VerifyData(data, rawSignature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True if the bytes are a SubjectPublicKeyInfo for a P-256 key (used at device enrollment).</summary>
    public static bool IsValidP256PublicKey(byte[] publicKeySpki)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out int read);
            return read == publicKeySpki.Length && IsP256(ecdsa);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool IsP256(ECDsa ecdsa)
    {
        var curve = ecdsa.ExportParameters(false).Curve;
        if (!curve.IsNamed) return false;
        return curve.Oid.Value == P256Oid
               || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1";
    }
}
