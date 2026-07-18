using Edgewise.Contracts.Common;
using Edgewise.Infrastructure.Auth;

namespace Edgewise.Api;

/// <summary>
/// Maps exceptions to the standard error envelope {"error":{"code","message"}}.
/// Throw <see cref="ApiException"/> from endpoints/services for expected failures.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, "bad_request", "The request body is missing or malformed.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to write.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.");
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(new ErrorDetail(code, message)));
    }
}
