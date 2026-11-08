using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentWarnings.Data;

public sealed class DddException : Exception
{
    public DddException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the error code: no_key, invalid_api_key, rate_limit_exceeded, monthly_limit_exceeded, http, network.</summary>
    public string Code { get; }
}

/// <summary>
/// Minimal client for the DoesTheDogDie.com API v3. Keeps below the per-minute limit on its own and records the
/// remaining monthly requests the API reports in every response.
/// </summary>
public sealed class DddClient
{
    private const int PerMinute = 25; // free tier allows 30

    private readonly IHttpClientFactory _httpFactory;
    private readonly WarningStore _store;
    private readonly ILogger<DddClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _recent = new();
    private DateTime _blockedUntil = DateTime.MinValue;

    public DddClient(IHttpClientFactory httpFactory, WarningStore store, ILogger<DddClient> logger)
    {
        _httpFactory = httpFactory;
        _store = store;
        _logger = logger;
    }

    public async Task<JsonElement> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? throw new DddException("no_key", "Plugin not loaded");
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            throw new DddException("no_key", "No API key configured");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _blockedUntil)
            {
                throw new DddException("rate_limit_exceeded", "Rate limited until " + _blockedUntil.ToString("u", CultureInfo.InvariantCulture));
            }

            // sliding window: at most PerMinute requests in 60 s
            while (_recent.Count > 0 && DateTime.UtcNow - _recent.Peek() > TimeSpan.FromSeconds(60))
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= PerMinute)
            {
                var wait = TimeSpan.FromSeconds(61) - (DateTime.UtcNow - _recent.Peek());
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _recent.Dequeue();
            }

            _recent.Enqueue(DateTime.UtcNow);

            var url = cfg.ApiBaseUrl.TrimEnd('/') + "/" + pathAndQuery.TrimStart('/');
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-API-KEY", cfg.ApiKey.Trim());
            req.Headers.UserAgent.ParseAdd("Jellyfin-ContentWarnings/" + (Plugin.Instance?.Version?.ToString() ?? "1.0"));

            HttpResponseMessage resp;
            try
            {
                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new DddException("network", ex.Message);
            }

            using (resp)
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Remaining-Month", out var vals)
                    && int.TryParse(vals.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
                {
                    _store.SetMonthRemaining(remaining);
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    return doc.RootElement.Clone();
                }

                var code = "http";
                var message = $"HTTP {(int)resp.StatusCode}";
                try
                {
                    using var err = JsonDocument.Parse(body);
                    if (err.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    {
                        code = e.GetString()!;
                    }

                    if (err.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        message = m.GetString()!;
                    }
                }
                catch (JsonException)
                {
                    // not JSON
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    code = "not_found";
                }
                else if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (code == "monthly_limit_exceeded")
                    {
                        _store.SetMonthRemaining(0);
                    }
                    else
                    {
                        code = "rate_limit_exceeded";
                        var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                        _blockedUntil = DateTime.UtcNow + retry;
                    }
                }

                _logger.LogDebug("Content Warnings: {Url} -> {Code} {Message}", url, code, message);
                throw new DddException(code, message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
