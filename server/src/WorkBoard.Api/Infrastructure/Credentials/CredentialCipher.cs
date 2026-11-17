using System.Security.Cryptography;
using System.Text;

namespace WorkBoard.Api.Infrastructure.Credentials;

public sealed record SealedCredential(byte[] Ciphertext, byte[] Nonce, int KeyVersion);

public sealed class CredentialException(string message, Exception? inner = null) : Exception(message, inner);

// AES-256-GCM. The tenant and connection ids are the associated data, so a row copied to another connection
// or tenant does not open. Ciphertext is the encrypted bytes followed by the 16-byte tag.
public sealed class CredentialCipher(ICredentialKeys keys)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public int CurrentKeyVersion => keys.Get().CurrentVersion;

    public SealedCredential Seal(string token, Guid tenantId, Guid connectionId)
    {
        var ring = keys.Get();
        var plain = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[plain.Length + TagSize];
        using var aes = new AesGcm(ring.Get(ring.CurrentVersion), TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length, TagSize), Aad(tenantId, connectionId));
        CryptographicOperations.ZeroMemory(plain);
        return new SealedCredential(output, nonce, ring.CurrentVersion);
    }

    public string Open(SealedCredential sealedCredential, Guid tenantId, Guid connectionId)
    {
        var data = sealedCredential.Ciphertext;
        if (data.Length < TagSize || sealedCredential.Nonce.Length != NonceSize)
            throw new CredentialException("The stored credential is malformed.");
        var plain = new byte[data.Length - TagSize];
        try
        {
            using var aes = new AesGcm(keys.Get().Get(sealedCredential.KeyVersion), TagSize);
            aes.Decrypt(sealedCredential.Nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length, TagSize), plain,
                Aad(tenantId, connectionId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException e)
        {
            throw new CredentialException("The stored credential could not be decrypted.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Aad(Guid tenantId, Guid connectionId) =>
        Encoding.UTF8.GetBytes($"work-board:github-credential:v1:{tenantId:N}:{connectionId:N}");
}
