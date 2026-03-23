using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Gelato.Config;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

public sealed class TmdbClient(HttpClient http, ILogger<TmdbClient> log)
{
    private const string BaseUrl = "https://api.themoviedb.org/3/";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";

    private string? GetApiKey()
    {
        var key = GelatoPlugin.Instance?.Configuration?.TmdbApiKey?.Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public bool IsEnabled(PluginConfiguration cfg)
    {
        return cfg.UseTmdbForDiscovery && !string.IsNullOrWhiteSpace(cfg.TmdbApiKey);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return default;

        var separator = path.Contains('?') ? '&' : '?';
        var url = $"{BaseUrl}{path}{separator}api_key={Uri.EscapeDataString(apiKey)}";

        try
        {
            return await http.GetFromJsonAsync<T>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "TMDB request failed for {Path}", path);
            return default;
        }
    }

    public async Task<TmdbPagedResponse<TmdbMovieSummary>?> SearchMoviesAsync(
        string query,
        int page,
        CancellationToken ct
    ) =>
        await GetAsync<TmdbPagedResponse<TmdbMovieSummary>>(
            $"search/movie?query={Uri.EscapeDataString(query)}&page={page}&include_adult=false",
            ct
        ).ConfigureAwait(false);

    public async Task<TmdbPagedResponse<TmdbSeriesSummary>?> SearchSeriesAsync(
        string query,
        int page,
        CancellationToken ct
    ) =>
        await GetAsync<TmdbPagedResponse<TmdbSeriesSummary>>(
            $"search/tv?query={Uri.EscapeDataString(query)}&page={page}&include_adult=false",
            ct
        ).ConfigureAwait(false);

    public async Task<TmdbPagedResponse<TmdbPersonSummary>?> SearchPeopleAsync(
        string query,
        int page,
        CancellationToken ct
    ) =>
        await GetAsync<TmdbPagedResponse<TmdbPersonSummary>>(
            $"search/person?query={Uri.EscapeDataString(query)}&page={page}&include_adult=false",
            ct
        ).ConfigureAwait(false);

    public async Task<TmdbMovieDetails?> GetMovieAsync(int tmdbId, CancellationToken ct) =>
        await GetAsync<TmdbMovieDetails>($"movie/{tmdbId}?append_to_response=credits", ct)
            .ConfigureAwait(false);

    public async Task<TmdbSeriesDetails?> GetSeriesAsync(int tmdbId, CancellationToken ct) =>
        await GetAsync<TmdbSeriesDetails>($"tv/{tmdbId}?append_to_response=credits,external_ids", ct)
            .ConfigureAwait(false);

    public async Task<TmdbSeasonDetails?> GetSeasonAsync(
        int tmdbId,
        int seasonNumber,
        CancellationToken ct
    ) =>
        await GetAsync<TmdbSeasonDetails>($"tv/{tmdbId}/season/{seasonNumber}", ct)
            .ConfigureAwait(false);

    public async Task<TmdbPersonDetails?> GetPersonAsync(int tmdbId, CancellationToken ct) =>
        await GetAsync<TmdbPersonDetails>($"person/{tmdbId}", ct).ConfigureAwait(false);

    public async Task<TmdbPersonCreditsResponse?> GetPersonCreditsAsync(
        int tmdbId,
        CancellationToken ct
    ) =>
        await GetAsync<TmdbPersonCreditsResponse>($"person/{tmdbId}/combined_credits", ct)
            .ConfigureAwait(false);

    public async Task<TmdbFindPersonResponse?> FindPersonByImdbAsync(string imdbId, CancellationToken ct) =>
        await GetAsync<TmdbFindPersonResponse>($"find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", ct)
            .ConfigureAwait(false);

    public string? ToImageUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : $"{ImageBaseUrl}{path}";
}

public sealed class TmdbPagedResponse<T>
{
    public int Page { get; set; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
    public List<T> Results { get; set; } = [];
}

public sealed class TmdbMovieSummary
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }
}

public sealed class TmdbSeriesSummary
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }
}

public sealed class TmdbPersonSummary
{
    public int Id { get; set; }
    public string? Name { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }

    [JsonPropertyName("known_for_department")]
    public string? KnownForDepartment { get; set; }

    [JsonPropertyName("known_for")]
    public List<TmdbKnownFor> KnownFor { get; set; } = [];
}

public sealed class TmdbKnownFor
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }
}

public sealed class TmdbMovieDetails
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    public TmdbCredits Credits { get; set; } = new();
}

public sealed class TmdbSeriesDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }

    public List<TmdbSeasonSummary> Seasons { get; set; } = [];
    public TmdbCredits Credits { get; set; } = new();

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds ExternalIds { get; set; } = new();
}

public sealed class TmdbSeasonSummary
{

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    public string? Name { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }
}

public sealed class TmdbSeasonDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    public List<TmdbEpisodeDetails> Episodes { get; set; } = [];
}

public sealed class TmdbEpisodeDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }

    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }
}

public sealed class TmdbCredits
{
    public List<TmdbCastMember> Cast { get; set; } = [];
}

public sealed class TmdbCastMember
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Character { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
}

public sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
}

public sealed class TmdbPersonDetails
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Biography { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }

    [JsonPropertyName("known_for_department")]
    public string? KnownForDepartment { get; set; }
}

public sealed class TmdbPersonCreditsResponse
{
    public List<TmdbCreditItem> Cast { get; set; } = [];
}

public sealed class TmdbCreditItem
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }
}

public sealed class TmdbFindPersonResponse
{
    [JsonPropertyName("person_results")]
    public List<TmdbPersonSummary> PersonResults { get; set; } = [];
}
