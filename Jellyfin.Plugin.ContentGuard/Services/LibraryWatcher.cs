using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.ContentGuard.Services;

/// <summary>
/// Clears caches whenever the library or the plugin configuration changes, so new tags
/// take effect immediately - no library rescan or metadata refresh required.
/// </summary>
public sealed class LibraryWatcher : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly RuleEvaluator _evaluator;
    private readonly BlockedSetCache _blockedSets;
    private Timer? _debounce;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="evaluator">Evaluator.</param>
    /// <param name="blockedSets">Blocked set cache.</param>
    public LibraryWatcher(ILibraryManager libraryManager, RuleEvaluator evaluator, BlockedSetCache blockedSets)
    {
        _libraryManager = libraryManager;
        _evaluator = evaluator;
        _blockedSets = blockedSets;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigChanged;
        }

        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        // Build the index once the server has settled.
        _ = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)
            .ContinueWith(_ => _blockedSets.RebuildAsync(), TaskScheduler.Default);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigChanged;
        }

        _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _debounce?.Dispose();

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        // Scans fire thousands of events; collapse them into one flush.
        _debounce?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    private void OnConfigChanged(object? sender, BasePluginConfiguration e)
    {
        Flush();
    }

    private void Flush()
    {
        _evaluator.ClearCache();
        _blockedSets.Invalidate();
    }
}
