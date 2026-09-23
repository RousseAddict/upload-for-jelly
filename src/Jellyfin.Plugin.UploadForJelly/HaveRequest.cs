using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// The body of <c>POST /UploadForJelly/Have</c>.
/// </summary>
public class HaveRequest
{
    /// <summary>
    /// Gets or sets the library to ask about, in the dashless form <c>Targets</c> returns.
    /// </summary>
    /// <remarks>
    /// Resolved through the same function the preflight and the upload use, so this endpoint
    /// can never enumerate a directory the other two would refuse to describe.
    /// </remarks>
    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    /// <summary>
    /// Gets or sets the photos being asked about, at most five hundred of them.
    /// </summary>
    /// <remarks>
    /// Batching costs no authority: five hundred keys in one request are judged by the same
    /// code as five hundred requests, and it is what turns a 20 000-photo reconciliation into
    /// forty calls with no iCloud download behind any of them. The cap is enforced rather
    /// than trimmed — a client that sent more has a bug, and answering for a prefix would
    /// leave it believing the remainder was already stored.
    /// </remarks>
    [JsonPropertyName("keys")]
    public IReadOnlyList<HaveKey>? Keys { get; set; }
}
