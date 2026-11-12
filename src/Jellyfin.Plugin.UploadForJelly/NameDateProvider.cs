using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// Gives a photo this plugin filed the date its name carries, when nothing inside the file
/// supplied one.
/// </summary>
/// <remarks>
/// <para>
/// A JPEG saved from WhatsApp is a JFIF header and image data, nothing else: no EXIF, no XMP,
/// no date. Jellyfin then leaves <c>PremiereDate</c> null, and a client that falls back on
/// <c>DateCreated</c> files the photo under the day it was uploaded. The phone did know a
/// date — the upload's <c>capturedAt</c>, which leads the name this plugin wrote — and this
/// provider is the only place it reaches the library. The file itself is never modified.
/// </para>
/// <para>
/// It runs after every provider that reads the file (<see cref="Order"/>) and acts only on a
/// null <c>PremiereDate</c>, so a date inside the file always wins. <c>PhotoProvider</c>
/// sets a date only when it finds one and never clears it, so nothing upstream can undo
/// this one either. See <c>docs/02-jellyfin-internals.md</c> §17.
/// </para>
/// <para>
/// The date is the phone's <c>PHAsset.creationDate</c>. For a camera photo that is the shot,
/// and the EXIF date wins anyway; for a photo another app saved, it is the moment that app
/// saved it — for WhatsApp roughly when the photo was received, not when it was taken.
/// </para>
/// </remarks>
public class NameDateProvider : ICustomMetadataProvider<Photo>, IForcedProvider, IHasItemChangeMonitor, IHasOrder
{
    private readonly ILogger<NameDateProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NameDateProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public NameDateProvider(ILogger<NameDateProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the name the dashboard shows against a refresh.
    /// </summary>
    public string Name => "Upload for Jelly: date from file name";

    /// <summary>
    /// Gets the position among custom providers: after every one that reads the file.
    /// </summary>
    /// <remarks>
    /// <c>PhotoProvider</c> and <c>heic-for-jelly</c>'s provider declare no order and sort at
    /// the default of 50. Running last is what turns "fill the date only if nobody found one"
    /// into a rule rather than a race.
    /// </remarks>
    public int Order => 100;

    /// <summary>
    /// Reports whether the file changed since the last refresh.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="directoryService">The per-scan directory cache.</param>
    /// <returns><c>true</c> when the file is newer than what was recorded.</returns>
    /// <remarks>
    /// The same test as <c>PhotoProvider.HasChanged</c>, so this provider re-runs exactly when
    /// the one that reads the EXIF date does.
    /// </remarks>
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        if (item.IsFileProtocol)
        {
            var file = directoryService.GetFile(item.Path);
            return file is not null && item.HasChanged(file.LastWriteTimeUtc);
        }

        return false;
    }

    /// <summary>
    /// Sets the date from the file name when the item has none.
    /// </summary>
    /// <param name="item">The photo.</param>
    /// <param name="options">The refresh options, unused.</param>
    /// <param name="cancellationToken">The cancellation token, unused: there is no I/O, only a
    /// parse of the path the item already holds.</param>
    /// <returns><see cref="ItemUpdateType.MetadataImport"/> when a date was set, otherwise
    /// <see cref="ItemUpdateType.None"/>.</returns>
    /// <remarks>
    /// The three fields are the ones <c>PhotoProvider</c> sets from EXIF, set the same way:
    /// <c>PremiereDate</c> keeps the wall clock unconverted, <c>DateCreated</c> takes the
    /// server's conversion to UTC. Any other treatment would place this photo hours away
    /// from a camera photo taken the same afternoon.
    /// </remarks>
    public Task<ItemUpdateType> FetchAsync(Photo item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (item.PremiereDate is not null || string.IsNullOrEmpty(item.Path))
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        if (UploadWriter.NameDate(item.Path) is not { } date)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        item.DateCreated = date.ToUniversalTime();
        item.PremiereDate = date;
        item.ProductionYear = date.Year;

        _logger.LogDebug("No date inside {Path}; using the one its name carries, {Date}.", item.Path, date);
        return Task.FromResult(ItemUpdateType.MetadataImport);
    }
}
