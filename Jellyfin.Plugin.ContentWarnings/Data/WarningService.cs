using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.ContentWarnings.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentWarnings.Data;

public sealed record WarningResult(string Status, CacheEntry? Entry, string? Error = null);

/// <summary>
/// Looks up the warnings for a movie or series: from the cache when it is fresh, otherwise from the API
/// (2 requests: find the title by IMDb/TMDb id, then its topic stats). Also keeps the optional tags in sync.
/// </summary>
public sealed class WarningService
{
    /// <summary>Lazy lookups from page views stop when fewer monthly requests than this are left.</summary>
    private const int PageViewFloor = 20;

    private readonly DddClient _client;
    private readonly WarningStore _store;
    private readonly ILibraryManager _library;
    private readonly ILogger<WarningService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    public WarningService(DddClient client, WarningStore store, ILibraryManager library, ILogger<WarningService> logger)
    {
        _client = client;
        _store = store;
        _library = library;
        _logger = logger;
    }

    private static PluginConfiguration Cfg => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The movie or series whose warnings apply to <paramref name="item"/> (episodes and seasons use their series).</summary>
    public static BaseItem? TargetOf(BaseItem item) => item switch
    {
        Movie => item,
        Series => Cfg.IncludeSeries ? item : null,
        Season s => Cfg.IncludeSeries ? s.Series : null,
        Episode e => Cfg.IncludeSeries ? e.Series : null,
        _ => null
    };

