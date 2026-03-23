using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities; // User
using Gelato;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Gelato.Services;

namespace Gelato.Decorators;

public sealed class DtoServiceDecorator(IDtoService inner, Lazy<GelatoManager> manager)
    : IDtoService
{
    private readonly Lazy<GelatoManager> _manager = manager;

    public double? GetPrimaryImageAspectRatio(BaseItem item) =>
        inner.GetPrimaryImageAspectRatio(item);

    public BaseItemDto GetBaseItemDto(
        BaseItem item,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        var dto = inner.GetBaseItemDto(item, options, user, owner);
        Patch(dto, item, false, user);
        return dto;
    }

    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
        IReadOnlyList<BaseItem> items,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        // im going to hell for this
        var item = items.FirstOrDefault();

        if (item != null && item.GetBaseItemKind() == BaseItemKind.BoxSet)
        {
            options.EnableUserData = false;
        }

        var list = inner.GetBaseItemDtos(items, options, user, owner);
        foreach (var itemDto in list)
        {
            Patch(itemDto, item, true, user);
        }
        return list;
    }

    public BaseItemDto GetItemByNameDto(
        BaseItem item,
        DtoOptions options,
        List<BaseItem>? taggedItems,
        User? user = null
    )
    {
        var dto = inner.GetItemByNameDto(item, options, taggedItems, user);
        Patch(dto, item, false, user);
        return dto;
    }

    static bool IsGelato(BaseItemDto dto)
    {
        return dto.LocationType == LocationType.Remote
            && (
                dto.Type == BaseItemKind.Movie
                || dto.Type == BaseItemKind.Episode
                || dto.Type == BaseItemKind.Series
                || dto.Type == BaseItemKind.Season
            );
    }

    private void Patch(BaseItemDto dto, BaseItem? item, bool isList, User? user)
    {
        var manager = _manager.Value;
        if (item is not null && user is not null && IsGelato(dto) && manager.CanDelete(item, user))
        {
            dto.CanDelete = true;
        }
        if (IsGelato(dto))
        {
            dto.CanDownload = true;
            if (GelatoPlugin.Instance?.Configuration?.UseTmdbForDiscovery == true
                && item is not null
                && item.GelatoData<List<DiscoveryPersonLink>>("people") is { Count: > 0 } people)
            {
                dto.People = people
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.TmdbId is not null)
                    .Select(p => new BaseItemPerson
                    {
                        Name = p.Name,
                        Role = p.Role,
                        Id = new DiscoveryCacheEntry
                        {
                            Provider = DiscoveryProvider.Tmdb,
                            Kind = DiscoveryItemKind.Person,
                            Key = p.TmdbId!.Value.ToString(),
                            Name = p.Name,
                            ImageUrl = p.ImageUrl,
                            ProviderIds = new Dictionary<string, string> { { "Tmdb", p.TmdbId.Value.ToString() } },
                        }.ToGuid(),
                    })
                    .ToArray();

                foreach (var person in people.Where(p => p.TmdbId is not null))
                {
                    var entry = new DiscoveryCacheEntry
                    {
                        Provider = DiscoveryProvider.Tmdb,
                        Kind = DiscoveryItemKind.Person,
                        Key = person.TmdbId!.Value.ToString(),
                        Name = person.Name,
                        ImageUrl = person.ImageUrl,
                        ProviderIds = new Dictionary<string, string>
                        {
                            { "Tmdb", person.TmdbId.Value.ToString() },
                        },
                    };
                    _manager.Value.SaveDiscoveryEntry(entry.ToGuid(), entry);
                }
            }
            // mark if placeholder
            if (
                isList
                || dto.MediaSources?.Length != 1
                || dto.Path is null
                || !dto.MediaSources[0]
                    .Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)
            )
                return;
            dto.LocationType = LocationType.Virtual;
            dto.Path = null;
            dto.CanDownload = false;
        }
    }
}
