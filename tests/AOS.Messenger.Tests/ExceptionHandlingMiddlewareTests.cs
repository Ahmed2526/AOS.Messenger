using System.Net;
using System.Text;
using System.Text.Json;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AOS.Messenger.Tests;

public class ExceptionHandlingMiddlewareTests
{
    private static DefaultHttpContext CreateHttpContext(string path = "/api/test", string accept = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (!string.IsNullOrEmpty(accept))
        {
            context.Request.Headers.Accept = accept;
        }
        context.TraceIdentifier = "trace-test-" + Guid.NewGuid().ToString("N");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task InvokeAsync_WhenNextSucceeds_DoesNotAlterResponse()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_DomainException_Returns400BadRequestWithTraceId()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new DomainException("Domain constraint violated."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal((int)HttpStatusCode.BadRequest, root.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", root.GetProperty("title").GetString());
        Assert.Equal("Domain constraint violated.", root.GetProperty("detail").GetString());
        Assert.Equal(context.Request.Path.Value, root.GetProperty("instance").GetString());
        Assert.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_NotFoundException_ScrubsInternalEntityKeyInNonDevelopment_AndIncludesInDevelopment()
    {
        var prodCtx = CreateHttpContext();
        var prodMiddleware = new ExceptionHandlingMiddleware(
            next: _ => throw new NotFoundException("Conversation", "c123"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await prodMiddleware.InvokeAsync(prodCtx);

        Assert.Equal((int)HttpStatusCode.NotFound, prodCtx.Response.StatusCode);
        Assert.Equal("application/problem+json", prodCtx.Response.ContentType);

        var prodJson = await ReadResponseBodyAsync(prodCtx);
        using (var doc = JsonDocument.Parse(prodJson))
        {
            var root = doc.RootElement;
            Assert.Equal((int)HttpStatusCode.NotFound, root.GetProperty("status").GetInt32());
            Assert.Equal("Not Found", root.GetProperty("title").GetString());
            Assert.Equal("The requested resource was not found.", root.GetProperty("detail").GetString());
            Assert.DoesNotContain("Entity 'Conversation'", prodJson);
            Assert.DoesNotContain("c123", prodJson);
            Assert.Equal(prodCtx.TraceIdentifier, root.GetProperty("traceId").GetString());
        }

        var devCtx = CreateHttpContext();
        var devMiddleware = new ExceptionHandlingMiddleware(
            next: _ => throw new NotFoundException("Sticker", "6d219f17-fee8-f111-9586-047c1647fc91"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: new TestHostEnvironment { EnvironmentName = "Development" });

        await devMiddleware.InvokeAsync(devCtx);

        Assert.Equal((int)HttpStatusCode.NotFound, devCtx.Response.StatusCode);
        var devJson = await ReadResponseBodyAsync(devCtx);
        Assert.Contains("Sticker", devJson);
        Assert.Contains("6d219f17-fee8-f111-9586-047c1647fc91", devJson);
    }

    [Fact]
    public async Task InvokeAsync_ForbiddenException_Returns403ForbiddenWithTraceId()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new ForbiddenException("Not a conversation participant."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Forbidden, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal((int)HttpStatusCode.Forbidden, root.GetProperty("status").GetInt32());
        Assert.Equal("Forbidden", root.GetProperty("title").GetString());
        Assert.Equal("Not a conversation participant.", root.GetProperty("detail").GetString());
        Assert.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_ConflictException_Returns409ConflictWithTraceId()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new ConflictException("Idempotency conflict."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Conflict, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal((int)HttpStatusCode.Conflict, root.GetProperty("status").GetInt32());
        Assert.Equal("Conflict", root.GetProperty("title").GetString());
        Assert.Equal("Idempotency conflict.", root.GetProperty("detail").GetString());
        Assert.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_GenericException_Returns500InternalServerErrorWithSanitizedMessage()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new InvalidOperationException("Sensitive internal database connection error"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal((int)HttpStatusCode.InternalServerError, root.GetProperty("status").GetInt32());
        Assert.Equal("Internal Server Error", root.GetProperty("title").GetString());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("detail").GetString());
        Assert.DoesNotContain("Sensitive internal", json);
        Assert.Equal(context.TraceIdentifier, root.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task InvokeAsync_WhenProductionEnvironment_ScrubsSensitiveExceptionDetails()
    {
        var context = CreateHttpContext();
        var prodEnv = new TestHostEnvironment { EnvironmentName = "Production" };
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new InvalidOperationException("SELECT * FROM AspNetUsers WHERE PasswordHash = 'secret'"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: prodEnv);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);
        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("An unexpected error occurred.", root.GetProperty("detail").GetString());
        Assert.DoesNotContain("AspNetUsers", json);
        Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public async Task InvokeAsync_WhenDevelopmentEnvironment_IncludesExceptionDetails()
    {
        var context = CreateHttpContext();
        var devEnv = new TestHostEnvironment { EnvironmentName = "Development" };
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new InvalidOperationException("Detailed debug information for developers"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: devEnv);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);
        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Contains("Detailed debug information for developers", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task InvokeAsync_ClientCancellation_WhenRequestAborted_HandledCleanlyWithout500()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = CreateHttpContext();
        context.RequestAborted = cts.Token;

        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new OperationCanceledException(context.RequestAborted),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        // Must not throw OperationCanceledException out of middleware
        var exception = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));
        Assert.Null(exception);

        // Status code must NOT be set to 500
        Assert.NotEqual((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Cancellation_WhenRequestNotAborted_TreatedAsGenericException500()
    {
        var context = CreateHttpContext();
        Assert.False(context.RequestAborted.IsCancellationRequested);

        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new OperationCanceledException("Server side timeout"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);
        var json = await ReadResponseBodyAsync(context);
        Assert.Contains("Internal Server Error", json);
    }

    [Fact]
    public async Task InvokeAsync_HtmlRequest_ReturnsHtmlFormattedError()
    {
        var context = CreateHttpContext(path: "/chat", accept: "text/html");
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new NotFoundException("Chat not found"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.NotFound, context.Response.StatusCode);
        Assert.StartsWith("text/html", context.Response.ContentType);

        var html = await ReadResponseBodyAsync(context);
        Assert.Contains("<h1>404</h1>", html);
        Assert.Contains("<h2>Not Found</h2>", html);
        Assert.Contains("Chat not found", html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/css/app.css", html);
    }

    [Fact]
    public async Task InvokeAsync_MediaStorageException_Returns503WithEnvironmentScrubbingAndConfiguredCsp()
    {
        var securityOptions = SecurityHeadersOptions.FromServiceUrl("https://acct999.r2.cloudflarestorage.com");

        var prodCtx = CreateHttpContext();
        var prodMiddleware = new ExceptionHandlingMiddleware(
            next: _ => throw new MediaStorageException("R2 secret key mismatch on bucket"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: new TestHostEnvironment { EnvironmentName = "Production" },
            securityHeadersOptions: securityOptions);

        await prodMiddleware.InvokeAsync(prodCtx);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, prodCtx.Response.StatusCode);
        Assert.Equal("application/problem+json", prodCtx.Response.ContentType);
        Assert.Equal(securityOptions.ContentSecurityPolicy, prodCtx.Response.Headers["Content-Security-Policy"]);

        var prodJson = await ReadResponseBodyAsync(prodCtx);
        using (var doc = JsonDocument.Parse(prodJson))
        {
            var root = doc.RootElement;
            Assert.Equal(503, root.GetProperty("status").GetInt32());
            Assert.Equal("Service Unavailable", root.GetProperty("title").GetString());
            Assert.Equal("Media storage is temporarily unavailable. Please try again.", root.GetProperty("detail").GetString());
            Assert.DoesNotContain("secret", prodJson, StringComparison.OrdinalIgnoreCase);
        }

        var devCtx = CreateHttpContext();
        var devMiddleware = new ExceptionHandlingMiddleware(
            next: _ => throw new MediaStorageException("R2 request timeout"),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: new TestHostEnvironment { EnvironmentName = "Development" },
            securityHeadersOptions: securityOptions);

        await devMiddleware.InvokeAsync(devCtx);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, devCtx.Response.StatusCode);
        var devJson = await ReadResponseBodyAsync(devCtx);
        Assert.Contains("R2 request timeout", devJson);
    }

    [Fact]
    public async Task InvokeAsync_MediaProcessingBusyException_Returns503ServiceUnavailable()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new MediaProcessingBusyException("Image processing capacity is currently busy. Please try again."),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: new TestHostEnvironment { EnvironmentName = "Production" });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(503, root.GetProperty("status").GetInt32());
        Assert.Equal("Service Unavailable", root.GetProperty("title").GetString());
        Assert.Equal("Image processing capacity is currently busy. Please try again.", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task InvokeAsync_BadHttpRequestException413_Returns413WithNoStoreAndScrubbedDetail()
    {
        var context = CreateHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new BadHttpRequestException(
                "Internal Kestrel detail: Request body too large.",
                StatusCodes.Status413PayloadTooLarge),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.RequestEntityTooLarge, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);

        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(413, root.GetProperty("status").GetInt32());
        Assert.Equal("Payload Too Large", root.GetProperty("title").GetString());
        Assert.Equal("Request payload exceeds the maximum allowed size.", root.GetProperty("detail").GetString());
        Assert.DoesNotContain("Kestrel", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_WhenBadHttpRequestExceptionHasNon413StatusCode_DerivesReasonPhraseTitle()
    {
        var context = CreateHttpContext();
        var env = new TestHostEnvironment { EnvironmentName = "Production" };
        var middleware = new ExceptionHandlingMiddleware(
            next: _ => throw new BadHttpRequestException("Unsupported media", StatusCodes.Status415UnsupportedMediaType),
            logger: NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment: env);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, context.Response.StatusCode);
        var json = await ReadResponseBodyAsync(context);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(415, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Unsupported Media Type", doc.RootElement.GetProperty("title").GetString());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "AOS.Messenger";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
