using System.Security.Cryptography;
using System.Text;
using Factory.Core.Tenancy;

namespace Factory.Infrastructure.Security;

// Local ISecretProtector: AES-256-GCM with a 32-byte master key from configuration, never stored in
// the database. The context is authenticated data, so a ciphertext copied to another model or
// organization fails to decrypt. Format: version(1) | nonce(12) | tag(16) | ciphertext.
// An Azure Key Vault implementation follows with M13A.
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte Version = 1;
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] masterKey)
    {
        if (masterKey is not { Length: 32 }) throw new ArgumentException("invalid_master_key");
        _key = masterKey.ToArray();
    }

    public static AesGcmSecretProtector? FromConfiguration(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try { return new AesGcmSecretProtector(Convert.FromBase64String(base64)); }
        catch (FormatException) { throw new InvalidOperationException("Secrets:MasterKey must be 32 bytes, base64 encoded."); }
        catch (ArgumentException) { throw new InvalidOperationException("Secrets:MasterKey must be 32 bytes, base64 encoded."); }
    }

    public byte[] Protect(string secret, string context)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret); ArgumentException.ThrowIfNullOrEmpty(context);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var output = new byte[1 + 12 + 16 + plaintext.Length];
            output[0] = Version;
            var nonce = output.AsSpan(1, 12); RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plaintext, output.AsSpan(29), output.AsSpan(13, 16), Encoding.UTF8.GetBytes(context));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string Unprotect(byte[] protectedSecret, string context)
    {
        if (protectedSecret is not { Length: > 29 } || protectedSecret[0] != Version) throw new CryptographicException("invalid_protected_secret");
        var plaintext = new byte[protectedSecret.Length - 29];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(protectedSecret.AsSpan(1, 12), protectedSecret.AsSpan(29), protectedSecret.AsSpan(13, 16), plaintext, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
