/**
 * @fileoverview The Data Protection key ring (AD-12, AD-50 rule 2): persisted to Redis,
 * wrapped with the X.509 certificate from Vault when one is configured, guarded at startup
 * @module ContactsApi.WebApi.Services.KeyRing
 */
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ContactsApi.WebApi.Services.KeyRing;

/// <summary>Configuration keys of the key ring</summary>
public static class DataProtectionConfig
{
    /// <summary>The application name; it stays ContactsApi (AD-12, AD-27)</summary>
    public const string ApplicationName = "ContactsApi";

    /// <summary>The Redis list holding the ring (unchanged, so existing keys stay readable)</summary>
    public const string RedisKey = "DataProtection-Keys";

    /// <summary>The certificate and its private key: PEM text, or base64 PKCS#12 (Vault: DataProtection__Certificate)</summary>
    public const string Certificate = "DataProtection:Certificate";

    /// <summary>A file holding the certificate and key (PEM, or .pfx/.p12), as the Vault Agent can render it</summary>
    public const string CertificatePath = "DataProtection:CertificatePath";

    /// <summary>The PKCS#12 password, when the certificate is a password-protected PKCS#12</summary>
    public const string CertificatePassword = "DataProtection:CertificatePassword";

    /// <summary>The previous certificate during a roll-over (decryption only)</summary>
    public const string PreviousCertificate = "DataProtection:PreviousCertificate";

    /// <summary>The previous certificate as a file</summary>
    public const string PreviousCertificatePath = "DataProtection:PreviousCertificatePath";

    /// <summary>The one-shot rotation: off (default), dry-run, reprotect, retire</summary>
    public const string Rotation = "DataProtection:Rotation";
}

/// <summary>
/// The certificates that wrap the key ring: the current one (wraps new keys, and unwraps)
/// and an optional previous one (unwraps only, during a certificate roll-over).
/// </summary>
public sealed class DataProtectionCertificates
{
    /// <summary>The certificate new keys are wrapped with; null when wrapping is not configured</summary>
    public X509Certificate2? Current { get; }

    /// <summary>Every certificate that may unwrap a key (current first)</summary>
    public IReadOnlyList<X509Certificate2> All { get; }

    /// <summary>Whether wrapping is on</summary>
    public bool Enabled => Current != null;

    /// <summary>Creates the set</summary>
    public DataProtectionCertificates(X509Certificate2? current, X509Certificate2? previous = null)
    {
        Current = current;
        All = new[] { current, previous }.Where(c => c != null).Cast<X509Certificate2>().ToList();
    }

    /// <summary>
    /// Reads the certificates from configuration. Nothing configured: wrapping stays off and
    /// the app behaves as before. Configured but unusable (no private key, not RSA, not
    /// parseable): throws, because starting without the certificate could make every
    /// wrapped key unreadable. The message never contains the certificate.
    /// </summary>
    public static DataProtectionCertificates Load(IConfiguration configuration)
    {
        var password = configuration[DataProtectionConfig.CertificatePassword];
        var current = Read(configuration[DataProtectionConfig.CertificatePath], configuration[DataProtectionConfig.Certificate], password, "DataProtection__Certificate");
        var previous = Read(configuration[DataProtectionConfig.PreviousCertificatePath], configuration[DataProtectionConfig.PreviousCertificate], password, "DataProtection__PreviousCertificate");
        if (current == null && previous != null)
        {
            throw new InvalidOperationException("DataProtection__PreviousCertificate is set without DataProtection__Certificate");
        }

        return new DataProtectionCertificates(current, previous);
    }

