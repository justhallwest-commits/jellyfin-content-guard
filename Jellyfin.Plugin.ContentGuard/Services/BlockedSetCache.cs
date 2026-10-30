using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContentGuard.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentGuard.Services;

/// <summary>
/// Pre-computed set of blocked item ids per profile. Used to push exclusions down into the
/// database query for library browsing so paging and counts stay correct. The live response
/// filter is still the source of truth; this cache only makes browsing cleaner.
/// </summary>
public class BlockedSetCache
{
    /// <summary>Above this many ids we skip query-level exclusion (the response filter still applies).</summary>
#if JELLYFIN_10_10
    // 10.10 writes one SQL parameter per excluded id, so keep this modest.
    public const int MaxInjectedIds = 3000;
#else
    public const int MaxInjectedIds = 20000;
#endif

    private readonly ILibraryManager _libraryManager;
    private readonly RuleEvaluator _evaluator;
    private readonly ILogger<BlockedSetCache> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    private Dictionary<Guid, HashSet<Guid>> _sets = new();
    private volatile bool _dirty = true;
    private DateTime _lastBuilt = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockedSetCache"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="evaluator">Evaluator.</param>
    /// <param name="logger">Logger.</param>
    public BlockedSetCache(ILibraryManager libraryManager, RuleEvaluator evaluator, ILogger<BlockedSetCache> logger)
    {
        _libraryManager = libraryManager;
        _evaluator = evaluator;
        _logger = logger;
    }

    /// <summary>Gets when the sets were last rebuilt (UTC).</summary>
    public DateTime LastBuilt => _lastBuilt;

    /// <summary>Marks the cache stale; it rebuilds in the background on next use.</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>
    /// Gets the union of blocked ids for the given profiles, or null if not ready yet.
    /// Never blocks the calling request: a stale set triggers a background rebuild.
    /// </summary>
    /// <param name="profiles">Profiles.</param>
    /// <returns>Blocked ids or null.</returns>
    public IReadOnlyCollection<Guid>? TryGet(IReadOnlyList<RuleProfile> profiles)
    {
        if (_dirty)
        {
            _ = Task.Run(RebuildAsync);
        }

        var sets = _sets;
        if (profiles.Count == 1)
        {
            return sets.TryGetValue(profiles[0].Id, out var single) ? single : null;
        }

        var union = new HashSet<Guid>();
        foreach (var p in profiles)
        {
            if (!sets.TryGetValue(p.Id, out var s))
            {
                return null;
            }

            union.UnionWith(s);
        }

        return union;
    }

    /// <summary>Gets the blocked ids for one profile (rebuilding synchronously if needed).</summary>
    /// <param name="profile">Profile.</param>
    /// <returns>Ids.</returns>
    public async Task<IReadOnlyCollection<Guid>> GetForProfileAsync(RuleProfile profile)
    {
        if (_dirty || !_sets.ContainsKey(profile.Id))
        {
            await RebuildAsync().ConfigureAwait(false);
        }

        return _sets.TryGetValue(profile.Id, out var s) ? s : new HashSet<Guid>();
    }

    /// <summary>Rebuilds all sets.</summary>
    /// <returns>Task.</returns>
    public async Task RebuildAsync()
    {
        if (!await _buildLock.WaitAsync(0).ConfigureAwait(false))
        {
            return; // a rebuild is already running
        }

        try
        {
            _dirty = false;
            var sw = Stopwatch.StartNew();
            var profiles = RuleEvaluator.Config.Profiles.Where(p => p.Enabled).ToList();
            var result = profiles.ToDictionary(p => p.Id, _ => new HashSet<Guid>());

            if (profiles.Count > 0)
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = RuleEvaluator.ScopedKinds.ToArray()
                });

                foreach (var item in items)
                {
                    foreach (var profile in profiles)
                    {
                        if (_evaluator.Evaluate(item, new[] { profile }).Blocked)
                        {
                            result[profile.Id].Add(item.Id);
                        }
                    }
                }

                _logger.LogInformation(
                    "Content Guard: indexed {Count} items for {Profiles} profile(s) in {Ms} ms",
                    items.Count,
                    profiles.Count,
                    sw.ElapsedMilliseconds);
            }

            _sets = result;
            _lastBuilt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _dirty = true;
            _logger.LogError(ex, "Content Guard: failed to build blocked-item index");
        }
        finally
        {
            _buildLock.Release();
        }
    }
}
