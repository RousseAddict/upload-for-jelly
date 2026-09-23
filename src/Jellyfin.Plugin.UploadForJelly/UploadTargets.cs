using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UploadForJelly;

internal sealed class UploadTargets
{
    // Dot-prefixed so that Jellyfin's own "**/.*" ignore pattern covers it if a scan ever
    // races the microsecond it exists. IgnorePatterns only globs the final segment, which
    // is fine here precisely because this is a file and not a directory.
    private const string ProbePrefix = ".upload-for-jelly-probe-";

    private static readonly CollectionType?[] _photoCollectionTypes =
    {
        CollectionType.photos,
        CollectionType.homevideos,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    internal UploadTargets(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    // Resolution lives here, alone, because the upload path must write into the very
    // directory the probe validated. Two implementations of "where does this library live"
    // would eventually disagree, and the disagreement would only show as a failed upload
    // after a successful preflight.
    internal string? WriteRoot(BaseItem folder)
    {
        foreach (var location in folder.PhysicalLocations)
        {
            // PhysicalLocations is [Path, ...AdditionalLocations] (ItemResolveArgs), so its
            // first entry is always the virtual folder itself: the directory inside
            // Jellyfin's config that holds the library's .mblink shortcuts. Only the
            // additional entries are media, and they are already resolved through the
            // shortcuts (FileData.GetFilteredFileSystemEntries calls AddAdditionalLocation
            // with the expanded target).
            //
            // Skipping it is not defensive tidiness. Without this the write root is the
            // config directory every single time, which is the one outcome the whole
            // Path-versus-PhysicalLocations distinction exists to prevent. Core does the
            // same filter in CollectionFolder.GetPhysicalFolders, and AreEqual is used
            // rather than a string compare so the host decides case sensitivity.
            if (_fileSystem.AreEqual(location, folder.Path))
            {
                continue;
            }

            if (Directory.Exists(location))
            {
                return location;
            }
        }

        return null;
    }

    internal IReadOnlyList<TargetInfo> ForUser(User user)
    {
        var allowed = AllowedIds();
        var targets = new List<TargetInfo>();

        // The same enumeration UserViewManager.GetUserViews walks, deliberately: for a
        // photo or home-video library it hands back the CollectionFolder itself, so the id
        // a client stored from /UserViews is the id matched here. It also applies the
        // user's own library permissions, which is the visibility half of the access rule.
        foreach (var folder in _libraryManager.GetUserRootFolder().GetChildren(user, true).OfType<Folder>())
        {
            if (folder is not ICollectionFolder collection)
            {
                continue;
            }

            var isAllowed = allowed.Contains(folder.Id);
            if (!isAllowed && Array.IndexOf(_photoCollectionTypes, collection.CollectionType) < 0)
            {
                continue;
            }

            targets.Add(Describe(folder, isAllowed));
        }

        return targets;
    }

    // The administrator's view, and the one place that ignores the allowlist on purpose:
    // the whole point of the page is to show what could be opted in. It probes every
    // candidate, including the ones already ticked - deciding whether to grant write
    // access to a mount is exactly the moment to learn that the mount went read-only.
    //
    // GetUserRootFolder().Children, not RootFolder.Children: the latter is the
    // AggregateFolder over the physical media roots, whose children are not collection
    // folders, so the ICollectionFolder filter below silently emptied the whole list.
    // Libraries hang off the UserRootFolder, which is what LibraryManager.GetVirtualFolders
    // enumerates for the dashboard's own library list. Children rather than
    // GetChildren(user, true) because an administrator configures libraries they may not
    // themselves be permitted to browse.
    internal IReadOnlyList<LibraryInfo> ForAdministrator()
    {
        var allowed = AllowedIds();
        var libraries = new List<LibraryInfo>();

        foreach (var folder in _libraryManager.GetUserRootFolder().Children.OfType<Folder>())
        {
            if (folder is not ICollectionFolder collection)
            {
                continue;
            }

            // Non-photo libraries appear only if something already opted them in, so a
            // setting made before this page existed cannot become invisible-but-active.
            if (Array.IndexOf(_photoCollectionTypes, collection.CollectionType) < 0
                && !allowed.Contains(folder.Id))
            {
                continue;
            }

            var id = folder.Id.ToString("N", CultureInfo.InvariantCulture);
            var root = WriteRoot(folder);
            if (root is null)
            {
                libraries.Add(new LibraryInfo(id, folder.Name, null, false, "PATH_MISSING"));
                continue;
            }

            var refusal = Probe(root);
            libraries.Add(new LibraryInfo(id, folder.Name, root, refusal is null, refusal));
        }

        return libraries;
    }

    // Both halves of the access rule, in the order that leaks the least: the enumeration
    // is the user's own, so a library the caller cannot see is indistinguishable from one
    // that does not exist, and the allowlist is checked on top of that. An upload resolves
    // its destination through here and nowhere else, so it can never reach a directory the
    // preflight would have refused to describe.
    internal string? WriteRootFor(User user, Guid targetId)
    {
        if (!AllowedIds().Contains(targetId))
        {
            return null;
        }

        foreach (var folder in _libraryManager.GetUserRootFolder().GetChildren(user, true).OfType<Folder>())
        {
            if (folder.Id.Equals(targetId) && folder is ICollectionFolder)
            {
                return WriteRoot(folder);
            }
        }

        return null;
    }

    private static HashSet<Guid> AllowedIds()
    {
        var ids = new HashSet<Guid>();
        var configured = Plugin.Instance?.Configuration.AllowedLibraryIds;
        if (configured is null)
        {
            return ids;
        }

        foreach (var entry in configured)
        {
            // Accepts both forms: an administrator pasting an id out of the dashboard URL
            // gets dashes, one pasting it out of an API response does not.
            if (Guid.TryParse(entry, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private TargetInfo Describe(Folder folder, bool isAllowed)
    {
        var id = folder.Id.ToString("N", CultureInfo.InvariantCulture);

        if (!isAllowed)
        {
            return new TargetInfo(id, folder.Name, false, "NOT_CONFIGURED", null);
        }

        var root = WriteRoot(folder);
        if (root is null)
        {
            return new TargetInfo(id, folder.Name, false, "PATH_MISSING", null);
        }

        var refusal = Probe(root);
        return refusal is null
            ? new TargetInfo(id, folder.Name, true, null, Volume.FreeBytes(root))
            : new TargetInfo(id, folder.Name, false, refusal, null);
    }

    private string? Probe(string root)
    {
        var path = Path.Combine(root, ProbePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            // Create and delete, not merely create: a directory can be writable and still
            // refuse a delete under a sticky bit, and the upload path deletes its own
            // staging file on every failure.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Remove(path);

            if (Volume.IsReadOnlyMount(root))
            {
                return "READ_ONLY_MOUNT";
            }

            if (ex is UnauthorizedAccessException)
            {
                return "PERMISSION_DENIED";
            }

            _logger.LogWarning(ex, "Upload write probe failed in {Root}", root);
            return "WRITE_FAILED";
        }
    }

    private void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A probe file left behind is hidden and harmless; failing the whole preflight
            // over the cleanup of a failure would report the wrong problem.
            _logger.LogDebug(ex, "Could not remove the write probe {Path}", path);
        }
    }
}
