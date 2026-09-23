using System;
using System.IO;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// The two facts about a volume that neither Jellyfin nor .NET will answer directly:
/// whether it is mounted read-only, and how much room is left on it.
/// </summary>
/// <remarks>
/// Shared by the preflight and by the upload deliberately. The preflight's whole purpose is
/// to predict the upload's verdict, and it cannot do that if the two read the numbers
/// differently.
/// </remarks>
internal static class Volume
{
    private const string MountsFile = "/proc/self/mounts";

    /// <summary>
    /// Reports whether the path sits on a read-only mount, from the mount table.
    /// </summary>
    /// <remarks>
    /// Read rather than guessed from the exception: .NET's errno mapping decides whether
    /// EROFS surfaces as <see cref="UnauthorizedAccessException"/> or
    /// <see cref="IOException"/>, and that is not a contract to hang a user-visible message
    /// on. Where there is no /proc - a Mac, a BSD - this says nothing rather than something
    /// wrong, and the caller falls back to a code that claims less.
    /// </remarks>
    /// <param name="path">The path to locate in the mount table.</param>
    /// <returns>True only when a mount covering the path is known to be read-only.</returns>
    internal static bool IsReadOnlyMount(string path)
    {
        if (!File.Exists(MountsFile))
        {
            return false;
        }

        try
        {
            var longest = -1;
            var readOnly = false;

            foreach (var line in File.ReadLines(MountsFile))
            {
                var fields = line.Split(' ');
                if (fields.Length < 4)
                {
                    continue;
                }

                var mountPoint = fields[1].Replace("\\040", " ", StringComparison.Ordinal);
                if (mountPoint.Length <= longest || !IsUnder(path, mountPoint))
                {
                    continue;
                }

                longest = mountPoint.Length;
                readOnly = Array.IndexOf(fields[3].Split(','), "ro") >= 0;
            }

            return readOnly;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Free space on the volume holding the given directory, or null when it cannot be
    /// determined.
    /// </summary>
    /// <remarks>
    /// Longest mount-point match, not <c>new DriveInfo(root)</c>: the latter answers for
    /// "/", which on a server is never the volume the media sits on.
    /// </remarks>
    /// <param name="root">The directory whose volume is wanted.</param>
    /// <returns>Available bytes, or null.</returns>
    internal static long? FreeBytes(string root)
    {
        try
        {
            DriveInfo? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                var mount = drive.RootDirectory.FullName;
                if (!IsUnder(root, mount))
                {
                    continue;
                }

                if (best is null || mount.Length > best.RootDirectory.FullName.Length)
                {
                    best = drive;
                }
            }

            return best?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string mountPoint)
    {
        if (string.Equals(path, mountPoint, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = mountPoint.EndsWith(Path.DirectorySeparatorChar) ? mountPoint : mountPoint + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
