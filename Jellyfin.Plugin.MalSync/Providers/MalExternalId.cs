using MediaBrowser.Controller.Entities;
using Jellyfin.Plugin.MalSync.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.MalSync.Providers;

/// <summary>
/// Registers "MyAnimeList" as a known external ID for series and seasons.
/// <para>
/// Two things follow from this: Jellyfin's metadata editor gains a MyAnimeList
/// field, so a MAL ID can be entered once and shared by every user on the server,
/// and any item carrying that ID gets a MyAnimeList link on its detail page via
/// <see cref="MalExternalUrlProvider"/>.
/// </para>
/// <para>
/// The key matches the one <c>MalSyncService</c> already reads when resolving a
/// season, so an ID entered here is treated as authoritative and skips both the
/// title search and the per-user cache.
/// </para>
/// </summary>
public class MalSeriesExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MyAnimeList";

    /// <inheritdoc />
    public string Key => "MyAnimeList";

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series;
}

/// <summary>
/// The season-level counterpart of <see cref="MalSeriesExternalId"/>. Seasons carry
/// their own ID because one Jellyfin series usually spans several MAL entries.
/// </summary>
public class MalSeasonExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MyAnimeList";

    /// <inheritdoc />
    public string Key => "MyAnimeList";

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Season;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Season;
}

/// <summary>
/// Puts a <strong>MyAnimeList</strong> link on a series, season or episode page,
/// alongside Jellyfin's own IMDb and TMDB links.
/// <para>
/// The ID stored on the item is used when there is one. Otherwise the match MAL Sync
/// worked out itself is used — but only where every user agrees on it, because an item
/// page is shared and matches are per user. Where users disagree, no link is shown
/// rather than someone else's; the MAL Sync page always shows each user their own.
/// </para>
/// </summary>
public class MalExternalUrlProvider : IExternalUrlProvider
{
    private readonly MalSyncService _sync;

    /// <summary>Initialises the provider.</summary>
    /// <param name="sync">Used to look up matches this plugin resolved.</param>
    public MalExternalUrlProvider(MalSyncService sync)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public string Name => "MyAnimeList";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        var malId = ResolveId(item);
        if (!string.IsNullOrWhiteSpace(malId))
            yield return $"https://myanimelist.net/anime/{malId}";
    }

    private string? ResolveId(BaseItem item)
    {
        // An ID on the item itself is authoritative and shared by definition.
        if (item.TryGetProviderId("MyAnimeList", out var own) && !string.IsNullOrWhiteSpace(own))
            return own;

        // Otherwise fall back to what MAL Sync resolved, where it is unambiguous.
        return item switch
        {
            Series series => Lookup(series.Name, series.Id, 1),
            Season season => Lookup(season.SeriesName, season.SeriesId, season.IndexNumber ?? 1),
            Episode episode => Lookup(episode.SeriesName, episode.SeriesId, episode.ParentIndexNumber ?? 1),
            _ => null,
        };
    }

    private string? Lookup(string? seriesName, Guid seriesId, int seasonNumber)
    {
        try
        {
            return _sync.GetSharedMalId(seriesName, seriesId.ToString("N"), seasonNumber);
        }
        catch
        {
            // An item page must never fail because of a missing link.
            return null;
        }
    }
}
