using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// One photo a client is asking about: the hash of its bytes, and the month it would be
/// filed under.
/// </summary>
/// <remarks>
/// The month is what makes a database unnecessary. The hash is already in the stored
/// filename, so a key that carries its own month names the one directory the server has to
/// list; without it the only honest answer would come from walking the whole library.
/// </remarks>
public class HaveKey
{
    /// <summary>
    /// Gets or sets the SHA-256 of the photo's bytes, as 64 lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// Only the first sixteen characters are matched, because sixteen is what the filename
    /// carries; the full value is echoed back untouched and the upload re-verifies all 256
    /// bits against the bytes it receives. Nothing is assumed about the shape of this string
    /// before it is validated — Jellyfin's <c>JsonStringConverter</c> will bind a JSON number
    /// straight into a <c>string</c> property, so the declared type is not a check.
    /// </remarks>
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    /// <summary>
    /// Gets or sets the month the photo would be filed under, as <c>yyyy-MM</c>.
    /// </summary>
    /// <remarks>
    /// Absent, null or empty means the <c>undated/</c> bucket — the same bucket an upload
    /// lands in when its <c>capturedAt</c> is missing or unparseable, so the two endpoints
    /// agree about where a photo without a capture date lives. A present value must be
    /// exactly <c>yyyy-MM</c> with a real month number, because it goes on to name a
    /// directory and it is the only part of that path the caller chooses.
    /// </remarks>
    [JsonPropertyName("month")]
    public string? Month { get; set; }
}
