using System;
using System.Buffers;
using System.Collections.Generic;
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

    // The capture date that leads every dated name. Written by WriteAsync and read back by
    // NameDate: one constant, so the two directions cannot disagree about it.
    private const string NameDateFormat = "yyyyMMdd-HHmmss";

    private const int BufferSize = 81920;

    // A write failed and the volume has under a mebibyte left: that is an inference worth
    // making, because the alternative is reading errno out of an IOException, which .NET
    // does not promise to expose the same way twice. It holds only if the volume is measured
    // as the failed write left it, which is why WriteAsync diagnoses before it cleans up.
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

    // The whole no-database answer. Null means the batch was malformed and nothing was
    // looked at; otherwise the result is the hashes no stored filename matched, in the
    // order they were asked about.
    //
    // Validation runs over every key before a single directory is opened, and one bad key
    // refuses all of them. Answering for the well-formed part of a batch would leave the
    // client believing the rest was already stored, which is a photo silently dropped from
    // a backup - the one failure this endpoint exists to prevent.
    internal static IReadOnlyList<string>? Missing(string root, IReadOnlyList<HaveKey> keys)
    {
        var asked = new List<(string Sha, string? Month)>(keys.Count);

        foreach (var key in keys)
        {
            // A JSON null in the array is a malformed key like any other. The element type
            // says non-nullable; the deserializer does not keep that promise.
            if (key is null)
            {
                return null;
            }

            var sha = key.Sha;
            if (!IsSha256(sha) || !IsMonth(key.Month))
            {
                return null;
            }

            asked.Add((sha, string.IsNullOrEmpty(key.Month) ? null : key.Month));
        }

        var listed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var (sha, month) in asked)
        {
            // One listing per distinct month, however many keys name it. That is the entire
            // design: at 20 000 photos over ten years a month holds a couple of hundred
            // entries, so a 500-key batch costs a handful of directory reads and no index.
            var bucket = month ?? UndatedDirectory;
            if (!listed.TryGetValue(bucket, out var present))
            {
                present = ShortHashesIn(Bucket(root, month));
                listed[bucket] = present;
            }

            if (!present.Contains(sha[..16]))
            {
                missing.Add(sha);
            }
        }

        return missing;
    }

    // The capture date leading a name this plugin wrote, or null for any other name -
    // including the undated bucket's, which has no date to lead with. Read back with the
    // format the name was written with and the hash parse Have uses, for the reason Bucket
    // and ShortHash are shared: a second copy of the format would drift, and the drift would
    // leave every photo without a date of its own undated again, with nothing to notice.
    internal static DateTime? NameDate(string path)
    {
        if (ShortHash(path) is null)
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(path.AsSpan());

        // The date, "_", a slug of at least one character, "_", sixteen hex. Anything shorter
        // is an undated name whose slug merely happens to look like a date.
        if (stem.Length < NameDateFormat.Length + 2 + 17 || stem[NameDateFormat.Length] != '_')
        {
            return null;
        }

        // DateTimeStyles.None keeps Kind Unspecified: the wall clock the phone reported, with
        // the zone already dropped by CapturedAt, as an EXIF date is.
        return DateTime.TryParseExact(
            stem[..NameDateFormat.Length],
            NameDateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
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
        var directory = Bucket(root, capturedAt?.ToString("yyyy-MM", CultureInfo.InvariantCulture));

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
            : string.Create(CultureInfo.InvariantCulture, $"{capturedAt.Value.ToString(NameDateFormat, CultureInfo.InvariantCulture)}_{deviceSlug}_{shortHash}.{extension}");

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
        catch (BadHttpRequestException ex)
        {
            // Must precede the filesystem filter, because BadHttpRequestException derives
            // from IOException and MaxBytes is enforced lazily inside the body read rather
            // than up front. Diagnosed as a write failure it would answer 500 - retry with
            // backoff, forever, for an item that can never fit - or 507 on a nearly full
            // volume, which is terminal for the whole queue.
            Remove(part);
            return ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? (StatusCodes.Status413PayloadTooLarge, null, "TOO_LARGE")
                : (StatusCodes.Status400BadRequest, null, "BAD_REQUEST");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnosed before the cleanup, not after. The partial file is what filled the
            // volume; removed first, it hands its space back and Refuse measures a volume
            // that is no longer full - a 4 GiB video onto 2 GiB free then reads as an
            // unexplained, retryable WRITE_FAILED, retried with backoff and never reported.
            try
            {
                return Refuse(root, ex, destination);
            }
            finally
            {
                Remove(part);
            }
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

    // Absent, null or empty is the undated bucket - the same place an upload goes when its
    // capturedAt was missing or unparseable, so Have and Items agree about a photo with no
    // capture date. Anything present must be exactly yyyy-MM with a real month number:
    // this value becomes a directory name, and it is the only component of the path a
    // caller chooses.
    private static bool IsMonth(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value.Length != 7 || value[4] != '-')
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (i != 4 && (value[i] < '0' || value[i] > '9'))
            {
                return false;
            }
        }

        var month = ((value[5] - '0') * 10) + (value[6] - '0');
        return month >= 1 && month <= 12;
    }

    // The one mapping from a month to a directory, shared by the upload and by Have. Two
    // implementations of it would drift, and the drift would show as Have reporting every
    // photo as missing while every re-upload answered 200 - a backup that never converges
    // and never errors.
    //
    // month is either null, or seven characters IsMonth has already checked. Combined
    // rather than concatenated, out of the two validated halves: the year is the month's
    // own first four digits, so no part of this path is a string the caller supplied whole.
    private static string Bucket(string root, string? month)
    {
        return string.IsNullOrEmpty(month)
            ? Path.Combine(root, UndatedDirectory)
            : Path.Combine(root, month[..4], month);
    }

    // The sixteen hex characters between the last underscore and the extension, which is
    // where every name this plugin writes carries them, or null for a file this plugin did
    // not write. Parsed rather than matched with a glob so that one listing answers a whole
    // batch of keys; globbing per hash would reopen the same directory five hundred times.
    private static string? ShortHash(string path)
    {
        var name = Path.GetFileName(path.AsSpan());
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var stem = name[..dot];
        if (stem.Length < 17 || stem[^17] != '_')
        {
            return null;
        }

        var hash = stem[^16..];
        foreach (var c in hash)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return null;
            }
        }

        return hash.ToString();
    }

    private static HashSet<string> ShortHashesIn(string directory)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(directory))
        {
            return hashes;
        }

        try
        {
            // The filesystem is the index, and this is the read side of it: the hash is in
            // the name, so one enumeration answers for every key that named this month.
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var hash = ShortHash(path);
                if (hash is not null)
                {
                    hashes.Add(hash);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that will not list is not evidence of absence, but the other
            // answer available - claiming the photos are present - drops them from the
            // backup with no way to notice. Reporting them missing costs one upload, which
            // the write path then answers as an idempotent replay.
        }

        return hashes;
    }

    private static string? FindExisting(string directory, string shortHash)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            // Matched through the same parse Have uses rather than a "*_<hash>.*" glob, so
            // the two endpoints cannot come to disagree about what "already stored" means.
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(ShortHash(path), shortHash, StringComparison.Ordinal))
                {
                    return path;
                }
            }

            return null;
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
