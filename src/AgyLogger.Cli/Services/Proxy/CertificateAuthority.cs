namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

/// <summary>
/// In-memory Certificate Authority for generating a local Root CA and synthesizing
/// per-host TLS leaf certificates dynamically for HTTPS MitM interception.
/// Implemented using pure .NET 10 BCL cryptography without external dependencies.
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    public const string DefaultCaCommonName = "AgyLogger Development CA";
    public const string DefaultOrganization = "AgyLogger";
    public const string DefaultStoreName = "Root";
    public const StoreLocation DefaultStoreLocation = StoreLocation.CurrentUser;

    private readonly X509Certificate2 _rootCertificate;
    private readonly string _storeName;
    private readonly StoreLocation _storeLocation;
    private readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> _leafCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Gets the certificate store name targeted for trust operations.
    /// Defaults to "Root".
    /// </summary>
    public string StoreName => _storeName;

    /// <summary>
    /// Gets the certificate store location targeted for trust operations.
    /// Defaults to CurrentUser.
    /// </summary>
    public StoreLocation StoreLocation => _storeLocation;

    /// <summary>
    /// Gets the active Root CA certificate with its bound private key.
    /// </summary>
    public X509Certificate2 RootCertificate
    {
        get
        {
            ThrowIfDisposed();
            return _rootCertificate;
        }
    }

    /// <summary>
    /// Initializes a new instance of <see cref="CertificateAuthority"/> with an existing Root CA certificate.
    /// </summary>
    /// <param name="rootCertificate">A certificate containing a private key authorized for certificate signing.</param>
    /// <param name="storeName">Target certificate store name for trust operations. Defaults to "Root".</param>
    /// <param name="storeLocation">Target certificate store location for trust operations. Defaults to CurrentUser.</param>
    public CertificateAuthority(
        X509Certificate2 rootCertificate,
        string storeName = DefaultStoreName,
        StoreLocation storeLocation = DefaultStoreLocation)
    {
        ArgumentNullException.ThrowIfNull(rootCertificate);
        if (!rootCertificate.HasPrivateKey)
        {
            throw new ArgumentException("Root certificate must contain a private key to sign leaf certificates.", nameof(rootCertificate));
        }

        _rootCertificate = rootCertificate;
        _storeName = string.IsNullOrWhiteSpace(storeName) ? DefaultStoreName : storeName;
        _storeLocation = storeLocation;
    }

    /// <summary>
    /// Generates a new in-memory ephemeral Root CA certificate valid for 5 years.
    /// </summary>
    public static CertificateAuthority CreateInMemory(
        string commonName = DefaultCaCommonName,
        string storeName = DefaultStoreName,
        StoreLocation storeLocation = DefaultStoreLocation)
    {
        var rootCert = GenerateRootCertificate(commonName);
        return new CertificateAuthority(rootCert, storeName, storeLocation);
    }

    /// <summary>
    /// Gets the default storage directory for CA certificates (~/.agylogs/ca).
    /// </summary>
    public static string DefaultStorageDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agylogs",
        "ca");

    /// <summary>
    /// Gets the default PFX file path (~/.agylogs/ca/ca.pfx).
    /// </summary>
    public static string DefaultPfxPath => Path.Combine(DefaultStorageDirectory, "ca.pfx");

    /// <summary>
    /// Gets the default CRT file path (~/.agylogs/ca/ca.crt).
    /// </summary>
    public static string DefaultCrtPath => Path.Combine(DefaultStorageDirectory, "ca.crt");

    /// <summary>
    /// Checks whether a Root CA certificate file (ca.pfx) exists in the specified directory.
    /// </summary>
    public static bool Exists(string? storageDirectory = null)
    {
        var dir = storageDirectory ?? DefaultStorageDirectory;
        return File.Exists(Path.Combine(dir, "ca.pfx"));
    }

    /// <summary>
    /// Attempts to load an existing Root CA from the specified storage directory without creating one.
    /// Returns null if the certificate file does not exist or cannot be loaded.
    /// </summary>
    public static CertificateAuthority? TryLoad(
        string? storageDirectory = null,
        string storeName = DefaultStoreName,
        StoreLocation storeLocation = DefaultStoreLocation)
    {
        var dir = storageDirectory ?? DefaultStorageDirectory;
        var pfxPath = Path.Combine(dir, "ca.pfx");

        if (!File.Exists(pfxPath))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(pfxPath);
            var loaded = X509CertificateLoader.LoadPkcs12(bytes, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
            if (loaded.HasPrivateKey)
            {
                return new CertificateAuthority(loaded, storeName, storeLocation);
            }
        }
        catch
        {
            // Corrupted or unreadable file
        }

        return null;
    }

    /// <summary>
    /// Gets or creates the default Root CA. If a saved PFX exists in <paramref name="storageDirectory"/>,
    /// it is loaded; otherwise, a new Root CA is generated and saved.
    /// </summary>
    public static CertificateAuthority GetOrCreateDefault(
        string? storageDirectory = null,
        string storeName = DefaultStoreName,
        StoreLocation storeLocation = DefaultStoreLocation)
    {
        var existing = TryLoad(storageDirectory, storeName, storeLocation);
        if (existing is not null)
        {
            return existing;
        }

        var dir = storageDirectory ?? DefaultStorageDirectory;
        var pfxPath = Path.Combine(dir, "ca.pfx");
        var crtPath = Path.Combine(dir, "ca.crt");

        var ca = CreateInMemory(DefaultCaCommonName, storeName, storeLocation);
        try
        {
            Directory.CreateDirectory(dir);
            var pfxBytes = ca.RootCertificate.Export(X509ContentType.Pfx);
            File.WriteAllBytes(pfxPath, pfxBytes);
            ca.ExportRootCertificatePem(crtPath);
        }
        catch
        {
            // If disk persistence fails (e.g. read-only filesystem), continue with in-memory instance
        }

        return ca;
    }

    /// <summary>
    /// Retrieves a cached TLS leaf certificate for the specified hostname, or generates a new one.
    /// </summary>
    public X509Certificate2 GetOrCreateLeafCertificate(string hostName)
    {
        ThrowIfDisposed();
        var normalizedHost = NormalizeHost(hostName);

        if (_leafCache.TryGetValue(normalizedHost, out var cachedLazy) && cachedLazy.IsValueCreated)
        {
            ThrowIfDisposed();
            return cachedLazy.Value;
        }

        var lazy = _leafCache.GetOrAdd(
            normalizedHost,
            host => new Lazy<X509Certificate2>(
                () => GenerateLeafCertificate(host),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var cert = lazy.Value;
            if (_disposed)
            {
                cert.Dispose();
                throw new ObjectDisposedException(nameof(CertificateAuthority));
            }

            return cert;
        }
        catch
        {
            _leafCache.TryRemove(normalizedHost, out _);
            throw;
        }
    }

    /// <summary>
    /// Exports the public Root CA certificate in standard PEM format (RFC 7468).
    /// </summary>
    public string ExportRootCertificatePem()
    {
        ThrowIfDisposed();
        return _rootCertificate.ExportCertificatePem();
    }

    /// <summary>
    /// Writes the public Root CA certificate in standard PEM format to the specified file path.
    /// </summary>
    public void ExportRootCertificatePem(string filePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var pem = ExportRootCertificatePem();
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, pem);
    }

    /// <summary>
    /// Compatibility alias for PROJECT.md contract.
    /// </summary>
    public void ExportRootCertPem(string filePath) => ExportRootCertificatePem(filePath);

    /// <summary>
    /// Installs the Root CA certificate into the target certificate store.
    /// </summary>
    /// <returns><c>true</c> if successfully installed; otherwise, <c>false</c>.</returns>
    public bool TrustRootCertificate(string? storeName = null, StoreLocation? storeLocation = null)
    {
        ThrowIfDisposed();
        try
        {
            using var store = new X509Store(storeName ?? _storeName, storeLocation ?? _storeLocation);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                _rootCertificate.Thumbprint,
                validOnly: false);

            if (existing.Count == 0)
            {
                store.Add(_rootCertificate);
            }

            foreach (var cert in existing)
            {
                cert.Dispose();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compatibility alias for PROJECT.md contract.
    /// </summary>
    public bool TrustInCurrentUserStore(string? storeName = null) => TrustRootCertificate(storeName);

    /// <summary>
    /// Removes the Root CA certificate from the target certificate store.
    /// </summary>
    /// <returns><c>true</c> if successfully removed or not present; otherwise, <c>false</c>.</returns>
    public bool UntrustRootCertificate(string? storeName = null, StoreLocation? storeLocation = null)
    {
        ThrowIfDisposed();
        try
        {
            using var store = new X509Store(storeName ?? _storeName, storeLocation ?? _storeLocation);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                _rootCertificate.Thumbprint,
                validOnly: false);

            foreach (var cert in existing)
            {
                store.Remove(cert);
                cert.Dispose();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the Root CA certificate is installed in the target certificate store.
    /// </summary>
    public bool IsRootCertificateTrusted(string? storeName = null, StoreLocation? storeLocation = null)
    {
        ThrowIfDisposed();
        try
        {
            using var store = new X509Store(storeName ?? _storeName, storeLocation ?? _storeLocation);
            store.Open(OpenFlags.ReadOnly);

            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                _rootCertificate.Thumbprint,
                validOnly: false);

            var isTrusted = matches.Count > 0;
            foreach (var cert in matches)
            {
                cert.Dispose();
            }

            return isTrusted;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a host string by stripping ports, removing IPv6 brackets, and converting to lowercase.
    /// </summary>
    public static string NormalizeHost(string hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            throw new ArgumentException("Hostname must not be null or whitespace.", nameof(hostName));
        }

        var host = hostName.Trim();

        // Strip bracketed IPv6, e.g. "[::1]:443" or "[::1]"
        if (host.StartsWith('['))
        {
            var closingIndex = host.IndexOf(']');
            if (closingIndex > 0)
            {
                var ipCandidate = host[1..closingIndex];
                if (IPAddress.TryParse(ipCandidate, out _))
                {
                    return ipCandidate;
                }
            }
        }

        // Strip trailing port if present (single colon, e.g. "example.com:443" or "127.0.0.1:8888")
        var colonIndex = host.LastIndexOf(':');
        if (colonIndex > 0 && !host.Contains("::") && host.IndexOf(':') == colonIndex)
        {
            var portPart = host[(colonIndex + 1)..];
            if (int.TryParse(portPart, out _))
            {
                host = host[..colonIndex];
            }
        }

        return host.ToLowerInvariant();
    }

    private static X509Certificate2 GenerateRootCertificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}, O={DefaultOrganization}"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Basic Constraints: CA = true, critical = true
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));

        // Key Usage: KeyCertSign, CrlSign, DigitalSignature, critical = true
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));

        // Subject Key Identifier
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(5);

        using var selfSigned = req.CreateSelfSigned(notBefore, notAfter);

        // Export and reload via PKCS#12 to ensure private key remains bound to the certificate
        // in an exportable container across Windows Schannel and Linux OpenSSL.
        var pfxBytes = selfSigned.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
    }

    private X509Certificate2 GenerateLeafCertificate(string hostName)
    {
        var leafRsa = RSA.Create(2048);
        var req = new CertificateRequest(
            new X500DistinguishedName($"CN={hostName}"),
            leafRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Basic Constraints: CA = false, critical = false
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: false));

        // Subject Alternative Name (SAN)
        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(hostName, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(hostName);
        }
        req.CertificateExtensions.Add(sanBuilder.Build());

        // Extended Key Usage: Server Authentication (1.3.6.1.5.5.7.3.1)
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication") },
            critical: false));

        // Key Usage: DigitalSignature, KeyEncipherment, critical = true
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        // Authority Key Identifier pointing to Root CA
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            _rootCertificate,
            includeKeyIdentifier: true,
            includeIssuerAndSerial: false));

        // Cryptographically random 16-byte serial number (positive integer)
        byte[] serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F; // Positive DER integer

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        using var certWithoutKey = req.Create(_rootCertificate, notBefore, notAfter, serialNumber);
        using var certWithKey = certWithoutKey.CopyWithPrivateKey(leafRsa);

        // Export and reload via PKCS#12 to ensure private key container is self-contained and durable
        var pfxBytes = certWithKey.Export(X509ContentType.Pfx);
        leafRsa.Dispose();

        return X509CertificateLoader.LoadPkcs12(pfxBytes, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var lazy in _leafCache.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }
        _leafCache.Clear();

        _rootCertificate.Dispose();
    }
}