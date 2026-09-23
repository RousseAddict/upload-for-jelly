using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// The body of every refusal from the upload endpoints.
/// </summary>
/// <remarks>
/// One shape for all of them, <c>Have</c> included, because the client's decision is driven
/// by the code and the status together: some refusals are terminal for a single photo,
/// others must stop the whole queue. A read-only mount that produced twenty thousand retries
/// would be the failure this taxonomy exists to prevent.
/// </remarks>
public class UploadError
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadError"/> class.
    /// </summary>
    /// <param name="code">The refusal code from the contract's error table.</param>
    public UploadError(string code)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the refusal code: BAD_REQUEST, UNKNOWN_TARGET, UNSUPPORTED_TYPE, EMPTY_BODY,
    /// HASH_MISMATCH, TARGET_NOT_WRITABLE, INSUFFICIENT_STORAGE or WRITE_FAILED.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; }
}
