using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace WebSshTerminal.Infrastructure;

/// <summary>
/// Xifra les claus de Data Protection (cookies, antiforgery) en repòs amb una clau derivada de la clau mestra
/// <c>app_key</c> (HKDF-SHA256 + AES-256-GCM). Si es perd o canvia l'app_key, les sessions web s'invaliden.
/// </summary>
internal static class KeyDerivation
{
    public static byte[] DataProtectionKey(byte[] appKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, appKey, 32, info: Encoding.UTF8.GetBytes("web-ssh-terminal/dataprotection/v1"));
}

internal sealed class AppKeyXmlEncryptor(AppSecrets secrets) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var blob = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using var aes = new AesGcm(KeyDerivation.DataProtectionKey(secrets.AppKey), 16);
        aes.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(28), blob.AsSpan(12, 16));
        var element = new XElement("encryptedKey", new XElement("value", Convert.ToBase64String(blob)));
        return new EncryptedXmlInfo(element, typeof(AppKeyXmlDecryptor));
    }
}

/// <summary>El framework la instancia amb IServiceProvider quan llegeix una clau xifrada.</summary>
internal sealed class AppKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var blob = Convert.FromBase64String((string)encryptedElement.Element("value")!);
        var plain = new byte[blob.Length - 28];
        var secrets = (AppSecrets)services.GetService(typeof(AppSecrets))!;
        using var aes = new AesGcm(KeyDerivation.DataProtectionKey(secrets.AppKey), 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return XElement.Parse(Encoding.UTF8.GetString(plain));
    }
}