    /// <summary>Cache key: the same title in several libraries shares one entry.</summary>
    public static string? KeyOf(BaseItem target)
    {
        var kind = target is Series ? "tv" : "movie";
        if (target.TryGetProviderId(MetadataProvider.Imdb, out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            return $"{kind}:imdb:{imdb.Trim().ToLowerInvariant()}";
        }

        if (target.TryGetProviderId(MetadataProvider.Tmdb, out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
        {
            return $"{kind}:tmdb:{tmdb.Trim()}";
        }

        return null;
    }

    public static bool IsStale(CacheEntry entry)
    {
        var days = Math.Max(1, Cfg.RefreshDays);
        if (entry.ReleaseYear is int y && y >= DateTime.UtcNow.Year - 1)
        {
            days = Math.Max(1, days / 4); // new titles still collect votes
        }

        return DateTime.UtcNow - entry.FetchedAt > TimeSpan.FromDays(days);
    }

    /// <summary>Whether a topic counts as present with the configured vote thresholds.</summary>
    public static bool Applies(TopicStat t)
    {
        var cfg = Cfg;
        var total = t.Yes + t.No;
        return t.Yes >= Math.Max(1, cfg.MinYesVotes) && total > 0 && t.Yes * 100 >= Math.Clamp(cfg.MinYesPercent, 0, 100) * total;
    }

    public int? MonthRemaining => _store.MonthBudget.Remaining;

    /// <param name="reserve">Requests that must remain this month for a new lookup to start.</param>
    public async Task<WarningResult> GetAsync(BaseItem target, bool allowFetch, int reserve, CancellationToken ct)
    {
        var key = KeyOf(target);
        if (key is null)
        {
            return new WarningResult("no_id", null);
        }

        var cached = _store.Get(key);
        if (cached is not null && (!IsStale(cached) || !allowFetch))
        {
            return new WarningResult(cached.NotFound ? "not_found" : "ok", cached);
        }

        if (!allowFetch)
        {
            return new WarningResult("not_cached", null);
        }

        if (string.IsNullOrWhiteSpace(Cfg.ApiKey))
        {
            return cached is null ? new WarningResult("no_key", null) : new WarningResult(cached.NotFound ? "not_found" : "ok", cached);
        }

        if (_store.MonthBudget.Remaining is int left && left < Math.Max(reserve, 2))
        {
            return cached is null ? new WarningResult("budget", null) : new WarningResult(cached.NotFound ? "not_found" : "ok", cached);
        }

        var gate = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // another request may have fetched it meanwhile
            var again = _store.Get(key);
            if (again is not null && !IsStale(again))
            {
                return new WarningResult(again.NotFound ? "not_found" : "ok", again);
            }

            var entry = await FetchAsync(target, ct).ConfigureAwait(false);
            _store.Set(key, entry);
            await SyncTagsAsync(target, entry, ct).ConfigureAwait(false);
            return new WarningResult(entry.NotFound ? "not_found" : "ok", entry);
        }
        catch (DddException ex)
        {
            _logger.LogInformation("Content Warnings: lookup of {Name} failed: {Code} {Message}", target.Name, ex.Code, ex.Message);
            return cached is null ? new WarningResult("error", null, ex.Code) : new WarningResult(cached.NotFound ? "not_found" : "ok", cached, ex.Code);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<WarningResult> GetForPageAsync(BaseItem target, CancellationToken ct) => GetAsync(target, true, PageViewFloor, ct);

    private async Task<CacheEntry> FetchAsync(BaseItem target, CancellationToken ct)
    {
        var wantType = target is Series ? "TV" : "Movie";
        int? dddId = null;

        if (target.TryGetProviderId(MetadataProvider.Imdb, out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            dddId = Pick(await _client.GetAsync("items?imdb=" + Uri.EscapeDataString(imdb.Trim()), ct).ConfigureAwait(false), wantType);
        }

        if (dddId is null && target.TryGetProviderId(MetadataProvider.Tmdb, out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
        {
            dddId = Pick(await _client.GetAsync("items?tmdb=" + Uri.EscapeDataString(tmdb.Trim()), ct).ConfigureAwait(false), wantType);
        }

        if (dddId is null)
        {
            return new CacheEntry { NotFound = true, FetchedAt = DateTime.UtcNow, ReleaseYear = target.ProductionYear };
        }

        var detail = await _client.GetAsync("items/" + dddId.Value, ct).ConfigureAwait(false);
        var entry = new CacheEntry { DddId = dddId, FetchedAt = DateTime.UtcNow, ReleaseYear = target.ProductionYear };
        if (detail.TryGetProperty("releaseYear", out var ry) && ry.ValueKind == JsonValueKind.Number)
        {
            entry.ReleaseYear = ry.GetInt32();
        }

        if (detail.TryGetProperty("topicItemStats", out var stats) && stats.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stats.EnumerateArray())
            {
                entry.Topics.Add(new TopicStat
                {
                    TopicId = Int(s, "topicId"),
                    Name = Str(s, "topicName"),
                    Yes = Int(s, "yesSum"),
                    No = Int(s, "noSum"),
                    Comments = Int(s, "numComments"),
                    Severity = SeverityLabel(s)
                });
            }
        }

        return entry;
    }

    /// <summary>The DoesTheDogDie id of the result with the right media type (TMDb ids overlap between movies and shows).</summary>
    private static int? Pick(JsonElement results, string wantType)
    {
        if (results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var r in results.EnumerateArray())
        {
            if (string.Equals(Str(r, "itemTypeName"), wantType, StringComparison.OrdinalIgnoreCase))
            {
                return Int(r, "id");
            }
        }

        return null;
    }

    /// <summary>The reviewers' level if verified, otherwise the most voted one.</summary>
    private static string? SeverityLabel(JsonElement stat)
    {
        if (!stat.TryGetProperty("severity", out var sev) || sev.ValueKind != JsonValueKind.Object
            || !sev.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = levels.EnumerateArray().Select(l => l.GetString() ?? string.Empty).ToList();
        if (sev.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.Number)
        {
            var i = v.GetInt32() - 1;
            if (i >= 0 && i < names.Count)
            {
                return names[i];
            }
        }

        if (sev.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Array)
        {
            var c = counts.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList();
            if (c.Count == names.Count && c.Sum() > 0)
            {
                return names[c.IndexOf(c.Max())];
            }
        }

        return null;
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    // ------------------------------------------------------------------ catalog (topics, categories)

    public async Task<CatalogDto?> GetCatalogAsync(bool allowFetch, CancellationToken ct)
    {
        var cat = _store.Catalog;
        var stale = cat is null || DateTime.UtcNow - cat.FetchedAt > TimeSpan.FromDays(14);
        if (!stale || !allowFetch || string.IsNullOrWhiteSpace(Cfg.ApiKey))
        {
            return cat;
        }

        try
        {
            var topics = await _client.GetAsync("topics", ct).ConfigureAwait(false);
            var cats = await _client.GetAsync("topiccategories", ct).ConfigureAwait(false);
            var supers = await _client.GetAsync("topicsupercategories", ct).ConfigureAwait(false);
            var dto = new CatalogDto { FetchedAt = DateTime.UtcNow };
            foreach (var t in topics.EnumerateArray())
            {
                dto.Topics.Add(new TopicInfo { Id = Int(t, "id"), Name = Str(t, "name"), CategoryId = Int(t, "topicCategoryId") });
            }

            foreach (var c in cats.EnumerateArray())
            {
                dto.Categories.Add(new CategoryDto { Id = Int(c, "id"), Name = Str(c, "name"), SuperId = Int(c, "topicSuperCategoryId") });
            }

            foreach (var s in supers.EnumerateArray())
            {
                dto.SuperCategories.Add(new SuperDto { Id = Int(s, "id"), Name = Str(s, "name") });
            }

            _store.Catalog = dto;
            return dto;
        }
        catch (DddException ex)
        {
            _logger.LogInformation("Content Warnings: topic list not loaded: {Code} {Message}", ex.Code, ex.Message);
            return cat;
        }
    }

    // ------------------------------------------------------------------ optional tags

    /// <summary>Writes "CW: …" tags for the configured topics and removes outdated ones; other tags are left alone.</summary>
    public async Task SyncTagsAsync(BaseItem target, CacheEntry entry, CancellationToken ct)
    {
        var cfg = Cfg;
        var prefix = string.IsNullOrWhiteSpace(cfg.TagPrefix) ? "CW: " : cfg.TagPrefix;
        var wanted = new HashSet<int>(cfg.TagTopicIds ?? new List<int>());
        var desired = entry.Topics
            .Where(t => wanted.Contains(t.TopicId) && Applies(t))
            .Select(t => prefix + t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = target.Tags ?? Array.Empty<string>();
        var kept = current.Where(t => !t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var next = kept.Concat(desired).ToArray();
        if (next.Length == current.Length && next.All(t => current.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        if (wanted.Count == 0 && !current.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        target.Tags = next;
        await _library.UpdateItemAsync(target, target.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }
}
