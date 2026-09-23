using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// Entry point: gives Jellyfin the plugin's identity and owns its configuration.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths, used to locate the configuration file.</param>
    /// <param name="xmlSerializer">Serializer for that configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded instance, so that a controller can read the configuration without
    /// taking a dependency the plugin system does not inject.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Upload for Jelly";

    /// <summary>
    /// Gets the plugin id.
    /// </summary>
    /// <remarks>
    /// Never change this. Jellyfin keys the installed-plugin record, the configuration
    /// directory and the catalogue entry on it; a new value is a different plugin, and
    /// every existing install would keep the old one alongside.
    /// </remarks>
    public override Guid Id => Guid.Parse("f85a21b6-831f-41be-bc52-0831f45355ab");

    /// <inheritdoc />
    public override string Description =>
        "Accepts photo uploads from a client and files them into a photo library.";

    /// <summary>
    /// Registers the administration page that opts libraries into the allowlist.
    /// </summary>
    /// <remarks>
    /// The page exists because the allowlist is the one setting that must be changed on
    /// every install, and requiring a curl for it would make the plugin undeployable to
    /// anyone but its author.
    /// <para>
    /// The HTML itself is served by <c>DashboardController.GetDashboardConfigurationPage</c>,
    /// which carries no <c>[Authorize]</c> attribute — it is public. That is why the page
    /// is pure markup and script: every fact it displays, and every change it makes, goes
    /// through an endpoint gated by <c>Policies.RequiresElevation</c>.
    /// </para>
    /// </remarks>
    /// <returns>The single configuration page.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                // Matched case-insensitively against the ?name= query parameter. No space,
                // so the dashboard's link needs no encoding; the plugin's own Name is what
                // the dashboard displays, via ConfigurationPageInfo.DisplayName.
                Name = "UploadForJelly",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
        };
    }
}
