using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// The body of <c>GET /UploadForJelly/Targets</c>.
/// </summary>
public class TargetsResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TargetsResponse"/> class.
    /// </summary>
    /// <param name="targets">The libraries this caller may consider.</param>
    public TargetsResponse(IReadOnlyList<TargetInfo> targets)
    {
        Targets = targets;
    }

    /// <summary>
    /// Gets the libraries this caller may consider, writable or not.
    /// </summary>
    /// <remarks>
    /// Non-writable ones are listed rather than hidden: a client that got an empty array
    /// could only say "no library is open for upload", while a listed NOT_CONFIGURED entry
    /// lets it name the library the administrator has to opt in.
    /// </remarks>
    [JsonPropertyName("targets")]
    public IReadOnlyList<TargetInfo> Targets { get; }
}
