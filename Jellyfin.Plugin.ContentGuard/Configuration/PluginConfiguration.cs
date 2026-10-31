using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ContentGuard.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether enforcement is on at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether blocked items are also removed at the database
    /// query level for library browsing (keeps paging/counts accurate).
    /// </summary>
    public bool UseQueryExclusion { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether each block is written to the log (debug aid).</summary>
    public bool LogBlocks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all versions of a title (e.g. 1080p and 4K) are treated as one:
    /// if any version is blocked, every version is. Jellyfin 12 only.
    /// </summary>
    public bool ShareAcrossVersions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether "Because you watched…" and other recommendation rows drop blocked
    /// titles, and drop the whole row when it is based on a blocked title.
    /// </summary>
    public bool FilterRecommendations { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether playlists and collections whose every title is blocked are hidden.</summary>
    public bool HideEmptyCollections { get; set; }

    /// <summary>Gets or sets the rule profiles.</summary>
    public List<RuleProfile> Profiles { get; set; } = new();
}

/// <summary>
/// A named set of rules applied to one or more users.
/// </summary>
public class RuleProfile
{
    /// <summary>Gets or sets the profile id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = "New profile";

    /// <summary>Gets or sets a value indicating whether this profile is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the user ids (N format or D format) this profile applies to.</summary>
    public List<string> UserIds { get; set; } = new();

    /// <summary>Gets or sets tag patterns that hide an item. Case-insensitive, * wildcard allowed.</summary>
    public List<string> BlockedTags { get; set; } = new();

    /// <summary>Gets or sets genre patterns that hide an item.</summary>
    public List<string> BlockedGenres { get; set; } = new();

    /// <summary>Gets or sets official-rating patterns that hide an item (e.g. "R", "TV-MA", "NC-17").</summary>
    public List<string> BlockedRatings { get; set; } = new();

    /// <summary>
    /// Gets or sets allow-list tag patterns. When non-empty, ONLY items carrying one of these tags
    /// (directly or inherited) are visible.
    /// </summary>
    public List<string> AllowedTags { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether items with no official rating are hidden.</summary>
    public bool BlockUnrated { get; set; }

    /// <summary>Gets or sets a value indicating whether episodes/seasons inherit tags, genres and ratings from their series.</summary>
    public bool InheritFromParents { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether items inherit tags from the library they live in.</summary>
    public bool InheritFromLibrary { get; set; } = true;
}
