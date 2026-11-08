using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.ContentWarnings.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ContentWarnings.Api;

[ApiController]
[Route("ContentWarnings")]
public class ContentWarningsController : ControllerBase
{
    private const string Site = "https://www.doesthedogdie.com";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WarningService _service;
    private readonly WarningStore _store;
    private readonly DddClient _client;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public ContentWarningsController(WarningService service, WarningStore store, DddClient client, ILibraryManager library, IUserManager users)
    {
        _service = service;
        _store = store;
        _client = client;
        _library = library;
        _users = users;
    }

    // ------------------------------------------------------------------ client assets (anonymous: loaded before login)

    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var cfg = plugin.Configuration;
        var clientConfig = new { cfg.Enabled, cfg.ExpandedByDefault, Version = plugin.CacheKey };
        var script = "window.ContentWarningsConfig = " + JsonSerializer.Serialize(clientConfig, JsonOptions) + ";\n"
            + ReadResource("Web.contentwarnings.js");
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(script, "application/javascript; charset=utf-8");
    }

    [HttpGet("ClientStyle")]
    [AllowAnonymous]
    [Produces("text/css")]
    public ActionResult GetClientStyle()
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(ReadResource("Web.contentwarnings.css"), "text/css; charset=utf-8");
    }

    // ------------------------------------------------------------------ data for a details page

    /// <summary>The warnings for a movie or series (episodes and seasons get their series' warnings).</summary>
    [HttpGet("Item/{itemId}")]
    [Authorize]
    public async Task<ActionResult> GetItem([FromRoute] Guid itemId, CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || !CanSee(item))
        {
            return NotFound();
        }

        var target = WarningService.TargetOf(item);
        if (target is null)
        {
            return Ok(new { status = "unsupported" });
        }

        var result = await _service.GetForPageAsync(target, ct).ConfigureAwait(false);
        var catalog = result.Entry is { NotFound: false } ? await _service.GetCatalogAsync(true, ct).ConfigureAwait(false) : _store.Catalog;
        var topicCat = catalog?.Topics.ToDictionary(t => t.Id, t => t.CategoryId) ?? new Dictionary<int, int>();
        var cats = catalog?.Categories.ToDictionary(c => c.Id) ?? new Dictionary<int, CategoryDto>();
        var supers = catalog?.SuperCategories.ToDictionary(s => s.Id, s => s.Name) ?? new Dictionary<int, string>();

        var topics = (result.Entry?.Topics ?? new List<TopicStat>())
            .Where(WarningService.Applies)
            .Select(t =>
            {
                cats.TryGetValue(topicCat.GetValueOrDefault(t.TopicId), out var cat);
                return new
                {
                    id = t.TopicId,
                    name = t.Name,
                    category = cat?.Name,
                    group = cat is not null && supers.TryGetValue(cat.SuperId, out var s) ? s : null,
                    yes = t.Yes,
                    no = t.No,
                    comments = t.Comments,
                    severity = t.Severity
                };
            })
            .OrderByDescending(t => t.yes - t.no)
            .ToList();

        return Ok(new
        {
            status = result.Status,
            error = result.Error,
            scope = target is Series ? (item is Series ? "series" : "parentSeries") : "movie",
            seriesName = item is Series ? null : (target is Series ? target.Name : null),
            dddUrl = result.Entry?.DddId is int id ? $"{Site}/media/{id}" : null,
            siteUrl = Site,
            fetchedAt = result.Entry?.FetchedAt,
            topics
        });
    }

    /// <summary>All topics, grouped, for the personal "my topics" choice.</summary>
    [HttpGet("Topics")]
    [Authorize]
    public async Task<ActionResult> GetTopics(CancellationToken ct)
    {
        var catalog = await _service.GetCatalogAsync(true, ct).ConfigureAwait(false);
        if (catalog is null)
        {
            return Ok(Array.Empty<object>());
        }

        var cats = catalog.Categories.ToDictionary(c => c.Id);
        var supers = catalog.SuperCategories.ToDictionary(s => s.Id, s => s.Name);
        var groups = catalog.Topics
            .Select(t =>
            {
                cats.TryGetValue(t.CategoryId, out var c);
                var g = c is not null && supers.TryGetValue(c.SuperId, out var s) ? s : "Other";
                return new { t.Id, t.Name, Category = c?.Name ?? string.Empty, Group = g };
            })
            .GroupBy(t => t.Group)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                group = g.Key,
                topics = g.OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new { id = t.Id, name = t.Name, category = t.Category })
            });
        return Ok(groups);
    }

    // ------------------------------------------------------------------ admin

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        var cfg = Plugin.Instance?.Configuration;
        var catalog = _store.Catalog;
        return Ok(new
        {
            hasKey = !string.IsNullOrWhiteSpace(cfg?.ApiKey),
            cached = _store.Count,
            found = _store.FoundCount,
            monthRemaining = _store.MonthBudget.Remaining,
            monthRemainingAt = _store.MonthBudget.At,
            topics = catalog?.Topics.Count ?? 0,
            catalogAt = catalog?.FetchedAt
        });
    }

    /// <summary>Checks the key with one request (and loads the topic list if it is missing).</summary>
    [HttpPost("TestKey")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestKey(CancellationToken ct)
    {
        try
        {
            var r = await _client.GetAsync("items?imdb=tt0050798", ct).ConfigureAwait(false);
            await _service.GetCatalogAsync(true, ct).ConfigureAwait(false);
            return Ok(new { ok = true, monthRemaining = _store.MonthBudget.Remaining });
        }
        catch (DddException ex)
        {
            return Ok(new { ok = false, error = ex.Code, message = ex.Message });
        }
    }

    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearCache()
    {
        _store.Clear();
        return NoContent();
    }

    private bool CanSee(BaseItem item)
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return true;
        }

        var user = _users.GetUserById(userId);
        return user is null || item.IsVisible(user);
    }

    private static string ReadResource(string relativeName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{typeof(Plugin).Namespace}.{relativeName}";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
