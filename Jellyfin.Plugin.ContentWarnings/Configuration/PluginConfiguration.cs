using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ContentWarnings.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the warnings are shown in the web client.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the DoesTheDogDie.com API key (X-API-KEY). Never sent to clients.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the API base URL (only changed for testing).</summary>
    public string ApiBaseUrl { get; set; } = "https://www.doesthedogdie.com/api/v3";

    /// <summary>Gets or sets a value indicating whether series are looked up, too.</summary>
    public bool IncludeSeries { get; set; } = true;

    /// <summary>Gets or sets the minimum number of "yes" votes for a warning to count.</summary>
    public int MinYesVotes { get; set; } = 3;

    /// <summary>Gets or sets the minimum share of "yes" among all votes, in percent.</summary>
    public int MinYesPercent { get; set; } = 60;

    /// <summary>Gets or sets after how many days cached data is refreshed (titles from the last two years: a quarter of it).</summary>
    public int RefreshDays { get; set; } = 60;

    /// <summary>Gets or sets how many monthly API requests the prefetch task always leaves for page views.</summary>
    public int MonthlyReserve { get; set; } = 800;

    /// <summary>Gets or sets the maximum number of titles the prefetch task looks up per run.</summary>
    public int PrefetchMaxPerRun { get; set; } = 300;

    /// <summary>Gets or sets a value indicating whether the details are expanded by default.</summary>
    public bool ExpandedByDefault { get; set; }

    /// <summary>Gets or sets the topics written to the items as Jellyfin tags (for parental controls and native apps).</summary>
    public List<int> TagTopicIds { get; set; } = new();

    /// <summary>Gets or sets the prefix of those tags.</summary>
    public string TagPrefix { get; set; } = "CW: ";

    /// <summary>Gets or sets a value that changes on every save (cache busting for the client script).</summary>
    public long Revision { get; set; }
}
