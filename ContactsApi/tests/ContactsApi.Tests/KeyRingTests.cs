using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ContactsApi.WebApi.Services;
using ContactsApi.WebApi.Services.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>The key ring wrapped with the Vault certificate, and the retirement of the exposed keys (AD-50 rule 2)</summary>
public sealed class KeyRingTests : IDisposable
{
    /// <summary>The ring's storage, shared by every "process" of a test (a stand-in for the Redis list)</summary>
    private readonly MemoryKeyRing _store = new();
    private readonly List<ServiceProvider> _providers = new();

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private static X509Certificate2 NewCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static string Pem(X509Certificate2 cert) =>
        cert.ExportCertificatePem() + "\n" + cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();

    private static DataProtectionCertificates Wrapping(X509Certificate2? current, X509Certificate2? previous = null)
    {
        var settings = new Dictionary<string, string?>();
        if (current != null) settings[DataProtectionConfig.Certificate] = Pem(current);
        if (previous != null) settings[DataProtectionConfig.PreviousCertificate] = Pem(previous);
        return DataProtectionCertificates.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    /// <summary>A key ring on disk (a stand-in for the Redis list), as the app configures it</summary>
    private ServiceProvider Ring(DataProtectionCertificates certificates)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddDataProtection()
            .SetApplicationName(DataProtectionConfig.ApplicationName);
        services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o => o.XmlRepository = _store);
        if (certificates.Current != null)
        {
            builder.ProtectKeysWithCertificate(certificates.Current);
            builder.UnprotectKeysWithAnyCertificate(certificates.All.ToArray());
        }

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static KeyRingRotator Rotator(ServiceProvider ring, DataProtectionCertificates certificates, params IProtectedValueSource[] sources) =>
        new(ring.GetRequiredService<IDataProtectionProvider>(), ring.GetRequiredService<IKeyManager>(),
            KeyRingInspector.Repository(ring), certificates, sources, NullLogger.Instance);

    private static Dictionary<Guid, bool> Keys(ServiceProvider ring) => KeyRingInspector.Keys(KeyRingInspector.Repository(ring));

    /// <summary>Values written by the running app before wrapping existed</summary>
    private static (MemorySource Tokens, MemorySource Snapshots) SeedUnwrapped(ServiceProvider ring)
    {
        var provider = ring.GetRequiredService<IDataProtectionProvider>();
        var tokens = new MemorySource("google-refresh-tokens", GoogleTokenService.RefreshTokenPurpose);
        var snapshots = new MemorySource("activity-snapshots", ActivityStore.Purpose);
        for (var i = 0; i < 3; i++)
        {
            tokens.Values[$"refresh-token:owner-{i}"] = provider.CreateProtector(tokens.Purpose).Protect($"refresh-{i}");
        }

        snapshots.Values["activity:owner-0"] = provider.CreateProtector(snapshots.Purpose).Protect("{\"snapshot\":0}");
        return (tokens, snapshots);
    }

    private static void AssertReadable(ServiceProvider ring, MemorySource tokens, MemorySource snapshots)
    {
        var provider = ring.GetRequiredService<IDataProtectionProvider>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal($"refresh-{i}", provider.CreateProtector(tokens.Purpose).Unprotect(tokens.Values[$"refresh-token:owner-{i}"]));
        }

