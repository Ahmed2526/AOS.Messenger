using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application use-case contract for initiating, verifying, cancelling, and downloading chat media.
/// </summary>
public interface IMediaService
{
    Task<InitiateMediaUploadResponseDto> InitiateUploadAsync(
        UserId callerId,
        ConversationId conversationId,
        InitiateMediaUploadRequestDto dto,
        CancellationToken ct = default);

    Task<MediaAttachmentDto> CompleteUploadAsync(
        UserId callerId,
        ConversationId conversationId,
        MediaId mediaId,
        CancellationToken ct = default);

    Task CancelUploadAsync(
        UserId callerId,
        ConversationId conversationId,
        MediaId mediaId,
        CancellationToken ct = default);

    Task<MediaDownloadUrlDto> GetDownloadUrlAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        bool forceDownload = false,
        bool thumbnail = false,
        CancellationToken ct = default);
}
