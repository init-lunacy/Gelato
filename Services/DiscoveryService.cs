using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

public sealed class DiscoveryService(
    TmdbClient tmdb,
    IDtoService dtoService,
    IMemoryCache cache,
    ILogger<DiscoveryService> log
)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    public bool UseTmdb(PluginConfiguration cfg)
    {
        var enabled = tmdb.IsEnabled(cfg);
        if (cfg.UseTmdbForDiscovery && !enabled)
            log.LogWarning("TMDB discovery enabled but API key missing; falling back to Stremio.");
        return enabled;
    }

    public void Save(DiscoveryCacheEntry entry)
    {
        cache.Set(Key(entry.ToGuid()), entry, CacheTtl);
    }

    public DiscoveryCacheEntry? Get(Guid id) =>
        cache.TryGetValue(Key(id), out DiscoveryCacheEntry? entry) ? entry : null;

    public string? GetImageUrl(Guid id) => Get(id)?.ImageUrl;

    public async Task<List<DiscoverySearchResult>> SearchTmdbAsync(
        string query,
        HashSet<BaseItemKind> requestedTypes,
        int start,
        int limit,
        CancellationToken ct
    )
    {
        var page = Math.Max(1, (start / Math.Max(limit, 1)) + 1);
        var results = new List<DiscoverySearchResult>();

        if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            var movies = await tmdb.SearchMoviesAsync(query, page, ct).ConfigureAwait(false);
            results.AddRange((movies?.Results ?? []).Select(ToMovieSearchResult));
        }

        if (requestedTypes.Contains(BaseItemKind.Series))
        {
            var shows = await tmdb.SearchSeriesAsync(query, page, ct).ConfigureAwait(false);
            results.AddRange((shows?.Results ?? []).Select(ToSeriesSearchResult));
        }

        if (requestedTypes.Contains(BaseItemKind.Person))
        {
            var people = await tmdb.SearchPeopleAsync(query, page, ct).ConfigureAwait(false);
            results.AddRange((people?.Results ?? []).Select(ToPersonSearchResult));
        }

        return results;
    }

    public BaseItemDto ToDto(DiscoverySearchResult result)
    {
        var dto = dtoService.GetBaseItemDto(
            result.Item,
            new DtoOptions { EnableImages = true, EnableUserData = false }
        );
        dto.Id = result.Entry.ToGuid();
        Save(result.Entry);
        return dto;
    }

    public BaseItemDto ToPersonDto(DiscoveryCacheEntry entry)
    {
        var person = CreatePerson(entry);
        var dto = dtoService.GetBaseItemDto(
            person,
            new DtoOptions { EnableImages = true, EnableUserData = false }
        );
        dto.Id = entry.ToGuid();
        Save(entry);
        return dto;
    }

    public async Task<DiscoveryCacheEntry?> GetTmdbMovieEntryAsync(int tmdbId, CancellationToken ct)
    {
        var details = await tmdb.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
        return details is null ? null : ToMovieEntry(details);
    }

    public async Task<DiscoveryCacheEntry?> GetTmdbSeriesEntryAsync(int tmdbId, CancellationToken ct)
    {
        var details = await tmdb.GetSeriesAsync(tmdbId, ct).ConfigureAwait(false);
        return details is null ? null : ToSeriesEntry(details);
    }

    public async Task<DiscoveryCacheEntry?> GetTmdbPersonEntryAsync(int tmdbId, CancellationToken ct)
    {
        var details = await tmdb.GetPersonAsync(tmdbId, ct).ConfigureAwait(false);
        if (details is null)
            return null;

        var entry = new DiscoveryCacheEntry
        {
            Provider = DiscoveryProvider.Tmdb,
            Kind = DiscoveryItemKind.Person,
            Key = details.Id.ToString(),
            Name = details.Name,
            Overview = details.Biography,
            ImageUrl = tmdb.ToImageUrl(details.ProfilePath),
            Payload = details,
        };
        entry.ProviderIds["Tmdb"] = details.Id.ToString();
        Save(entry);
        return entry;
    }

    public async Task<DiscoveryPersonCredits> GetPersonCreditsAsync(
        int tmdbPersonId,
        CancellationToken ct
    )
    {
        var credits = await tmdb.GetPersonCreditsAsync(tmdbPersonId, ct).ConfigureAwait(false);
        var items = (credits?.Cast ?? [])
            .Where(c => string.Equals(c.MediaType, "movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
            .Select(ToCreditSearchResult)
            .ToList();

        return new DiscoveryPersonCredits { Items = items, TotalCount = items.Count };
    }

    public async Task<int?> ResolveTmdbPersonIdAsync(Person person, CancellationToken ct)
    {
        if (person.ProviderIds.TryGetValue(nameof(MetadataProvider.Imdb), out var imdbId)
            && !string.IsNullOrWhiteSpace(imdbId))
        {
            var found = await tmdb.FindPersonByImdbAsync(imdbId, ct).ConfigureAwait(false);
            var personResult = found?.PersonResults.FirstOrDefault();
            if (personResult is not null)
                return personResult.Id;
        }

        var byName = await tmdb.SearchPeopleAsync(person.Name ?? string.Empty, 1, ct)
            .ConfigureAwait(false);
        return byName?.Results.FirstOrDefault(p =>
            string.Equals(p.Name, person.Name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public async Task<TmdbSeriesDetails?> GetTmdbSeriesDetailsAsync(int tmdbId, CancellationToken ct) =>
        await tmdb.GetSeriesAsync(tmdbId, ct).ConfigureAwait(false);

    public async Task<TmdbSeasonDetails?> GetTmdbSeasonDetailsAsync(
        int tmdbId,
        int seasonNumber,
        CancellationToken ct
    ) => await tmdb.GetSeasonAsync(tmdbId, seasonNumber, ct).ConfigureAwait(false);

    private DiscoverySearchResult ToMovieSearchResult(TmdbMovieSummary movie)
    {
        var entry = new DiscoveryCacheEntry
        {
            Provider = DiscoveryProvider.Tmdb,
            Kind = DiscoveryItemKind.Movie,
            Key = movie.Id.ToString(),
            Name = movie.Title,
            Overview = movie.Overview,
            ImageUrl = tmdb.ToImageUrl(movie.PosterPath),
            PremiereDate = ParseDate(movie.ReleaseDate),
            Year = ParseYear(movie.ReleaseDate),
            Payload = movie,
        };
        entry.ProviderIds["Tmdb"] = movie.Id.ToString();

        var item = new Movie
        {
            Name = movie.Title ?? "",
            Overview = movie.Overview,
            PremiereDate = entry.PremiereDate,
            ProductionYear = entry.Year,
            Path = $"gelato://tmdb/movie/{movie.Id}",
            IsVirtualItem = false,
        };
        item.SetProviderId(nameof(MetadataProvider.Tmdb), movie.Id.ToString());
        if (!string.IsNullOrWhiteSpace(entry.ImageUrl))
            item.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = entry.ImageUrl }];
        return new DiscoverySearchResult { Entry = entry, Item = item };
    }

    private DiscoverySearchResult ToSeriesSearchResult(TmdbSeriesSummary series)
    {
        var entry = new DiscoveryCacheEntry
        {
            Provider = DiscoveryProvider.Tmdb,
            Kind = DiscoveryItemKind.Series,
            Key = series.Id.ToString(),
            Name = series.Name,
            Overview = series.Overview,
            ImageUrl = tmdb.ToImageUrl(series.PosterPath),
            PremiereDate = ParseDate(series.FirstAirDate),
            Year = ParseYear(series.FirstAirDate),
            Payload = series,
        };
        entry.ProviderIds["Tmdb"] = series.Id.ToString();

        var item = new Series
        {
            Name = series.Name ?? "",
            Overview = series.Overview,
            PremiereDate = entry.PremiereDate,
            ProductionYear = entry.Year,
            Path = $"gelato://tmdb/tv/{series.Id}",
            IsVirtualItem = false,
        };
        item.SetProviderId(nameof(MetadataProvider.Tmdb), series.Id.ToString());
        if (!string.IsNullOrWhiteSpace(entry.ImageUrl))
            item.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = entry.ImageUrl }];
        return new DiscoverySearchResult { Entry = entry, Item = item };
    }

    private DiscoverySearchResult ToPersonSearchResult(TmdbPersonSummary person)
    {
        var entry = new DiscoveryCacheEntry
        {
            Provider = DiscoveryProvider.Tmdb,
            Kind = DiscoveryItemKind.Person,
            Key = person.Id.ToString(),
            Name = person.Name,
            Overview = person.KnownForDepartment,
            ImageUrl = tmdb.ToImageUrl(person.ProfilePath),
            Payload = person,
        };
        entry.ProviderIds["Tmdb"] = person.Id.ToString();
        return new DiscoverySearchResult { Entry = entry, Item = CreatePerson(entry) };
    }

    private DiscoverySearchResult ToCreditSearchResult(TmdbCreditItem credit)
    {
        return string.Equals(credit.MediaType, "movie", StringComparison.OrdinalIgnoreCase)
            ? ToMovieSearchResult(
                new TmdbMovieSummary
                {
                    Id = credit.Id,
                    Title = credit.Title,
                    Overview = credit.Overview,
                    PosterPath = credit.PosterPath,
                    ReleaseDate = credit.ReleaseDate,
                    MediaType = credit.MediaType,
                }
            )
            : ToSeriesSearchResult(
                new TmdbSeriesSummary
                {
                    Id = credit.Id,
                    Name = credit.Name,
                    Overview = credit.Overview,
                    PosterPath = credit.PosterPath,
                    FirstAirDate = credit.FirstAirDate,
                    MediaType = credit.MediaType,
                }
            );
    }

    private DiscoveryCacheEntry ToMovieEntry(TmdbMovieDetails details)
    {
        var entry = new DiscoveryCacheEntry
        {
            Provider = DiscoveryProvider.Tmdb,
            Kind = DiscoveryItemKind.Movie,
            Key = details.Id.ToString(),
            Name = details.Title,
            Overview = details.Overview,
            ImageUrl = tmdb.ToImageUrl(details.PosterPath),
            PremiereDate = ParseDate(details.ReleaseDate),
            Year = ParseYear(details.ReleaseDate),
            Payload = details,
            People = details.Credits.Cast.Take(20)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new DiscoveryPersonLink
                {
                    Name = c.Name!,
                    Role = c.Character,
                    TmdbId = c.Id,
                    ImageUrl = tmdb.ToImageUrl(c.ProfilePath),
                })
                .ToList(),
        };
        entry.ProviderIds["Tmdb"] = details.Id.ToString();
        if (!string.IsNullOrWhiteSpace(details.ImdbId))
            entry.ProviderIds["Imdb"] = details.ImdbId;
        Save(entry);
        return entry;
    }

    private DiscoveryCacheEntry ToSeriesEntry(TmdbSeriesDetails details)
    {
        var entry = new DiscoveryCacheEntry
        {
            Provider = DiscoveryProvider.Tmdb,
            Kind = DiscoveryItemKind.Series,
            Key = details.Id.ToString(),
            Name = details.Name,
            Overview = details.Overview,
            ImageUrl = tmdb.ToImageUrl(details.PosterPath),
            PremiereDate = ParseDate(details.FirstAirDate),
            Year = ParseYear(details.FirstAirDate),
            Payload = details,
            People = details.Credits.Cast.Take(20)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new DiscoveryPersonLink
                {
                    Name = c.Name!,
                    Role = c.Character,
                    TmdbId = c.Id,
                    ImageUrl = tmdb.ToImageUrl(c.ProfilePath),
                })
                .ToList(),
        };
        entry.ProviderIds["Tmdb"] = details.Id.ToString();
        if (!string.IsNullOrWhiteSpace(details.ExternalIds.ImdbId))
            entry.ProviderIds["Imdb"] = details.ExternalIds.ImdbId;
        Save(entry);
        return entry;
    }

    private static Person CreatePerson(DiscoveryCacheEntry entry)
    {
        var person = new Person
        {
            Name = entry.Name ?? "",
            Overview = entry.Overview,
            Path = $"gelato://tmdb/person/{entry.Key}",
            IsVirtualItem = false,
        };
        if (entry.ProviderIds.TryGetValue("Tmdb", out var tmdbId))
            person.SetProviderId(nameof(MetadataProvider.Tmdb), tmdbId);
        if (!string.IsNullOrWhiteSpace(entry.ImageUrl))
            person.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = entry.ImageUrl }];
        return person;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;

    private static int? ParseYear(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed.Year : null;

    private static string Key(Guid id) => $"discovery:{id}";
}
