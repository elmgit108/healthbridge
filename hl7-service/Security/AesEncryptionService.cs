using System.Security.Cryptography;
using System.Text;

namespace HealthBridge.HL7Service.Security;

/// <summary>
/// AES-256-GCM authenticated encryption for PHI at rest.
///
/// AES-GCM is the recommended symmetric cipher for HIPAA-compliant systems:
///   - 256-bit key length (NIST-approved)
///   - Authenticated encryption (detects tampering via the auth tag)
///   - Built into .NET (System.Security.Cryptography.AesGcm)
///
/// Output format (Base64):
///   [12-byte nonce] [ciphertext] [16-byte auth tag]
///
/// Production note: the encryption key must come from a secrets manager
/// (AWS KMS, Azure Key Vault, HashiCorp Vault) — never hardcoded or in env files
/// in long-running production. For the POC, we read it from PHI_ENCRYPTION_KEY env var.
///
/// Sources:
///   AES-GCM (nonce/tag sizes, uniqueness requirement) — NIST SP 800-38D:
///     https://csrc.nist.gov/pubs/sp/800/38/d/final
///   HIPAA Security Rule, technical safeguards — 45 CFR §164.312(a)(2)(iv), (e)(2)(ii):
///     https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-C/part-164/subpart-C/section-164.312
///   Implementing those safeguards — NIST SP 800-66r2:
///     https://csrc.nist.gov/pubs/sp/800/66/r2/final
/// See docs/STANDARDS.md §4.
/// </summary>
public class AesEncryptionService : IEncryptionService
{
    // Sizes per NIST SP 800-38D §5.2.1.1 / §5.2.1.2 — https://csrc.nist.gov/pubs/sp/800/38/d/final
    private const int NonceSize = 12;  // 96-bit nonce — the size SP 800-38D recommends
    private const int TagSize = 16;    // 128-bit authentication tag — the maximum, and the default
    private const int KeySize = 32;    // 256-bit key

    private readonly byte[] _key;
    private readonly ILogger<AesEncryptionService> _logger;

    public AesEncryptionService(IConfiguration config, ILogger<AesEncryptionService> logger)
    {
        _logger = logger;

        // Key resolution order: env var → config file → derived demo key
        var keyMaterial = config["PHI_ENCRYPTION_KEY"]
                          ?? Environment.GetEnvironmentVariable("PHI_ENCRYPTION_KEY");

        if (string.IsNullOrEmpty(keyMaterial))
        {
            // Demo fallback — derives a deterministic key for the POC.
            // NEVER use this in production; it provides no real security.
            _logger.LogWarning(
                "PHI_ENCRYPTION_KEY not set — using demo key. NOT SAFE FOR PRODUCTION.");
            _key = SHA256.HashData(Encoding.UTF8.GetBytes("healthbridge-demo-do-not-use-in-prod"));
        }
        else
        {
            // Accept either a base64-encoded 32-byte key or a passphrase
            try
            {
                _key = Convert.FromBase64String(keyMaterial);
                if (_key.Length != KeySize)
                    _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
            }
            catch (FormatException)
            {
                // Not base64 — derive a key from the passphrase via SHA-256
                _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
            }
        }
    }

    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Generate a fresh random nonce per encryption. SP 800-38D §8 makes this a hard
        // requirement: a repeated (key, nonce) pair breaks confidentiality *and* lets an
        // attacker forge tags. Never derive the nonce from the plaintext or a counter reset.
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack [nonce | ciphertext | tag] into a single Base64 string for storage
        var output = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, NonceSize + ciphertext.Length, TagSize);

        return Convert.ToBase64String(output);
    }

    public string Decrypt(string ciphertextBase64)
    {
        if (string.IsNullOrEmpty(ciphertextBase64))
            return ciphertextBase64;

        var input = Convert.FromBase64String(ciphertextBase64);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short to be valid");

        // Unpack the layout written by Encrypt()
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[input.Length - NonceSize - TagSize];

        Buffer.BlockCopy(input, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(input, NonceSize, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(input, NonceSize + ciphertext.Length, tag, 0, TagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