    private static X509Certificate2? Read(string? path, string? inline, string? password, string name)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"{name}: the certificate file does not exist");
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".pfx" or ".p12"
                ? Usable(X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable), name)
                : Parse(File.ReadAllText(path), password, name);
        }

        return string.IsNullOrWhiteSpace(inline) ? null : Parse(inline, password, name);
    }

    /// <summary>A certificate with its RSA private key from PEM text (both blocks) or base64 PKCS#12</summary>
    public static X509Certificate2 Parse(string text, string? password, string name)
    {
        try
        {
            var trimmed = text.Trim();
            var cert = trimmed.Contains("-----BEGIN", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(trimmed, trimmed)
                : X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(trimmed), password, X509KeyStorageFlags.Exportable);
            return Usable(cert, name);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException($"{name} could not be read (expected PEM with the certificate and its private key, or base64 PKCS#12): {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Checks the certificate and re-imports it so the private key works for XML decryption
    /// on every platform (a key from PEM is ephemeral on Windows).
    /// </summary>
    private static X509Certificate2 Usable(X509Certificate2 cert, string name)
    {
        if (!cert.HasPrivateKey || cert.GetRSAPrivateKey() == null)
        {
            throw new InvalidOperationException($"{name} needs an RSA certificate with its private key");
        }

        var flags = OperatingSystem.IsLinux() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.Exportable;
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, flags);
    }
}

/// <summary>Registration of the key ring</summary>
public static class EchoDataProtection
{
    /// <summary>
    /// Data Protection with application name ContactsApi, keys in Redis (<c>DataProtection-Keys</c>,
    /// the list the previous code used), wrapped with the certificate when one is configured.
    /// Without a certificate the ring stays unwrapped, exactly as before, and the startup guard
    /// logs a warning. Also registers the startup guard and the one-shot rotation.
    /// </summary>
    public static IServiceCollection AddEchoDataProtection(this IServiceCollection services, IConfiguration configuration,
        IConnectionMultiplexer redis)
    {
        var certificates = DataProtectionCertificates.Load(configuration);
        services.AddSingleton(certificates);
        var builder = services.AddDataProtection().SetApplicationName(DataProtectionConfig.ApplicationName);
        services.Configure<KeyManagementOptions>(o =>
            o.XmlRepository = new RedisKeyRingRepository(() => redis.GetDatabase(), DataProtectionConfig.RedisKey));
        if (certificates.Current != null)
        {
            builder.ProtectKeysWithCertificate(certificates.Current);
            builder.UnprotectKeysWithAnyCertificate(certificates.All.ToArray());
        }

        // The guard starts before every other hosted service: a worker touching Data Protection
        // first would otherwise make the key manager create a new (unwrapped) key next to the
        // wrapped ones it cannot read
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, KeyRingGuard>());
        KeyRingRotation.Register(services, configuration);
        return services;
    }
}

/// <summary>
/// The key ring in a Redis list, in the format of the StackExchangeRedis package's repository
/// (one XML element per list entry), which also supports deleting keys (AD-50 rule 2: the
/// exposed keys are deleted after re-protection).
/// </summary>
public class RedisKeyRingRepository : IDeletableXmlRepository
{
    private readonly Func<IDatabase> _database;
    private readonly RedisKey _key;

    /// <summary>Creates the repository</summary>
    public RedisKeyRingRepository(Func<IDatabase> database, RedisKey key)
    {
        _database = database;
        _key = key;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements() =>
        _database().ListRange(_key).Select(v => XElement.Parse((string)v!)).ToList();

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName) =>
        _database().ListRightPush(_key, element.ToString(SaveOptions.DisableFormatting));

    /// <inheritdoc />
    public bool DeleteElements(Action<IReadOnlyCollection<IDeletableElement>> chooseElements)
    {
        var database = _database();
        var stored = database.ListRange(_key);
        var elements = stored.Select(v => new DeletableElement((string)v!)).ToList();
        chooseElements(elements);
        foreach (var element in elements.Where(e => e.DeletionOrder.HasValue).OrderBy(e => e.DeletionOrder))
        {
            database.ListRemove(_key, element.Raw, 1);
        }

        return true;
    }

