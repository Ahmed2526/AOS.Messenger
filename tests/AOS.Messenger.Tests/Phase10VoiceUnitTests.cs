using System.Buffers.Binary;
using System.Text;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase10VoiceUnitTests
{
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

    [Fact]
    public async Task MediaVerifier_AcceptsValidAudioMp4WebmOgg_AndRejectsSpoofedOrVideoSmuggledStreams()
    {
        // Valid M4A (ftyp M4A  with audio hdlr 'soun', stsz sample_count=1, non-empty mdat)
        var validM4a = BuildIsoBmffAudioBytes("M4A ", "soun", includeSampleEvidence: true);
        var m4aRes = await VerifyVoiceAsync("voice.m4a", "audio/mp4", validM4a, 2400);
        Assert.Equal("audio/mp4", m4aRes.VerifiedContentType);
        Assert.Equal(2400, m4aRes.DurationMs);

        // Valid Fragmented MP4 (moov/mvex + moof/traf/tfhd+trun + mdat)
        var validFragmentedM4a = BuildFragmentedAudioMp4Bytes("isom", trackId: 1, tfhdTrackId: 1, sampleCount: 4);
        var fragRes = await VerifyVoiceAsync("frag.m4a", "audio/mp4", validFragmentedM4a, 2400);
        Assert.Equal("audio/mp4", fragRes.VerifiedContentType);

        // Valid WebM with TrackType=2 (audio), TrackNumber=1, and Cluster/SimpleBlock
        var validWebm = BuildWebmBytes("webm", trackType: 2, includeClusterAudioBlock: true);
        var webmRes = await VerifyVoiceAsync("voice.webm", "audio/webm", validWebm, 3500);
        Assert.Equal("audio/webm", webmRes.VerifiedContentType);

        // Valid Ogg Opus (complete RFC 7845 OpusHead + OpusTags + audio packet) & Vorbis (3-header + audio packet)
        var validOggOpus = BuildValidOggOpusStream();
        var opusRes = await VerifyVoiceAsync("voice.ogg", "audio/ogg", validOggOpus, 5000);
        Assert.Equal("audio/ogg", opusRes.VerifiedContentType);

        var validOggVorbis = BuildValidOggVorbisStream();
        var vorbisRes = await VerifyVoiceAsync("voice.oga", "audio/ogg", validOggVorbis, 5000);
        Assert.Equal("audio/ogg", vorbisRes.VerifiedContentType);

        // Multi-channel OpusHead with valid mapping family 1
        var multichannelOpus = BuildValidOggOpusStream(BuildValidOpusHead(channels: 2, mappingFamily: 1, streamCount: 1, coupledCount: 1));
        await VerifyVoiceAsync("multi.ogg", "audio/ogg", multichannelOpus, 5000);

        // Reject MP4 with HEIC image brand, embedded 'vide' track handler, or zero sample evidence
        var heicMp4 = BuildIsoBmffAudioBytes("heic", "soun", includeSampleEvidence: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("evil.m4a", "audio/mp4", heicMp4, 1500));

        var smuggledVideoMp4 = BuildIsoBmffAudioBytes("mp42", "vide", includeSampleEvidence: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("smuggled.m4a", "audio/mp4", smuggledVideoMp4, 1500));

        var samplelessMp4 = BuildIsoBmffAudioBytes("M4A ", "soun", includeSampleEvidence: false);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("empty-samples.m4a", "audio/mp4", samplelessMp4, 1500));

        var mismatchedFragMp4 = BuildFragmentedAudioMp4Bytes("isom", trackId: 1, tfhdTrackId: 99, sampleCount: 4);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("mismatch-frag.m4a", "audio/mp4", mismatchedFragMp4, 1500));

        // Reject WebM with video TrackType=1, 'matroska' DocType, missing Cluster, or non-audio-track Cluster block
        var videoWebm = BuildWebmBytes("webm", trackType: 1, includeClusterAudioBlock: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("video.webm", "audio/webm", videoWebm, 1500));

        var mkvAudio = BuildWebmBytes("matroska", trackType: 2, includeClusterAudioBlock: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("mkv.webm", "audio/webm", mkvAudio, 1500));

        var clusterlessWebm = BuildWebmBytes("webm", trackType: 2, includeClusterAudioBlock: false);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("no-cluster.webm", "audio/webm", clusterlessWebm, 1500));

        var wrongTrackBlockWebm = BuildWebmBytes("webm", trackType: 2, includeClusterAudioBlock: true, blockTrackNumber: 2);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("wrong-track.webm", "audio/webm", wrongTrackBlockWebm, 1500));

        // Reject Ogg with missing BOS flag, Theora video header, or truncated/invalid Opus/Vorbis ID packets
        var nonBosOgg = BuildOggBytes(BuildValidOpusHead(), bos: false);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("nobos.ogg", "audio/ogg", nonBosOgg, 1500));

        var theoraOgg = BuildOggBytes([0x80, .. "theora"u8, 0x03, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], bos: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("theora.ogg", "audio/ogg", theoraOgg, 1500));

        var truncatedOpus = BuildOggBytes([.. "OpusHead"u8, 0x01, 0x01, 0x00, 0x00], bos: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("truncated-opus.ogg", "audio/ogg", truncatedOpus, 1500));

        var truncatedVorbis = BuildOggBytes([0x01, .. "vorbis"u8, 0x00, 0x00, 0x00, 0x00, 0x01, 0x44, 0xAC, 0x00, 0x00, 0x00, 0x00, 0x00], bos: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("truncated-vorbis.ogg", "audio/ogg", truncatedVorbis, 1500));

        var badMappingOpus = BuildOggBytes([.. "OpusHead"u8, 0x01, 0x02, 0x00, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x01], bos: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("bad-map.ogg", "audio/ogg", badMappingOpus, 1500));

        // Reject PE/HTML payload posing as audio
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("fake.m4a", "audio/mp4", "MZ\x90\x00fake"u8.ToArray(), 1500));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("fake.webm", "audio/webm", "<script>alert(1)</script>"u8.ToArray(), 1500));
    }

    [Fact]
    public async Task MediaVerifier_HandlesMdatBeforeMoov_SeekHeadWebm_And_RejectsContainerBombsAndPartialOgg()
    {
        // 1. Faststart valid M4A should still complete in a single 4 KB storage read
        var faststartM4a = BuildIsoBmffAudioBytes("M4A ", "soun", includeSampleEvidence: true);
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

        // R2-16: Deep-nesting & non-trailing zero-size child tests include valid stsz + mdat alongside valid finite-box positive controls
        var shallowNestedMp4 = BuildDeeplyNestedIsoBmff("M4A ", nestLayers: 0, "soun");
        var shallowRes = await VerifyVoiceAsync("shallow.m4a", "audio/mp4", shallowNestedMp4, 1500);
        Assert.Equal("audio/mp4", shallowRes.VerifiedContentType);

        var deepNestedMp4 = BuildDeeplyNestedIsoBmff("M4A ", nestLayers: 7, "soun");
        var deepEx = await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("deep.m4a", "audio/mp4", deepNestedMp4, 1500));
        Assert.Contains("nesting", deepEx.Message, StringComparison.OrdinalIgnoreCase);

        var manyBoxesMp4 = BuildManyFreeBoxesPlusAudioIsoBmff("M4A ", freeBoxCount: 130);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("manyboxes.m4a", "audio/mp4", manyBoxesMp4, 1500));

        var finiteFreeInsideMoovMp4 = BuildFiniteFreeInsideMoov("M4A ");
        var finiteFreeRes = await VerifyVoiceAsync("finitefree.m4a", "audio/mp4", finiteFreeInsideMoovMp4, 1500);
        Assert.Equal("audio/mp4", finiteFreeRes.VerifiedContentType);

        var nonTrailingZeroSizeChildMp4 = BuildNonTrailingZeroSizeInsideMoov("M4A ");
        var zeroEx = await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("zerochild.m4a", "audio/mp4", nonTrailingZeroSizeChildMp4, 1500));
        Assert.Contains("box structure", zeroEx.Message, StringComparison.OrdinalIgnoreCase);

        // R2-1 / R3-2: Large fragmented MP4 passes via bounded root scan, while trailing truncated root header in cached page or trailing video moov fails
        var largeFragMp4 = BuildMultiFragmentAudioMp4Bytes("isom", trackId: 1, fragmentCount: 35, paddingBytesPerMoof: 4096);
        var largeFragRes = await VerifyVoiceAsync("large-frag.m4a", "audio/mp4", largeFragMp4, 5000);
        Assert.Equal("audio/mp4", largeFragRes.VerifiedContentType);

        var truncTrailingMp4 = BuildSmallTruncatedTrailingMp4Bytes("M4A ");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("trunc-trailing.m4a", "audio/mp4", truncTrailingMp4, 1500));

        var trailingVideoMoovMp4 = BuildTrailingVideoMoovAfterAudioMp4("M4A ");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("trailing-vide.m4a", "audio/mp4", trailingVideoMoovMp4, 1500));

        // R2-5 / R3-3 / R4-2 / R4-3: fixed stsz, variable stsz, and compact stz2 validate declared sample bytes against payload
        var fixedStszMp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildFixedStszBox(sampleSize: 64, sampleCount: 4), mdatPayloadBytes: 256);
        Assert.Equal("audio/mp4", (await VerifyVoiceAsync("fixed-stsz.m4a", "audio/mp4", fixedStszMp4, 1500)).VerifiedContentType);

        var oversizedFixedStszMp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildFixedStszBox(sampleSize: 512, sampleCount: 100), mdatPayloadBytes: 4);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("oversized-stsz.m4a", "audio/mp4", oversizedFixedStszMp4, 1500));

        var paddedInsufficientMdatStszMp4 = BuildPaddedFixedStszMp4Bytes("M4A ", sampleSize: 64, sampleCount: 8, freePadBytes: 4096, mdatPayloadBytes: 4);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("padded-short-mdat.m4a", "audio/mp4", paddedInsufficientMdatStszMp4, 1500));

        // R4-2: Unfinished scan hitting box limit after small mdat cannot bypass fixed-size payload check
        var unfinishedShortMdatStszMp4 = BuildPaddedFixedStszMp4Bytes("M4A ", sampleSize: 64, sampleCount: 8, freePadBytes: 1024, mdatPayloadBytes: 4, trailingFreeBoxCount: 130);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("unfinished-short-mdat.m4a", "audio/mp4", unfinishedShortMdatStszMp4, 1500));

        // R4-2/R4-3 hybrid defense: mvex+moof cannot excuse an stsz declaring more bytes than mdat
        var hybridMvexOversizedStsz = BuildHybridMvexOversizedStszMp4Bytes("isom");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("hybrid-oversized-stsz.m4a", "audio/mp4", hybridMvexOversizedStsz, 1500));

        var truncatedStszMp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildTruncatedStszBox(sampleCount: 4));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("trunc-stsz.m4a", "audio/mp4", truncatedStszMp4, 1500));

        // R4-3: variable stsz exceeding mdat or declaring all-zero entries is rejected
        var oversizedVarStszMp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStszBox(sampleCount: 4, entrySize: 256), mdatPayloadBytes: 4);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("oversized-var-stsz.m4a", "audio/mp4", oversizedVarStszMp4, 1500));

        var zeroVarStszMp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStszBox(sampleCount: 3, entrySize: 0), mdatPayloadBytes: 64);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("zero-var-stsz.m4a", "audio/mp4", zeroVarStszMp4, 1500));

        var validStz2Mp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStz2Box(fieldSize: 8, sampleCount: 3, packedBytes: 3), mdatPayloadBytes: 96);
        Assert.Equal("audio/mp4", (await VerifyVoiceAsync("valid-stz2.m4a", "audio/mp4", validStz2Mp4, 1500)).VerifiedContentType);

        // R4-3: 4-bit odd-sampleCount stz2 ignores trailing padding nibble ([0x44, 0x40] -> 4+4+4 = 12 bytes)
        var validOdd4BitStz2Mp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStz2Box(fieldSize: 4, sampleCount: 3, packedBytes: 2, rawEntries: [0x44, 0x4F]), mdatPayloadBytes: 12);
        Assert.Equal("audio/mp4", (await VerifyVoiceAsync("valid-4bit-stz2.m4a", "audio/mp4", validOdd4BitStz2Mp4, 1500)).VerifiedContentType);

        var oversizedStz2Mp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStz2Box(fieldSize: 8, sampleCount: 3, packedBytes: 3), mdatPayloadBytes: 4);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("oversized-stz2.m4a", "audio/mp4", oversizedStz2Mp4, 1500));

        var truncatedStz2Mp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStz2Box(fieldSize: 8, sampleCount: 3, packedBytes: 2));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("trunc-stz2.m4a", "audio/mp4", truncatedStz2Mp4, 1500));

        var invalidFieldStz2Mp4 = BuildIsoBmffWithCustomSampleTable("M4A ", BuildStz2Box(fieldSize: 7, sampleCount: 1, packedBytes: 1));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("badfield-stz2.m4a", "audio/mp4", invalidFieldStz2Mp4, 1500));

        // R2-6 / R3-4 / R4-4 / R4-5 / R4-9: WebM multi-cluster, unknown-sized sibling Clusters, Void inside Cluster + after Cues,
        // and rejection of spoofed Tracks inside SimpleBlock #2 or smuggled video Tracks before indexed Tracks
        var multiClusterWebm = BuildWebmMultiClusterBytes();
        Assert.Equal("audio/webm", (await VerifyVoiceAsync("multicluster.webm", "audio/webm", multiClusterWebm, 2000)).VerifiedContentType);

        var unknownClusterSeekWebm = BuildWebmSeekHeadUnknownClusterTrailingTracks(includeCuesBetweenClusterAndTracks: false);
        Assert.Equal("audio/webm", (await VerifyVoiceAsync("unknown-cluster-seek.webm", "audio/webm", unknownClusterSeekWebm, 2000)).VerifiedContentType);

        var unknownClusterCuesSeekWebm = BuildWebmSeekHeadUnknownClusterTrailingTracks(includeCuesBetweenClusterAndTracks: true);
        Assert.Equal("audio/webm", (await VerifyVoiceAsync("unknown-cluster-cues-seek.webm", "audio/webm", unknownClusterCuesSeekWebm, 2000)).VerifiedContentType);

        var siblingUnknownClustersWebm = BuildWebmSeekHeadUnknownClusterTrailingTracks(
            includeCuesBetweenClusterAndTracks: false,
            prependTimestampOnlyUnknownCluster: true);
        Assert.Equal("audio/webm", (await VerifyVoiceAsync("sibling-unknown-clusters.webm", "audio/webm", siblingUnknownClustersWebm, 2000)).VerifiedContentType);

        var cuesPlusVoidWebm = BuildWebmSeekHeadUnknownClusterTrailingTracks(
            includeCuesBetweenClusterAndTracks: true,
            includeVoidInsideCluster: true,
            includeVoidAfterCues: true);
        Assert.Equal("audio/webm", (await VerifyVoiceAsync("cues-void-tracks.webm", "audio/webm", cuesPlusVoidWebm, 2000)).VerifiedContentType);

        var smuggledVideoBeforeIndexedTracksWebm = BuildWebmSeekHeadUnknownClusterTrailingTracks(
            includeCuesBetweenClusterAndTracks: true,
            injectSmuggledVideoTracksBeforeAudioTracks: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("smuggled-video-before-tracks.webm", "audio/webm", smuggledVideoBeforeIndexedTracksWebm, 2000));

        var spoofedUnknownClusterWebm = BuildMalformedSeekHeadInsideUnknownClusterSimpleBlock2Webm();
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("spoof-unknown-cluster.webm", "audio/webm", spoofedUnknownClusterWebm, 2000));

        // R2-7: Malicious SeekHead pointing Tracks inside an already-scanned Cluster is rejected
        var spoofedClusterSeekWebm = BuildMalformedSeekHeadInsideClusterWebm();
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("spoof-seek.webm", "audio/webm", spoofedClusterSeekWebm, 2000));

        // R2-8 / R3-5 / R3-6 / R3-12: Ogg stream completeness, OpusTags RFC 7845 §5.2 bounds, Vorbis audio packet-type bit, and CRC-32
        var opusCrcVerifyPage = BuildOggBytes(BuildValidOpusHead(), bos: true, serial: 1, pageSeq: 0);
        var actualPageCrc = BinaryPrimitives.ReadUInt32LittleEndian(opusCrcVerifyPage.AsSpan(22, 4));
        Assert.NotEqual(0u, actualPageCrc);
        var zeroedCrcCopy = opusCrcVerifyPage.ToArray();
        zeroedCrcCopy.AsSpan(22, 4).Clear();
        Assert.Equal(ComputeOggCrc32(zeroedCrcCopy), actualPageCrc);

        var paddedOpusTags = ConcatBytes("OpusTags"u8.ToArray(), [0x03, 0x00, 0x00, 0x00], "aos"u8.ToArray(), [0x01, 0x00, 0x00, 0x00], [0x04, 0x00, 0x00, 0x00], "k=v1"u8.ToArray(), new byte[4]);
        Assert.Equal("audio/ogg", (await VerifyVoiceAsync("padded-tags.ogg", "audio/ogg", BuildValidOggOpusStream(opusTags: paddedOpusTags), 1500)).VerifiedContentType);

        var shortOpusTagsOgg = BuildValidOggOpusStream(opusTags: "OpusTags"u8.ToArray());
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("short-opustags.ogg", "audio/ogg", shortOpusTagsOgg, 1500));

        var badVendorTags = ConcatBytes("OpusTags"u8.ToArray(), [0x50, 0x00, 0x00, 0x00], new byte[4]);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("bad-vendor-tags.ogg", "audio/ogg", BuildValidOggOpusStream(opusTags: badVendorTags), 1500));

        var badCommentLenTags = ConcatBytes("OpusTags"u8.ToArray(), new byte[4], [0x01, 0x00, 0x00, 0x00], [0x99, 0x00, 0x00, 0x00]);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("bad-comment-tags.ogg", "audio/ogg", BuildValidOggOpusStream(opusTags: badCommentLenTags), 1500));

        var extraVorbisHeaderOgg = BuildValidOggVorbisStream(audioPacket: [0x05, 0x76, 0x6F, 0x72]);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("extra-vorbis-hdr.ogg", "audio/ogg", extraVorbisHeaderOgg, 1500));

        var continuedPage3Audio = new byte[255];
        continuedPage3Audio[0] = 0x00; // Vorbis audio packet type bit = 0 on first byte of packet
        byte[] continuedPage4Audio = [0xFF, 0x01, 0x02, 0x03]; // LSB = 1 on continuation page must not overwrite packet first byte!
        var multiPageVorbisAudio = ConcatBytes(
            BuildOggBytes(BuildValidVorbisHead(), bos: true, serial: 1, pageSeq: 0),
            BuildOggBytes([0x03, .. "vorbis"u8, 0, 0, 0, 0, 0, 0, 0, 0, 0x01], bos: false, serial: 1, pageSeq: 1),
            BuildOggBytes([0x05, .. "vorbis"u8, 0x01, 0x02, 0x03], bos: false, serial: 1, pageSeq: 2),
            // Hand-craft page 3 with single segment=255 (unterminated continued packet) + page 4 with continuation=0x01, eos=0x04
            [.. "OggS"u8, 0x00, 0x00, 0,0,0,0,0,0,0,0, 1,0,0,0, 3,0,0,0, 0,0,0,0, 1, 255, .. continuedPage3Audio],
            BuildOggPage(continuedPage4Audio, headerTypeFlag: 0x05, serial: 1, pageSeq: 4, granulePosition: 2048));
        Assert.Equal("audio/ogg", (await VerifyVoiceAsync("multipage-vorbis.ogg", "audio/ogg", multiPageVorbisAudio, 1500)).VerifiedContentType);

        var headerOnlyOpus = BuildOggBytes(BuildValidOpusHead(), bos: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("header-only.ogg", "audio/ogg", headerOnlyOpus, 1500));

        var muxBeforeAudioOgg = ConcatBytes(
            BuildOggBytes(BuildValidOpusHead(), bos: true, serial: 1, pageSeq: 0),
            BuildOggBytes(BuildValidOpusHead(), bos: true, serial: 2, pageSeq: 0),
            BuildOggBytes([.. "OpusTags"u8, 0,0,0,0, 0,0,0,0], bos: false, serial: 1, pageSeq: 1),
            BuildOggBytes([0xFC, 0xFF, 0xFE], bos: false, serial: 1, pageSeq: 2));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("mux.ogg", "audio/ogg", muxBeforeAudioOgg, 1500));

        // R2-9: Opus mappingFamily=0 with 3 channels, or mappingFamily=1 with out-of-range channel map index, is rejected
        var invalidFam0Opus = BuildValidOggOpusStream(BuildValidOpusHead(channels: 3, mappingFamily: 0));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("fam0-3ch.ogg", "audio/ogg", invalidFam0Opus, 1500));

        var invalidMapIdxOpus = BuildValidOggOpusStream(
            BuildValidOpusHead(channels: 2, mappingFamily: 1, streamCount: 1, coupledCount: 0, channelMap: [0, 5]));
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("bad-map.ogg", "audio/ogg", invalidMapIdxOpus, 1500));

        // 6. Reject header-only EBML DocType=webm stub without Segment / Tracks
        var webmStubOnly = BuildWebmEbmlHeaderOnly("webm");
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("stub.webm", "audio/webm", webmStubOnly, 1500));

        // 7. Void (>8 KB) prefixed WebM: reject TrackType=1 (video), accept TrackType=2 (audio) with Cluster audio block
        var paddedVideoWebm = BuildPaddedUnknownSegmentWebm("webm", voidPaddingBytes: 8 * 1024, trackType: 1, appendUnknownCluster: true);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("padded-vid.webm", "audio/webm", paddedVideoWebm, 1500));

        var paddedAudioWebm = BuildPaddedUnknownSegmentWebm("webm", voidPaddingBytes: 8 * 1024, trackType: 2, appendUnknownCluster: true);
        var (paddedWebmRes, _) = await VerifyVoiceWithStatsAsync("padded-aud.webm", "audio/webm", paddedAudioWebm, 3000);
        Assert.Equal("audio/webm", paddedWebmRes.VerifiedContentType);

        // 7b. SeekHead indexing Tracks located AFTER first Cluster must be resolved cleanly within budget
        var seekHeadAudioWebm = BuildSeekHeadTrailingTracksWebm(trackType: 2);
        var (seekHeadRes, _) = await VerifyVoiceWithStatsAsync("seekhead-aud.webm", "audio/webm", seekHeadAudioWebm, 3000);
        Assert.Equal("audio/webm", seekHeadRes.VerifiedContentType);

        var seekHeadVideoWebm = BuildSeekHeadTrailingTracksWebm(trackType: 1);
        await Assert.ThrowsAsync<DomainException>(() => VerifyVoiceAsync("seekhead-vid.webm", "audio/webm", seekHeadVideoWebm, 3000));

        // 8. Reject Ogg whose first segment is 7-byte "OpusHea" even if segment 2 begins with 'd'
        var splitOpusSpoof = BuildTwoSegmentOgg([.. "OpusHea"u8], [.. "d\x01\x01\x00\x00\x80\xBB\x00\x00\x00\x00\x00"u8]);
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

    internal static byte[] BuildValidOpusHead(byte channels = 1, byte mappingFamily = 0, byte streamCount = 1, byte coupledCount = 0, byte[]? channelMap = null)
    {
        using var ms = new MemoryStream();
        ms.Write("OpusHead"u8);
        ms.WriteByte(0x01); // version 1 (major nibble 0)
        ms.WriteByte(channels);
        ms.Write([0x38, 0x01]); // pre-skip LE
        ms.Write([0x80, 0xBB, 0x00, 0x00]); // 48000 Hz LE
        ms.Write([0x00, 0x00]); // output gain
        ms.WriteByte(mappingFamily);
        if (mappingFamily != 0)
        {
            ms.WriteByte(streamCount);
            ms.WriteByte(coupledCount);
            for (byte i = 0; i < channels; i++)
            {
                ms.WriteByte(channelMap is not null && i < channelMap.Length ? channelMap[i] : i);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] BuildValidVorbisHead()
    {
        var pkt = new byte[30];
        "\x01vorbis"u8.CopyTo(pkt.AsSpan(0, 7));
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(7, 4), 0u); // version
        pkt[11] = 1; // channels
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(12, 4), 48000u); // sample rate
        pkt[28] = 0xB8; // blocksize0=8 (256), blocksize1=11 (2048)
        pkt[29] = 0x01; // framing flag
        return pkt;
    }

    internal static byte[] BuildIsoBmffAudioBytes(string majorBrand, string handlerType, bool includeSampleEvidence = true)
    {
        var tkhd = BuildTkhdBox(trackId: 1);
        var hdlr = BuildHdlrBox(handlerType);
        var mdiaChildren = includeSampleEvidence
            ? ConcatBytes(hdlr, WrapIsoBox("minf", WrapIsoBox("stbl", BuildStszBox(sampleCount: 1))))
            : hdlr;
        var trak = WrapIsoBox("trak", ConcatBytes(tkhd, WrapIsoBox("mdia", mdiaChildren)));
        var moov = WrapIsoBox("moov", trak);

        if (!includeSampleEvidence)
        {
            return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov);
        }

        var mdat = WrapIsoBox("mdat", [0xAA, 0xBB, 0xCC, 0xDD]);
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov, mdat);
    }

    internal static byte[] BuildFragmentedAudioMp4Bytes(string majorBrand, uint trackId, uint tfhdTrackId, uint sampleCount)
    {
        var trak = WrapIsoBox("trak", ConcatBytes(
            BuildTkhdBox(trackId),
            WrapIsoBox("mdia", BuildHdlrBox("soun"))));
        var mvex = WrapIsoBox("mvex", []);
        var moov = WrapIsoBox("moov", ConcatBytes(trak, mvex));

        var tfhdInner = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tfhdInner.AsSpan(4, 4), tfhdTrackId);
        var trunInner = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(trunInner.AsSpan(4, 4), sampleCount);
        var moof = WrapIsoBox("moof", WrapIsoBox("traf", ConcatBytes(
            WrapIsoBox("tfhd", tfhdInner),
            WrapIsoBox("trun", trunInner))));
        var mdat = WrapIsoBox("mdat", [0x01, 0x02, 0x03, 0x04]);

        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov, moof, mdat);
    }

    internal static byte[] BuildWebmBytes(string docType, int trackType, bool includeClusterAudioBlock = true, int blockTrackNumber = 1)
    {
        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly(docType));

        var tracks = BuildWebmTracksElement(trackNumber: 1, trackType: trackType);
        var cluster = includeClusterAudioBlock
            ? BuildWebmClusterWithSimpleBlock(blockTrackNumber)
            : [];

        var segPayloadLen = tracks.Length + cluster.Length;
        ms.Write([0x18, 0x53, 0x80, 0x67]);
        ms.Write(EncodeEbmlUintSize4Byte(segPayloadLen));
        ms.Write(tracks);
        ms.Write(cluster);
        return ms.ToArray();
    }

    private static readonly uint[] OggCrcTable = BuildOggCrcTable();

    private static uint[] BuildOggCrcTable()
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var r = (uint)i << 24;
            for (var j = 0; j < 8; j++)
            {
                r = (r & 0x8000_0000u) != 0 ? (r << 1) ^ 0x04C1_1DB7u : (r << 1);
            }
            table[i] = r;
        }
        return table;
    }

    internal static uint ComputeOggCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc = (crc << 8) ^ OggCrcTable[((crc >> 24) ^ b) & 0xFF];
        }
        return crc;
    }

    internal static byte[] BuildOggPage(
        ReadOnlySpan<byte> payload,
        byte headerTypeFlag,
        uint serial = 1,
        uint pageSeq = 0,
        ulong granulePosition = 0)
    {
        var fullSegments = payload.Length / 255;
        var remainder = payload.Length % 255;
        var segmentCount = fullSegments + 1;
        if (segmentCount > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Single Ogg page cannot exceed 255 segments.");
        }

        var page = new byte[27 + segmentCount + payload.Length];
        "OggS"u8.CopyTo(page.AsSpan(0, 4));
        page[4] = 0x00; // stream_structure_version
        page[5] = headerTypeFlag;
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(6, 8), granulePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), pageSeq);
        // bytes 22..25 left zeroed for CRC computation
        page[26] = (byte)segmentCount;
        for (var i = 0; i < fullSegments; i++)
        {
            page[27 + i] = 255;
        }
        page[27 + fullSegments] = (byte)remainder;
        payload.CopyTo(page.AsSpan(27 + segmentCount));

        var crc = ComputeOggCrc32(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), crc);
        return page;
    }

    internal static byte[] BuildOggBytes(
        byte[] identificationPacket,
        bool bos,
        uint serial = 0,
        uint pageSeq = 0,
        bool eos = false,
        ulong granulePosition = 0)
    {
        byte flags = 0;
        if (bos) flags |= 0x02;
        if (eos) flags |= 0x04;
        return BuildOggPage(identificationPacket, flags, serial, pageSeq, granulePosition);
    }

    internal static byte[] BuildValidOggOpusStream(byte[]? opusHead = null, byte[]? opusTags = null)
    {
        var head = opusHead ?? BuildValidOpusHead();
        // RFC 7845 §5.2 compliant OpusTags (vendor + 0 comments) unless overridden
        var tags = opusTags ?? ConcatBytes("OpusTags"u8.ToArray(), [0x04, 0x00, 0x00, 0x00], "libo"u8.ToArray(), new byte[4]);
        // RFC 6716 decoder-valid 20ms CELT/SILK silent Opus frame (config 31, stereo=0, code=0)
        byte[] audioPkt = [0xFC, 0xFF, 0xFE];
        return ConcatBytes(
            BuildOggBytes(head, bos: true, serial: 1, pageSeq: 0, granulePosition: 0),
            BuildOggBytes(tags, bos: false, serial: 1, pageSeq: 1, granulePosition: 0),
            BuildOggBytes(audioPkt, bos: false, serial: 1, pageSeq: 2, eos: true, granulePosition: 960));
    }

    internal static byte[] BuildValidOggVorbisStream(byte[]? vorbisHead = null, byte[]? audioPacket = null)
    {
        var head = vorbisHead ?? BuildValidVorbisHead();
        // Vorbis comment header includes framing bit (0x01) per Vorbis I §5.2
        byte[] comment = [0x03, .. "vorbis"u8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01];
        byte[] setup = [0x05, .. "vorbis"u8, 0x01, 0x02, 0x03, 0x04];
        byte[] audioPkt = audioPacket ?? [0x00, 0x42, 0x13, 0x37];
        return ConcatBytes(
            BuildOggBytes(head, bos: true, serial: 1, pageSeq: 0, granulePosition: 0),
            BuildOggBytes(comment, bos: false, serial: 1, pageSeq: 1, granulePosition: 0),
            BuildOggBytes(setup, bos: false, serial: 1, pageSeq: 2, granulePosition: 0),
            BuildOggBytes(audioPkt, bos: false, serial: 1, pageSeq: 3, eos: true, granulePosition: 1024));
    }

    internal static byte[] ConcatBytes(params byte[][] parts)
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

    private static byte[] BuildTkhdBox(uint trackId)
    {
        var inner = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(12, 4), trackId);
        return WrapIsoBox("tkhd", inner);
    }

    private static byte[] BuildHdlrBox(string handlerType)
    {
        var inner = new byte[16];
        Encoding.ASCII.GetBytes(handlerType, inner.AsSpan(8, 4));
        return WrapIsoBox("hdlr", inner);
    }

    private static byte[] BuildStszBox(uint sampleCount, uint entrySize = 4)
    {
        var inner = new byte[12 + checked((int)sampleCount) * 4];
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(8, 4), sampleCount);
        for (var i = 0; i < (int)sampleCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(12 + i * 4, 4), entrySize);
        }
        return WrapIsoBox("stsz", inner);
    }

    private static byte[] BuildFixedStszBox(uint sampleSize, uint sampleCount)
    {
        var inner = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(4, 4), sampleSize);
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(8, 4), sampleCount);
        return WrapIsoBox("stsz", inner);
    }

    private static byte[] BuildTruncatedStszBox(uint sampleCount)
    {
        var inner = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(8, 4), sampleCount);
        return WrapIsoBox("stsz", inner);
    }

    private static byte[] BuildStz2Box(byte fieldSize, uint sampleCount, int packedBytes, byte[]? rawEntries = null)
    {
        var inner = new byte[12 + packedBytes];
        inner[7] = fieldSize;
        BinaryPrimitives.WriteUInt32BigEndian(inner.AsSpan(8, 4), sampleCount);
        if (rawEntries is not null)
        {
            rawEntries.AsSpan(0, Math.Min(packedBytes, rawEntries.Length)).CopyTo(inner.AsSpan(12));
        }
        else
        {
            for (var i = 0; i < packedBytes; i++)
            {
                inner[12 + i] = 0x20;
            }
        }
        return WrapIsoBox("stz2", inner);
    }

    private static byte[] BuildIsoBmffWithCustomSampleTable(string majorBrand, byte[] sampleSizeBox, int mdatPayloadBytes = 4)
    {
        var hdlr = BuildHdlrBox("soun");
        var stbl = WrapIsoBox("stbl", sampleSizeBox);
        var minf = WrapIsoBox("minf", stbl);
        var mdia = WrapIsoBox("mdia", ConcatBytes(hdlr, minf));
        var trak = WrapIsoBox("trak", mdia);
        var moov = WrapIsoBox("moov", trak);
        var mdat = WrapIsoBox("mdat", new byte[mdatPayloadBytes]);
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov, mdat);
    }

    private static byte[] BuildSmallTruncatedTrailingMp4Bytes(string majorBrand) =>
        ConcatBytes(BuildIsoBmffAudioBytes(majorBrand, "soun", includeSampleEvidence: true), [0x00, 0x00, 0x00, 0x10]);

    private static byte[] BuildTrailingVideoMoovAfterAudioMp4(string majorBrand)
    {
        var valid = BuildIsoBmffAudioBytes(majorBrand, "soun", includeSampleEvidence: true);
        var trailingVideoMoov = WrapIsoBox("moov", WrapIsoBox("trak", ConcatBytes(BuildTkhdBox(2), WrapIsoBox("mdia", BuildHdlrBox("vide")))));
        return ConcatBytes(valid, trailingVideoMoov);
    }

    private static byte[] BuildPaddedFixedStszMp4Bytes(string majorBrand, uint sampleSize, uint sampleCount, int freePadBytes, int mdatPayloadBytes, int trailingFreeBoxCount = 0)
    {
        var hdlr = BuildHdlrBox("soun");
        var stbl = WrapIsoBox("stbl", BuildFixedStszBox(sampleSize, sampleCount));
        var moov = WrapIsoBox("moov", WrapIsoBox("trak", WrapIsoBox("mdia", ConcatBytes(hdlr, WrapIsoBox("minf", stbl)))));
        var parts = new List<byte[]>
        {
            BuildIsoBmffFtypOnly(majorBrand),
            moov,
            WrapIsoBox("free", new byte[freePadBytes]),
            WrapIsoBox("mdat", new byte[mdatPayloadBytes])
        };
        var tinyFree = WrapIsoBox("free", []);
        for (var i = 0; i < trailingFreeBoxCount; i++)
        {
            parts.Add(tinyFree);
        }
        return ConcatBytes([.. parts]);
    }

    private static byte[] BuildHybridMvexOversizedStszMp4Bytes(string majorBrand)
    {
        var trak = WrapIsoBox("trak", ConcatBytes(
            BuildTkhdBox(1),
            WrapIsoBox("mdia", ConcatBytes(
                BuildHdlrBox("soun"),
                WrapIsoBox("minf", WrapIsoBox("stbl", BuildFixedStszBox(sampleSize: 64, sampleCount: 16)))))));
        var mvex = WrapIsoBox("mvex", WrapIsoBox("trex", new byte[24]));
        var moov = WrapIsoBox("moov", ConcatBytes(trak, mvex));
        var tfhdInner = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tfhdInner.AsSpan(4, 4), 1u);
        var trunInner = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(trunInner.AsSpan(4, 4), 1u);
        var moof = WrapIsoBox("moof", WrapIsoBox("traf", ConcatBytes(WrapIsoBox("tfhd", tfhdInner), WrapIsoBox("trun", trunInner))));
        // Large free box ensures 1024 <= TotalLength, while mdat is only 4 bytes (< 1024 declared in stsz)
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov, WrapIsoBox("free", new byte[2048]), moof, WrapIsoBox("mdat", [0x01, 0x02, 0x03, 0x04]));
    }

    private static byte[] BuildWebmCuesForCluster(uint trackNumber, uint clusterOffsetInSegment)
    {
        var posBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(posBytes, clusterOffsetInSegment);
        byte[] cueTrack = [0xF7, 0x81, (byte)trackNumber];
        byte[] cueClusterPos = [0xF1, 0x84, .. posBytes];
        byte[] trackPositionsBody = [.. cueTrack, .. cueClusterPos];
        byte[] trackPositions = [0xB7, (byte)(0x80 | trackPositionsBody.Length), .. trackPositionsBody];
        byte[] cueTime = [0xB3, 0x81, 0x00];
        byte[] cuePointBody = [.. cueTime, .. trackPositions];
        byte[] cuePoint = [0xBB, (byte)(0x80 | cuePointBody.Length), .. cuePointBody];
        return [0x1C, 0x53, 0xBB, 0x6B, (byte)(0x80 | cuePoint.Length), .. cuePoint];
    }

    private static byte[] BuildWebmSeekHeadUnknownClusterTrailingTracks(
        bool includeCuesBetweenClusterAndTracks,
        bool includeVoidInsideCluster = false,
        bool includeVoidAfterCues = false,
        bool prependTimestampOnlyUnknownCluster = false,
        bool injectSmuggledVideoTracksBeforeAudioTracks = false)
    {
        byte[] seekIdEl = [0x53, 0xAB, 0x84, 0x16, 0x54, 0xAE, 0x6B];
        byte[] seekPosElHeader = [0x53, 0xAC, 0x84];
        var seekBodyLen = seekIdEl.Length + seekPosElHeader.Length + 4;
        var seekElLen = 2 + 1 + seekBodyLen;
        var seekHeadTotalLen = 4 + 1 + seekElLen;

        byte[] leadingUnknownCluster = prependTimestampOnlyUnknownCluster
            ? [0x1F, 0x43, 0xB6, 0x75, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xE7, 0x81, 0x00]
            : [];
        byte[] cluster = BuildWebmClusterWithSimpleBlock(trackNumber: 1, unknownSize: true);
        if (includeVoidInsideCluster)
        {
            // Global Void inside Cluster (0xEC, 2 bytes) followed by another valid SimpleBlock
            byte[] innerVoid = [0xEC, 0x82, 0x00, 0x00];
            cluster = ConcatBytes(cluster, innerVoid);
        }

        var audioClusterOffsetInSegment = (uint)(seekHeadTotalLen + leadingUnknownCluster.Length);
        byte[] cues = includeCuesBetweenClusterAndTracks
            ? BuildWebmCuesForCluster(trackNumber: 1, audioClusterOffsetInSegment)
            : [];
        byte[] voidAfterCues = includeVoidAfterCues ? [0xEC, 0x84, 0x00, 0x00, 0x00, 0x00] : [];
        byte[] smuggledVideoTracks = injectSmuggledVideoTracksBeforeAudioTracks
            ? BuildWebmTracksElement(trackNumber: 2, trackType: 1)
            : [];
        var tracks = BuildWebmTracksElement(trackNumber: 1, trackType: 2);

        var tracksOffsetInSegment = (uint)(seekHeadTotalLen + leadingUnknownCluster.Length + cluster.Length + cues.Length + voidAfterCues.Length + smuggledVideoTracks.Length);
        var posBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(posBytes, tracksOffsetInSegment);

        byte[] seekBody = [.. seekIdEl, .. seekPosElHeader, .. posBytes];
        byte[] seekEl = [0x4D, 0xBB, (byte)(0x80 | seekBody.Length), .. seekBody];
        byte[] seekHead = [0x11, 0x4D, 0x9B, 0x74, (byte)(0x80 | seekEl.Length), .. seekEl];

        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly("webm"));
        ms.Write([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        ms.Write(seekHead);
        ms.Write(leadingUnknownCluster);
        ms.Write(cluster);
        ms.Write(cues);
        ms.Write(voidAfterCues);
        ms.Write(smuggledVideoTracks);
        ms.Write(tracks);
        return ms.ToArray();
    }

    private static byte[] BuildMalformedSeekHeadInsideUnknownClusterSimpleBlock2Webm()
    {
        // Unknown-sized Cluster containing valid SimpleBlock #1 followed by SimpleBlock #2 embedding fake Tracks
        byte[] sb1Payload = [0x81, 0x00, 0x00, 0x80, 0xAA, 0xBB];
        byte[] sb1 = [0xA3, (byte)(0x80 | sb1Payload.Length), .. sb1Payload];
        var spoofedTracks = BuildWebmTracksElement(trackNumber: 1, trackType: 2);
        byte[] sb2 = [0xA3, .. EncodeEbmlUintSize4Byte(4 + spoofedTracks.Length), 0x81, 0x00, 0x00, 0x80, .. spoofedTracks];
        // Unknown-sized Cluster header is 12 bytes (4B ID + 8B unknown VINT)
        byte[] clusterUnknown = [0x1F, 0x43, 0xB6, 0x75, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. sb1, .. sb2];

        byte[] seekIdEl = [0x53, 0xAB, 0x84, 0x16, 0x54, 0xAE, 0x6B];
        byte[] seekPosElHeader = [0x53, 0xAC, 0x84];
        var seekBodyLen = seekIdEl.Length + seekPosElHeader.Length + 4;
        var seekElLen = 2 + 1 + seekBodyLen;
        var seekHeadTotalLen = 4 + 1 + seekElLen;

        // Point directly inside SimpleBlock #2 payload (12B clusterHdr + sb1.Length + 9B sb2Hdr)
        var spoofedOffsetInSegment = (uint)(seekHeadTotalLen + 12 + sb1.Length + 9);
        var posBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(posBytes, spoofedOffsetInSegment);

        byte[] seekBody = [.. seekIdEl, .. seekPosElHeader, .. posBytes];
        byte[] seekEl = [0x4D, 0xBB, (byte)(0x80 | seekBody.Length), .. seekBody];
        byte[] seekHead = [0x11, 0x4D, 0x9B, 0x74, (byte)(0x80 | seekEl.Length), .. seekEl];

        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly("webm"));
        ms.Write([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        ms.Write(seekHead);
        ms.Write(clusterUnknown);
        return ms.ToArray();
    }

    private static byte[] BuildMultiFragmentAudioMp4Bytes(string majorBrand, uint trackId, int fragmentCount, int paddingBytesPerMoof)
    {
        var ftyp = BuildIsoBmffFtypOnly(majorBrand);

        var tkhdPayload = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload.AsSpan(12, 4), trackId);
        var tkhd = WrapIsoBox("tkhd", tkhdPayload);
        var mdia = WrapIsoBox("mdia", BuildHdlrBox("soun"));
        var trak = WrapIsoBox("trak", ConcatBytes(tkhd, mdia));
        var mvex = WrapIsoBox("mvex", WrapIsoBox("trex", new byte[24]));
        var moov = WrapIsoBox("moov", ConcatBytes(trak, mvex));

        var chunks = new List<byte[]> { ftyp, moov };
        for (var i = 0; i < fragmentCount; i++)
        {
            var tfhdPayload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(tfhdPayload.AsSpan(4, 4), trackId);
            var tfhd = WrapIsoBox("tfhd", tfhdPayload);
            var trunPayload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(trunPayload.AsSpan(4, 4), 2);
            var trun = WrapIsoBox("trun", trunPayload);
            var traf = WrapIsoBox("traf", ConcatBytes(tfhd, trun));
            var pad = WrapIsoBox("free", new byte[paddingBytesPerMoof]);
            chunks.Add(WrapIsoBox("moof", ConcatBytes(pad, traf)));
            chunks.Add(WrapIsoBox("mdat", [0xAA, 0xBB, 0xCC, 0xDD]));
        }

        return ConcatBytes([.. chunks]);
    }

    private static byte[] BuildMdatBeforeMoovIsoBmff(string majorBrand, int mdatPayloadBytes, string handlerType)
    {
        var ftyp = BuildIsoBmffFtypOnly(majorBrand);
        var mdat = WrapIsoBox("mdat", new byte[mdatPayloadBytes]);
        var mdia = WrapIsoBox("mdia", ConcatBytes(
            BuildHdlrBox(handlerType),
            WrapIsoBox("minf", WrapIsoBox("stbl", BuildStszBox(sampleCount: 1)))));
        var moov = WrapIsoBox("moov", WrapIsoBox("trak", ConcatBytes(BuildTkhdBox(1), mdia)));
        return ConcatBytes(ftyp, mdat, moov);
    }

    private static byte[] BuildDeeplyNestedIsoBmff(string majorBrand, int nestLayers, string handlerType)
    {
        var cur = WrapIsoBox("trak", WrapIsoBox("mdia", ConcatBytes(
            BuildHdlrBox(handlerType),
            WrapIsoBox("minf", WrapIsoBox("stbl", BuildStszBox(sampleCount: 1))))));
        for (var i = 0; i < nestLayers; i++)
        {
            cur = WrapIsoBox("moov", cur);
        }
        return ConcatBytes(
            BuildIsoBmffFtypOnly(majorBrand),
            WrapIsoBox("moov", cur),
            WrapIsoBox("mdat", [0x01, 0x02, 0x03, 0x04]));
    }

    private static byte[] BuildManyFreeBoxesPlusAudioIsoBmff(string majorBrand, int freeBoxCount)
    {
        var parts = new List<byte[]> { BuildIsoBmffFtypOnly(majorBrand) };
        var freeBox = WrapIsoBox("free", []);
        for (var i = 0; i < freeBoxCount; i++)
        {
            parts.Add(freeBox);
        }
        parts.Add(WrapIsoBox("moov", WrapIsoBox("trak", ConcatBytes(
            BuildTkhdBox(1),
            WrapIsoBox("mdia", ConcatBytes(BuildHdlrBox("soun"), WrapIsoBox("minf", WrapIsoBox("stbl", BuildStszBox(1)))))))));
        parts.Add(WrapIsoBox("mdat", [0x01, 0x02]));
        return ConcatBytes([.. parts]);
    }

    private static byte[] BuildFiniteFreeInsideMoov(string majorBrand)
    {
        var finiteFree = WrapIsoBox("free", []);
        var validTrak = WrapIsoBox("trak", ConcatBytes(
            BuildTkhdBox(1),
            WrapIsoBox("mdia", ConcatBytes(BuildHdlrBox("soun"), WrapIsoBox("minf", WrapIsoBox("stbl", BuildStszBox(1)))))));
        var moov = WrapIsoBox("moov", ConcatBytes(finiteFree, validTrak));
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov, WrapIsoBox("mdat", [0x01, 0x02, 0x03, 0x04]));
    }

    private static byte[] BuildNonTrailingZeroSizeInsideMoov(string majorBrand)
    {
        var zeroFree = new byte[8];
        "free"u8.CopyTo(zeroFree.AsSpan(4, 4));
        var validTrak = WrapIsoBox("trak", ConcatBytes(
            BuildTkhdBox(1),
            WrapIsoBox("mdia", ConcatBytes(BuildHdlrBox("soun"), WrapIsoBox("minf", WrapIsoBox("stbl", BuildStszBox(1)))))));
        var moov = WrapIsoBox("moov", ConcatBytes(zeroFree, validTrak));
        return ConcatBytes(BuildIsoBmffFtypOnly(majorBrand), moov, WrapIsoBox("mdat", [0x01, 0x02, 0x03, 0x04]));
    }

    private static byte[] BuildWebmMultiClusterBytes()
    {
        var tracks = BuildWebmTracksElement(trackNumber: 1, trackType: 2);
        // Cluster 1: Timestamp (0xE7) only, no SimpleBlock
        byte[] cluster1 = [0x1F, 0x43, 0xB6, 0x75, 0x83, 0xE7, 0x81, 0x00];
        var cluster2 = BuildWebmClusterWithSimpleBlock(trackNumber: 1, unknownSize: false);
        var segPayloadLen = tracks.Length + cluster1.Length + cluster2.Length;
        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly("webm"));
        ms.Write([0x18, 0x53, 0x80, 0x67]);
        ms.Write(EncodeEbmlUintSize4Byte(segPayloadLen));
        ms.Write(tracks);
        ms.Write(cluster1);
        ms.Write(cluster2);
        return ms.ToArray();
    }

    private static byte[] BuildMalformedSeekHeadInsideClusterWebm()
    {
        var spoofedTracks = BuildWebmTracksElement(trackNumber: 1, trackType: 2);
        // SimpleBlock payload: 4-byte track/timecode/flags header + embedded spoofed Tracks bytes
        var blockBodyLen = 4 + spoofedTracks.Length;
        byte[] simpleBlock = [0xA3, .. EncodeEbmlUintSize4Byte(blockBodyLen), 0x81, 0x00, 0x00, 0x80, .. spoofedTracks];
        byte[] cluster = [0x1F, 0x43, 0xB6, 0x75, .. EncodeEbmlUintSize4Byte(simpleBlock.Length), .. simpleBlock];

        // Compute SeekHead fixed lengths:
        byte[] seekIdEl = [0x53, 0xAB, 0x84, 0x16, 0x54, 0xAE, 0x6B];
        byte[] seekPosElHeader = [0x53, 0xAC, 0x84];
        var seekBodyLen = seekIdEl.Length + seekPosElHeader.Length + 4;
        var seekElLen = 2 + 1 + seekBodyLen;
        var seekHeadTotalLen = 4 + 1 + seekElLen;

        // Point SeekHead Tracks offset directly to spoofedTracks embedded inside Cluster/SimpleBlock (8B Cluster hdr + 9B SimpleBlock hdr)
        var spoofedOffsetInSegment = (uint)(seekHeadTotalLen + 8 + 9);
        var posBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(posBytes, spoofedOffsetInSegment);

        byte[] seekBody = [.. seekIdEl, .. seekPosElHeader, .. posBytes];
        byte[] seekEl = [0x4D, 0xBB, (byte)(0x80 | seekBody.Length), .. seekBody];
        byte[] seekHead = [0x11, 0x4D, 0x9B, 0x74, (byte)(0x80 | seekEl.Length), .. seekEl];

        var segPayloadLen = seekHead.Length + cluster.Length;
        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly("webm"));
        ms.Write([0x18, 0x53, 0x80, 0x67]);
        ms.Write(EncodeEbmlUintSize4Byte(segPayloadLen));
        ms.Write(seekHead);
        ms.Write(cluster);
        return ms.ToArray();
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

    private static byte[] BuildWebmTracksElement(int trackNumber, int trackType)
    {
        byte[] trackNumEl = [0xD7, 0x81, (byte)trackNumber];
        byte[] trackTypeEl = [0x83, 0x81, (byte)trackType];
        byte[] entryBody = [.. trackNumEl, .. trackTypeEl];
        byte[] trackEntry = [0xAE, (byte)(0x80 | entryBody.Length), .. entryBody];
        return [0x16, 0x54, 0xAE, 0x6B, (byte)(0x80 | trackEntry.Length), .. trackEntry];
    }

    private static byte[] BuildWebmClusterWithSimpleBlock(int trackNumber, bool unknownSize = false)
    {
        // RFC 9559 Cluster: mandatory Timestamp (0xE7 = 0) + SimpleBlock (0xA3: track VINT + 2B timecode + 1B flags + 2B payload)
        byte[] timestampEl = [0xE7, 0x81, 0x00];
        byte[] blockPayload = [(byte)(0x80 | trackNumber), 0x00, 0x00, 0x80, 0xAA, 0xBB];
        byte[] simpleBlock = [0xA3, (byte)(0x80 | blockPayload.Length), .. blockPayload];
        byte[] clusterBody = [.. timestampEl, .. simpleBlock];
        if (unknownSize)
        {
            return [0x1F, 0x43, 0xB6, 0x75, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. clusterBody];
        }
        return [0x1F, 0x43, 0xB6, 0x75, (byte)(0x80 | clusterBody.Length), .. clusterBody];
    }

    private static byte[] BuildPaddedUnknownSegmentWebm(string docType, int voidPaddingBytes, int trackType, bool appendUnknownCluster)
    {
        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly(docType));
        // Unknown-size Segment: 18 53 80 67 + 8-byte VINT all-1s
        ms.Write([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        // Void element (0xEC) with 4-byte VINT size pushing Tracks past 8 KB
        ms.WriteByte(0xEC);
        ms.Write(EncodeEbmlUintSize4Byte(voidPaddingBytes));
        ms.Write(new byte[voidPaddingBytes]);
        ms.Write(BuildWebmTracksElement(trackNumber: 1, trackType: trackType));
        if (appendUnknownCluster)
        {
            ms.Write(BuildWebmClusterWithSimpleBlock(trackNumber: 1, unknownSize: true));
            ms.Write(new byte[64]);
        }
        return ms.ToArray();
    }

    private static byte[] BuildSeekHeadTrailingTracksWebm(int trackType)
    {
        var cluster = BuildWebmClusterWithSimpleBlock(trackNumber: 1, unknownSize: false);
        var tracks = BuildWebmTracksElement(trackNumber: 1, trackType: trackType);

        // Build Seek entry pointing to Tracks after SeekHead + Cluster:
        // SeekID (53 AB) = 16 54 AE 6B (4 bytes)
        byte[] seekIdEl = [0x53, 0xAB, 0x84, 0x16, 0x54, 0xAE, 0x6B];
        // SeekPosition (53 AC) = 4-byte uint32 BE (fixed length so SeekHead size is known)
        byte[] seekPosElHeader = [0x53, 0xAC, 0x84];
        var seekBodyLen = seekIdEl.Length + seekPosElHeader.Length + 4;
        var seekElLen = 2 + 1 + seekBodyLen; // 4D BB <1B size> + body
        var seekHeadTotalLen = 4 + 1 + seekElLen; // 11 4D 9B 74 <1B size> + Seek

        var tracksOffsetInSegment = (uint)(seekHeadTotalLen + cluster.Length);
        var posBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(posBytes, tracksOffsetInSegment);

        byte[] seekBody = [.. seekIdEl, .. seekPosElHeader, .. posBytes];
        byte[] seekEl = [0x4D, 0xBB, (byte)(0x80 | seekBody.Length), .. seekBody];
        byte[] seekHead = [0x11, 0x4D, 0x9B, 0x74, (byte)(0x80 | seekEl.Length), .. seekEl];

        var segPayloadLen = seekHead.Length + cluster.Length + tracks.Length;
        using var ms = new MemoryStream();
        ms.Write(BuildWebmEbmlHeaderOnly("webm"));
        ms.Write([0x18, 0x53, 0x80, 0x67]);
        ms.Write(EncodeEbmlUintSize4Byte(segPayloadLen));
        ms.Write(seekHead);
        ms.Write(cluster);
        ms.Write(tracks);
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
        ms.WriteByte(2);
        ms.WriteByte((byte)seg1.Length);
        ms.WriteByte((byte)seg2.Length);
        ms.Write(seg1);
        ms.Write(seg2);
        return ms.ToArray();
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
}
