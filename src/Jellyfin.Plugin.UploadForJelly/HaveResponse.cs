using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// The body of <c>POST /UploadForJelly/Have</c>: the subset worth uploading.
/// </summary>
public class HaveResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HaveResponse"/> class.
    /// </summary>
    /// <param name="missing">The hashes no stored filename matched.</param>
    public HaveResponse(IReadOnlyList<string> missing)
    {
        Missing = missing;
    }

    /// <summary>
    /// Gets the hashes the server did not find, in the order they were asked about.
    /// </summary>
    /// <remarks>
    /// The negative is returned rather than the positive because it is the shorter list on a
    /// second run and the actionable one on a first. It recognises only what this plugin
    /// wrote: a photo already in the folder under some other tool's name is reported missing
    /// and will be uploaded again. Deduplicating the whole folder is not what this claims to
    /// do.
    /// </remarks>
    [JsonPropertyName("missing")]
    public IReadOnlyList<string> Missing { get; }
}
