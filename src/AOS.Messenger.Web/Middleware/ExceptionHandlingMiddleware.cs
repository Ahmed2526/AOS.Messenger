using System.Net;
using System.Text.Json;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace AOS.Messenger.Web.Middleware;

/// <summary>
/// Centralized exception handling middleware.
/// Translates domain and application exceptions into standardized HTTP responses
/// so that controllers and hubs remain thin and free of exception-mapping logic.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment? _environment;
    private readonly SecurityHeadersOptions _securityHeadersOptions;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment? environment = null,
        SecurityHeadersOptions? securityHeadersOptions = null)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
        _securityHeadersOptions = securityHeadersOptions ?? SecurityHeadersOptions.Default;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "Resource not found: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.NotFound, "Not Found", ex.Message);
        }
        catch (ForbiddenException ex)
        {
            _logger.LogWarning(ex, "Access forbidden: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.Forbidden, "Forbidden", ex.Message);
        }
        catch (ConflictException ex)
        {
            _logger.LogWarning(ex, "State conflict: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.Conflict, "Conflict", ex.Message);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Domain rule violation: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.BadRequest, "Bad Request", ex.Message);
        }
        catch (MediaStorageException ex)
        {
            _logger.LogError(ex, "Media storage operation failed.");
            string detail = (_environment?.IsDevelopment() ?? false)
                ? ex.Message
                : "Media storage is temporarily unavailable. Please try again.";
            await HandleExceptionAsync(context, HttpStatusCode.ServiceUnavailable, "Service Unavailable", detail);
        }
        catch (BadHttpRequestException ex)
        {
            _logger.LogWarning(ex, "Bad HTTP request ({StatusCode}).", ex.StatusCode);
            var statusCode = Enum.IsDefined(typeof(HttpStatusCode), ex.StatusCode)
                ? (HttpStatusCode)ex.StatusCode
                : HttpStatusCode.BadRequest;
            var (title, detail) = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? ("Payload Too Large", "Request payload exceeds the maximum allowed size.")
                : ("Bad Request", "The request is malformed or invalid.");
            await HandleExceptionAsync(context, statusCode, title, detail);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request was cancelled by the client.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled server exception occurred: {Message}", ex.Message);
            string detail = (_environment?.IsDevelopment() ?? false)
                ? ex.ToString()
                : "An unexpected error occurred.";
            await HandleExceptionAsync(context, HttpStatusCode.InternalServerError, "Internal Server Error", detail);
        }
    }

    private async Task HandleExceptionAsync(
        HttpContext context,
        HttpStatusCode statusCode,
        string title,
        string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = (int)statusCode;
        SecurityHeadersMiddleware.ApplySecurityHeaders(context.Response, _securityHeadersOptions);

        if (context.User.Identity?.IsAuthenticated == true ||
            context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store, no-cache";
            context.Response.Headers.Pragma = "no-cache";
        }

        if (statusCode == HttpStatusCode.InternalServerError && !(_environment?.IsDevelopment() ?? false))
        {
            detail = "An unexpected error occurred.";
        }

        bool isJsonExpected = context.Request.Headers.Accept.Any(h => h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(context.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/api");

        try
        {
            if (isJsonExpected)
            {
                context.Response.ContentType = "application/problem+json";
                var problemDetails = new ProblemDetails
                {
                    Status = (int)statusCode,
                    Title = title,
                    Detail = detail,
                    Instance = context.Request.Path
                };
                problemDetails.Extensions["traceId"] = context.TraceIdentifier;

                var json = JsonSerializer.Serialize(problemDetails);
                await context.Response.WriteAsync(json, context.RequestAborted);
            }
            else
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                var encodedTitle = WebUtility.HtmlEncode(title);
                var encodedDetail = WebUtility.HtmlEncode(detail);
                var html = $$"""
                    <!DOCTYPE html>
                    <html lang="en">
                    <head>
                        <meta charset="utf-8" />
                        <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                        <title>{{(int)statusCode}} - {{encodedTitle}}</title>
                        <link rel="stylesheet" href="/css/app.css" />
                    </head>
                    <body class="min-h-screen flex items-center justify-center p-4 bg-[#F8F6F3] dark:bg-[#302F2C] text-[#2B2725] dark:text-[#F0E9E2] font-sans">
                        <div class="bg-[#FFFFFF] dark:bg-[#3A3835] border border-[#E3DCD7] dark:border-[#514D48] rounded-xl p-8 max-w-md w-full shadow-lg text-center">
                            <div class="text-4xl font-bold text-[#B3929D] mb-2"><h1>{{(int)statusCode}}</h1></div>
                            <div class="text-lg font-semibold mb-2"><h2>{{encodedTitle}}</h2></div>
                            <p class="text-sm text-[#6E6560] dark:text-[#C9C0B9] mb-6">{{encodedDetail}}</p>
                            <a href="/" class="inline-block px-5 py-2 bg-[#B3929D] hover:bg-[#A07860] text-white rounded-md font-medium text-sm transition-colors">Return Home</a>
                        </div>
                    </body>
                    </html>
                    """;
                await context.Response.WriteAsync(html, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request was cancelled by the client while writing error response.");
        }
    }
}
