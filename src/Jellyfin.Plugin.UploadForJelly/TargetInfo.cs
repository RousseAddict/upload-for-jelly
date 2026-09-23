using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// One library, as an upload destination, seen by one caller.
/// </summary>
public class TargetInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TargetInfo"/> class.
    /// </summary>
    /// <param name="id">The library id, in the same dashless form the rest of the API uses.</param>
    /// <param name="name">The library name, as the caller already sees it.</param>
    /// <param name="writable">Whether a file was just created and deleted in the target.</param>
    /// <param name="reason">Why not, when it is not.</param>
    /// <param name="freeBytes">Free space on the volume holding the target, when known.</param>
    public TargetInfo(string id, string name, bool writable, string? reason, long? freeBytes)
    {
        Id = id;
        Name = name;
        Writable = writable;
        Reason = reason;
        FreeBytes = freeBytes;
    }

    /// <summary>
    /// Gets the library id.
    /// </summary>
    /// <remarks>
    /// Dashless, because that is what Jellyfin's own <c>JsonGuidConverter</c> emits and
    /// therefore what a client stored when it read the id from <c>/UserViews</c>. A dashed
    /// id here would simply never match on the client and no error would be raised.
    /// </remarks>
    [JsonPropertyName("id")]
    public string Id { get; }

    /// <summary>
    /// Gets the library name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the server really can write there.
    /// </summary>
    /// <remarks>
    /// The answer comes from creating and deleting a file in the target on every call, not
    /// from mode bits: ACLs, mount flags, SELinux and quotas all lie to a stat().
    /// </remarks>
    [JsonPropertyName("writable")]
    public bool Writable { get; }

    /// <summary>
    /// Gets the refusal code: NOT_CONFIGURED, PATH_MISSING, READ_ONLY_MOUNT,
    /// PERMISSION_DENIED or WRITE_FAILED. Absent when <see cref="Writable"/> is true.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; }

    /// <summary>
    /// Gets the free space on the volume holding the target, when it can be determined.
    /// </summary>
    [JsonPropertyName("freeBytes")]
    public long? FreeBytes { get; }
}
