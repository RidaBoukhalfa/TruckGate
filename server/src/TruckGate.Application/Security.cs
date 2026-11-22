using System.Security.Cryptography;
using System.Text;

namespace TruckGate.Application;

/// <summary>
/// The server's own ECDSA P-256 key. It signs device configurations; phones pin its public key
/// when they scan the enrollment QR. Stored as a PKCS#8 PEM file readable by the service account only.
/// </summary>
public sealed class ServerSigningKey : IDisposable
{
    private readonly ECDsa _key;

    private ServerSigningKey(ECDsa key)
    {
        if (key.KeySize != 256) throw new InvalidOperationException("server signing key must be P-256");
        _key = key;
        PublicKeySpki = key.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKeySpki { get; }

    public static ServerSigningKey LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = ECDsa.Create();
            existing.ImportFromPem(File.ReadAllText(path));
            return new ServerSigningKey(existing);
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(key.ExportPkcs8PrivateKeyPem());
        return new ServerSigningKey(key);
    }

    /// <summary>In-memory key, for tests.</summary>
    public static ServerSigningKey Ephemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>ECDSA P-256 / SHA-256, raw r||s — the same format as tickets.</summary>
    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Operator PINs: PBKDF2-SHA256, 100 000 iterations, 16-byte salt, encoded
/// <c>pbkdf2-sha256$iterations$saltB64$hashB64</c>. Phones verify them offline (quarry-core PinHash).
/// </summary>
public static class PinHasher
{
    public const int Iterations = 100_000;

    public static string Hash(string pin)
    {
        Validate(pin);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations is < 10_000 or > 2_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>4–8 digits, not all identical, not a straight run (1234, 9876...).</summary>
    public static void Validate(string pin)
    {
        if (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
            throw TruckGateException.Validation("PIN_FORMAT", "The PIN must be 4 to 8 digits.");
        bool same = pin.All(c => c == pin[0]);
        bool up = true, down = true;
        for (int i = 1; i < pin.Length; i++)
        {
            up &= pin[i] - pin[i - 1] == 1;
            down &= pin[i - 1] - pin[i] == 1;
        }
        if (same || up || down)
            throw TruckGateException.Validation("PIN_TOO_SIMPLE", "The PIN is too easy to guess.");
    }
}

/// <summary>Request signature used by phones for background calls (see quarry-core DeviceAuth).</summary>
public static class DeviceAuthMessage
{
    public static byte[] Build(int deviceId, long unixSeconds, string method, string path, byte[] body)
    {
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        return Encoding.UTF8.GetBytes(
            $"TGAUTH1|{deviceId}|{unixSeconds}|{method.ToUpperInvariant()}|{path}|{bodyHash}");
    }
}
