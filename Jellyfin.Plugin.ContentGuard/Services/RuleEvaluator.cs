using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentGuard.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentGuard.Services;

/// <summary>
/// Result of evaluating one item for one user.
/// </summary>
/// <param name="Blocked">Whether the item is hidden.</param>
/// <param name="Reason">Human-readable reason (null when visible).</param>
public record BlockDecision(bool Blocked, string? Reason)
{
    /// <summary>Gets a visible decision.</summary>
    public static BlockDecision Visible { get; } = new(false, null);
}

/// <summary>
/// Evaluates items against rule profiles. Inheritance is computed live from the item tree,
/// so it does not depend on Jellyfin's stored "inherited tags" being up to date.
/// </summary>
public class RuleEvaluator
{
    /// <summary>Item kinds that rules apply to. Everything else (libraries, views, people, folders) is never hidden.</summary>
    public static readonly HashSet<BaseItemKind> ScopedKinds = new()
    {
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.MusicVideo,
        BaseItemKind.Trailer,
        BaseItemKind.BoxSet,
        BaseItemKind.Audio,
        BaseItemKind.MusicAlbum,
        BaseItemKind.AudioBook,
        BaseItemKind.Book,
        BaseItemKind.Photo,
        BaseItemKind.PhotoAlbum
    };

    private static readonly TimeSpan AttrTtl = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RuleEvaluator> _logger;
    private readonly ConcurrentDictionary<(Guid Item, bool Parents, bool Library), CachedAttrs> _attrCache = new();
    private readonly ConcurrentDictionary<string, Regex> _patternCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleEvaluator"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public RuleEvaluator(ILibraryManager libraryManager, ILogger<RuleEvaluator> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Gets the current configuration.</summary>
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Drops all cached item attributes.</summary>
    public void ClearCache()
    {
        _attrCache.Clear();
        _patternCache.Clear();
    }

    /// <summary>Gets the active profiles assigned to a user.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Profiles (empty when the user is unrestricted or the plugin is disabled).</returns>
    public static IReadOnlyList<RuleProfile> GetProfilesForUser(Guid userId)
    {
        var config = Config;
        if (!config.Enabled || userId == Guid.Empty)
        {
            return Array.Empty<RuleProfile>();
        }

        return config.Profiles
            .Where(p => p.Enabled && p.UserIds.Any(u => Guid.TryParse(u, out var g) && g == userId))
            .ToList();
    }

    /// <summary>
    /// Whether a playlist or collection should be hidden because every title in it is blocked
    /// (only when "Hide playlists and collections with nothing left" is on).
    /// </summary>
    /// <param name="containerId">Playlist or collection id.</param>
    /// <param name="profiles">Profiles.</param>
    /// <returns>True when it has titles and all of them are blocked.</returns>
    public bool IsEmptyAfterBlocking(Guid containerId, IReadOnlyList<RuleProfile> profiles)
    {
        if (!Config.HideEmptyCollections || profiles.Count == 0)
        {
            return false;
        }

        try
        {
            if (_libraryManager.GetItemById(containerId) is not Folder folder)
            {
                return false;
            }

            var children = folder.GetLinkedChildren();
            if (children.Count == 0)
            {
                return false;
            }

            return children.All(c => c is null || Evaluate(c, profiles).Blocked);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Content Guard: couldn't check contents of {Id}", containerId);
            return false;
        }
    }

