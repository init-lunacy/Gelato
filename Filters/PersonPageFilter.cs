using Gelato.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public sealed class PersonPageFilter(
    DiscoveryService discovery,
    GelatoManager manager,
    ILibraryManager libraryManager,
    ILogger<PersonPageFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var cfg = GelatoPlugin.Instance?.Configuration;
        if (cfg is null || !discovery.UseTmdb(cfg))
        {
            await next();
            return;
        }

        if (ctx.GetActionName() == "GetItem" && TryGetGuid(ctx, out var itemId))
        {
            var isLocalPerson = libraryManager.GetItemById(itemId) is Person;
            var personEntry = await ResolvePersonEntryAsync(itemId, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (personEntry is not null)
            {
                var dto = discovery.ToPersonDto(personEntry);
                if (isLocalPerson)
                {
                    dto.Id = itemId;
                    manager.SaveDiscoveryEntry(itemId, personEntry);
                }

                ctx.Result = new OkObjectResult(dto);
                return;
            }
        }

        if (ctx.HttpContext.IsApiListing() && TryGetPersonId(ctx.HttpContext.Request, out var personId))
        {
            var personEntry = await ResolvePersonEntryAsync(personId, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (personEntry is null)
            {
                await next();
                return;
            }

            var tmdbId = personEntry.ProviderIds.TryGetValue("Tmdb", out var tmdb)
                && int.TryParse(tmdb, out var parsed)
                ? parsed
                : (int?)null;

            if (!tmdbId.HasValue)
            {
                await next();
                return;
            }

            var credits = await discovery
                .GetPersonCreditsAsync(tmdbId.Value, ctx.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var items = credits.Items.Select(discovery.ToDto).ToArray();
            ctx.Result = new OkObjectResult(
                new QueryResult<BaseItemDto> { Items = items, TotalRecordCount = items.Length }
            );
            return;
        }

        await next();
    }

    private async Task<DiscoveryCacheEntry?> ResolvePersonEntryAsync(Guid id, CancellationToken ct)
    {
        var cached = discovery.Get(id);
        if (cached?.Kind == DiscoveryItemKind.Person)
            return cached;

        if (libraryManager.GetItemById(id) is Person localPerson)
        {
            var tmdbId = await discovery.ResolveTmdbPersonIdAsync(localPerson, ct).ConfigureAwait(false);
            if (tmdbId.HasValue)
            {
                return await discovery.GetTmdbPersonEntryAsync(tmdbId.Value, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static bool TryGetGuid(ActionExecutingContext ctx, out Guid id)
    {
        id = Guid.Empty;
        return ctx.TryGetRouteGuid(out id);
    }

    private static bool TryGetPersonId(HttpRequest request, out Guid id)
    {
        id = Guid.Empty;
        var value = request.Query["PersonIds"].FirstOrDefault()
            ?? request.Query["personIds"].FirstOrDefault()
            ?? request.Query["PersonId"].FirstOrDefault()
            ?? request.Query["personId"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var first = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return Guid.TryParse(first, out id);
    }
}
