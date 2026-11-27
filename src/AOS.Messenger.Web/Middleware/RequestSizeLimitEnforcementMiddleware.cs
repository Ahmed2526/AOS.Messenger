using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace AOS.Messenger.Web.Middleware;

/// <summary>
/// Enforces endpoint <see cref="IRequestSizeLimitMetadata"/> body-size limits uniformly across
/// <c>Content-Length</c>, Kestrel native <see cref="IHttpMaxRequestBodySizeFeature"/>,
/// <see cref="HttpRequest.Body"/>, and <see cref="IRequestBodyPipeFeature"/> (<see cref="HttpRequest.BodyReader"/>).
/// </summary>
public sealed class RequestSizeLimitEnforcementMiddleware
{
    private readonly RequestDelegate _next;

    public RequestSizeLimitEnforcementMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        var sizeLimitMetadata = endpoint?.Metadata.GetOrderedMetadata<IRequestSizeLimitMetadata>();
        var maxBodySize = sizeLimitMetadata is { Count: > 0 }
            ? sizeLimitMetadata[^1].MaxRequestBodySize
            : null;

        if (!maxBodySize.HasValue)
        {
            await _next(context);
            return;
        }

        if (context.Request.ContentLength > maxBodySize.Value)
        {
            throw new BadHttpRequestException(
                "Request body too large.",
                StatusCodes.Status413PayloadTooLarge);
        }

        var maxBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature is not null && !maxBodySizeFeature.IsReadOnly)
        {
            maxBodySizeFeature.MaxRequestBodySize = maxBodySize.Value;
        }

        var originalBody = context.Request.Body;
        var originalPipeFeature = context.Features.Get<IRequestBodyPipeFeature>();
        var boundedStream = new BoundedRequestBodyStream(originalBody, maxBodySize.Value);
        var boundedPipeReader = PipeReader.Create(boundedStream, new StreamPipeReaderOptions(leaveOpen: true));

        context.Request.Body = boundedStream;
        context.Features.Set<IRequestBodyPipeFeature>(new BoundedRequestBodyPipeFeature(boundedPipeReader));
        try
        {
            await _next(context);
        }
        finally
        {
            boundedPipeReader.Complete();
            context.Request.Body = originalBody;
            context.Features.Set<IRequestBodyPipeFeature?>(originalPipeFeature);
        }
    }

    private sealed class BoundedRequestBodyPipeFeature : IRequestBodyPipeFeature
    {
        public BoundedRequestBodyPipeFeature(PipeReader reader)
        {
            Reader = reader;
        }

        public PipeReader Reader { get; }
    }

    private sealed class BoundedRequestBodyStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _bytesRead;

        public BoundedRequestBodyStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = Math.Max(0L, maxBytes);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var boundedCount = ComputeBoundedReadCount(count);
            var read = _inner.Read(buffer, offset, boundedCount);
            RecordAndEnforceLimit(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var boundedCount = ComputeBoundedReadCount(buffer.Length);
            var read = _inner.Read(buffer[..boundedCount]);
            RecordAndEnforceLimit(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var boundedCount = ComputeBoundedReadCount(count);
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, boundedCount), cancellationToken);
            RecordAndEnforceLimit(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var boundedCount = ComputeBoundedReadCount(buffer.Length);
            var read = await _inner.ReadAsync(buffer[..boundedCount], cancellationToken);
            RecordAndEnforceLimit(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Host owns the underlying Request.Body stream lifetime.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private int ComputeBoundedReadCount(int requestedCount)
        {
            if (requestedCount <= 0)
            {
                return 0;
            }

            var remaining = Math.Max(0L, _maxBytes - _bytesRead);
            var maxAllowedSlice = remaining >= int.MaxValue - 1
                ? int.MaxValue
                : (int)(remaining + 1);

            return Math.Min(requestedCount, maxAllowedSlice);
        }

        private void RecordAndEnforceLimit(int bytesJustRead)
        {
            if (bytesJustRead <= 0)
            {
                return;
            }

            _bytesRead += bytesJustRead;
            if (_bytesRead > _maxBytes)
            {
                throw new BadHttpRequestException(
                    "Request body too large.",
                    StatusCodes.Status413PayloadTooLarge);
            }
        }
    }
}