    /// <summary>Evaluates an item id for a set of profiles.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="profiles">Profiles.</param>
    /// <returns>Decision.</returns>
    public BlockDecision Evaluate(Guid itemId, IReadOnlyList<RuleProfile> profiles)
    {
        if (profiles.Count == 0 || itemId == Guid.Empty)
        {
            return BlockDecision.Visible;
        }

        BaseItem? item;
        try
        {
            item = _libraryManager.GetItemById(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Content Guard: could not load item {Id}", itemId);
            return BlockDecision.Visible;
        }

        return item is null ? BlockDecision.Visible : Evaluate(item, profiles);
    }

    /// <summary>Evaluates an item for a set of profiles.</summary>
    /// <param name="item">Item.</param>
    /// <param name="profiles">Profiles.</param>
    /// <returns>Decision.</returns>
    public BlockDecision Evaluate(BaseItem item, IReadOnlyList<RuleProfile> profiles)
    {
        if (profiles.Count == 0 || !ScopedKinds.Contains(item.GetBaseItemKind()))
        {
            return BlockDecision.Visible;
        }

        foreach (var profile in profiles)
        {
            var decision = EvaluateProfile(item, profile);
            if (decision.Blocked)
            {
                return decision;
            }
        }

        return BlockDecision.Visible;
    }

    private BlockDecision EvaluateProfile(BaseItem item, RuleProfile profile)
    {
        var attrs = GetAttrs(item, profile.InheritFromParents, profile.InheritFromLibrary);

        var tag = FirstMatch(attrs.Tags, profile.BlockedTags);
        if (tag is not null)
        {
            return new BlockDecision(true, $"[{profile.Name}] tag \"{tag.Value.Value}\" matches blocked \"{tag.Value.Pattern}\"");
        }

        var genre = FirstMatch(attrs.Genres, profile.BlockedGenres);
        if (genre is not null)
        {
            return new BlockDecision(true, $"[{profile.Name}] genre \"{genre.Value.Value}\" matches blocked \"{genre.Value.Pattern}\"");
        }

        if (string.IsNullOrWhiteSpace(attrs.Rating))
        {
            if (profile.BlockUnrated)
            {
                return new BlockDecision(true, $"[{profile.Name}] item has no rating and unrated items are blocked");
            }
        }
        else
        {
            var rating = FirstMatch(new[] { attrs.Rating! }, profile.BlockedRatings);
            if (rating is not null)
            {
                return new BlockDecision(true, $"[{profile.Name}] rating \"{rating.Value.Value}\" matches blocked \"{rating.Value.Pattern}\"");
            }
        }

        var allowed = profile.AllowedTags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (allowed.Count > 0 && FirstMatch(attrs.Tags, allowed) is null)
        {
            return new BlockDecision(true, $"[{profile.Name}] allow-only mode: item has none of the allowed tags");
        }

        return BlockDecision.Visible;
    }

    private (string Value, string Pattern)? FirstMatch(IEnumerable<string> values, IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (Matches(value, pattern))
                {
                    return (value, pattern);
                }
            }
        }

        return null;
    }

    private bool Matches(string value, string pattern)
    {
        var v = value.Trim();
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(v, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = _patternCache.GetOrAdd(pattern, p => new Regex(
            "^" + Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        return regex.IsMatch(v);
    }

    private CachedAttrs GetAttrs(BaseItem item, bool parents, bool library)
    {
        var key = (item.Id, parents, library);
        if (_attrCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Created < AttrTtl)
        {
            return cached;
        }

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? rating = item.OfficialRating;

        AddAll(tags, item.Tags);
        AddAll(genres, item.Genres);

#if JELLYFIN_12
        // Jellyfin 12 groups versions of a title (1080p / 4K, cuts) as separate items. Treat them as one,
        // so a tag on any version applies to all of them.
        if (Config.ShareAcrossVersions && item is Video video)
        {
            try
            {
                foreach (var version in video.GetAllVersions())
                {
                    if (version.Id.Equals(item.Id))
                    {
                        continue;
                    }

                    AddAll(tags, version.Tags);
                    AddAll(genres, version.Genres);
                    if (string.IsNullOrWhiteSpace(rating) && !string.IsNullOrWhiteSpace(version.OfficialRating))
                    {
                        rating = version.OfficialRating;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content Guard: version lookup failed for {Id}", item.Id);
            }
        }
#endif

        if (parents)
        {
            var chain = new List<BaseItem>();
            try
            {
                chain.AddRange(item.GetParents());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content Guard: parent walk failed for {Id}", item.Id);
            }

            // Belt and braces: episodes/seasons can be orphaned from their folder chain
            // (virtual items, flat series folders), so also resolve the series directly.
            if (item is Episode ep)
            {
                if (ep.Season is not null)
                {
                    chain.Add(ep.Season);
                }

                if (ep.Series is not null)
                {
                    chain.Add(ep.Series);
                }
            }
            else if (item is Season season && season.Series is not null)
            {
                chain.Add(season.Series);
            }

            foreach (var parent in chain)
            {
                AddAll(tags, parent.Tags);
                AddAll(genres, parent.Genres);
                if (string.IsNullOrWhiteSpace(rating) && !string.IsNullOrWhiteSpace(parent.OfficialRating))
                {
                    rating = parent.OfficialRating;
                }
            }
        }

        if (library)
        {
            try
            {
                foreach (var folder in _libraryManager.GetCollectionFolders(item))
                {
                    AddAll(tags, folder.Tags);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content Guard: library lookup failed for {Id}", item.Id);
            }
        }

        var result = new CachedAttrs(tags, genres, rating, DateTime.UtcNow);
        _attrCache[key] = result;
        return result;
    }

    private static void AddAll(HashSet<string> set, string[]? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                set.Add(v.Trim());
            }
        }
    }

    private sealed record CachedAttrs(HashSet<string> Tags, HashSet<string> Genres, string? Rating, DateTime Created);
}
