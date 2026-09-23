using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UploadForJelly;

internal sealed class UploadWriter
{
    // Declared rather than removed. DisableRequestSizeLimit would let one authenticated
    // request stream without bound onto the host; a number can be quoted in a bug report.
    internal const long MaxBytes = 4L * 1024 * 1024 * 1024;

    // Hidden, and holding an empty .ignore. Two independent mechanisms, because
    // IgnorePatterns' "**/.*" only globs the final segment - which is why its own list
    // spells out "**/.actors" AND "**/.actors/**" - so a dot-prefixed directory alone
    // guarantees nothing about the files inside it.
    private const string StagingDirectory = ".upload-for-jelly";

    private const string UndatedDirectory = "undated";

    private const int BufferSize = 81920;

    // A write failed and the volume has under a mebibyte left: that is an inference worth
    // making, because the alternative is reading errno out of an IOException, which .NET
    // does not promise to expose the same way twice.
    private const long StorageExhausted = 1024 * 1024;

    // A write-path guard, not a formality. .nfo and .xml are files Jellyfin *interprets*
    // inside a media folder, so an unrestricted extension would let any authenticated user
    // plant metadata next to someone's photos.
    private static readonly string[] _allowedExtensions =
    {
        "heic", "heif", "jpg", "jpeg", "png", "gif", "webp", "avif", "tif", "tiff", "dng",
        "mov", "mp4", "m4v",
    };

    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger _logger;

    internal UploadWriter(ILibraryMonitor libraryMonitor, ILogger logger)
    {
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    internal static bool IsSha256([NotNullWhen(true)] string? value)
    {
        return value is not null
            && value.Length == 64
            && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    // Parsed here rather than by model binding, because an unparseable date is not a bad
    // request: the contract sends it to undated/, the same bucket the reader already uses
    // for photos without one. Binding it as a DateTime would answer 400 instead.
    //
    // The offset is read and then dropped on purpose. capturedAt is the wall clock at the
    // shutter, and EXIF - which is what Jellyfin will later read for the timeline - has no
    // timezone either. Converting to UTC would move a photo taken near midnight into the
    // neighbouring month's folder.
    internal static DateTime? CapturedAt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.DateTime
            : null;
    }

    // Only the extension is taken from the client's filename, and only from the allowlist.
    // Everything else about the name on disk is rebuilt from validated components, so no
    // separator, no traversal and no interpreted suffix can survive the trip.
    internal static string? Extension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return null;
        }

