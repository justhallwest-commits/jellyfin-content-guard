using Jellyfin.Plugin.ContentGuard.Filters;
using Jellyfin.Plugin.ContentGuard.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ContentGuard;

/// <summary>
/// Registers Content Guard services and the global API filter.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RuleEvaluator>();
        serviceCollection.AddSingleton<BlockedSetCache>();
        serviceCollection.AddSingleton<ContentGuardFilter>();
        serviceCollection.AddHostedService<LibraryWatcher>();

        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<ContentGuardFilter>();
        });
    }
}
