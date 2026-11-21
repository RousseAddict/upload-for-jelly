using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.UploadForJelly;

/// <summary>
/// Answers a request body that could not be read before the action ran with the contract's
/// refusal, instead of letting Jellyfin turn it into a 500.
/// </summary>
/// <remarks>
/// <para>
/// <c>[RequestSizeLimit]</c> is enforced lazily, inside the body read (<c>docs/02</c> §16).
/// For <c>Items</c> that read is the action's own, which catches it. For an endpoint whose body
/// MVC deserializes — <c>Have</c> — the read happens during model binding, before any plugin
/// code runs, and the <see cref="BadHttpRequestException"/> it raises would travel on to
/// <c>Jellyfin.Api/Middleware/ExceptionMiddleware.cs:GetStatusCode</c>, which has no case for
/// it and answers 500. A 500 is the one status the client retries, so an oversize batch would
/// be sent again and again.
/// </para>
/// <para>
/// An exception filter sees exceptions from model binding, which is why this is one and not a
/// try/catch. It handles that one exception type and nothing else, mapped exactly as
/// <c>UploadWriter.WriteAsync</c> maps it, so both endpoints refuse a bad body the same way.
/// </para>
/// </remarks>
internal sealed class RequestBodyErrorsAttribute : ExceptionFilterAttribute
{
    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not BadHttpRequestException ex)
        {
            return;
        }

        context.Result = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? new ObjectResult(new UploadError("TOO_LARGE")) { StatusCode = StatusCodes.Status413PayloadTooLarge }
            : new ObjectResult(new UploadError("BAD_REQUEST")) { StatusCode = StatusCodes.Status400BadRequest };
        context.ExceptionHandled = true;
    }
}