        var extension = fileName[(dot + 1)..].ToLowerInvariant();
        return Array.IndexOf(_allowedExtensions, extension) < 0 ? null : extension;
    }

    // Rebuilt character by character rather than validated and passed through: a slug is
    // the one component a caller chooses freely, so it is the one that must not be able to
    // contain a separator, a dot, or anything a shell or a scanner would read as syntax.
    internal static string Slug(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "device";
        }

        var builder = new StringBuilder(24);
        foreach (var c in value)
        {
            if (builder.Length == 24)
            {
                break;
            }

            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                builder.Append(c);
            }
            else if (c >= 'A' && c <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().TrimEnd('-');
        return slug.Length == 0 ? "device" : slug;
    }

    internal async Task<(int Status, UploadResult? Result, string? Code)> WriteAsync(
        string root,
        Stream body,
        string sha256,
        string extension,
        DateTime? capturedAt,
        string deviceSlug,
        CancellationToken cancellationToken)
    {
        // Sixteen hex characters, not eight: over 20 000 files a 32-bit tag collides with
        // probability ~4.7 %, a 64-bit one with ~1e-11.
        var shortHash = sha256[..16];
        var directory = capturedAt is null
            ? Path.Combine(root, UndatedDirectory)
            : Path.Combine(
                root,
                capturedAt.Value.ToString("yyyy", CultureInfo.InvariantCulture),
                capturedAt.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture));

        var existing = FindExisting(directory, shortHash);
        if (existing is not null)
        {
            // The idempotent replay, answered without touching the body. Have exists so
            // this stays rare, but a queue that retries after a lost response must not
            // produce a second copy under a second name.
            return (StatusCodes.Status200OK, new UploadResult(sha256, Relative(root, existing), false), null);
        }

        var name = capturedAt is null
            ? string.Create(CultureInfo.InvariantCulture, $"{deviceSlug}_{shortHash}.{extension}")
            : string.Create(CultureInfo.InvariantCulture, $"{capturedAt.Value:yyyyMMdd-HHmmss}_{deviceSlug}_{shortHash}.{extension}");

        var destination = Path.Combine(directory, name);
        var staging = Path.Combine(root, StagingDirectory);
        var part = Path.Combine(staging, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".part");

        try
        {
            EnsureStaging(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(root, ex, "staging directory");
        }

        try
        {
            var (received, digest) = await DrainAsync(body, part, cancellationToken).ConfigureAwait(false);

            if (received == 0)
            {
                // The one length the hash cannot flag on its own: a client that declares
                // the hash of nothing would otherwise pass verification and file an empty
                // photo.
                Remove(part);
                return (StatusCodes.Status422UnprocessableEntity, null, "EMPTY_BODY");
            }

            if (!string.Equals(digest, sha256, StringComparison.Ordinal))
            {
                Remove(part);
                return (StatusCodes.Status422UnprocessableEntity, null, "HASH_MISMATCH");
            }

            Directory.CreateDirectory(directory);

            try
            {
                // A rename, not a copy: the staging directory is inside the library root,
                // so the destination never exists in a half-written state.
                File.Move(part, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Two uploads of the same bytes crossed. Whoever lost is still correct.
                Remove(part);
                return (StatusCodes.Status200OK, new UploadResult(sha256, Relative(root, destination), false), null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Remove(part);
            return Refuse(root, ex, destination);
        }
        catch (OperationCanceledException)
        {
            // The client hung up mid-body. Nothing was renamed, so nothing is visible; all
            // that is owed is the staging file.
            Remove(part);
            throw;
        }

        // A bare report, never the Beginning/Complete pair: that pair holds the path in
        // LibraryMonitor's temp-ignore set for a hard-coded 45 seconds, which would
        // suppress the very file we want noticed.
        _libraryMonitor.ReportFileSystemChanged(destination);

        return (StatusCodes.Status201Created, new UploadResult(sha256, Relative(root, destination), true), null);
    }

    private static string? FindExisting(string directory, string shortHash)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            // The filesystem is the index: the hash is in the name, so one directory
            // listing answers "do you already have this".
            return Directory.EnumerateFiles(directory, "*_" + shortHash + ".*").FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Relative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void EnsureStaging(string staging)
    {
        Directory.CreateDirectory(staging);

        var ignore = Path.Combine(staging, ".ignore");
        if (!File.Exists(ignore))
        {
            // Empty on purpose: DotIgnoreIgnoreRule treats blank content as "ignore
            // everything beneath", on the file branch as much as the directory branch.
            File.Create(ignore).Dispose();
        }
    }

    private static async Task<(long Received, string Digest)> DrainAsync(Stream body, string part, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var received = 0L;

            var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    // Hashed in the same pass that writes it. The bytes are never held in
                    // memory and never read back: a second pass over a 4 GiB video would
                    // cost as much as the upload itself.
                    hasher.AppendData(buffer, 0, read);
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                }
            }

            return (received, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private (int Status, UploadResult? Result, string? Code) Refuse(string root, Exception ex, string context)
    {
        if (Volume.IsReadOnlyMount(root) || ex is UnauthorizedAccessException)
        {
            return (StatusCodes.Status409Conflict, null, "TARGET_NOT_WRITABLE");
        }

        var free = Volume.FreeBytes(root);
        if (free is not null && free < StorageExhausted)
        {
            return (StatusCodes.Status507InsufficientStorage, null, "INSUFFICIENT_STORAGE");
        }

        // Neither diagnosis fits, so claim neither. 500 is the one bucket the client is
        // allowed to retry, which is the right default for a write that may simply have
        // been unlucky.
        _logger.LogWarning(ex, "Upload write failed for {Context}", context);
        return (StatusCodes.Status500InternalServerError, null, "WRITE_FAILED");
    }

    private void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .part is hidden, ignored by the scanner twice over, and swept at
            // the next opportunity. Failing the request over it would report the wrong
            // problem.
            _logger.LogDebug(ex, "Could not remove the staging file {Path}", path);
        }
    }
}