    private sealed class DeletableElement : IDeletableElement
    {
        public DeletableElement(string raw)
        {
            Raw = raw;
            Element = XElement.Parse(raw);
        }

        public string Raw { get; }
        public XElement Element { get; }
        public int? DeletionOrder { get; set; }
    }
}

/// <summary>What the key ring holds, read from its XML (never the key material)</summary>
public static class KeyRingInspector
{
    /// <summary>Key id → whether its secret is wrapped (an <c>encryptedSecret</c> element)</summary>
    public static Dictionary<Guid, bool> Keys(IXmlRepository repository) =>
        repository.GetAllElements()
            .Where(e => e.Name.LocalName == "key" && Guid.TryParse((string?)e.Attribute("id"), out _))
            .GroupBy(e => Guid.Parse((string)e.Attribute("id")!))
            .ToDictionary(g => g.Key, g => g.All(e => e.Descendants().Any(d => d.Name.LocalName == "encryptedSecret")));

    /// <summary>The repository the key manager uses</summary>
    public static IXmlRepository Repository(IServiceProvider services) =>
        services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
        ?? throw new InvalidOperationException("No key ring repository is configured");
}

/// <summary>
/// At startup: refuses to run with wrapped keys and no certificate, or with wrapped keys the
/// configured certificates cannot unwrap (a silent start would make every stored token
/// unreadable); logs whether wrapping is on and how many keys are still unwrapped.
/// </summary>
public class KeyRingGuard : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly DataProtectionCertificates _certificates;
    private readonly ILogger<KeyRingGuard> _logger;

    /// <summary>Creates the guard</summary>
    public KeyRingGuard(IServiceProvider services, DataProtectionCertificates certificates, ILogger<KeyRingGuard> logger)
    {
        _services = services;
        _certificates = certificates;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Dictionary<Guid, bool> keys;
        try
        {
            keys = KeyRingInspector.Keys(KeyRingInspector.Repository(_services));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Redis not up yet: the ring cannot be checked, and nothing can be decrypted either
            _logger.LogWarning(ex, "The Data Protection key ring could not be read at startup; not checked");
            return Task.CompletedTask;
        }

        Check(keys, _certificates, _services.GetRequiredService<IKeyManager>());
        var wrapped = keys.Count(k => k.Value);
        if (_certificates.Enabled)
        {
            _logger.LogInformation("Data Protection key ring wrapped with the Vault certificate: {Keys} keys, {Unwrapped} not wrapped",
                keys.Count, keys.Count - wrapped);
        }
        else
        {
            _logger.LogWarning("Data Protection key ring is NOT wrapped (DataProtection__Certificate is not set): {Keys} keys in clear",
                keys.Count);
        }

        return Task.CompletedTask;
    }

    /// <summary>Throws when wrapped keys exist that the configuration cannot unwrap</summary>
    public static void Check(IReadOnlyDictionary<Guid, bool> keys, DataProtectionCertificates certificates, IKeyManager keyManager)
    {
        var wrapped = keys.Count(k => k.Value);
        if (wrapped > 0 && !certificates.Enabled)
        {
            throw new InvalidOperationException(
                $"The Data Protection key ring holds {wrapped} keys wrapped with a certificate, but DataProtection__Certificate is not configured. " +
                "Refusing to start: every stored token would be unreadable. Restore the certificate in Vault.");
        }

        if (wrapped == 0)
        {
            return;
        }

        var unreadable = 0;
        foreach (var key in keyManager.GetAllKeys().Where(k => !k.IsRevoked && keys.GetValueOrDefault(k.KeyId)))
        {
            try
            {
                _ = key.Descriptor;
            }
            catch (Exception)
            {
                unreadable++;
            }
        }

        if (unreadable > 0)
        {
            throw new InvalidOperationException(
                $"{unreadable} wrapped Data Protection keys cannot be unwrapped with the configured certificate(s). " +
                "During a certificate roll-over set DataProtection__PreviousCertificate to the old certificate.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
