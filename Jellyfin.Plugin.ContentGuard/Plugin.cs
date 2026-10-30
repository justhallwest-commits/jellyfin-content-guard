using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ContentGuard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ContentGuard;

/// <summary>
/// Content Guard: reliable per-user content blocking by tags, genres and ratings.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin GUID.</summary>
    public static readonly Guid PluginGuid = Guid.Parse("c4c95473-00dd-462d-9561-f825460e5048");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Content Guard";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public override string Description =>
        "Per-user content blocking by tags, genres and ratings that actually inherits from series, seasons and libraries.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Content Guard",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
                EnableInMainMenu = true
            }
        };
    }
}
