namespace HealthBridge.HL7Service.Security;

/// <summary>
/// Abstraction for encrypting and decrypting Protected Health Information (PHI).
///
/// HIPAA Security Rule § 164.312(a)(2)(iv) requires encryption of electronic
/// PHI both at rest and in transit. This interface lets us swap encryption
/// implementations (AES-GCM, AWS KMS, Azure Key Vault) without changing
/// the calling code.
/// </summary>
public interface IEncryptionService
{
    /// <summary>Encrypts a plaintext string and returns a Base64-encoded ciphertext.</summary>
    string Encrypt(string plaintext);

    /// <summary>Decrypts a Base64 ciphertext produced by Encrypt().</summary>
    string Decrypt(string ciphertext);
}
