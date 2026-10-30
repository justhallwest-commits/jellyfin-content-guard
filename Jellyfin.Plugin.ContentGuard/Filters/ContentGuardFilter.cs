using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentGuard.Configuration;
using Jellyfin.Plugin.ContentGuard.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentGuard.Filters;

/// <summary>
/// Global MVC action filter. For restricted users it:
///  1. returns 404 for any request that targets a blocked item by id (details, playback, streams, seasons/episodes);
///  2. pushes blocked ids into excludeItemIds for library browsing (correct paging);
///  3. strips blocked items out of every item list the API returns (home rows, Latest, Next Up,
///     Continue Watching, search, collections, similar, etc.).
/// </summary>
public class ContentGuardFilter : IAsyncActionFilter
{
    /// <summary>Claim type Jellyfin uses for the authenticated user id.</summary>
    private const string UserIdClaim = "Jellyfin-UserId";

    private static readonly string[] IdRouteKeys = { "itemId", "routeItemId", "seriesId", "videoId", "mediaSourceId", "routeMediaSourceId" };

    private readonly RuleEvaluator _evaluator;
    private readonly BlockedSetCache _blockedSets;
    private readonly ILogger<ContentGuardFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentGuardFilter"/> class.
    /// </summary>
    /// <param name="evaluator">Evaluator.</param>
    /// <param name="blockedSets">Blocked sets.</param>
    /// <param name="logger">Logger.</param>
    public ContentGuardFilter(RuleEvaluator evaluator, BlockedSetCache blockedSets, ILogger<ContentGuardFilter> logger)
    {
        _evaluator = evaluator;
        _blockedSets = blockedSets;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        IReadOnlyList<RuleProfile> profiles;
        Guid userId;
        try
        {
            userId = GetUserId(context);
            profiles = RuleEvaluator.GetProfilesForUser(userId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Content Guard: could not resolve user");
            await next().ConfigureAwait(false);
            return;
        }

        if (profiles.Count == 0 || IsOwnController(context))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // 1. Direct access by id.
        foreach (var key in IdRouteKeys)
        {
            if (context.RouteData.Values.TryGetValue(key, out var raw)
                && raw is not null
                && Guid.TryParse(raw.ToString(), out var id))
            {
                var decision = _evaluator.Evaluate(id, profiles);
                if (decision.Blocked)
                {
                    Log(userId, id, decision, context.HttpContext.Request.Path);
                    context.Result = new NotFoundResult();
                    return;
                }
            }
        }

        // 2. Push exclusions down to the database query where the endpoint supports it.
        if (RuleEvaluator.Config.UseQueryExclusion)
        {
            TryInjectExclusions(context, profiles);
        }

        var executed = await next().ConfigureAwait(false);

        // 3. Strip anything blocked from the response.
        if (executed.Exception is null && executed.Result is ObjectResult objectResult && objectResult.Value is not null)
        {
            try
            {
                var filtered = FilterValue(objectResult.Value, profiles, userId, context.HttpContext.Request.Path, out var notFound);
                if (notFound)
                {
                    executed.Result = new NotFoundResult();
                }
                else if (!ReferenceEquals(filtered, objectResult.Value))
                {
                    objectResult.Value = filtered;
                    objectResult.DeclaredType = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content Guard: failed to filter response for {Path}", context.HttpContext.Request.Path);
            }
        }
    }

    private static Guid GetUserId(ActionExecutingContext context)
    {
        var value = context.HttpContext.User?.Claims
            .FirstOrDefault(c => string.Equals(c.Type, UserIdClaim, StringComparison.OrdinalIgnoreCase))?.Value;
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    private static bool IsOwnController(ActionExecutingContext context)
    {
        return context.Controller?.GetType().Namespace?.StartsWith("Jellyfin.Plugin.ContentGuard", StringComparison.Ordinal) == true;
    }

    private void TryInjectExclusions(ActionExecutingContext context, IReadOnlyList<RuleProfile> profiles)
    {
        const string ParamName = "excludeItemIds";
        var param = context.ActionDescriptor.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, ParamName, StringComparison.OrdinalIgnoreCase));
        if (param is null || param.ParameterType != typeof(Guid[]))
        {
            return;
        }

        var blocked = _blockedSets.TryGet(profiles);
        if (blocked is null || blocked.Count == 0 || blocked.Count > BlockedSetCache.MaxInjectedIds)
        {
            return;
        }

        var existing = context.ActionArguments.TryGetValue(param.Name, out var current) && current is Guid[] arr
            ? arr
            : Array.Empty<Guid>();

        context.ActionArguments[param.Name] = existing.Length == 0
            ? blocked.ToArray()
            : existing.Concat(blocked).Distinct().ToArray();
    }

    private object FilterValue(object value, IReadOnlyList<RuleProfile> profiles, Guid userId, string path, out bool notFound)
    {
        notFound = false;
        switch (value)
        {
            case BaseItemDto single:
            {
                var decision = _evaluator.Evaluate(single.Id, profiles);
                if (decision.Blocked)
                {
                    Log(userId, single.Id, decision, path);
                    notFound = true;
                }

                return value;
            }

            case QueryResult<BaseItemDto> query:
            {
                var kept = FilterDtos(query.Items, profiles, userId, path, out var removed);
                if (removed == 0)
                {
                    return value;
                }

                query.Items = kept;
                query.TotalRecordCount = Math.Max(kept.Count, query.TotalRecordCount - removed);
                return query;
            }

            case IEnumerable<BaseItemDto> list:
            {
                var source = list as IReadOnlyList<BaseItemDto> ?? list.ToList();
                var kept = FilterDtos(source, profiles, userId, path, out var removed);
                return removed == 0 ? value : kept;
            }

            case SearchHintResult hints:
            {
                var kept = new List<SearchHint>(hints.SearchHints.Count);
                var removed = 0;
                foreach (var hint in hints.SearchHints)
                {
                    var decision = _evaluator.Evaluate(hint.Id, profiles);
                    if (decision.Blocked)
                    {
                        removed++;
                        Log(userId, hint.Id, decision, path);
                    }
                    else
                    {
                        kept.Add(hint);
                    }
                }

                return removed == 0
                    ? value
                    : new SearchHintResult(kept, Math.Max(kept.Count, hints.TotalRecordCount - removed));
            }

            default:
                return value;
        }
    }

    private List<BaseItemDto> FilterDtos(IReadOnlyList<BaseItemDto> items, IReadOnlyList<RuleProfile> profiles, Guid userId, string path, out int removed)
    {
        removed = 0;
        var kept = new List<BaseItemDto>(items.Count);
        foreach (var dto in items)
        {
            if (!RuleEvaluator.ScopedKinds.Contains(dto.Type))
            {
                kept.Add(dto);
                continue;
            }

            var decision = _evaluator.Evaluate(dto.Id, profiles);
            if (decision.Blocked)
            {
                removed++;
                Log(userId, dto.Id, decision, path);
            }
            else
            {
                kept.Add(dto);
            }
        }

        return kept;
    }

    private void Log(Guid userId, Guid itemId, BlockDecision decision, string path)
    {
        if (RuleEvaluator.Config.LogBlocks)
        {
            _logger.LogInformation(
                "Content Guard: hid {ItemId} from user {UserId} on {Path} - {Reason}",
                itemId,
                userId,
                path,
                decision.Reason);
        }
    }
}