        Assert.Equal("{\"snapshot\":0}", provider.CreateProtector(snapshots.Purpose).Unprotect(snapshots.Values["activity:owner-0"]));
    }

    [Fact]
    public void Without_a_certificate_wrapping_is_off_and_the_app_starts_as_before()
    {
        var certificates = Wrapping(null);
        var ring = Ring(certificates);
        SeedUnwrapped(ring);

        Assert.False(certificates.Enabled);
        Assert.All(Keys(ring).Values, wrapped => Assert.False(wrapped));
        KeyRingGuard.Check(Keys(ring), certificates, ring.GetRequiredService<IKeyManager>());
    }

    [Fact]
    public void With_a_certificate_new_keys_are_wrapped()
    {
        var certificates = Wrapping(NewCertificate("dp"));
        var ring = Ring(certificates);

        ring.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect("y");

        Assert.All(Keys(ring).Values, Assert.True);
        Assert.Contains("encryptedSecret", string.Concat(_store.GetAllElements().Select(e => e.ToString())));
    }

    [Fact]
    public async Task The_full_rotation_keeps_every_value_readable_and_leaves_only_wrapped_keys()
    {
        var (tokens, snapshots) = SeedUnwrapped(Ring(Wrapping(null)));
        var before = tokens.Values.ToDictionary(v => v.Key, v => v.Value);
        var certificates = Wrapping(NewCertificate("dp"));
        var ring = Ring(certificates);
        var oldKeys = Keys(ring);

        // Dry run: counts only, nothing written, no key created
        var dry = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.DryRun, CancellationToken.None);
        Assert.Null(dry.Refused);
        Assert.True(dry.KeyCreated);
        Assert.Equal(3, dry.Kinds["google-refresh-tokens"].Reprotected);
        Assert.Equal(before, tokens.Values);
        Assert.Equal(oldKeys.Keys.Order(), Keys(ring).Keys.Order());

        // Reprotect: a new wrapped key, every value moved to it, all still readable
        var first = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.Reprotect, CancellationToken.None);
        Assert.True(first.KeyCreated);
        Assert.True(Keys(ring)[first.CurrentKey]);
        Assert.Equal(3, first.Kinds["google-refresh-tokens"].Reprotected);
        Assert.Equal(1, first.Kinds["activity-snapshots"].Reprotected);
        Assert.All(tokens.Values.Values, v => Assert.Equal(first.CurrentKey, KeyRingRotator.KeyIdOf(v)));
        AssertReadable(ring, tokens, snapshots);

        // Idempotent: a second run changes nothing
        var again = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.Reprotect, CancellationToken.None);
        Assert.False(again.KeyCreated);
        Assert.Equal(0, again.Kinds["google-refresh-tokens"].Reprotected);
        Assert.Equal(3, again.Kinds["google-refresh-tokens"].Current);

        // Retire: the old keys revoked and deleted; only wrapped keys remain
        var retire = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.Retire, CancellationToken.None);
        Assert.Null(retire.RetireBlocked);
        Assert.Equal(oldKeys.Count, retire.KeysRetired);
        var left = Keys(ring);
        Assert.Equal(new[] { first.CurrentKey }, left.Keys);
        Assert.True(left[first.CurrentKey]);
        Assert.DoesNotContain(_store.GetAllElements(), e => e.Name.LocalName == "key" && !e.ToString().Contains("encryptedSecret"));

        // A fresh process with the certificate still reads every value
        AssertReadable(Ring(certificates), tokens, snapshots);
    }

    [Fact]
    public async Task Rotation_without_the_certificate_is_refused_and_writes_nothing()
    {
        var unwrapped = Wrapping(null);
        var ring = Ring(unwrapped);
        var (tokens, snapshots) = SeedUnwrapped(ring);
        var before = tokens.Values.ToDictionary(v => v.Key, v => v.Value);
        var keys = Keys(ring).Count;

        foreach (var mode in new[] { RotationMode.Reprotect, RotationMode.Retire })
        {
            var report = await Rotator(ring, unwrapped, tokens, snapshots).RunAsync(mode, CancellationToken.None);
            Assert.NotNull(report.Refused);
        }

        Assert.Equal(before, tokens.Values);
        Assert.Equal(keys, Keys(ring).Count);
    }

    [Fact]
    public async Task Retiring_is_blocked_while_a_value_on_an_old_key_cannot_be_read()
    {
        var (tokens, snapshots) = SeedUnwrapped(Ring(Wrapping(null)));
        var oldRing = Ring(Wrapping(null));
        // A value whose purpose the rotation does not know: it would be lost with its key
        var stranger = new MemorySource("stranger", "Some.Other.Purpose.v1");
        stranger.Values["x"] = oldRing.GetRequiredService<IDataProtectionProvider>().CreateProtector("Unlisted.v1").Protect("secret");
        var certificates = Wrapping(NewCertificate("dp"));
        var ring = Ring(certificates);
        var oldKeys = Keys(ring).Keys.ToList();

        var report = await Rotator(ring, certificates, tokens, snapshots, stranger).RunAsync(RotationMode.Retire, CancellationToken.None);

        Assert.Equal(1, report.Kinds["stranger"].Unreadable);
        Assert.NotNull(report.RetireBlocked);
        Assert.Equal(0, report.KeysRetired);
        Assert.All(oldKeys, k => Assert.Contains(k, Keys(ring).Keys));
        AssertReadable(ring, tokens, snapshots);
    }

    [Fact]
    public async Task A_value_that_changes_during_the_run_is_left_for_the_next_run_and_blocks_retiring()
    {
        var (tokens, snapshots) = SeedUnwrapped(Ring(Wrapping(null)));
        var certificates = Wrapping(NewCertificate("dp"));
        var ring = Ring(certificates);
        tokens.ChangeBeforeReplace = "refresh-token:owner-1";

        var report = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.Retire, CancellationToken.None);

        Assert.Equal(1, report.Kinds["google-refresh-tokens"].ChangedMeanwhile);
        Assert.NotNull(report.RetireBlocked);

        // The next run finishes the job
        tokens.ChangeBeforeReplace = null;
        var next = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.Retire, CancellationToken.None);
        Assert.Null(next.RetireBlocked);
        Assert.True(next.KeysRetired > 0);
    }

    [Fact]
    public async Task A_value_whose_key_is_already_gone_does_not_block_retiring()
    {
        var (tokens, snapshots) = SeedUnwrapped(Ring(Wrapping(null)));
        var elsewhere = DataProtectionProvider.Create("another-app");
        tokens.Values["refresh-token:orphan"] = elsewhere.CreateProtector(tokens.Purpose).Protect("lost");
        var certificates = Wrapping(NewCertificate("dp"));
        var ring = Ring(certificates);

        var report = await Rotator(ring, certificates, tokens, snapshots).RunAsync(RotationMode.Retire, CancellationToken.None);

        Assert.Equal(1, report.Kinds["google-refresh-tokens"].Lost);
        Assert.Null(report.RetireBlocked);
    }

    [Fact]
    public void Wrapped_keys_without_the_certificate_stop_the_app_from_starting()
    {
        var certificate = NewCertificate("dp");
        var wrapped = Ring(Wrapping(certificate));
        wrapped.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect("y");

        var none = Wrapping(null);
        var withoutCert = Ring(none);
        Assert.Throws<InvalidOperationException>(() => KeyRingGuard.Check(Keys(withoutCert), none, withoutCert.GetRequiredService<IKeyManager>()));

        var other = Wrapping(NewCertificate("other"));
        var wrongCert = Ring(other);
        Assert.Throws<InvalidOperationException>(() => KeyRingGuard.Check(Keys(wrongCert), other, wrongCert.GetRequiredService<IKeyManager>()));

        // A roll-over: the new certificate wraps, the previous one still unwraps
        var rollOver = Wrapping(NewCertificate("next"), certificate);
        var rolled = Ring(rollOver);
        KeyRingGuard.Check(Keys(rolled), rollOver, rolled.GetRequiredService<IKeyManager>());
    }

    [Fact]
    public void A_certificate_without_its_private_key_is_refused_without_echoing_it()
    {
        var pem = NewCertificate("dp").ExportCertificatePem();

        var ex = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificates.Parse(pem, null, "DataProtection__Certificate"));

        Assert.DoesNotContain("BEGIN", ex.Message);
    }

    [Fact]
    public void A_base64_pkcs12_certificate_is_accepted()
    {
        var pfx = Convert.ToBase64String(NewCertificate("dp").Export(X509ContentType.Pkcs12, "pw"));

        var cert = DataProtectionCertificates.Parse(pfx, "pw", "DataProtection__Certificate");

        Assert.True(cert.HasPrivateKey);
    }

    [Theory]
    [InlineData(null, RotationMode.Off)]
    [InlineData("", RotationMode.Off)]
    [InlineData("nonsense", RotationMode.Off)]
    [InlineData("dry-run", RotationMode.DryRun)]
    [InlineData(" Reprotect ", RotationMode.Reprotect)]
    [InlineData("retire", RotationMode.Retire)]
    public void The_mode_comes_from_configuration_and_defaults_to_off(string? text, RotationMode mode)
    {
        Assert.Equal(mode, KeyRingRotation.ParseMode(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("AAAA")]
    public void Garbage_has_no_key_id(string value)
    {
        Assert.Null(KeyRingRotator.KeyIdOf(value));
    }

    /// <summary>A deletable key ring in memory, with the semantics of <see cref="RedisKeyRingRepository"/> (one entry per element)</summary>
    private sealed class MemoryKeyRing : Microsoft.AspNetCore.DataProtection.Repositories.IDeletableXmlRepository
    {
        private readonly List<string> _entries = new();

        public IReadOnlyCollection<System.Xml.Linq.XElement> GetAllElements()
        {
            lock (_entries)
            {
                return _entries.Select(System.Xml.Linq.XElement.Parse).ToList();
            }
        }

        public void StoreElement(System.Xml.Linq.XElement element, string friendlyName)
        {
            lock (_entries)
            {
                _entries.Add(element.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            }
        }

        public bool DeleteElements(Action<IReadOnlyCollection<Microsoft.AspNetCore.DataProtection.Repositories.IDeletableElement>> chooseElements)
        {
            lock (_entries)
            {
                var elements = _entries.Select(raw => new Entry(raw)).ToList();
                chooseElements(elements);
                foreach (var chosen in elements.Where(e => e.DeletionOrder.HasValue).OrderBy(e => e.DeletionOrder))
                {
                    _entries.Remove(chosen.Raw);
                }

                return true;
            }
        }

        private sealed class Entry : Microsoft.AspNetCore.DataProtection.Repositories.IDeletableElement
        {
            public Entry(string raw)
            {
                Raw = raw;
                Element = System.Xml.Linq.XElement.Parse(raw);
            }

            public string Raw { get; }
            public System.Xml.Linq.XElement Element { get; }
            public int? DeletionOrder { get; set; }
        }
    }

    /// <summary>Protected values in memory</summary>
    private sealed class MemorySource : IProtectedValueSource
    {
        public MemorySource(string kind, string purpose)
        {
            Kind = kind;
            Purpose = purpose;
        }

        public string Kind { get; }
        public string Purpose { get; }
        public Dictionary<string, string> Values { get; } = new();

        /// <summary>Simulates the app writing this value while the rotation runs</summary>
        public string? ChangeBeforeReplace { get; set; }

        public async IAsyncEnumerable<ProtectedValue> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var (id, value) in Values.ToList())
            {
                await Task.Yield();
                yield return new ProtectedValue(id, value);
            }
        }

        public Task<bool> ReplaceAsync(ProtectedValue value, string newProtected, CancellationToken ct)
        {
            if (value.Id == ChangeBeforeReplace || Values[value.Id] != value.Protected)
            {
                return Task.FromResult(false);
            }

            Values[value.Id] = newProtected;
            return Task.FromResult(true);
        }
    }
}
