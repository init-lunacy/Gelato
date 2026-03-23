using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace Gelato.Services;

public enum DiscoveryProvider
{
    Unknown = 0,
    Stremio,
    Tmdb,
}

public enum DiscoveryItemKind
{
    Unknown = 0,
    Movie,
    Series,
    Episode,
    Person,
}

public sealed class DiscoveryPersonLink
{
    public required string Name { get; set; }
    public string? Role { get; set; }
    public int? TmdbId { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class DiscoveryCacheEntry
{
    public required DiscoveryProvider Provider { get; set; }
    public required DiscoveryItemKind Kind { get; set; }
    public required string Key { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? ImageUrl { get; set; }
    public int? Year { get; set; }
    public DateTime? PremiereDate { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<DiscoveryPersonLink> People { get; set; } = [];
    public object? Payload { get; set; }

    public Guid ToGuid()
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{Provider}:{Kind}:{Key}"));
        return new Guid(hash);
    }
}

public sealed class DiscoverySearchResult
{
    public required DiscoveryCacheEntry Entry { get; set; }
    public required BaseItem Item { get; set; }
}

public sealed class DiscoveryPersonCredits
{
    public required IReadOnlyList<DiscoverySearchResult> Items { get; set; }
    public int TotalCount { get; set; }
}
