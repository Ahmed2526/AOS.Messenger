using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AOS.Messenger.Web.Filters;

/// <summary>
/// Converts antiforgery validation failures on API/JSON requests into a distinguishable
/// 400 application/problem+json payload with Cache-Control: no-store headers so client-side
/// handlers can differentiate CSRF token mismatches from domain/model validation errors.
/// </summary>
public sealed class AntiforgeryProblemDetailsFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    public const string ProblemType = "https://aos.messenger/errors/antiforgery";

    // Run before ASP.NET Core's [ApiController] ClientErrorResultFilter (Order = -2000)
    // so IAntiforgeryValidationFailedResult is preserved before generic ProblemDetails wrapping.
    public int Order => -3000;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult)
        {
            return;
        }

        var httpContext = context.HttpContext;
        var request = httpContext.Request;
        bool isApiOrJsonRequest = request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || request.Headers.Accept.Any(h => h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        if (!isApiOrJsonRequest)
        {
            return;
        }

        httpContext.Response.Headers.CacheControl = "no-store, no-cache";
        httpContext.Response.Headers.Pragma = "no-cache";

        var problemDetails = new ProblemDetails
        {
            Type = ProblemType,
            Title = "Antiforgery Validation Failed",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request verification token is missing or invalid.",
            Instance = request.Path.Value
        };
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

        var objectResult = new ObjectResult(problemDetails)
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
        objectResult.ContentTypes.Add("application/problem+json");
        context.Result = objectResult;
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
