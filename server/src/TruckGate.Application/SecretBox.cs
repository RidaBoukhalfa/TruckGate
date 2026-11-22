using System.Security.Cryptography;
using System.Text;

namespace TruckGate.Application;

/// <summary>
/// AES-256-GCM for secrets the server must be able to read back (camera passwords).
/// The key lives in a file readable by the service account only, never in the database.
/// Format: nonce (12) | ciphertext | tag (16).
/// </summary>
public sealed class SecretBox
{
    private readonly byte[] _key;

    private SecretBox(byte[] key)
    {
        if (key.Length != 32) throw new InvalidOperationException("secret key must be 32 bytes");
        _key = key;
    }

    public static SecretBox LoadOrCreate(string path)
    {
        if (File.Exists(path)) return new SecretBox(File.ReadAllBytes(path));
        var key = RandomNumberGenerator.GetBytes(32);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options)) stream.Write(key);
        return new SecretBox(key);
    }

    public static SecretBox Ephemeral() => new(RandomNumberGenerator.GetBytes(32));

    public byte[] Encrypt(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return [.. nonce, .. cipher, .. tag];
    }

    public string Decrypt(byte[] box)
    {
        if (box.Length < 28) throw new CryptographicException("secret too short");
        var nonce = box.AsSpan(0, 12);
        var tag = box.AsSpan(box.Length - 16);
        var cipher = box.AsSpan(12, box.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Where pictures are written. Paths in the database are relative to <see cref="Root"/>.</summary>
public sealed record StorageOptions(string Root);
