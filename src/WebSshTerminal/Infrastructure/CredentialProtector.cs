using System.Security.Cryptography;

namespace WebSshTerminal.Infrastructure;

/// <summary>
/// Xifra credencials en repòs amb AES-256-GCM i la clau mestra (app_key). El format és
/// <c>version(1) | nonce(12) | tag(16) | ciphertext</c>; l'AAD vincula el blob a l'identitat.
/// </summary>
public sealed class CredentialProtector(AppSecrets secrets)
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, Guid id)
    {
        var blob = new byte[1 + NonceSize + TagSize + plaintext.Length];
        blob[0] = Version;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(secrets.AppKey, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), id.ToByteArray());
        return blob;
    }

    public byte[] Unprotect(byte[] blob, Guid id)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != Version)
            throw new CryptographicException("Format de credencial no vàlid.");
        var plain = new byte[blob.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(secrets.AppKey, TagSize);
        aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), plain, id.ToByteArray());
        return plain;
    }
}
