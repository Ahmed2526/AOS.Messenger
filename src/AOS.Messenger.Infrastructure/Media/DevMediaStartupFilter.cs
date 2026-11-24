using AOS.Messenger.Domain.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Mounts the Development-only local-disk R2 simulator endpoints (<c>/dev-media/upload</c> and <c>/dev-media/download</c>)
/// strictly when running in <see cref="Environments.Development"/> without Cloudflare R2 configured.
/// </summary>
internal sealed class DevMediaStartupFilter : IStartupFilter
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<R2StorageOptions> _r2Options;
    private readonly LocalMediaSigningService _signingService;
    private readonly LocalDiskMediaStorage _storage;
    private readonly TimeProvider _timeProvider;

    public DevMediaStartupFilter(
        IHostEnvironment environment,
        IOptions<R2StorageOptions> r2Options,
        LocalMediaSigningService signingService,
        LocalDiskMediaStorage storage,
        TimeProvider timeProvider)
    {
        _environment = environment;
        _r2Options = r2Options;
        _signingService = signingService;
        _storage = storage;
        _timeProvider = timeProvider;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (!_environment.IsDevelopment() || _r2Options.Value.IsConfigured)
        {
            return next;
        }

        return app =>
        {
            app.Map("/dev-media/upload", branch => branch.Run(HandleUploadAsync));
            app.Map("/dev-media/download", branch => branch.Run(HandleDownloadAsync));
            next(app);
        };
    }

    private async Task HandleUploadAsync(HttpContext context)
    {
        if (!HttpMethods.IsPut(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "PUT";
            return;
        }

        var token = context.Request.Query["token"].ToString();
        if (!_signingService.TryValidateToken(token, LocalMediaSigningService.PutAction, _timeProvider.GetUtcNow(), out var payload)
            || payload is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var declaredContentLength = context.Request.ContentLength;
        if (!declaredContentLength.HasValue)
        {
            context.Response.StatusCode = StatusCodes.Status411LengthRequired;
            return;
        }

        if (!payload.ContentLength.HasValue
            || declaredContentLength.Value <= 0
            || declaredContentLength.Value > MediaConstraints.MaxVideoBytes
            || declaredContentLength.Value != payload.ContentLength.Value)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
        {
            bodySizeFeature.MaxRequestBodySize = MediaConstraints.MaxVideoBytes;
        }

        var normalizedContentType = NormalizeMediaType(context.Request.ContentType);
        if (!string.Equals(normalizedContentType, payload.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var finalPath = _storage.ResolveSafePath(payload.ObjectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        var expectedLength = payload.ContentLength.Value;
        long totalBytesCopied = 0;

        try
        {
            var buffer = new byte[81920];
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted)) > 0)
                {
                    totalBytesCopied += read;
                    if (totalBytesCopied > expectedLength)
                    {
                        break;
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }
            }

            if (totalBytesCopied != expectedLength)
            {
                TryDeleteFileQuietly(tempPath);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            TryDeleteFileQuietly(tempPath);
            return;
        }
        catch
        {
            TryDeleteFileQuietly(tempPath);
            throw;
        }

        var etag = await _storage.ComputeETagAsync(finalPath, context.RequestAborted);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.ETag = etag;
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private async Task HandleDownloadAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        var token = context.Request.Query["token"].ToString();
        if (!_signingService.TryValidateToken(token, LocalMediaSigningService.GetAction, _timeProvider.GetUtcNow(), out var payload)
            || payload is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var finalPath = _storage.ResolveSafePath(payload.ObjectKey);
        if (!File.Exists(finalPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        context.Response.Headers.CacheControl = "private, no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";

        if (!string.IsNullOrWhiteSpace(payload.ContentDisposition))
        {
            context.Response.Headers.ContentDisposition = payload.ContentDisposition;
        }

        await Results.File(
            finalPath,
            contentType: payload.ContentType,
            enableRangeProcessing: true).ExecuteAsync(context);
    }

    private static string NormalizeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semiIdx = contentType.IndexOf(';');
        return (semiIdx >= 0 ? contentType[..semiIdx] : contentType).Trim();
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
