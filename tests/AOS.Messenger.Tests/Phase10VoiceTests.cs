using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Infrastructure.Storage;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase10VoiceTests : IClassFixture<SqlTestDatabaseFixture>, IDisposable
{
    private readonly SqlTestDatabaseFixture _fixture;
    private readonly string _mediaTempRoot;

    public Phase10VoiceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _mediaTempRoot = Path.Combine(Path.GetTempPath(), $"aos_voice_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaTempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_mediaTempRoot))
            {
                Directory.Delete(_mediaTempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    // =========================================================================
    // 1. Domain Constraints, Entity Invariants & Preview Formatting
    // =========================================================================

    [Fact]
    public void MediaConstraints_VoiceAllowlist_And_WaveformValidation_WorkStrictlyAndIsolatePhase9()
    {
        // Voice sanitization & allowlist
        Assert.Equal("blob.m4a", MediaConstraints.SanitizeVoiceFileName("blob", "audio/mp4;codecs=mp4a.40.2"));
        Assert.Equal("voice-note.m4a", MediaConstraints.SanitizeVoiceFileName(".m4a", "audio/mp4;codecs=mp4a.40.2"));
        Assert.Equal("clip.webm", MediaConstraints.SanitizeVoiceFileName("clip.WEBM", "audio/webm;codecs=opus"));
        Assert.Equal("clip.ogg", MediaConstraints.SanitizeVoiceFileName("clip", "audio/ogg;codecs=opus"));

        var (mp4Kind, mp4Mime, mp4Max) = MediaConstraints.ResolveAllowedVoiceMedia("voice-note.m4a", "audio/mp4;codecs=mp4a.40.2");
        Assert.Equal(MediaKind.Voice, mp4Kind);
        Assert.Equal("audio/mp4", mp4Mime);
        Assert.Equal(MediaConstraints.MaxVoiceBytes, mp4Max);

        var (webmKind, webmMime, _) = MediaConstraints.ResolveAllowedVoiceMedia("note.webm", "audio/webm;codecs=opus");
        Assert.Equal(MediaKind.Voice, webmKind);
        Assert.Equal("audio/webm", webmMime);

        var (oggKind, oggMime, _) = MediaConstraints.ResolveAllowedVoiceMedia("note.ogg", "audio/ogg;codecs=opus");
        Assert.Equal(MediaKind.Voice, oggKind);
        Assert.Equal("audio/ogg", oggMime);

        // Phase 9 allowlist remains isolated from voice formats/mimes
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedMedia("voice.m4a", "audio/mp4"));
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedMedia("voice.ogg", "audio/ogg"));
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedVoiceMedia("video.webm", "video/webm"));
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedVoiceMedia("photo.jpg", "image/jpeg"));

        // Waveform serialization & bounds
        var peaks = Enumerable.Range(0, 48).Select(i => (i * 7) % 101).ToArray();
        var csv = MediaConstraints.ValidateAndSerializeWaveformPeaks(peaks);
        Assert.True(csv.Length <= MediaConstraints.MaxWaveformPeaksSerializedLength);
        var parsed = MediaConstraints.ParseWaveformPeaks(csv);
        Assert.NotNull(parsed);
        Assert.Equal(peaks, parsed!);

        Assert.Throws<DomainException>(() => MediaConstraints.ValidateAndSerializeWaveformPeaks(null));
        Assert.Throws<DomainException>(() => MediaConstraints.ValidateAndSerializeWaveformPeaks([]));
        Assert.Throws<DomainException>(() => MediaConstraints.ValidateAndSerializeWaveformPeaks(new int[65]));
        Assert.Throws<DomainException>(() => MediaConstraints.ValidateAndSerializeWaveformPeaks([50, 101]));
        Assert.Throws<DomainException>(() => MediaConstraints.ValidateAndSerializeWaveformPeaks([50, -1]));
    }

    [Fact]
    public void MediaEntity_VoiceInvariants_EnforceDurationWaveform_And_IdempotentMarkPlayed()
    {
        var user = UserId.New();
        var conv = ConversationId.New();
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        // Reject invalid voice bounds on CreatePending
        Assert.Throws<DomainException>(() => Media.CreatePending(
            user, conv, MediaKind.Voice, "v.webm", "audio/webm", 1024, now,
            durationMs: 499, waveformPeaks: "10,20,30"));
        Assert.Throws<DomainException>(() => Media.CreatePending(
            user, conv, MediaKind.Voice, "v.webm", "audio/webm", 1024, now,
            durationMs: MediaConstraints.MaxVoiceDurationMs + 1, waveformPeaks: "10,20,30"));
        Assert.Throws<DomainException>(() => Media.CreatePending(
            user, conv, MediaKind.Voice, "v.webm", "audio/webm", 1024, now,
            durationMs: 1200, waveformPeaks: null));

        // Reject voice metadata on non-voice pending media
        Assert.Throws<DomainException>(() => Media.CreatePending(
            user, conv, MediaKind.Image, "p.png", "image/png", 1024, now,
            durationMs: 1000));
        Assert.Throws<DomainException>(() => Media.CreatePending(
            user, conv, MediaKind.Image, "p.png", "image/png", 1024, now,
            waveformPeaks: "10,20"));

        var voice = Media.CreatePending(
            user, conv, MediaKind.Voice, "v.webm", "audio/webm", 4096, now,
            durationMs: 48200, waveformPeaks: "10,45,80");

        // MarkPlayed rejected while Pending/Uploaded or on non-voice
        Assert.Throws<DomainException>(() => voice.MarkPlayed(now.AddSeconds(5)));

        voice.MarkUploaded(
            $"{MediaConstraints.MediaKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}",
            "audio/webm",
            4096,
            null,
            null,
            voice.DurationMs,
            now.AddSeconds(1));
        Assert.Throws<DomainException>(() => voice.MarkPlayed(now.AddSeconds(5)));

        voice.MarkAttached(new MessageId(42), 0, now.AddSeconds(2));
        var firstPlayed = now.AddSeconds(10);
        voice.MarkPlayed(firstPlayed);
        Assert.Equal(firstPlayed, voice.PlayedAtUtc);

        // Idempotent monotonic: second call does not overwrite earliest timestamp
        voice.MarkPlayed(firstPlayed.AddMinutes(5));
        Assert.Equal(firstPlayed, voice.PlayedAtUtc);

        // Entity overload formats duration
        Assert.Equal("🎤 Voice message (0:48)", MediaPreviewFormatter.FormatPreview(null, [voice]));

        var photo = Media.CreatePending(user, conv, MediaKind.Image, "a.png", "image/png", 1000, now);
        photo.MarkUploaded($"{MediaConstraints.MediaKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}", "image/png", 1000, 10, 10, null, now);
        photo.MarkAttached(new MessageId(43), 0, now);
        Assert.Throws<DomainException>(() => photo.MarkPlayed(now));
    }

    // =========================================================================
    // 2. Binary Header Verification (MP4 AAC, WebM Opus, Ogg Opus/Vorbis)
    // =========================================================================

    [Fact]
    public async Task MediaVerifier_AcceptsValidAudioMp4WebmOgg_AndRejectsSpoofedOrVideoSmuggledStreams()
    {
        // Valid M4A (ftyp M4A  with audio hdlr 'soun')
        var validM4a = BuildIsoBmffAudioBytes("M4A ", "soun");
        var m4aRes = await VerifyVoiceAsync("voice.m4a", "audio/mp4", validM4a, 2400);
        Assert.Equal("audio/mp4", m4aRes.VerifiedContentType);
        Assert.Equal(2400, m4aRes.DurationMs);

        // Valid WebM with TrackType=2 (audio)
        var validWebm = BuildWebmBytes("webm", trackType: 2);
        var webmRes = await VerifyVoiceAsync("voice.webm", "audio/webm", validWebm, 3500);
        Assert.Equal("audio/webm", webmRes.VerifiedContentType);

        // Valid Ogg Opus & Vorbis
        var validOggOpus = BuildOggBytes("OpusHead\x01\x01\x00\x00"u8.ToArray(), bos: true);
        var opusRes = await VerifyVoiceAsync("voice.ogg", "audio/ogg", validOggOpus, 5000);
        Assert.Equal("audio/ogg", opusRes.VerifiedContentType);

        var validOggVorbis = BuildOggBytes("\x01vorbis\x00\x00\x00\x00"u8.ToArray(), bos: true);
        var vorbisRes = await VerifyVoiceAsync("voice.oga", "audio/ogg", validOggVorbis, 5000);
        Assert.Equal("audio/ogg", vorbisRes.VerifiedContentType);

        // Reject MP4 with HEIC image brand or embedded 'vide' track handler
        var heicMp4 = BuildIsoBmffAudioBytes("heic", "soun");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("evil.m4a", "audio/mp4", heicMp4, 1500));

        var smuggledVideoMp4 = BuildIsoBmffAudioBytes("mp42", "vide");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("smuggled.m4a", "audio/mp4", smuggledVideoMp4, 1500));

        // Reject WebM with video TrackType=1 or 'matroska' DocType
        var videoWebm = BuildWebmBytes("webm", trackType: 1);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("video.webm", "audio/webm", videoWebm, 1500));

        var mkvAudio = BuildWebmBytes("matroska", trackType: 2);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("mkv.webm", "audio/webm", mkvAudio, 1500));

        // Reject Ogg with missing BOS flag or Theora video identification header
        var nonBosOgg = BuildOggBytes("OpusHead\x01\x01\x00\x00"u8.ToArray(), bos: false);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("nobos.ogg", "audio/ogg", nonBosOgg, 1500));

        var theoraOgg = BuildOggBytes("\x80theora\x03\x02\x01"u8.ToArray(), bos: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("theora.ogg", "audio/ogg", theoraOgg, 1500));

        // Reject PE/HTML payload posing as audio
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("fake.m4a", "audio/mp4", "MZ\x90\x00fake"u8.ToArray(), 1500));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("fake.webm", "audio/webm", "<script>alert(1)</script>"u8.ToArray(), 1500));
    }

    [Fact]
    public async Task MediaVerifier_HandlesMdatBeforeMoov_SeekHeadWebm_And_RejectsContainerBombsAndPartialOgg()
    {
        // 1. Faststart valid M4A should still complete in a single 4 KB storage read
        var faststartM4a = BuildIsoBmffAudioBytes("M4A ", "soun");
        var (_, faststartReads) = await VerifyVoiceWithStatsAsync("faststart.m4a", "audio/mp4", faststartM4a, 2400);
        Assert.Equal(1, faststartReads);

        // 2. Reject bare 16-byte ftyp mp42 stub missing moov / soun track
        var stubMp4 = BuildIsoBmffFtypOnly("mp42");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("stub.m4a", "audio/mp4", stubMp4, 1500));

        // 3. Genuine mdat-before-moov .m4a (> 4 KB mdat) accepted without video
        var mdatFirstM4a = BuildMdatBeforeMoovIsoBmff("M4A ", mdatPayloadBytes: 12 * 1024, handlerType: "soun");
        var (mdatM4aRes, _) = await VerifyVoiceWithStatsAsync("mdat-first.m4a", "audio/mp4", mdatFirstM4a, 4200);
        Assert.Equal("audio/mp4", mdatM4aRes.VerifiedContentType);

        // 4. Reject mdat-before-moov MP4 video containing trailing hdlr='vide'
        var mdatFirstVideo = BuildMdatBeforeMoovIsoBmff("mp42", mdatPayloadBytes: 12 * 1024, handlerType: "vide");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("mdat-video.m4a", "audio/mp4", mdatFirstVideo, 4200));

        // 5. Reject ISO BMFF anti-DoS box bombs (sub-8 cyclic box, bad ext64 size, >6 depth, >128 boxes, nested rawSize==0)
        var cyclicBoxMp4 = ConcatBytes(BuildIsoBmffFtypOnly("M4A "), [0x00, 0x00, 0x00, 0x02, (byte)'f', (byte)'r', (byte)'e', (byte)'e']);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("cyclic.m4a", "audio/mp4", cyclicBoxMp4, 1500));

        var badExt64Mp4 = ConcatBytes(
            BuildIsoBmffFtypOnly("M4A "),
            [0x00, 0x00, 0x00, 0x01, (byte)'f', (byte)'r', (byte)'e', (byte)'e', 0, 0, 0, 0, 0, 0, 0, 8]);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("badext.m4a", "audio/mp4", badExt64Mp4, 1500));

        var deepNestedMp4 = BuildDeeplyNestedIsoBmff("M4A ", nestLayers: 7, "soun");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("deep.m4a", "audio/mp4", deepNestedMp4, 1500));

        var manyBoxesMp4 = BuildManyFreeBoxesPlusAudioIsoBmff("M4A ", freeBoxCount: 130);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("manyboxes.m4a", "audio/mp4", manyBoxesMp4, 1500));

        var nonTrailingZeroSizeChildMp4 = BuildNonTrailingZeroSizeInsideMoov("M4A ");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("zerochild.m4a", "audio/mp4", nonTrailingZeroSizeChildMp4, 1500));

        // 6. Reject header-only EBML DocType=webm stub without Segment / Tracks
        var webmStubOnly = BuildWebmEbmlHeaderOnly("webm");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("stub.webm", "audio/webm", webmStubOnly, 1500));

        // 7. Void/SeekHead (>8 KB) prefixed WebM: reject TrackType=1 (video), accept TrackType=2 (audio) even with unknown Segment & Cluster
        var paddedVideoWebm = BuildPaddedUnknownSegmentWebm("webm", voidPaddingBytes: 8 * 1024, trackType: 1, appendUnknownCluster: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("padded-vid.webm", "audio/webm", paddedVideoWebm, 1500));

        var paddedAudioWebm = BuildPaddedUnknownSegmentWebm("webm", voidPaddingBytes: 8 * 1024, trackType: 2, appendUnknownCluster: true);
        var (paddedWebmRes, _) = await VerifyVoiceWithStatsAsync("padded-aud.webm", "audio/webm", paddedAudioWebm, 3000);
        Assert.Equal("audio/webm", paddedWebmRes.VerifiedContentType);

        // 8. Reject Ogg whose first segment is 7-byte "OpusHea" even if segment 2 begins with 'd'
        var splitOpusSpoof = BuildTwoSegmentOgg([.. "OpusHea"u8], [.. "d\x01\x01\x00\x00"u8]);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("split.ogg", "audio/ogg", splitOpusSpoof, 1500));
    }

    private static async Task<VerifiedMediaResult> VerifyVoiceAsync(
        string fileName,
        string mime,
        byte[] payload,
        int durationMs)
    {
        var (res, _) = await VerifyVoiceWithStatsAsync(fileName, mime, payload, durationMs);
        return res;
    }

    private static async Task<(VerifiedMediaResult Result, int RangeReads)> VerifyVoiceWithStatsAsync(
        string fileName,
        string mime,
        byte[] payload,
        int durationMs)
    {
        var media = Media.CreatePending(
            UserId.New(),
            ConversationId.New(),
            MediaKind.Voice,
            fileName,
            mime,
            payload.Length,
            DateTimeOffset.UtcNow,
            durationMs: durationMs,
            waveformPeaks: "10,20,30,40");
        var storage = new InMemoryHeaderStorage(media.ObjectKey, payload, "\"etag-v\"");
        using var verifier = new MediaVerifier(
            storage,
            Options.Create(new MediaOptions()),
            NullLogger<MediaVerifier>.Instance);
        var result = await verifier.VerifyAndSanitizeAsync(
            media,
            new MediaObjectMetadata(payload.Length, "\"etag-v\"", mime));
        return (result, storage.ReadRangeCalls);
    }

    // =========================================================================
    // 3. Real SQL LocalDB Constraints, Standalone Enforcement & Concurrency
    // =========================================================================

    [Fact]
    public async Task SqlPersistence_EnforcesVoiceCheckConstraints_StandaloneRules_EditRejection_And_ConcurrentMarkPlayed()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var captureNotifier = new CapturingVoiceRealtimeNotifier();

        UserId aliceId;
        UserId bobId;
        UserId outsiderId;
        ConversationId convId;

        await using (var setupDb = _fixture.CreateDbContext())
        {
            var alice = await SeedUserAsync(setupDb, "v_alice", "Alice");
            var bob = await SeedUserAsync(setupDb, "v_bob", "Bob");
            var outsider = await SeedUserAsync(setupDb, "v_out", "Outsider");
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            outsiderId = new UserId(outsider.Id);
            var conv = await SeedConversationAsync(setupDb, aliceId, bobId, clock.GetUtcNow());
            convId = conv.Id;
        }

        // Verify SQL check constraint rejection when raw SQL inserts a voice row with missing duration
        await using (var constraintDb = _fixture.CreateDbContext())
        {
            await Assert.ThrowsAsync<SqlException>(() => constraintDb.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO [Media] ([Id], [StorageObjectId], [UploaderId], [ConversationId], [DisplayOrder], [Kind], [State], [ObjectKey], [SanitizedFileName], [DeclaredContentType], [DeclaredSizeBytes], [CreatedAtUtc], [DurationMs], [WaveformPeaks])
                   VALUES ({Guid.NewGuid()}, {MediaConstraints.GenerateStorageObjectId()}, {aliceId.Value}, {convId.Value}, {0}, {(int)MediaKind.Voice}, {(int)MediaState.Pending}, {$"pending/{MediaConstraints.GenerateStorageObjectId()}"}, {"bad.webm"}, {"audio/webm"}, {1024L}, {clock.GetUtcNow()}, {(int?)null}, {"10,20"})"));
        }

        await using var sp = BuildLocalServices(clock, captureNotifier);
        await using var scope = sp.CreateAsyncScope();
        var mediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var msgSvc = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        var blockSvc = scope.ServiceProvider.GetRequiredService<IBlockService>();

        // Upload valid WebM voice + normal image for negative standalone test
        var webmBytes = BuildWebmBytes("webm", trackType: 2);
        var initVoice = await mediaSvc.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("voice-note.webm", "audio/webm;codecs=opus", webmBytes.Length, "voice", 12500, [15, 45, 80, 60, 25]));

        await StageLocalFileAsync(initVoice.MediaId, webmBytes);
        var voiceDto = await mediaSvc.CompleteUploadAsync(aliceId, convId, initVoice.MediaId);
        Assert.Equal("voice", voiceDto.Kind);
        Assert.Equal(12500, voiceDto.DurationMs);
        Assert.Equal([15, 45, 80, 60, 25], voiceDto.WaveformPeaks);
        Assert.Null(voiceDto.PlayedAtUtc);

        // Standalone check 1: reject voice + text caption
        await Assert.ThrowsAsync<DomainException>(() => msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto("Look at my voice note", Guid.NewGuid().ToString(), null, [voiceDto.Id])));

        // Standalone check 2: reject voice + second image attachment
        var pngBytes = BuildMinimalPngBytes();
        var initImg = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("pic.png", "image/png", pngBytes.Length));
        await StageLocalFileAsync(initImg.MediaId, pngBytes);
        var imgDto = await mediaSvc.CompleteUploadAsync(aliceId, convId, initImg.MediaId);

        await Assert.ThrowsAsync<DomainException>(() => msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto(null, Guid.NewGuid().ToString(), null, [voiceDto.Id, imgDto.Id])));

        // Successful standalone voice message send
        var sentVoiceMsg = await msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto(null, Guid.NewGuid().ToString(), null, [voiceDto.Id]));
        var voiceMsgId = sentVoiceMsg.Id;
        Assert.Single(sentVoiceMsg.Attachments!);
        Assert.Equal(voiceDto.Id, sentVoiceMsg.Attachments![0].Id);
        Assert.Equal([15, 45, 80, 60, 25], sentVoiceMsg.Attachments![0].WaveformPeaks);

        // Edit rejection even for null or whitespace content
        await Assert.ThrowsAsync<DomainException>(() => msgSvc.EditMessageAsync(
            aliceId, convId, voiceMsgId, new EditMessageRequestDto(null)));
        await Assert.ThrowsAsync<DomainException>(() => msgSvc.EditMessageAsync(
            aliceId, convId, voiceMsgId, new EditMessageRequestDto("Edited voice caption")));

        // Inline streaming URL check (never attachment disposition by default)
        var streamUrlDto = await mediaSvc.GetDownloadUrlAsync(bobId, convId, voiceMsgId, voiceDto.Id);
        Assert.False(string.IsNullOrWhiteSpace(streamUrlDto.Url));

        // Outsider / own-voice / blocked MarkVoicePlayed checks
        await Assert.ThrowsAsync<ForbiddenException>(() => mediaSvc.MarkVoicePlayedAsync(outsiderId, convId, voiceMsgId, voiceDto.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => mediaSvc.MarkVoicePlayedAsync(aliceId, convId, voiceMsgId, voiceDto.Id));

        await blockSvc.BlockUserAsync(aliceId, bobId);
        await Assert.ThrowsAsync<ForbiddenException>(() => mediaSvc.MarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id));
        await blockSvc.UnblockUserAsync(aliceId, bobId);

        // Parallel concurrent MarkVoicePlayed race across tabs -> monotonic single broadcast
        clock.Advance(TimeSpan.FromSeconds(15));
        var raceTasks = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () =>
            {
                await using var raceScope = sp.CreateAsyncScope();
                var svc = raceScope.ServiceProvider.GetRequiredService<IMediaService>();
                return await svc.MarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id);
            }))
            .ToArray();

        var raceResults = await Task.WhenAll(raceTasks);
        var expectedPlayedAt = raceResults[0].PlayedAtUtc;
        Assert.All(raceResults, r => Assert.Equal(expectedPlayedAt, r.PlayedAtUtc));
        Assert.Single(captureNotifier.VoicePlayedEvents);

        // Delete for everyone scrubs cleanly without violating SQL check constraints
        clock.Advance(TimeSpan.FromMinutes(1));
        await msgSvc.DeleteForEveryoneAsync(aliceId, convId, voiceMsgId);
        await Assert.ThrowsAsync<NotFoundException>(() => mediaSvc.MarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id));

        await using var checkDb = _fixture.CreateDbContext();
        var tombstonedMedia = await checkDb.Media.AsNoTracking().SingleAsync(m => m.Id == voiceDto.Id);
        Assert.Equal(MediaState.Deleted, tombstonedMedia.State);
        Assert.Equal("15,45,80,60,25", tombstonedMedia.WaveformPeaks);
        Assert.Equal(12500, tombstonedMedia.DurationMs);
        _ = storage;
    }

    // =========================================================================
    // 4. SignalR ChatHub & Block Suppression Tests
    // =========================================================================

    [Fact]
    public async Task ChatHub_SendRecordingIndicator_And_RealtimeNotifier_SuppressBlockedPeer()
    {
        var convId = ConversationId.New();
        var userA = UserId.New();
        var userB = UserId.New();

        var notifierMock = new CapturingVoiceRealtimeNotifier();
        var typingSvc = new FakeTypingService(userB);
        var tracker = new InMemoryConnectionTracker();
        var hub = new ChatHub(
            new FakeConversationService(),
            tracker,
            new StubCurrentUser(userA),
            NullLogger<ChatHub>.Instance,
            presenceService: null!,
            typingService: typingSvc,
            realtimeNotifier: notifierMock)
        {
            Context = new StubHubCallerContext("conn-rec-1")
        };

        await hub.SendRecordingIndicator(convId.Value, true);
        Assert.Single(notifierMock.RecordingEvents);
        Assert.Equal((userB, true), (notifierMock.RecordingEvents[0].Recipient, notifierMock.RecordingEvents[0].Dto.IsRecording));

        // Now test real SignalRRealtimeNotifier block boundary suppression
        await using var setupDb = _fixture.CreateDbContext();
        var u1 = await SeedUserAsync(setupDb, "sig_a", "SigA");
        var u2 = await SeedUserAsync(setupDb, "sig_b", "SigB");
        var id1 = new UserId(u1.Id);
        var id2 = new UserId(u2.Id);
        var realConv = await SeedConversationAsync(setupDb, id1, id2, DateTimeOffset.UtcNow);
        await using (var sp = BuildLocalServices(new TestTimeProvider(DateTimeOffset.UtcNow), notifierMock))
        {
            await using var scope = sp.CreateAsyncScope();
            var blockSvc = scope.ServiceProvider.GetRequiredService<IBlockService>();
            await blockSvc.BlockUserAsync(id1, id2);
        }

        var realClients = new CapturingChatClients();
        var realNotifier = new SignalRRealtimeNotifier(
            new CapturingHubContext(realClients),
            new ConversationPersistence(setupDb),
            new BlockPersistence(setupDb),
            NullLogger<SignalRRealtimeNotifier>.Instance);

        await realNotifier.NotifyRecordingAsync(id2, new RecordingIndicatorDto(realConv.Id.Value, id1.Value, true));
        Assert.Empty(realClients.SentRecordingIndicators);

        // When blocked, NotifyVoicePlayedAsync still notifies caller's own tabs (multi-tab sync), NEVER blocked uploader
        var playedDto = new VoicePlayedDto(realConv.Id.Value, 99, Guid.NewGuid(), id2.Value, DateTimeOffset.UtcNow);
        await realNotifier.NotifyVoicePlayedAsync(realConv.Id, id2, id1, playedDto);
        Assert.Contains(id2.Value.ToString(), realClients.TargetedUsersForVoicePlayed);
        Assert.DoesNotContain(id1.Value.ToString(), realClients.TargetedUsersForVoicePlayed);
    }

    // =========================================================================
    // 5. End-to-End Web API Integration Test (CSRF + R2 LocalDisk + Played POST)
    // =========================================================================

    [Fact]
    public async Task WebApi_VoiceUpload_SendMessage_EditRejected_And_MarkPlayed_WorkEndToEnd()
    {
        Assert.Equal(
            "receipts",
            typeof(MediaController).GetMethod(nameof(MediaController.MarkVoicePlayed))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        using var factory = CreateWebFactory();
        UserId aliceId;
        UserId bobId;
        ConversationId convId;
        string aliceUsername;
        string bobUsername;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var alice = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"va_{Guid.NewGuid():N}"[..16], Email = $"va_{Guid.NewGuid():N}@example.com", EmailConfirmed = true, DisplayName = "Voice Alice", CreatedAtUtc = DateTimeOffset.UtcNow };
            var bob = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"vb_{Guid.NewGuid():N}"[..16], Email = $"vb_{Guid.NewGuid():N}@example.com", EmailConfirmed = true, DisplayName = "Voice Bob", CreatedAtUtc = DateTimeOffset.UtcNow };
            Assert.True((await um.CreateAsync(alice, "Password123!")).Succeeded);
            Assert.True((await um.CreateAsync(bob, "Password123!")).Succeeded);
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            var conv = await SeedConversationAsync(db, aliceId, bobId, DateTimeOffset.UtcNow);
            convId = conv.Id;
        }

        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);
        var aliceCsrf = await LoginAndGetCsrfAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfAsync(bobClient, bobUsername);

        var oggBytes = BuildOggBytes("OpusHead\x01\x01\x00\x00"u8.ToArray(), bos: true);
        var initReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/media/initiate",
            aliceCsrf,
            new { fileName = "voice-message.ogg", contentType = "audio/ogg;codecs=opus", sizeBytes = oggBytes.Length, kind = "voice", durationMs = 6200, waveformPeaks = new[] { 10, 35, 70, 90, 40 } });
        var initResp = await aliceClient.SendAsync(initReq);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var mediaId = Guid.Parse(initDoc.RootElement.GetProperty("mediaId").GetString()!);
        var uploadUrl = initDoc.RootElement.GetProperty("uploadUrl").GetString()!;
        var requiredMime = initDoc.RootElement.GetProperty("requiredContentType").GetString()!;
        Assert.Equal("voice", initDoc.RootElement.GetProperty("kind").GetString());

        var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(oggBytes)
        };
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(requiredMime);
        var putResp = await aliceClient.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var compResp = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/media/{mediaId}/complete",
            aliceCsrf));
        Assert.Equal(HttpStatusCode.OK, compResp.StatusCode);

        // Standalone enforcement via HTTP API
        var captionedSend = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages",
            aliceCsrf,
            new { content = "Caption", idempotencyKey = Guid.NewGuid().ToString(), mediaIds = new[] { mediaId } }));
        Assert.Equal(HttpStatusCode.BadRequest, captionedSend.StatusCode);

        var voiceSend = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString(), mediaIds = new[] { mediaId } }));
        Assert.Equal(HttpStatusCode.OK, voiceSend.StatusCode);
        using var sendDoc = JsonDocument.Parse(await voiceSend.Content.ReadAsStringAsync());
        var messageId = sendDoc.RootElement.GetProperty("id").GetInt64();

        // Edit attempt rejected with 400
        var editReq = new HttpRequestMessage(HttpMethod.Put, $"/api/conversations/{convId.Value}/messages/{messageId}")
        {
            Content = JsonContent.Create(new { content = "Changed" })
        };
        editReq.Headers.Add("RequestVerificationToken", aliceCsrf);
        editReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var editResp = await aliceClient.SendAsync(editReq);
        Assert.Equal(HttpStatusCode.BadRequest, editResp.StatusCode);

        // Uploader cannot mark own voice played -> 403
        var alicePlayed = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages/{messageId}/media/{mediaId}/played",
            aliceCsrf));
        Assert.Equal(HttpStatusCode.Forbidden, alicePlayed.StatusCode);

        // Recipient marks voice played -> 200 OK + idempotent second call
        var bobPlayed1 = await bobClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages/{messageId}/media/{mediaId}/played",
            bobCsrf));
        Assert.Equal(HttpStatusCode.OK, bobPlayed1.StatusCode);
        using var playedDoc1 = JsonDocument.Parse(await bobPlayed1.Content.ReadAsStringAsync());
        var playedAt1 = playedDoc1.RootElement.GetProperty("playedAtUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(playedAt1));

        var bobPlayed2 = await bobClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages/{messageId}/media/{mediaId}/played",
            bobCsrf));
        Assert.Equal(HttpStatusCode.OK, bobPlayed2.StatusCode);
        using var playedDoc2 = JsonDocument.Parse(await bobPlayed2.Content.ReadAsStringAsync());
        Assert.Equal(playedAt1, playedDoc2.RootElement.GetProperty("playedAtUtc").GetString());
    }

    // =========================================================================
    // Test Fixture Helpers & Fakes
    // =========================================================================

    private ServiceProvider BuildLocalServices(TestTimeProvider clock, IRealtimeNotifier notifier)
    {
        var services = _fixture.CreateServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.Replace(ServiceDescriptor.Singleton(notifier));
        services.Configure<MediaOptions>(o =>
        {
            o.MaxConcurrentPendingPerUser = 10;
            o.DailyUploadQuotaBytes = 50L * 1024 * 1024;
        });
        services.Replace(ServiceDescriptor.Singleton(sp =>
            new LocalDiskMediaStorage(sp.GetRequiredService<LocalMediaSigningService>(), _mediaTempRoot)));
        services.Replace(ServiceDescriptor.Singleton<IMediaStorage>(sp =>
            sp.GetRequiredService<LocalDiskMediaStorage>()));
        return services.BuildServiceProvider();
    }

    private async Task StageLocalFileAsync(MediaId mediaId, byte[] bytes)
    {
        await using var db = _fixture.CreateDbContext();
        var row = await db.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId);
        var rel = row.ObjectKey.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(_mediaTempRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private WebApplicationFactory<Program> CreateWebFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
            builder.UseSetting("PublicBaseUrl", "https://localhost:7001");
            builder.UseSetting("Media:LocalStoragePath", _mediaTempRoot);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                    ["PublicBaseUrl"] = "https://localhost:7001",
                    ["Media:LocalStoragePath"] = _mediaTempRoot,
                    ["R2:ServiceUrl"] = "",
                    ["R2:BucketName"] = "",
                    ["R2:AccessKeyId"] = "",
                    ["R2:SecretAccessKey"] = ""
                });
            });
            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (d is not null) services.Remove(d);
                services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlServer(_fixture.ConnectionString));
            });
        });
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> LoginAndGetCsrfAsync(HttpClient client, string username)
    {
        var loginHtml = await client.GetStringAsync("/Account/Login");
        var token = ExtractCsrf(loginHtml);
        var resp = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserNameOrEmail"] = username,
            ["Password"] = "Password123!",
            ["RememberMe"] = "false",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var chatHtml = await client.GetStringAsync("/Chat");
        return ExtractCsrf(chatHtml);
    }

    private static string ExtractCsrf(string html)
    {
        var m = Regex.Match(html, @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""", RegexOptions.IgnoreCase);
        Assert.True(m.Success);
        return m.Groups[1].Value;
    }

    private static HttpRequestMessage CreateCsrfJsonPost(string url, string csrf, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("RequestVerificationToken", csrf);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<ApplicationUser> SeedUserAsync(ApplicationDbContext db, string prefix, string displayName)
    {
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..16];
        var email = $"{userName}@example.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Conversation> SeedConversationAsync(ApplicationDbContext db, UserId u1, UserId u2, DateTimeOffset now)
    {
        var conv = Conversation.CreateDirect(u1, u2, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv;
    }

    private static byte[] BuildMinimalPngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00,
        0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
        0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
        0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82
    ];

    private static byte[] BuildIsoBmffAudioBytes(string majorBrand, string handlerType)
    {
        using var ms = new MemoryStream();
        Span<byte> be = stackalloc byte[4];

        // ftyp (16 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(be, 16u);
        ms.Write(be);
        ms.Write("ftyp"u8);
        ms.Write(Encoding.ASCII.GetBytes(majorBrand));
        ms.Write("\x00\x00\x00\x00"u8);

        // hdlr box (24 bytes): size(4) + hdlr(4) + version/flags(4) + pre_defined(4) + handler_type(4) + reserved(4)
        using var hdlrMs = new MemoryStream();
        BinaryPrimitives.WriteUInt32BigEndian(be, 24u);
        hdlrMs.Write(be);
        hdlrMs.Write("hdlr"u8);
        hdlrMs.Write(new byte[8]);
        hdlrMs.Write(Encoding.ASCII.GetBytes(handlerType));
        hdlrMs.Write(new byte[4]);
        var hdlr = hdlrMs.ToArray();

        // mdia -> trak -> moov wrapper
        byte[] WrapBox(string type, byte[] inner)
        {
            using var box = new MemoryStream();
            Span<byte> sz = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(sz, (uint)(8 + inner.Length));
            box.Write(sz);
            box.Write(Encoding.ASCII.GetBytes(type));
            box.Write(inner);
            return box.ToArray();
        }

        var moov = WrapBox("moov", WrapBox("trak", WrapBox("mdia", hdlr)));
        ms.Write(moov);
        return ms.ToArray();
    }

    private static byte[] BuildWebmBytes(string docType, int trackType)
    {
        using var ms = new MemoryStream();
        var docBytes = Encoding.ASCII.GetBytes(docType);
        // EBML header: 1A 45 DF A3 | size | 42 82 (DocType) | len | docType
        ms.Write([0x1A, 0x45, 0xDF, 0xA3]);
        var ebmlBodyLen = 2 + 1 + docBytes.Length;
        ms.WriteByte((byte)(0x80 | ebmlBodyLen));
        ms.Write([0x42, 0x82]);
        ms.WriteByte((byte)(0x80 | docBytes.Length));
        ms.Write(docBytes);

        // Segment (18 53 80 67) -> Tracks (16 54 AE 6B) -> TrackEntry (AE) -> TrackType (83 81 <trackType>)
        byte[] trackTypeEl = [0x83, 0x81, (byte)trackType];
        byte[] trackEntry = [0xAE, (byte)(0x80 | trackTypeEl.Length), .. trackTypeEl];
        byte[] tracks = [0x16, 0x54, 0xAE, 0x6B, (byte)(0x80 | trackEntry.Length), .. trackEntry];
        ms.Write([0x18, 0x53, 0x80, 0x67, (byte)(0x80 | tracks.Length)]);
        ms.Write(tracks);
        return ms.ToArray();
    }

    private static byte[] BuildOggBytes(byte[] identificationPacket, bool bos)
    {
        using var ms = new MemoryStream();
        ms.Write("OggS"u8);
        ms.WriteByte(0x00); // stream_structure_version
        ms.WriteByte(bos ? (byte)0x02 : (byte)0x00); // header_type_flag
        ms.Write(new byte[8]); // granule_position
        ms.Write(new byte[4]); // serial
        ms.Write(new byte[4]); // page seq
        ms.Write(new byte[4]); // checksum
        ms.WriteByte(1); // page_segments
        ms.WriteByte((byte)identificationPacket.Length);
        ms.Write(identificationPacket);
        return ms.ToArray();
    }

    private static byte[] ConcatBytes(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var res = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, res, offset, p.Length);
            offset += p.Length;
        }
        return res;
    }

    private static byte[] WrapIsoBox(string type, byte[] inner)
    {
        var buf = new byte[8 + inner.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)buf.Length);
        Encoding.ASCII.GetBytes(type, buf.AsSpan(4, 4));
        Buffer.BlockCopy(inner, 0, buf, 8, inner.Length);
        return buf;
    }

    private static byte[] BuildIsoBmffFtypOnly(string majorBrand)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 16u);
        "ftyp"u8.CopyTo(buf.AsSpan(4, 4));
        Encoding.ASCII.GetBytes(majorBrand, buf.AsSpan(8, 4));
        return buf;
    }

    private static byte[] BuildHdlrBox(string handlerType)
    {
        var inner = new byte[16];
        Encoding.ASCII.GetBytes(handlerType, inner.AsSpan(8, 4));
        return WrapIsoBox("hdlr", inner);
    }

    private static byte[] BuildMdatBeforeMoovIsoBmff(string majorBrand, int mdatPayloadBytes, string handlerType)
    {
        var ftyp = BuildIsoBmffFtypOnly(majorBrand);
        var mdat = WrapIsoBox("mdat", new byte[mdatPayloadBytes]);
        var moov = WrapIsoBox("moov", WrapIsoBox("trak", WrapIsoBox("mdia", BuildHdlrBox(handlerType))));
        return ConcatBytes(ftyp, mdat, moov);
    }

    private static byte[] BuildDeeplyNestedIsoBmff(string majorBrand, int nestLayers, string handlerType)
    {
        var cur = BuildHdlrBox(handlerType);
        for (var i = 0; i < nestLayers; i++)
        {
            cur = WrapIsoBox(i % 2 == 0 ? "mdia" : "trak", cur);
        }
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), WrapIsoBox("moov", cur));
    }

    private static byte[] BuildManyFreeBoxesPlusAudioIsoBmff(string majorBrand, int freeBoxCount)
    {
        var parts = new List<byte[]> { BuildIsoBmffFtypOnly(majorBrand) };
        var freeBox = WrapIsoBox("free", []);
        for (var i = 0; i < freeBoxCount; i++)
        {
            parts.Add(freeBox);
        }
        parts.Add(WrapIsoBox("moov", WrapIsoBox("trak", WrapIsoBox("mdia", BuildHdlrBox("soun")))));
        return ConcatBytes([.. parts]);
    }

    private static byte[] BuildNonTrailingZeroSizeInsideMoov(string majorBrand)
    {
        // Child with rawSize==0 inside moov followed by valid trak
        var zeroFree = new byte[8];
        "free"u8.CopyTo(zeroFree.AsSpan(4, 4));
        var validTrak = WrapIsoBox("trak", WrapIsoBox("mdia", BuildHdlrBox("soun")));
        var moov = WrapIsoBox("moov", ConcatBytes(zeroFree, validTrak));
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov);
    }

    private static byte[] BuildWebmEbmlHeaderOnly(string docType)
    {
        var docBytes = Encoding.ASCII.GetBytes(docType);
        using var ms = new MemoryStream();
        ms.Write([0x1A, 0x45, 0xDF, 0xA3]);
        var bodyLen = 2 + 1 + docBytes.Length;
        ms.WriteByte((byte)(0x80 | bodyLen));
        ms.Write([0x42, 0x82]);
        ms.WriteByte((byte)(0x80 | docBytes.Length));
        ms.Write(docBytes);
        return ms.ToArray();
    }

    private static byte[] EncodeEbmlUintSize4Byte(int size)
    {
        var val = (uint)size | 0x1000_0000u;
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, val);
        return b;
    }

    private static byte[] BuildPaddedUnknownSegmentWebm(string docType, int voidPaddingBytes, int trackType, bool appendUnknownCluster)
    {
        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly(docType));
        // Unknown-size Segment: 18 53 80 67 + 8-byte VINT all-1s (0x01 FF FF FF FF FF FF FF)
        ms.Write([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        // Void element (0xEC) with 4-byte VINT size pushing Tracks past 8 KB
        ms.WriteByte(0xEC);
        ms.Write(EncodeEbmlUintSize4Byte(voidPaddingBytes));
        ms.Write(new byte[voidPaddingBytes]);
        // Tracks -> TrackEntry -> TrackType
        byte[] trackTypeEl = [0x83, 0x81, (byte)trackType];
        byte[] trackEntry = [0xAE, (byte)(0x80 | trackTypeEl.Length), .. trackTypeEl];
        byte[] tracks = [0x16, 0x54, 0xAE, 0x6B, (byte)(0x80 | trackEntry.Length), .. trackEntry];
        ms.Write(tracks);
        if (appendUnknownCluster)
        {
            ms.Write([0x1F, 0x43, 0xB6, 0x75, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            ms.Write(new byte[64]);
        }
        return ms.ToArray();
    }

    private static byte[] BuildTwoSegmentOgg(byte[] seg1, byte[] seg2)
    {
        using var ms = new MemoryStream();
        ms.Write("OggS"u8);
        ms.WriteByte(0x00);
        ms.WriteByte(0x02);
        ms.Write(new byte[8]);
        ms.Write(new byte[4]);
        ms.Write(new byte[4]);
        ms.Write(new byte[4]);
        ms.WriteByte(2); // 2 lacing entries so packet 1 length is strictly seg1.Length (<255)
        ms.WriteByte((byte)seg1.Length);
        ms.WriteByte((byte)seg2.Length);
        ms.Write(seg1);
        ms.Write(seg2);
        return ms.ToArray();
    }

    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class InMemoryHeaderStorage(string expectedKey, byte[] data, string etag) : IMediaStorage
    {
        public int ReadRangeCalls { get; private set; }
        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc) => "/";
        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc) => "/";
        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default) =>
            Task.FromResult<MediaObjectMetadata?>(new MediaObjectMetadata(data.Length, etag, null));
        public Task<byte[]> ReadByteRangeAsync(string objectKey, long offset, int length, string? ifMatchETag = null, CancellationToken ct = default)
        {
            ReadRangeCalls++;
            Assert.Equal(expectedKey, objectKey);
            var start = (int)Math.Min(offset, data.Length);
            var count = Math.Min(length, data.Length - start);
            return Task.FromResult(data.AsSpan(start, count).ToArray());
        }
        public Task DownloadToStreamAsync(string objectKey, Stream destination, long maxBytes, string? ifMatchETag = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PromoteByCopyAsync(string stagingKey, string permanentKey, string verifiedContentType, string? contentDisposition, string? ifMatchETag = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PromoteFromStreamAsync(string stagingKey, string permanentKey, Stream content, long contentLength, string verifiedContentType, string? contentDisposition, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CapturingVoiceRealtimeNotifier : IRealtimeNotifier
    {
        public List<(UserId Recipient, RecordingIndicatorDto Dto)> RecordingEvents { get; } = [];
        public List<(ConversationId ConversationId, UserId Caller, UserId Uploader, VoicePlayedDto Dto)> VoicePlayedEvents { get; } = [];

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyRecordingAsync(UserId recipientUserId, RecordingIndicatorDto recording, CancellationToken ct = default)
        {
            RecordingEvents.Add((recipientUserId, recording));
            return Task.CompletedTask;
        }
        public Task NotifyVoicePlayedAsync(ConversationId conversationId, UserId callerUserId, UserId uploaderUserId, VoicePlayedDto played, CancellationToken ct = default)
        {
            VoicePlayedEvents.Add((conversationId, callerUserId, uploaderUserId, played));
            return Task.CompletedTask;
        }
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingChatClient(List<string> recordingTargets, List<string> playedTargets, string userId) : IChatClient
    {
        public Task ReceiveMessage(MessageDto message) => Task.CompletedTask;
        public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt) => Task.CompletedTask;
        public Task ReceiveTypingIndicator(TypingIndicatorDto typing) => Task.CompletedTask;
        public Task ReceiveRecordingIndicator(RecordingIndicatorDto recording)
        {
            recordingTargets.Add(userId);
            return Task.CompletedTask;
        }
        public Task ReceiveVoicePlayed(VoicePlayedDto played)
        {
            playedTargets.Add(userId);
            return Task.CompletedTask;
        }
        public Task ReceivePresenceChanged(UserPresenceDto presence) => Task.CompletedTask;
        public Task ReceiveMessageEdited(MessageEditedDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message) => Task.CompletedTask;
        public Task ReceiveReactionUpdated(ReactionUpdatedDto reaction) => Task.CompletedTask;
        public Task ReceiveUserProfileUpdated(UserProfileUpdatedDto profile) => Task.CompletedTask;
    }

    private sealed class CapturingChatClients : IHubClients<IChatClient>
    {
        public List<string> SentRecordingIndicators { get; } = [];
        public List<string> TargetedUsersForVoicePlayed { get; } = [];

        public IChatClient All => new CapturingChatClient(SentRecordingIndicators, TargetedUsersForVoicePlayed, "all");
        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IChatClient Client(string connectionId) => All;
        public IChatClient Clients(IReadOnlyList<string> connectionIds) => All;
        public IChatClient Group(string groupName) => All;
        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
        public IChatClient Groups(IReadOnlyList<string> groupNames) => All;
        public IChatClient User(string userId) => new CapturingChatClient(SentRecordingIndicators, TargetedUsersForVoicePlayed, userId);
        public IChatClient Users(IReadOnlyList<string> userIds) => new MultiUserChatClient(SentRecordingIndicators, TargetedUsersForVoicePlayed, userIds);
    }

    private sealed class MultiUserChatClient(List<string> rec, List<string> played, IReadOnlyList<string> users) : IChatClient
    {
        public Task ReceiveMessage(MessageDto message) => Task.CompletedTask;
        public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt) => Task.CompletedTask;
        public Task ReceiveTypingIndicator(TypingIndicatorDto typing) => Task.CompletedTask;
        public Task ReceiveRecordingIndicator(RecordingIndicatorDto recording) { rec.AddRange(users); return Task.CompletedTask; }
        public Task ReceiveVoicePlayed(VoicePlayedDto dto) { played.AddRange(users); return Task.CompletedTask; }
        public Task ReceivePresenceChanged(UserPresenceDto presence) => Task.CompletedTask;
        public Task ReceiveMessageEdited(MessageEditedDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message) => Task.CompletedTask;
        public Task ReceiveReactionUpdated(ReactionUpdatedDto reaction) => Task.CompletedTask;
        public Task ReceiveUserProfileUpdated(UserProfileUpdatedDto profile) => Task.CompletedTask;
    }

    private sealed class CapturingHubContext(CapturingChatClients clients) : IHubContext<ChatHub, IChatClient>
    {
        public IHubClients<IChatClient> Clients => clients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class FakeTypingService(UserId recipient) : ITypingService
    {
        public Task<UserId?> ValidateAndGetTypingRecipientAsync(UserId callerId, Guid conversationId, CancellationToken ct = default) =>
            Task.FromResult<UserId?>(recipient);
    }

    private sealed class FakeConversationService : IConversationService
    {
        public Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConversationSummaryDto>>([]);
        public Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubCurrentUser(UserId id) : ICurrentUser
    {
        public UserId? Id => id;
        public string? UserName => "voice_tester";
        public bool IsAuthenticated => true;
    }

    private sealed class StubHubCallerContext(string connId) : HubCallerContext
    {
        public override string ConnectionId => connId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
