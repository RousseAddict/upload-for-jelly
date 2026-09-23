using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// The plugin's HTTP surface.
/// </summary>
/// <remarks>
/// Routed because <c>ApplicationHost.GetApiPluginAssemblies</c> hands every plugin
/// assembly containing a <see cref="ControllerBase"/> to <c>AddJellyfinApi</c> as an MVC
/// application part. There is no automatic prefix, so the route below is the whole path.
/// </remarks>
[ApiController]
[Authorize]
[Route("UploadForJelly")]
[Produces(MediaTypeNames.Application.Json)]
public class UploadForJellyController : ControllerBase
{
    private readonly IAuthorizationContext _authorizationContext;
    private readonly UploadTargets _targets;
    private readonly UploadWriter _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadForJellyController"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to enumerate the libraries this caller can see.</param>
    /// <param name="libraryMonitor">Told about a new file once it is in place.</param>
    /// <param name="fileSystem">Supplies the host's own path comparison.</param>
    /// <param name="authorizationContext">Resolves the request's token to a user.</param>
    /// <param name="logger">Logger.</param>
    public UploadForJellyController(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IFileSystem fileSystem,
        IAuthorizationContext authorizationContext,
        ILogger<UploadForJellyController> logger)
    {
        _authorizationContext = authorizationContext;
        _targets = new UploadTargets(libraryManager, fileSystem, logger);
        _writer = new UploadWriter(libraryMonitor, logger);
    }

    /// <summary>
    /// Lists the libraries this caller may upload into, and whether the filesystem agrees.
    /// </summary>
    /// <response code="200">The targets, which may be an empty list.</response>
    /// <response code="401">No user behind the token.</response>
    /// <returns>The targets.</returns>
    /// <remarks>
    /// Existence of this route is also how a client detects that the plugin is installed:
    /// only a 404 means "not installed", so this action must never be the thing that 404s.
    /// An empty list is a valid, meaningful answer.
    /// </remarks>
    [HttpGet("Targets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TargetsResponse>> GetTargets()
    {
        var info = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);

        // An API key authenticates a caller without being a user, and every rule here -
        // library visibility, and later the owner of an upload - is defined per user.
        if (info.User is null)
        {
            return Unauthorized();
        }

        return new TargetsResponse(_targets.ForUser(info.User));
    }

    /// <summary>
    /// Every library that could accept uploads, probed, for the administration page.
    /// </summary>
    /// <response code="200">The candidates, opted in or not.</response>
    /// <response code="403">Not an administrator.</response>
    /// <returns>The candidate libraries.</returns>
    /// <remarks>
    /// Requires elevation, and is the only endpoint here that returns a host path. The
    /// configuration page is served publicly by the dashboard, so it must be able to hold
    /// no data of its own: everything it shows is fetched through this.
    /// </remarks>
    [HttpGet("Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<LibraryInfo>> GetLibraries()
    {
        return Ok(_targets.ForAdministrator());
    }

    /// <summary>
    /// Files one original into a library.
    /// </summary>
    /// <param name="targetId">The library id, as returned by <c>Targets</c>.</param>
    /// <param name="sha256">SHA-256 of the bytes, 64 lowercase hex characters.</param>
    /// <param name="capturedAt">Capture time. Chooses the folder and nothing else; absent means <c>undated/</c>.</param>
    /// <param name="deviceSlug">A short label for the sending device, rebuilt server-side.</param>
    /// <param name="fileName">The client's filename. Only its extension is used.</param>
    /// <response code="201">Stored.</response>
    /// <response code="200">Already present, byte for byte. Not an error.</response>
    /// <response code="400">A query parameter is malformed.</response>
    /// <response code="401">No user behind the token.</response>
    /// <response code="404">Unknown target, or one this caller may not upload into.</response>
    /// <response code="409">The filesystem refused the write.</response>
    /// <response code="413">Body above the plugin's ceiling.</response>
    /// <response code="422">Empty body, hash mismatch, or an extension off the allowlist.</response>
    /// <response code="507">The volume is full.</response>
    /// <returns>Where the original was filed.</returns>
    /// <remarks>
    /// Raw bytes in the body, not base64, which would cost a third of the wire and force
    /// the whole photo into memory. The size limit is raised here because nothing in
    /// Jellyfin raises Kestrel's 30 MB default, and a 4K video would otherwise be refused
    /// with a 413 that reads like a policy decision.
    /// </remarks>
    [HttpPost("Items")]
    [RequestSizeLimit(UploadWriter.MaxBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status507InsufficientStorage)]
    public async Task<ActionResult<UploadResult>> PostItem(
        [FromQuery] string? targetId,
        [FromQuery] string? sha256,
        [FromQuery] string? capturedAt,
        [FromQuery] string? deviceSlug,
        [FromQuery] string? fileName)
    {
        var info = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info.User is null)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(targetId, out var id) || !UploadWriter.IsSha256(sha256))
        {
            return BadRequest(new UploadError("BAD_REQUEST"));
        }

        var extension = UploadWriter.Extension(fileName);
        if (extension is null)
        {
            return UnprocessableEntity(new UploadError("UNSUPPORTED_TYPE"));
        }

        // Resolved through the same function the preflight uses, so an upload can never
        // reach a directory Targets would have refused to describe. A library the caller
        // cannot see and one that does not exist answer identically, on purpose.
        var root = _targets.WriteRootFor(info.User, id);
        if (root is null)
        {
            return NotFound(new UploadError("UNKNOWN_TARGET"));
        }

        var (status, result, code) = await _writer.WriteAsync(
            root,
            Request.Body,
            sha256,
            extension,
            UploadWriter.CapturedAt(capturedAt),
            UploadWriter.Slug(deviceSlug),
            HttpContext.RequestAborted).ConfigureAwait(false);

        return result is null
            ? StatusCode(status, new UploadError(code!))
            : StatusCode(status, result);
    }
}
