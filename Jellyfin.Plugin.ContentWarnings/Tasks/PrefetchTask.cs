using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentWarnings.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentWarnings.Tasks;

/// <summary>
/// Fills the cache ahead of time, within the monthly API budget: titles never looked up first, then the
/// oldest stale ones. Also brings the optional tags in line with the cache (no API requests needed for that).
/// </summary>
public sealed class PrefetchTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly WarningService _service;
    private readonly WarningStore _store;
    private readonly ILogger<PrefetchTask> _logger;

    public PrefetchTask(ILibraryManager library, WarningService service, WarningStore store, ILogger<PrefetchTask> logger)
    {
        _library = library;
        _service = service;
        _store = store;
        _logger = logger;
    }

    public string Name => "Fetch content warnings";

    public string Key => "ContentWarningsPrefetch";

    public string Description => "Looks up content warnings from DoesTheDogDie.com for movies and series that are not cached yet or are outdated, within the monthly request budget, and updates the content warning tags.";

    public string Category => "Content Warnings";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return;
        }

        var kinds = cfg.IncludeSeries ? new[] { BaseItemKind.Movie, BaseItemKind.Series } : new[] { BaseItemKind.Movie };
        var items = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = kinds, Recursive = true, IsVirtualItem = false });

        // one lookup per title, even if it exists in several libraries
        var groups = items
            .Select(i => (Item: i, Key: WarningService.KeyOf(i)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, x => x.Item)
            .ToList();

        var todo = groups
            .Select(g => (Group: g, Entry: _store.Get(g.Key)))
            .Where(x => x.Entry is null || WarningService.IsStale(x.Entry))
            .OrderBy(x => x.Entry is null ? 0 : 1)
            .ThenBy(x => x.Entry?.FetchedAt ?? DateTime.MinValue)
            .Take(Math.Max(0, cfg.PrefetchMaxPerRun))
            .ToList();

        var fetched = 0;
        var stopReason = string.Empty;
        if (todo.Count > 0 && !string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            await _service.GetCatalogAsync(true, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < todo.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_service.MonthRemaining is int left && left < Math.Max(cfg.MonthlyReserve, 2))
                {
                    stopReason = $"monthly budget reserve reached ({left} requests left)";
                    break;
                }

                var result = await _service.GetAsync(todo[i].Group.First(), true, cfg.MonthlyReserve, cancellationToken).ConfigureAwait(false);
                if (result.Error is "rate_limit_exceeded" or "monthly_limit_exceeded" or "invalid_api_key" or "missing_api_key" or "no_key")
                {
                    stopReason = result.Error;
                    break;
                }

                if (result.Entry is not null)
                {
                    fetched++;
                }

                progress.Report(80.0 * (i + 1) / todo.Count);
            }
        }
        else if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            stopReason = "no API key";
        }

        // tags for every copy of every cached title (cheap, local only)
        var tagged = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _store.Get(groups[i].Key);
            if (entry is not null)
            {
                foreach (var item in groups[i])
                {
                    await _service.SyncTagsAsync(item, entry, cancellationToken).ConfigureAwait(false);
                    tagged++;
                }
            }

            progress.Report(80 + (20.0 * (i + 1) / Math.Max(1, groups.Count)));
        }

        _store.Flush();
        _logger.LogInformation(
            "Content Warnings: {Fetched} of {Todo} titles looked up, {Cached} cached in total, {Remaining} requests left this month{Stop}",
            fetched,
            todo.Count,
            _store.Count,
            _service.MonthRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
            string.IsNullOrEmpty(stopReason) ? string.Empty : " (stopped: " + stopReason + ")");
        progress.Report(100);
    }
}
