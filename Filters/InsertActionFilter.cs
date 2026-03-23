using Jellyfin.Database.Implementations.Entities;
using Gelato.Services;
using Gelato;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class InsertActionFilter(
    GelatoManager manager,
    DiscoveryService discovery,
    IUserManager userManager,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        var discoveryEntry = manager.GetDiscoveryEntry(guid);
        var stremioMeta = manager.GetStremioMeta(guid);
        if (discoveryEntry is null && stremioMeta is null)
        {
            await next();
            return;
        }

        if (discoveryEntry?.Kind == DiscoveryItemKind.Person)
        {
            await next();
            return;
        }

        // Get root folder
        var isSeries = discoveryEntry?.Kind == DiscoveryItemKind.Series
            || stremioMeta?.Type == StremioMediaType.Series;
        var root = isSeries
            ? manager.TryGetSeriesFolder(userId)
            : manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        if ((discoveryEntry is not null ? manager.IntoBaseItem(discoveryEntry) : manager.IntoBaseItem(stremioMeta!)) is { } item)
        {
            var existing = manager.FindExistingItem(item, user);
            if (existing is not null)
            {
                log.LogInformation(
                    "Media already exists; redirecting to canonical id {Id}",
                    existing.Id
                );
                ctx.ReplaceGuid(existing.Id);
                await next();
                return;
            }
        }

        // Fetch full metadata
        BaseItem? baseItem;
        if (discoveryEntry is not null)
        {
            baseItem = await InsertDiscoveryEntryAsync(guid, root, discoveryEntry, user, ctx.HttpContext.RequestAborted);
            if (baseItem is not null)
            {
                ctx.ReplaceGuid(baseItem.Id);
                manager.RemoveDiscoveryEntry(guid);
            }
        }
        else
        {
            var cfg = GelatoPlugin.Instance!.GetConfig(userId);
            var meta = await cfg.Stremio.GetMetaAsync(
                stremioMeta!.ImdbId ?? stremioMeta.Id,
                stremioMeta.Type
            );
            if (meta is null)
            {
                log.LogError(
                    "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                    stremioMeta.Id,
                    stremioMeta.Type
                );
                await next();
                return;
            }

            baseItem = await InsertMetaAsync(guid, root, meta, user);
            if (baseItem is not null)
            {
                ctx.ReplaceGuid(baseItem.Id);
                manager.RemoveStremioMeta(guid);
            }
        }

        await next();
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    true,
                    meta.Type is StremioMediaType.Series,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }

    private async Task<BaseItem?> InsertDiscoveryEntryAsync(
        Guid guid,
        Folder root,
        DiscoveryCacheEntry entry,
        User user,
        CancellationToken ct
    )
    {
        BaseItem? item = null;

        await _lock.RunQueuedAsync(
            guid,
            async token =>
            {
                switch (entry.Kind)
                {
                    case DiscoveryItemKind.Movie:
                    {
                        if (entry.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                            && int.TryParse(tmdbId, out var parsedMovieId)
                            && await discovery.GetTmdbMovieEntryAsync(parsedMovieId, token).ConfigureAwait(false) is { } movieEntry)
                        {
                            var baseItem = manager.IntoBaseItem(movieEntry);
                            if (baseItem is not null)
                            {
                                item = await InsertBaseItemAsync(root, baseItem, user, token).ConfigureAwait(false);
                                item?.SetGelatoData("people", movieEntry.People);
                            }
                        }
                        break;
                    }
                    case DiscoveryItemKind.Series:
                    {
                        if (entry.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                            && int.TryParse(tmdbId, out var parsedSeriesId)
                            && await discovery.GetTmdbSeriesDetailsAsync(parsedSeriesId, token).ConfigureAwait(false) is { } details)
                        {
                            item = await InsertTmdbSeriesAsync(root, details, user, token).ConfigureAwait(false);
                        }
                        break;
                    }
                }
            },
            ct
        ).ConfigureAwait(false);

        return item;
    }

    private async Task<BaseItem?> InsertBaseItemAsync(
        Folder root,
        BaseItem item,
        User user,
        CancellationToken ct
    )
    {
        var entry = new StremioMeta
        {
            Id = item.GetProviderId(nameof(MetadataProvider.Imdb))
                ?? $"tmdb:{item.GetProviderId(nameof(MetadataProvider.Tmdb))}",
            Type = item.GetBaseItemKind() == Jellyfin.Data.Enums.BaseItemKind.Movie
                ? StremioMediaType.Movie
                : StremioMediaType.Series,
            Name = item.Name,
            Description = item.Overview,
            Poster = item.ImageInfos?.FirstOrDefault()?.Path,
            ImdbId = item.GetProviderId(nameof(MetadataProvider.Imdb)),
            Released = item.PremiereDate,
            Year = item.ProductionYear,
        };

        var (createdItem, _) = await manager
            .InsertMeta(
                root,
                entry,
                user,
                false,
                true,
                item is Series,
                ct
            )
            .ConfigureAwait(false);
        return createdItem;
    }

    private async Task<BaseItem?> InsertTmdbSeriesAsync(
        Folder root,
        TmdbSeriesDetails details,
        User user,
        CancellationToken ct
    )
    {
        var videos = new List<StremioMeta>();
        foreach (var season in details.Seasons.Where(s => s.SeasonNumber >= 0))
        {
            var seasonDetails = await discovery
                .GetTmdbSeasonDetailsAsync(details.Id, season.SeasonNumber, ct)
                .ConfigureAwait(false);
            if (seasonDetails is null)
                continue;

            videos.AddRange(
                seasonDetails.Episodes.Select(ep => new StremioMeta
                {
                    Id = $"{details.ExternalIds.ImdbId ?? $"tmdb:{details.Id}"}:{ep.SeasonNumber}:{ep.EpisodeNumber}",
                    Type = StremioMediaType.Episode,
                    Name = ep.Name,
                    Description = ep.Overview,
                    Thumbnail = !string.IsNullOrWhiteSpace(ep.StillPath)
                        ? $"https://image.tmdb.org/t/p/original{ep.StillPath}"
                        : null,
                    Season = ep.SeasonNumber,
                    Episode = ep.EpisodeNumber,
                    Released = DateTime.TryParse(ep.AirDate, out var airDate) ? airDate : null,
                })
            );
        }

        var meta = new StremioMeta
        {
            Id = $"tmdb:{details.Id}",
            Type = StremioMediaType.Series,
            Name = details.Name,
            Description = details.Overview,
            Poster = !string.IsNullOrWhiteSpace(details.PosterPath)
                ? $"https://image.tmdb.org/t/p/original{details.PosterPath}"
                : null,
            ImdbId = details.ExternalIds.ImdbId,
            Released = DateTime.TryParse(details.FirstAirDate, out var firstAirDate)
                ? firstAirDate
                : null,
            Videos = videos,
            App_Extras = new StremioAppExtras
            {
                SeasonPosters = details.Seasons
                    .OrderBy(s => s.SeasonNumber)
                    .Select(s =>
                        !string.IsNullOrWhiteSpace(s.PosterPath)
                            ? $"https://image.tmdb.org/t/p/original{s.PosterPath}"
                            : null
                    )
                    .ToList(),
            },
        };

        var (createdItem, _) = await manager
            .InsertMeta(root, meta, user, false, true, true, ct)
            .ConfigureAwait(false);
        if (createdItem is not null)
            createdItem.SetGelatoData(
                "people",
                details.Credits.Cast.Take(20)
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new DiscoveryPersonLink
                    {
                        Name = c.Name!,
                        Role = c.Character,
                        TmdbId = c.Id,
                        ImageUrl = !string.IsNullOrWhiteSpace(c.ProfilePath)
                            ? $"https://image.tmdb.org/t/p/original{c.ProfilePath}"
                            : null,
                    })
                    .ToList()
            );
        return createdItem;
    }
}
