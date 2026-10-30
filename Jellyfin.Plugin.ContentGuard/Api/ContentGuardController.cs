using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentGuard.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ContentGuard.Api;

/// <summary>
/// Admin-only endpoints used by the Content Guard dashboard page.
/// </summary>
[ApiController]
[Route("ContentGuard")]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class ContentGuardController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly RuleEvaluator _evaluator;
    private readonly BlockedSetCache _blockedSets;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentGuardController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="evaluator">Evaluator.</param>
    /// <param name="blockedSets">Blocked sets.</param>
    public ContentGuardController(ILibraryManager libraryManager, RuleEvaluator evaluator, BlockedSetCache blockedSets)
    {
        _libraryManager = libraryManager;
        _evaluator = evaluator;
        _blockedSets = blockedSets;
    }

    /// <summary>Checks what a given user would see for items matching a search.</summary>
    /// <param name="userId">User to test as.</param>
    /// <param name="searchTerm">Title search.</param>
    /// <returns>Matches with their verdict.</returns>
    [HttpGet("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Test([FromQuery] Guid userId, [FromQuery] string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Ok(new { ProfileCount = 0, Results = Array.Empty<TestResult>() });
        }

        _evaluator.ClearCache();
        var profiles = RuleEvaluator.GetProfilesForUser(userId);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            SearchTerm = searchTerm.Trim(),
            Recursive = true,
            IncludeItemTypes = RuleEvaluator.ScopedKinds.ToArray(),
            Limit = 60
        });

        var results = items.Select(i =>
        {
            var d = _evaluator.Evaluate(i, profiles);
            return new TestResult(i.Id, Describe(i), i.GetBaseItemKind().ToString(), d.Blocked, d.Reason, i.Tags ?? Array.Empty<string>());
        }).ToList();

        return Ok(new { ProfileCount = profiles.Count, Results = results });
    }

    /// <summary>Lists what a profile hides.</summary>
    /// <param name="profileId">Profile id.</param>
    /// <returns>Summary.</returns>
    [HttpGet("Preview/{profileId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Preview([FromRoute] Guid profileId)
    {
        var profile = RuleEvaluator.Config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return NotFound("Save the profile first, then preview it.");
        }

        _evaluator.ClearCache();
        _blockedSets.Invalidate();
        var ids = await _blockedSets.GetForProfileAsync(profile).ConfigureAwait(false);
        var items = ids.Select(id => _libraryManager.GetItemById(id)).Where(i => i is not null).Select(i => i!).ToList();

        var counts = items
            .GroupBy(i => i.GetBaseItemKind().ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        // Show top-level things only: skip episodes/seasons whose series is already listed.
        var blockedSeries = items.OfType<Series>().Select(s => s.Id).ToHashSet();
        var top = items
            .Where(i => i switch
            {
                Episode e => !blockedSeries.Contains(e.SeriesId),
                Season s => !blockedSeries.Contains(s.SeriesId),
                _ => true
            })
            .OrderBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .Select(i => new { i.Id, Name = Describe(i), Type = i.GetBaseItemKind().ToString(), Reason = _evaluator.Evaluate(i, new[] { profile }).Reason })
            .ToList();

        return Ok(new { Total = items.Count, Counts = counts, Items = top });
    }

    /// <summary>Lists tags, genres and ratings present in the library (for autocomplete).</summary>
    /// <returns>Values.</returns>
    [HttpGet("Values")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Values()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = RuleEvaluator.ScopedKinds.ToArray()
        });

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ratings = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            foreach (var t in i.Tags ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(t))
                {
                    tags.Add(t.Trim());
                }
            }

            foreach (var g in i.Genres ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(g))
                {
                    genres.Add(g.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(i.OfficialRating))
            {
                ratings.Add(i.OfficialRating.Trim());
            }
        }

        foreach (var folder in _libraryManager.GetUserRootFolder().Children)
        {
            foreach (var t in folder.Tags ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(t))
                {
                    tags.Add(t.Trim());
                }
            }
        }

        return Ok(new { Tags = tags, Genres = genres, Ratings = ratings });
    }

    /// <summary>Forces the blocked-item index to rebuild.</summary>
    /// <returns>Status.</returns>
    [HttpPost("Rebuild")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Rebuild()
    {
        _evaluator.ClearCache();
        _blockedSets.Invalidate();
        await _blockedSets.RebuildAsync().ConfigureAwait(false);
        return Ok(new { _blockedSets.LastBuilt });
    }

    private static string Describe(BaseItem item)
    {
        return item switch
        {
            Episode e => $"{e.SeriesName} - S{(e.ParentIndexNumber ?? 0):00}E{(e.IndexNumber ?? 0):00} - {e.Name}",
            Season s => $"{s.SeriesName} - {s.Name}",
            _ => item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name ?? string.Empty
        };
    }

    /// <summary>One tester row.</summary>
    /// <param name="Id">Item id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Type">Item kind.</param>
    /// <param name="Blocked">Whether hidden.</param>
    /// <param name="Reason">Reason.</param>
    /// <param name="OwnTags">Tags set directly on the item.</param>
    public record TestResult(Guid Id, string Name, string Type, bool Blocked, string? Reason, string[] OwnTags);
}
