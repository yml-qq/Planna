using System.Security.Cryptography;
using System.Text;

namespace Planna.Infrastructure.Identity;

public sealed class LoginCodeCipher
{
    private readonly byte[] key;

    public LoginCodeCipher(string base64Key)
    {
        try { key = Convert.FromBase64String(base64Key); }
        catch (FormatException ex) { throw new ArgumentException("AUTH_OUTBOX_KEY must be base64.", nameof(base64Key), ex); }
        if (key.Length != 32)
            throw new ArgumentException("AUTH_OUTBOX_KEY must decode to 32 bytes.", nameof(base64Key));
    }

    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(Guid challengeId, string email, string code)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[Encoding.UTF8.GetByteCount(code)];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(code), ciphertext, tag, AssociatedData(challengeId, email));
        return (ciphertext, nonce, tag);
    }

    public string Decrypt(Guid challengeId, string email, byte[] ciphertext, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(challengeId, email));
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] AssociatedData(Guid challengeId, string email) =>
        Encoding.UTF8.GetBytes($"{challengeId:N}:{email}");
}
