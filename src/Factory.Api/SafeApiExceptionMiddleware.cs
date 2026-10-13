using System.ClientModel;
using System.Text.RegularExpressions;

namespace Factory.Api;

internal static class SafeApiDiagnostics
{
    internal static string ProviderRequestId(ClientResultException exception)
    {
        string? id = null;
        exception.GetRawResponse()?.Headers.TryGetValue("x-request-id", out id);
        return id is not null && Regex.IsMatch(id, @"\A(?:req_)?[a-fA-F0-9-]{16,64}\z")
            ? id : "unavailable";
    }
}

// Covers binding, DI, middleware and response-writing failures outside agent handlers.
// Do not pass exceptions to a logger or the Development exception page.
public sealed class SafeApiExceptionMiddleware(RequestDelegate next, ILogger<SafeApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            var (status, code) = exception switch
            {
                OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "request_cancelled"),
                BadHttpRequestException badRequest => (badRequest.StatusCode, "invalid_http_request"),
                _ => (500, "unexpected_api_failure")
            };
            logger.LogWarning("API request {TraceId}: {FailureCode}; HTTP {StatusCode}; exception type {ExceptionType}",
                context.TraceIdentifier, code, status, exception.GetType().FullName);
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.Response.Clear();
            context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
            if (status == 499)
            {
                context.Response.StatusCode = status;
                return;
            }
            await Results.Problem(statusCode: status, title: code).ExecuteAsync(context);
        }
    }
}
