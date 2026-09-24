namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using AgyLogger.Cli.Services.Proxy;

using Xunit;

public class CertificateAuthorityTests
{
    [Fact]
    public void CreateInMemory_GeneratesValidRootCa()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        Assert.NotNull(ca.RootCertificate);
        Assert.True(ca.RootCertificate.HasPrivateKey);
        Assert.Contains(CertificateAuthority.DefaultCaCommonName, ca.RootCertificate.Subject);
        Assert.Equal(ca.RootCertificate.Subject, ca.RootCertificate.Issuer);
        Assert.True(ca.RootCertificate.NotBefore <= DateTimeOffset.UtcNow);
        Assert.True(ca.RootCertificate.NotAfter > DateTimeOffset.UtcNow.AddYears(4));
    }

    [Fact]
    public void RootCa_HasCorrectBasicConstraintsAndKeyUsage()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var cert = ca.RootCertificate;

        var basicConstraint = cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        Assert.NotNull(basicConstraint);
        Assert.True(basicConstraint.CertificateAuthority);
        Assert.True(basicConstraint.Critical);

        var keyUsage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        Assert.NotNull(keyUsage);
        Assert.True(keyUsage.Critical);
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign));
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));

        var ski = cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
        Assert.NotNull(ski);
    }

    [Fact]
    public void GetOrCreateLeafCertificate_GeneratesValidLeaf_SignedByRootCa()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("daily-cloudcode-pa.googleapis.com");

        Assert.NotNull(leaf);
        Assert.True(leaf.HasPrivateKey);
        Assert.Contains("daily-cloudcode-pa.googleapis.com", leaf.Subject);
        Assert.Equal(ca.RootCertificate.Subject, leaf.Issuer);
        Assert.True(leaf.NotBefore <= DateTimeOffset.UtcNow);
        Assert.True(leaf.NotAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void GetOrCreateLeafCertificate_HasServerAuthEku_AndKeyUsage()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("generativelanguage.googleapis.com");

        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        Assert.NotNull(eku);
        var hasServerAuth = eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.True(hasServerAuth);

        var keyUsage = leaf.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        Assert.NotNull(keyUsage);
        Assert.True(keyUsage.Critical);
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment));

        var basic = leaf.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        Assert.NotNull(basic);
        Assert.False(basic.CertificateAuthority);
    }

    [Fact]
    public void GetOrCreateLeafCertificate_SanDnsName_ForFqdnAndWildcard()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        var leafFqdn = ca.GetOrCreateLeafCertificate("api.anthropic.com");
        var leafWild = ca.GetOrCreateLeafCertificate("*.googleapis.com");

        var sanFqdn = leafFqdn.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        Assert.NotNull(sanFqdn);
        Assert.Contains("api.anthropic.com", sanFqdn.Format(false));

        var sanWild = leafWild.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        Assert.NotNull(sanWild);
        Assert.Contains("*.googleapis.com", sanWild.Format(false));
    }

    [Fact]
    public void GetOrCreateLeafCertificate_SanIpAddress_ForIpv4AndIpv6()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        var leafIpv4 = ca.GetOrCreateLeafCertificate("127.0.0.1");
        var leafIpv6 = ca.GetOrCreateLeafCertificate("::1");

        var sanIpv4 = leafIpv4.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        Assert.NotNull(sanIpv4);
        Assert.Contains("127.0.0.1", sanIpv4.Format(false));

        var sanIpv6 = leafIpv6.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        Assert.NotNull(sanIpv6);
        var formattedIpv6 = sanIpv6.Format(false);
        Assert.True(formattedIpv6.Contains("::1") || formattedIpv6.Contains("0000:0000:0000:0000:0000:0000:0000:0001") || formattedIpv6.Contains("0:0:0:0:0:0:0:1"));
    }

    [Theory]
    [InlineData("HOST.COM:443", "host.com")]
    [InlineData("127.0.0.1:8888", "127.0.0.1")]
    [InlineData("[::1]:443", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("*.googleapis.com:443", "*.googleapis.com")]
    [InlineData("generativelanguage.googleapis.com", "generativelanguage.googleapis.com")]
    public void NormalizeHost_StripsPortAndBracketedIpv6(string input, string expected)
    {
        var actual = CertificateAuthority.NormalizeHost(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeHost_ThrowsOnNullOrWhitespace(string? input)
    {
        Assert.Throws<ArgumentException>(() => CertificateAuthority.NormalizeHost(input!));
    }

    [Fact]
    public void GetOrCreateLeafCertificate_CachesCertificatePerHost()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        var cert1 = ca.GetOrCreateLeafCertificate("example.com");
        var cert2 = ca.GetOrCreateLeafCertificate("example.com");

        Assert.Same(cert1, cert2);
    }

    [Fact]
    public void GetOrCreateLeafCertificate_CaseInsensitiveCacheKey()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        var cert1 = ca.GetOrCreateLeafCertificate("Host.Example.COM:443");
        var cert2 = ca.GetOrCreateLeafCertificate("host.example.com");

        Assert.Same(cert1, cert2);
    }

    [Fact]
    public async Task GetOrCreateLeafCertificate_ConcurrentRequests_ThreadSafe()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => ca.GetOrCreateLeafCertificate("concurrent.host.test")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var first = results[0];
        Assert.NotNull(first);
        Assert.All(results, cert => Assert.Same(first, cert));
    }

    [Fact]
    public void LeafCertificate_ValidatesAgainstRootCa_UsingCustomTrustStore()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leafCert = ca.GetOrCreateLeafCertificate("generativelanguage.googleapis.com");

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Clear();
        chain.ChainPolicy.CustomTrustStore.Add(ca.RootCertificate);

        var isValid = chain.Build(leafCert);

        Assert.True(isValid, $"Chain build failed: {string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation))}");
        Assert.Equal(2, chain.ChainElements.Count);
        Assert.Equal(leafCert.Thumbprint, chain.ChainElements[0].Certificate.Thumbprint);
        Assert.Equal(ca.RootCertificate.Thumbprint, chain.ChainElements[1].Certificate.Thumbprint);
    }

    [Fact]
    public void ExportRootCertificatePem_ProducesValidPemStringAndFile()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        var pem = ca.ExportRootCertificatePem();
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem.Trim());
        Assert.EndsWith("-----END CERTIFICATE-----", pem.Trim());

        var tempDir = Path.Combine(Path.GetTempPath(), "AgyLogger_Tests_" + Guid.NewGuid().ToString("N"));
        var tempFile = Path.Combine(tempDir, "ca.crt");

        try
        {
            ca.ExportRootCertificatePem(tempFile);
            Assert.True(File.Exists(tempFile));

            var fileContent = File.ReadAllText(tempFile);
            Assert.Equal(pem, fileContent);

            using var reloaded = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(tempFile));
            Assert.Equal(ca.RootCertificate.Thumbprint, reloaded.Thumbprint);

            // Also test compatibility alias
            var aliasFile = Path.Combine(tempDir, "alias.crt");
            ca.ExportRootCertPem(aliasFile);
            Assert.True(File.Exists(aliasFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TrustRootCertificate_And_UntrustRootCertificate_Lifecycle()
    {
        const string testStoreName = "AgyLoggerTest";
        using var ca = CertificateAuthority.CreateInMemory(storeName: testStoreName);

        try
        {
            // Clean up any residual certificate from a previously aborted run
            ca.UntrustRootCertificate();

            // 1. Initial state: not trusted
            Assert.False(ca.IsRootCertificateTrusted());

            // 2. TrustRootCertificate installs to the configured test store without modal prompts
            var trustResult = ca.TrustRootCertificate();
            Assert.True(trustResult);
            Assert.True(ca.IsRootCertificateTrusted());

            // 3. TrustInCurrentUserStore alias maintains trust idempotently
            var aliasResult = ca.TrustInCurrentUserStore();
            Assert.True(aliasResult);
            Assert.True(ca.IsRootCertificateTrusted());

            // 4. UntrustRootCertificate removes certificate from the store
            var untrustResult = ca.UntrustRootCertificate();
            Assert.True(untrustResult);
            Assert.False(ca.IsRootCertificateTrusted());

            // 5. Idempotent untrust when certificate is absent
            var secondUntrustResult = ca.UntrustRootCertificate();
            Assert.True(secondUntrustResult);
        }
        finally
        {
            ca.UntrustRootCertificate();
        }
    }

    [Fact]
    public void IsRootCertificateTrusted_DefaultRootStore_ReturnsFalseWithoutPrompting()
    {
        using var ca = CertificateAuthority.CreateInMemory();

        // Read-only inspection of the default Root store is unprivileged, never prompts on Windows,
        // and verifies that an ephemeral CA is not trusted by default in the real OS root store.
        var isTrusted = ca.IsRootCertificateTrusted();
        Assert.False(isTrusted);
    }

    [Fact]
    public void Dispose_CleansUpRootAndCachedCertificates()
    {
        var ca = CertificateAuthority.CreateInMemory();
        _ = ca.GetOrCreateLeafCertificate("host.to.dispose.test");

        ca.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = ca.RootCertificate);
        Assert.Throws<ObjectDisposedException>(() => ca.GetOrCreateLeafCertificate("host.to.dispose.test"));
        Assert.Throws<ObjectDisposedException>(() => ca.ExportRootCertificatePem());
        Assert.Throws<ObjectDisposedException>(() => ca.TrustRootCertificate());
        Assert.Throws<ObjectDisposedException>(() => ca.UntrustRootCertificate());
        Assert.Throws<ObjectDisposedException>(() => ca.IsRootCertificateTrusted());

        // Repeated dispose should be a safe no-op
        ca.Dispose();
    }
}