using System.Security.Cryptography;
using System.Text;
using HealthBridge.HL7Service.Security;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Security;

/// <summary>
/// Tests for AES-256-GCM PHI encryption.
///
/// Verified against NIST SP 800-38D (nonce/tag sizing and the nonce-uniqueness rule):
///   https://csrc.nist.gov/pubs/sp/800/38/d/final
/// </summary>
public class AesEncryptionServiceTests
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>A valid base64-encoded 256-bit key.</summary>
    private static string ValidBase64Key() => Convert.ToBase64String(new byte[32]);

    private static AesEncryptionService WithKey(string? key)
    {
        var config = key == null
            ? TestHelpers.EmptyConfig()
            : TestHelpers.ConfigWith(("PHI_ENCRYPTION_KEY", key));

        return new AesEncryptionService(config, TestHelpers.NullLoggerFor<AesEncryptionService>());
    }

    [Fact]
    public void Round_trips_a_plaintext()
    {
        var service = WithKey(ValidBase64Key());

        var ciphertext = service.Encrypt("Smith^John");

        Assert.Equal("Smith^John", service.Decrypt(ciphertext));
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var service = WithKey(ValidBase64Key());

        var ciphertext = service.Encrypt("PAT001");

        Assert.DoesNotContain("PAT001", ciphertext);
    }

    [Fact]
    public void Encrypting_the_same_plaintext_twice_gives_different_ciphertexts()
    {
        // The nonce is fresh per call, so identical PHI must not produce identical
        // ciphertext — otherwise an observer can correlate records. SP 800-38D §8.
        var service = WithKey(ValidBase64Key());

        var first = service.Encrypt("PAT001");
        var second = service.Encrypt("PAT001");

        Assert.NotEqual(first, second);
        Assert.Equal("PAT001", service.Decrypt(first));
        Assert.Equal("PAT001", service.Decrypt(second));
    }

    [Fact]
    public void Nonces_are_unique_across_many_encryptions()
    {
        // A repeated (key, nonce) pair breaks both confidentiality and authenticity,
        // so this is the single most important property of the implementation.
        var service = WithKey(ValidBase64Key());

        var nonces = Enumerable.Range(0, 500)
            .Select(_ => Convert.FromBase64String(service.Encrypt("same input")))
            .Select(bytes => Convert.ToBase64String(bytes.Take(NonceSize).ToArray()))
            .ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
    }

    [Fact]
    public void Output_layout_is_nonce_then_ciphertext_then_tag()
    {
        var service = WithKey(ValidBase64Key());
        const string plaintext = "12345";

        var bytes = Convert.FromBase64String(service.Encrypt(plaintext));

        Assert.Equal(NonceSize + Encoding.UTF8.GetByteCount(plaintext) + TagSize, bytes.Length);
    }

    [Fact]
    public void Tampering_with_the_ciphertext_is_detected()
    {
        // This is the whole point of GCM over CBC: a modified record fails to decrypt
        // instead of silently yielding corrupted PHI.
        var service = WithKey(ValidBase64Key());
        var bytes = Convert.FromBase64String(service.Encrypt("Smith^John"));

        bytes[NonceSize] ^= 0xFF;   // flip a bit in the ciphertext body

        Assert.Throws<AuthenticationTagMismatchException>(
            () => service.Decrypt(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Tampering_with_the_authentication_tag_is_detected()
    {
        var service = WithKey(ValidBase64Key());
        var bytes = Convert.FromBase64String(service.Encrypt("Smith^John"));

        bytes[^1] ^= 0xFF;          // flip a bit in the tag

        Assert.Throws<AuthenticationTagMismatchException>(
            () => service.Decrypt(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void A_ciphertext_from_a_different_key_does_not_decrypt()
    {
        var writer = WithKey(Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()));
        var reader = WithKey(Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()));

        var ciphertext = writer.Encrypt("Smith^John");

        Assert.Throws<AuthenticationTagMismatchException>(() => reader.Decrypt(ciphertext));
    }

    [Fact]
    public void Truncated_input_is_rejected_with_a_clear_error()
    {
        var service = WithKey(ValidBase64Key());

        var ex = Assert.Throws<CryptographicException>(
            () => service.Decrypt(Convert.ToBase64String(new byte[8])));

        Assert.Contains("too short", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_and_null_inputs_pass_through_untouched(string? input)
    {
        var service = WithKey(ValidBase64Key());

        Assert.Equal(input, service.Encrypt(input!));
        Assert.Equal(input, service.Decrypt(input!));
    }

    [Fact]
    public void A_passphrase_is_accepted_and_derived_into_a_key()
    {
        var service = WithKey("not-base64-just-a-passphrase");

        Assert.Equal("Smith^John", service.Decrypt(service.Encrypt("Smith^John")));
    }

    [Fact]
    public void A_base64_value_of_the_wrong_length_is_derived_rather_than_rejected()
    {
        // Base64 that decodes to something other than 32 bytes is hashed into a key.
        // Worth pinning: it means a truncated key silently "works" instead of failing.
        var service = WithKey(Convert.ToBase64String(new byte[16]));

        Assert.Equal("PAT001", service.Decrypt(service.Encrypt("PAT001")));
    }

    [Fact]
    public void The_same_passphrase_produces_a_key_that_can_read_the_others_output()
    {
        // Two instances configured identically must interoperate — this is what makes
        // rolling restarts and multi-replica deployments work.
        var writer = WithKey("shared-passphrase");
        var reader = WithKey("shared-passphrase");

        Assert.Equal("PAT001", reader.Decrypt(writer.Encrypt("PAT001")));
    }

    [Fact]
    public void No_configured_key_falls_back_to_the_demo_key_instead_of_failing_startup()
    {
        // Deliberate POC behaviour, and a deployment risk: a missing PHI_ENCRYPTION_KEY
        // produces working encryption with a publicly known key rather than a hard failure.
        // Production should fail fast here instead.
        var service = WithKey(null);

        Assert.Equal("Smith^John", service.Decrypt(service.Encrypt("Smith^John")));
    }

    [Fact]
    public void The_demo_fallback_key_is_deterministic_across_instances()
    {
        // Confirms the fallback is the fixed demo key, not a random one — which is
        // exactly why it must never be used in production.
        var first = WithKey(null);
        var second = WithKey(null);

        Assert.Equal("PAT001", second.Decrypt(first.Encrypt("PAT001")));
    }

    [Fact]
    public void Round_trips_unicode_and_HL7_delimiter_characters()
    {
        var service = WithKey(ValidBase64Key());
        const string phi = @"Müller^José|^~\&  ماهر  日本語";

        Assert.Equal(phi, service.Decrypt(service.Encrypt(phi)));
    }

    [Fact]
    public void Round_trips_a_large_payload()
    {
        var service = WithKey(ValidBase64Key());
        var phi = new string('X', 200_000);

        Assert.Equal(phi, service.Decrypt(service.Encrypt(phi)));
    }

    [Fact]
    public void Concurrent_encryption_and_decryption_are_safe()
    {
        // Registered as a singleton, so this runs concurrently in production.
        var service = WithKey(ValidBase64Key());

        Parallel.For(0, 200, i =>
        {
            var value = $"PAT{i:D4}";
            Assert.Equal(value, service.Decrypt(service.Encrypt(value)));
        });
    }
}
