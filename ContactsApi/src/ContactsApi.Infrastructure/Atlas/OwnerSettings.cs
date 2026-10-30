/**
 * @fileoverview The Owner's settings documents (AD-41): one JSON object per Owner and
 * kind, kept as the browser sends it (the browser owns the schema, its defaults and
 * ranges)
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>A settings document as returned</summary>
/// <param name="Json">The document; an empty object when none was saved</param>
/// <param name="UpdatedAt">When it was saved; null when none was</param>
public record OwnerSettingsDoc(JsonElement Json, DateTime? UpdatedAt);

/// <summary>Reads and writes the Owner's settings documents</summary>
public class OwnerSettingsService
{
    /// <summary>The kinds of settings kept</summary>
    public static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "ui", "segments" };

    /// <summary>The largest document kept (UTF-8 bytes)</summary>
    public const int MaxBytes = 64 * 1024;

    private readonly AtlasDbContext _db;

    /// <summary>Creates the service</summary>
    public OwnerSettingsService(AtlasDbContext db) => _db = db;

    /// <summary>The Owner's document of a kind (an empty object when none)</summary>
    /// <exception cref="ArgumentException">An unknown kind</exception>
    public async Task<OwnerSettingsDoc> GetAsync(string owner, string kind, CancellationToken ct)
    {
        Check(kind);
        var row = await _db.OwnerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == owner && s.Kind == kind, ct);
        using var doc = JsonDocument.Parse(row?.Json ?? "{}");
        return new OwnerSettingsDoc(doc.RootElement.Clone(), row?.UpdatedAt);
    }

    /// <summary>Stores the Owner's document of a kind: a JSON object of at most <see cref="MaxBytes"/></summary>
    /// <exception cref="ArgumentException">An unknown kind, not an object, or too large</exception>
    public async Task<OwnerSettingsDoc> SaveAsync(string owner, string kind, JsonElement json, CancellationToken ct)
    {
        Check(kind);
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Settings must be a JSON object");
        }

        var text = json.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new ArgumentException($"Settings are larger than {MaxBytes / 1024} KB");
        }

        // Two first saves at once: the second finds the row the first added, and updates it
        for (var attempt = 0; ; attempt++)
        {
            var row = await _db.OwnerSettings.FirstOrDefaultAsync(s => s.OwnerId == owner && s.Kind == kind, ct);
            if (row == null)
            {
                row = new OwnerSetting { OwnerId = owner, Kind = kind };
                _db.OwnerSettings.Add(row);
            }

            row.Json = text;
            row.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _db.SaveChangesAsync(ct);
                return new OwnerSettingsDoc(json.Clone(), row.UpdatedAt);
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Forgets the Owner's document of a kind (the defaults apply); true when there was one</summary>
    /// <exception cref="ArgumentException">An unknown kind</exception>
    public async Task<bool> ResetAsync(string owner, string kind, CancellationToken ct)
    {
        Check(kind);
        return await _db.OwnerSettings.Where(s => s.OwnerId == owner && s.Kind == kind).ExecuteDeleteAsync(ct) > 0;
    }

    private static void Check(string kind)
    {
        if (!Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unknown settings kind: {kind}");
        }
    }
}
