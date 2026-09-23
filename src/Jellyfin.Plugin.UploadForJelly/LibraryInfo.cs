using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// One candidate library, as an administrator sees it on the configuration page.
/// </summary>
/// <remarks>
/// Deliberately a different type from <see cref="TargetInfo"/> rather than a few extra
/// nullable fields on it. This one carries an absolute server path, which the upload
/// contract forbids returning to an ordinary caller; keeping the two shapes apart means a
/// future change to the caller-facing DTO cannot accidentally start leaking one.
/// </remarks>
public class LibraryInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryInfo"/> class.
    /// </summary>
    /// <param name="id">The library id, dashless, as stored in the allowlist.</param>
    /// <param name="name">The library name.</param>
    /// <param name="path">The folder uploads would be written to, or null if none exists.</param>
    /// <param name="writable">Whether a file was just created and deleted there.</param>
    /// <param name="reason">Why not, when it is not.</param>
    public LibraryInfo(string id, string name, string? path, bool writable, string? reason)
    {
        Id = id;
        Name = name;
        Path = path;
        Writable = writable;
        Reason = reason;
    }

    /// <summary>
    /// Gets the library id, dashless.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; }

    /// <summary>
    /// Gets the library name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; }

    /// <summary>
    /// Gets the absolute folder uploads would be written to.
    /// </summary>
    /// <remarks>
    /// Shown because two libraries can share a name and an administrator needs to know
    /// which mount is about to be opened for writing. This is the plugin's only endpoint
    /// that returns a host path, and it is the reason the endpoint requires elevation.
    /// </remarks>
    [JsonPropertyName("path")]
    public string? Path { get; }

    /// <summary>
    /// Gets a value indicating whether the server really can write there right now.
    /// </summary>
    /// <remarks>
    /// Probed on every page load, for every candidate, opted in or not. An administrator
    /// deciding whether to tick a box is exactly who needs to know before rather than
    /// after.
    /// </remarks>
    [JsonPropertyName("writable")]
    public bool Writable { get; }

    /// <summary>
    /// Gets the refusal code: PATH_MISSING, READ_ONLY_MOUNT, PERMISSION_DENIED or
    /// WRITE_FAILED. Absent when <see cref="Writable"/> is true.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; }
}
