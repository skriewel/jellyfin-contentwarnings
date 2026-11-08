using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentWarnings.Data;

/// <summary>Vote totals for one topic on one title, as DoesTheDogDie reports them.</summary>
public sealed class TopicStat
{
    public int TopicId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Yes { get; set; }

    public int No { get; set; }

    public int Comments { get; set; }

    /// <summary>Gets or sets the verified or most voted severity label ("Depicted"), if the topic has levels.</summary>
    public string? Severity { get; set; }
}

/// <summary>Cached result for one title (keyed by IMDb / TMDb id, so copies of a movie share it).</summary>
public sealed class CacheEntry
{
    public int? DddId { get; set; }

    public bool NotFound { get; set; }

    public DateTime FetchedAt { get; set; }

    public int? ReleaseYear { get; set; }

    public List<TopicStat> Topics { get; set; } = new();
}

public sealed class TopicInfo
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int CategoryId { get; set; }
}

public sealed class StoreData
{
    public Dictionary<string, CacheEntry> Entries { get; set; } = new();

    public CatalogDto? Catalog { get; set; }

    /// <summary>Gets or sets the remaining monthly requests as last reported by the API.</summary>
    public int? MonthRemaining { get; set; }

    public DateTime? MonthRemainingAt { get; set; }
}

/// <summary>All topics with their categories and super categories (refreshed weekly).</summary>
public sealed class CatalogDto
{
    public DateTime FetchedAt { get; set; }

    public List<TopicInfo> Topics { get; set; } = new();

    public List<CategoryDto> Categories { get; set; } = new();

    public List<SuperDto> SuperCategories { get; set; } = new();
}

public sealed class CategoryDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SuperId { get; set; }
}

public sealed class SuperDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The plugin's own cache: one JSON file in the plugin's data folder. Jellyfin's database is never touched
/// (except for the optional tags), and uninstalling the plugin removes everything with its folder.
/// </summary>
public sealed class WarningStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly ILogger<WarningStore> _logger;
    private StoreData? _data;
    private Timer? _saveTimer;

    public WarningStore(ILogger<WarningStore> logger)
    {
        _logger = logger;
    }

    private static string FilePath
    {
        get
        {
            var dir = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "cache.json");
        }
    }

    private StoreData Data
    {
        get
        {
            if (_data is not null)
            {
                return _data;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    _data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), JsonOptions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content Warnings: cache file unreadable, starting empty");
            }

            return _data ??= new StoreData();
        }
    }

    public CacheEntry? Get(string key)
    {
        lock (_lock)
        {
            return Data.Entries.TryGetValue(key, out var e) ? e : null;
        }
    }

    public void Set(string key, CacheEntry entry)
    {
        lock (_lock)
        {
            Data.Entries[key] = entry;
        }

        ScheduleSave();
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return Data.Entries.Count;
            }
        }
    }

    public int FoundCount
    {
        get
        {
            lock (_lock)
            {
                return Data.Entries.Values.Count(e => !e.NotFound);
            }
        }
    }

    public CatalogDto? Catalog
    {
        get
        {
            lock (_lock)
            {
                return Data.Catalog;
            }
        }

        set
        {
            lock (_lock)
            {
                Data.Catalog = value;
            }

            ScheduleSave();
        }
    }

    public (int? Remaining, DateTime? At) MonthBudget
    {
        get
        {
            lock (_lock)
            {
                // the monthly counter resets with the calendar month
                if (Data.MonthRemainingAt is DateTime at && (at.Year != DateTime.UtcNow.Year || at.Month != DateTime.UtcNow.Month))
                {
                    return (null, null);
                }

                return (Data.MonthRemaining, Data.MonthRemainingAt);
            }
        }
    }

    public void SetMonthRemaining(int remaining)
    {
        lock (_lock)
        {
            Data.MonthRemaining = remaining;
            Data.MonthRemainingAt = DateTime.UtcNow;
        }

        ScheduleSave();
    }

    public void Clear()
    {
        lock (_lock)
        {
            Data.Entries.Clear();
        }

        Flush();
    }

    public void Flush()
    {
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(Data, JsonOptions);
        }

        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content Warnings: could not save the cache");
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            _saveTimer ??= new Timer(_ => Flush());
            _saveTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }
}
