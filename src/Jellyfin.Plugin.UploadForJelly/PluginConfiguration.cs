using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// Administrator-set policy for this plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the ids of the libraries clients are allowed to upload into.
    /// </summary>
    /// <remarks>
    /// Empty by default, and that default is the whole point: installing the plugin must
    /// not, by itself, open a write surface on the host. An administrator opts one library
    /// in, deliberately. A library absent from this list is reported as NOT_CONFIGURED,
    /// never as a permission failure, so the two causes stay distinguishable.
    /// </remarks>
    public string[] AllowedLibraryIds { get; set; } = Array.Empty<string>();
}
