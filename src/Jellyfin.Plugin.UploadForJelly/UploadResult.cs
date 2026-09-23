using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// What became of one uploaded original.
/// </summary>
/// <remarks>
/// There is deliberately no item id here. Filing the bytes and Jellyfin noticing them are
/// separate events: the notification is debounced by <c>LibraryMonitorDelay</c>, sixty
/// seconds by default, and restarts for every sibling arriving in the same folder. A
/// response carrying an id would be promising something the server cannot yet deliver.
/// </remarks>
public class UploadResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadResult"/> class.
    /// </summary>
    /// <param name="key">The verified SHA-256 of the stored bytes.</param>
    /// <param name="path">The path relative to the library root.</param>
    /// <param name="created">Whether this call is what put the file there.</param>
    public UploadResult(string key, string path, bool created)
    {
        Key = key;
        Path = path;
        Created = created;
    }

    /// <summary>
    /// Gets the SHA-256 of the stored bytes, as 64 lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// Verified against the bytes actually received, not echoed from the request.
    /// </remarks>
    [JsonPropertyName("key")]
    public string Key { get; }

    /// <summary>
    /// Gets the path relative to the library root, such as <c>2026/2026-09/….heic</c>.
    /// </summary>
    /// <remarks>
    /// Relative on purpose. An absolute path would teach a non-administrator the host's
    /// layout, and the app has no use for one.
    /// </remarks>
    [JsonPropertyName("path")]
    public string Path { get; }

    /// <summary>
    /// Gets a value indicating whether this request stored the file, as opposed to finding
    /// it already present. False is the idempotent replay, and is not an error.
    /// </summary>
    [JsonPropertyName("created")]
    public bool Created { get; }
}
