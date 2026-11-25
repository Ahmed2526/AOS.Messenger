using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Web.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class EmojiSupportTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public EmojiSupportTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx")) &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln")) &&
               !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not locate solution root.");
    }

    private static string JsUrl(string fileName)
    {
        var path = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", fileName);
        Assert.True(File.Exists(path), $"JS module not found: {path}");
        return new Uri(path).AbsoluteUri;
    }

    private static (int ExitCode, string Output, string Error) RunNodeModuleScript(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--input-type=module -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start node process for ES module verification.");

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) errorBuilder.AppendLine(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Node ES module verification timed out.");
        }

        process.WaitForExit();
        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    private static async Task<ApplicationUser> SeedUserAsync(ApplicationDbContext db, string userName, string displayName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var uniqueUserName = $"{userName}_{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = uniqueUserName,
            NormalizedUserName = uniqueUserName.ToUpperInvariant(),
            Email = $"{uniqueUserName}@example.com",
            NormalizedEmail = $"{uniqueUserName.ToUpperInvariant()}@EXAMPLE.COM",
            DisplayName = displayName
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Conversation> SeedConversationAsync(ApplicationDbContext db, UserId userA, UserId userB)
    {
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private (MessageService Service, ApplicationDbContext Db, TestTimeProvider TimeProvider) CreateService(
        DateTimeOffset? initialTime = null)
    {
        var time = initialTime ?? DateTimeOffset.UtcNow;
        var fakeTime = new TestTimeProvider(time);

        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = new MessageService(
            new MessagePersistence(db),
            new ConversationPersistence(db),
            new BlockPersistence(db),
            new UserReader(db),
            NoOpRealtimeNotifier.Instance,
            new ReactionPersistence(db),
            scope.ServiceProvider.GetRequiredService<ILogger<MessageService>>(),
            fakeTime);

        return (service, db, fakeTime);
    }

    private sealed class NoOpRealtimeNotifier : IRealtimeNotifier
    {
        public static readonly NoOpRealtimeNotifier Instance = new();
        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("🔥", EmojiDisplayTier.Single)]
    [InlineData("❤️", EmojiDisplayTier.Single)]
    [InlineData("❤", EmojiDisplayTier.Single)]
    [InlineData("👍🏽", EmojiDisplayTier.Single)]
    [InlineData("👨‍👩‍👧‍👦", EmojiDisplayTier.Single)]
    [InlineData("🤷🏽‍♀️", EmojiDisplayTier.Single)]
    [InlineData("🇺🇸", EmojiDisplayTier.Single)]
    [InlineData("1️⃣", EmojiDisplayTier.Single)]
    [InlineData("🏴󠁧󠁢󠁥󠁮󠁧󠁿", EmojiDisplayTier.Single)]
    [InlineData("   😂   ", EmojiDisplayTier.Single)]
    [InlineData("😀😂", EmojiDisplayTier.Few)]
    [InlineData("🔥 ❤️ 🎉", EmojiDisplayTier.Few)]
    [InlineData("👍🏽\n🙏🏽", EmojiDisplayTier.Few)]
    [InlineData("😀😂🔥🎉", EmojiDisplayTier.Normal)]
    [InlineData("Hello 🔥", EmojiDisplayTier.Normal)]
    [InlineData("123", EmojiDisplayTier.Normal)]
    [InlineData("#", EmojiDisplayTier.Normal)]
    [InlineData("*", EmojiDisplayTier.Normal)]
    [InlineData("©", EmojiDisplayTier.Normal)]
    [InlineData("®", EmojiDisplayTier.Normal)]
    [InlineData("™", EmojiDisplayTier.Normal)]
    [InlineData("A♡S", EmojiDisplayTier.Normal)]
    [InlineData(":)", EmojiDisplayTier.Normal)]
    [InlineData("", EmojiDisplayTier.Normal)]
    [InlineData("   ", EmojiDisplayTier.Normal)]
    public void EmojiPresentationHelper_ClassifiesWhatsAppTiersAccurately(string content, EmojiDisplayTier expected)
    {
        Assert.Equal(expected, EmojiPresentationHelper.ClassifyMessage(content));

        var css = EmojiPresentationHelper.GetMessageContentCssClasses(content);
        switch (expected)
        {
            case EmojiDisplayTier.Single:
                Assert.Contains("emoji-jumbo-single", css);
                Assert.Contains("text-4xl", css);
                break;
            case EmojiDisplayTier.Few:
                Assert.Contains("emoji-jumbo-few", css);
                Assert.Contains("text-3xl", css);
                break;
            default:
                Assert.Contains("text-xs", css);
                Assert.DoesNotContain("emoji-jumbo-", css);
                break;
        }
    }

    [Fact]
    public void EmojiPresentationHelper_AndJsCatalog_AgreeOnAll1800PlusBaseEmojis()
    {
        var dataPath = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", "emoji-data.js");
        Assert.True(File.Exists(dataPath));

        var content = File.ReadAllText(dataPath, Encoding.UTF8);
        var marker = "export const EMOJI_ENTRIES = ";
        var startIdx = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(startIdx >= 0);

        var jsonSlice = content[(startIdx + marker.Length)..].Trim();
        if (jsonSlice.EndsWith(';'))
        {
            jsonSlice = jsonSlice[..^1];
        }

        using var doc = JsonDocument.Parse(jsonSlice);
        var entries = doc.RootElement;
        Assert.True(entries.GetArrayLength() >= 1800, $"Expected at least 1800 base emojis, found {entries.GetArrayLength()}");

        var mismatches = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            var emoji = entry[0].GetString()!;
            if (EmojiPresentationHelper.ClassifyMessage(emoji) != EmojiDisplayTier.Single)
            {
                mismatches.Add($"{emoji} ({entry[1].GetString()})");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"EmojiPresentationHelper missed {mismatches.Count} catalog emojis: " + string.Join(", ", mismatches.Take(10)));
    }

    [Fact]
    public void CompiledCss_ContainsEmojiUtilitiesAndFontFallbacks()
    {
        var slnRoot = FindSolutionRoot();
        var compiledCssPath = Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "css", "app.css");
        var compiledCss = File.ReadAllText(compiledCssPath, Encoding.UTF8);

        Assert.Contains("Apple Color Emoji", compiledCss);
        Assert.Contains("Segoe UI Emoji", compiledCss);
        Assert.Contains("Noto Color Emoji", compiledCss);
        Assert.Contains(".emoji-font", compiledCss);
        Assert.Contains(".emoji-jumbo-single", compiledCss);
        Assert.Contains(".emoji-jumbo-few", compiledCss);
        Assert.Contains(".text-4xl", compiledCss);
        Assert.Contains(".text-3xl", compiledCss);
        Assert.Contains(".grid-cols-8", compiledCss);
    }

    [Fact]
    public async Task SqlServer_RoundTripsComplexUnicodeEmojis_AcrossSendEditReplyAndIdempotency()
    {
        var (service, db, _) = CreateService();
        var alice = await SeedUserAsync(db, "emoji_alice", "Alice");
        var bob = await SeedUserAsync(db, "emoji_bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(alice.Id), new UserId(bob.Id));

        // 1. Send complex ZWJ, skin-tone, flag, and keycap emojis
        var emojiPayload = "👨‍👩‍👧‍👦 👍🏽 🇺🇸 1️⃣ 🏴󠁧󠁢󠁥󠁮󠁧󠁿 ❤️‍🔥";
        var idempKey = Guid.NewGuid().ToString("D");

        var sent = await service.SendMessageAsync(
            new UserId(alice.Id),
            conv.Id,
            new SendMessageRequestDto(emojiPayload, idempKey));

        Assert.Equal(emojiPayload, sent.Content);

        // 2. Idempotent replay returns same message without conflict
        var replayed = await service.SendMessageAsync(
            new UserId(alice.Id),
            conv.Id,
            new SendMessageRequestDto(emojiPayload, idempKey));

        Assert.Equal(sent.Id, replayed.Id);
        Assert.Equal(emojiPayload, replayed.Content);

        // 3. Conflicting idempotency key with different emoji throws ConflictException
        await Assert.ThrowsAsync<ConflictException>(() => service.SendMessageAsync(
            new UserId(alice.Id),
            conv.Id,
            new SendMessageRequestDto("🔥", idempKey)));

        // 4. Edit message to single jumbo emoji and verify SQL Server round-trip
        var edited = await service.EditMessageAsync(
            new UserId(alice.Id),
            conv.Id,
            sent.Id,
            new EditMessageRequestDto("🔥"));

        Assert.Equal("🔥", edited.Content);
        Assert.NotNull(edited.EditedAtUtc);

        // 5. Reply to a long ZWJ emoji message and confirm grapheme-safe truncation (no broken surrogates)
        var longEmojiMsg = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 25)); // 275 UTF-16 chars
        var parentSent = await service.SendMessageAsync(
            new UserId(alice.Id),
            conv.Id,
            new SendMessageRequestDto(longEmojiMsg, Guid.NewGuid().ToString("D")));

        var replySent = await service.SendMessageAsync(
            new UserId(bob.Id),
            conv.Id,
            new SendMessageRequestDto("😂", Guid.NewGuid().ToString("D"), parentSent.Id));

        Assert.NotNull(replySent.ReplyTo);
        Assert.EndsWith("...", replySent.ReplyTo!.PreviewContent);
        Assert.True(replySent.ReplyTo.PreviewContent.Length <= 103);
        foreach (var rune in replySent.ReplyTo.PreviewContent.EnumerateRunes())
        {
            Assert.NotEqual(Rune.ReplacementChar, rune);
        }

        // 6. Phase 8 ReactionAllowlist remains strictly enforced (8 emojis only)
        Assert.True(ReactionAllowlist.IsAllowed("🔥"));
        Assert.False(ReactionAllowlist.IsAllowed("🚀"));
        await Assert.ThrowsAsync<DomainException>(() => service.ToggleReactionAsync(
            new UserId(bob.Id),
            conv.Id,
            parentSent.Id,
            new ToggleReactionRequestDto("🚀")));

        // 7. Read back from SQL Server and verify persisted content
        var page = await service.GetMessagesAsync(new UserId(bob.Id), conv.Id, null, 20);
        Assert.Contains(page.Messages, m => m.Id == sent.Id && m.Content == "🔥");
        Assert.Contains(page.Messages, m => m.Id == parentSent.Id && m.Content == longEmojiMsg);
        Assert.Contains(page.Messages, m => m.Id == replySent.Id && m.Content == "😂");
    }

    [Fact]
    public void ClientModules_EmojiUtilsPickerAndAutocomplete_VerifyWhatsAppBehavior()
    {
        var utilsUrl = JsUrl("emoji-utils.js");
        var dataUrl = JsUrl("emoji-data.js");
        var pickerUrl = JsUrl("emoji-picker.js");
        var autoUrl = JsUrl("emoji-autocomplete.js");
        var rendererUrl = JsUrl("message-renderer.js");
        var composerUrl = JsUrl("composer.js");

        var script = $$"""
            import assert from 'node:assert/strict';

            // Minimal DOM + localStorage mock for pure ES module verification
            const storageBacking = new Map();
            globalThis.localStorage = {
                getItem: (k) => (storageBacking.has(k) ? storageBacking.get(k) : null),
                setItem: (k, v) => { storageBacking.set(k, String(v)); },
                removeItem: (k) => { storageBacking.delete(k); },
                clear: () => { storageBacking.clear(); }
            };

            const docCaptureListeners = new Map();
            const docBubbleListeners = new Map();
            const winListeners = new Map();

            class MockEl {
                constructor(tagName) {
                    this.nodeType = 1;
                    this.tagName = String(tagName || 'DIV').toUpperCase();
                    this.children = [];
                    this.parentNode = null;
                    this._attrs = new Map();
                    this._capture = new Map();
                    this._bubble = new Map();
                    this.style = {};
                    this.className = '';
                    this.textContent = '';
                    this.value = '';
                    this.disabled = false;
                    this.selectionStart = 0;
                    this.selectionEnd = 0;
                    this.scrollTop = 0;
                    this.scrollHeight = 40;
                    this.focused = false;
                    this.classList = {
                        add: (...cls) => {
                            const s = new Set(this.className.split(/\s+/).filter(Boolean));
                            cls.forEach(c => s.add(c));
                            this.className = [...s].join(' ');
                        },
                        remove: (...cls) => {
                            const s = new Set(this.className.split(/\s+/).filter(Boolean));
                            cls.forEach(c => s.delete(c));
                            this.className = [...s].join(' ');
                        },
                        contains: (c) => this.className.split(/\s+/).includes(c)
                    };
                }
                get id() { return this._attrs.get('id') || ''; }
                set id(v) { if (v) this._attrs.set('id', String(v)); else this._attrs.delete('id'); }
                get firstChild() { return this.children[0] || null; }
                setAttribute(k, v) { this._attrs.set(k, String(v)); }
                getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
                removeAttribute(k) { this._attrs.delete(k); }
                appendChild(child) {
                    if (child && child.isFragment) {
                        for (const c of [...child.children]) this.appendChild(c);
                        child.children = [];
                        return child;
                    }
                    child.parentNode = this;
                    this.children.push(child);
                    return child;
                }
                insertBefore(child, ref) {
                    const idx = this.children.indexOf(ref);
                    child.parentNode = this;
                    if (idx < 0) this.children.push(child);
                    else this.children.splice(idx, 0, child);
                    return child;
                }
                removeChild(child) {
                    const idx = this.children.indexOf(child);
                    if (idx >= 0) {
                        this.children.splice(idx, 1);
                        child.parentNode = null;
                    }
                    return child;
                }
                remove() {
                    if (this.parentNode) this.parentNode.removeChild(this);
                }
                contains(node) {
                    if (node === this) return true;
                    return this.children.some(c => c.contains(node));
                }
                focus() { this.focused = true; }
                setSelectionRange(s, e) { this.selectionStart = s; this.selectionEnd = e; }
                getBoundingClientRect() { return { left: 200, right: 236, top: 700, bottom: 736, width: 36, height: 36 }; }
                addEventListener(type, fn, capture = false) {
                    const map = capture ? this._capture : this._bubble;
                    if (!map.has(type)) map.set(type, []);
                    map.get(type).push(fn);
                }
                removeEventListener(type, fn, capture = false) {
                    const map = capture ? this._capture : this._bubble;
                    const arr = map.get(type);
                    if (arr) map.set(type, arr.filter(x => x !== fn));
                }
                dispatchEvent(ev) {
                    const isNativeEvent = typeof Event === 'function' && ev instanceof Event;
                    let stoppedImmediate = false;
                    if (!isNativeEvent) {
                        ev.target = ev.target || this;
                        ev.preventDefault = ev.preventDefault || (() => { ev.defaultPrevented = true; });
                        ev.stopPropagation = ev.stopPropagation || (() => { ev.cancelBubble = true; });
                        ev.stopImmediatePropagation = () => { stoppedImmediate = true; ev.cancelBubble = true; };
                    }
                    for (const fn of [...(this._capture.get(ev.type) || [])]) {
                        fn(ev);
                        if (stoppedImmediate) return !ev.defaultPrevented;
                    }
                    for (const fn of [...(this._bubble.get(ev.type) || [])]) {
                        fn(ev);
                        if (stoppedImmediate) return !ev.defaultPrevented;
                    }
                    if (!isNativeEvent && !ev.cancelBubble && (ev.bubbles === true || ev.type === 'click' || ev.type === 'mousedown') && this.parentNode && typeof this.parentNode.dispatchEvent === 'function') {
                        this.parentNode.dispatchEvent(ev);
                    }
                    return !ev.defaultPrevented;
                }
                querySelectorAll(sel) {
                    const res = [];
                    const walk = (n) => {
                        for (const c of n.children) {
                            if (sel.startsWith('[') && sel.endsWith(']')) {
                                const inner = sel.slice(1, -1);
                                const eq = inner.indexOf('=');
                                if (eq > 0) {
                                    const k = inner.slice(0, eq);
                                    const v = inner.slice(eq + 1).replace(/^"|"$/g, '');
                                    if (c.getAttribute(k) === v) res.push(c);
                                } else if (c.getAttribute(inner) !== null) {
                                    res.push(c);
                                }
                            } else if (sel.startsWith('.') && c.classList.contains(sel.slice(1))) {
                                res.push(c);
                            }
                            walk(c);
                        }
                    };
                    walk(this);
                    return res;
                }
                querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
            }

            const body = new MockEl('BODY');
            globalThis.document = {
                body,
                documentElement: { clientWidth: 1440, clientHeight: 900 },
                createElement: (tag) => new MockEl(tag),
                createDocumentFragment: () => ({ isFragment: true, children: [], appendChild(c) { this.children.push(c); return c; } }),
                getElementById: (id) => {
                    const walk = (n) => {
                        if (n.id === id) return n;
                        for (const c of n.children) {
                            const found = walk(c);
                            if (found) return found;
                        }
                        return null;
                    };
                    return walk(body);
                },
                querySelectorAll: (sel) => body.querySelectorAll(sel),
                querySelector: (sel) => body.querySelector(sel),
                addEventListener: (type, fn, capture = false) => {
                    const map = capture ? docCaptureListeners : docBubbleListeners;
                    if (!map.has(type)) map.set(type, []);
                    map.get(type).push(fn);
                },
                removeEventListener: (type, fn, capture = false) => {
                    const map = capture ? docCaptureListeners : docBubbleListeners;
                    const arr = map.get(type);
                    if (arr) map.set(type, arr.filter(x => x !== fn));
                },
                dispatchEvent: (ev) => {
                    let stoppedImmediate = false;
                    ev.preventDefault = ev.preventDefault || (() => { ev.defaultPrevented = true; });
                    ev.stopPropagation = ev.stopPropagation || (() => { ev.cancelBubble = true; });
                    ev.stopImmediatePropagation = () => { stoppedImmediate = true; ev.cancelBubble = true; };
                    for (const fn of [...(docCaptureListeners.get(ev.type) || [])]) {
                        fn(ev);
                        if (stoppedImmediate) return;
                    }
                    for (const fn of [...(docBubbleListeners.get(ev.type) || [])]) {
                        fn(ev);
                        if (stoppedImmediate) return;
                    }
                }
            };
            globalThis.window = {
                innerWidth: 1440,
                innerHeight: 900,
                matchMedia: () => ({ matches: false }),
                addEventListener: (t, fn) => {
                    if (!winListeners.has(t)) winListeners.set(t, []);
                    winListeners.get(t).push(fn);
                },
                removeEventListener: (t, fn) => {
                    const arr = winListeners.get(t);
                    if (arr) winListeners.set(t, arr.filter(x => x !== fn));
                }
            };

            const utils = await import('{{utilsUrl}}');
            const data = await import('{{dataUrl}}');
            const pickerMod = await import('{{pickerUrl}}');
            const autoMod = await import('{{autoUrl}}');
            const renderer = await import('{{rendererUrl}}');
            const composerMod = await import('{{composerUrl}}');

            // 1. Catalog verification
            assert.equal(data.EMOJI_CATEGORIES.length, 8);
            assert.equal(data.SKIN_TONES.length, 6);
            assert.ok(data.EMOJI_ENTRIES.length >= 1800);

            // 2. Tier classification + parity
            assert.equal(utils.classifyEmojiOnlyMessage('🔥'), 'single');
            assert.equal(utils.classifyEmojiOnlyMessage('👨‍👩‍👧‍👦'), 'single');
            assert.equal(utils.classifyEmojiOnlyMessage('👍🏽 ❤️'), 'few');
            assert.equal(utils.classifyEmojiOnlyMessage('😀 😂 🔥'), 'few');
            assert.equal(utils.classifyEmojiOnlyMessage('😀😂🔥🎉'), 'normal');
            assert.equal(utils.classifyEmojiOnlyMessage('hello 🔥'), 'normal');
            assert.equal(utils.classifyEmojiOnlyMessage('123'), 'normal');
            assert.equal(utils.classifyEmojiOnlyMessage('©'), 'normal');

            // 3. Skin-tone application
            assert.equal(utils.applySkinTone('👍', '\u{1F3FD}', true), '👍🏽');
            assert.equal(utils.applySkinTone('☝️', '\u{1F3FD}', true), '☝🏽');
            assert.equal(utils.applySkinTone('🤷‍♀️', '\u{1F3FD}', true), '🤷🏽‍♀️');
            assert.equal(utils.applySkinTone('🔥', '\u{1F3FD}', false), '🔥');

            // 4. Grapheme-safe reply truncation (never splits ZWJ family)
            const families = '👨‍👩‍👧‍👦'.repeat(5);
            assert.equal(utils.truncateGraphemes(families, 3), '👨‍👩‍👧‍👦👨‍👩‍👧‍👦👨‍👩‍👧‍👦...');

            // 5. Caret insertion + 4000-char boundary guard + user-scoped localStorage
            const inputEl = document.createElement('textarea');
            inputEl.id = 'message-content-input';
            inputEl.value = 'Hello world';
            inputEl.selectionStart = 5;
            inputEl.selectionEnd = 5;
            let inputEvents = 0;
            inputEl.addEventListener('input', () => inputEvents++);
            assert.equal(utils.insertTextAtCaret(inputEl, ' 🔥'), true);
            assert.equal(inputEl.value, 'Hello 🔥 world');
            assert.equal(inputEvents, 1);

            inputEl.value = 'a'.repeat(3999);
            inputEl.selectionStart = 3999;
            inputEl.selectionEnd = 3999;
            assert.equal(utils.insertTextAtCaret(inputEl, '🔥'), false);
            assert.equal(inputEl.value.length, 3999);

            utils.recordRecentEmoji('user-a', '🔥');
            utils.savePreferredSkinTone('user-a', '\u{1F3FD}');
            assert.deepEqual(utils.loadRecentEmojis('user-a'), ['🔥']);
            assert.deepEqual(utils.loadRecentEmojis('user-b'), []);
            assert.equal(utils.loadPreferredSkinTone('user-a'), '\u{1F3FD}');
            assert.equal(utils.loadPreferredSkinTone('user-b'), '');

            // 6. Picker stay-open multi-pick, search, skin-tone switch, and Escape close
            const triggerBtn = document.createElement('button');
            triggerBtn.id = 'composer-emoji-btn';
            body.appendChild(inputEl);
            body.appendChild(triggerBtn);
            inputEl.value = '';
            inputEl.selectionStart = 0;
            inputEl.selectionEnd = 0;

            const picker = pickerMod.createEmojiPicker({ getUserId: () => 'user-a' });
            const opened = await picker.toggle(triggerBtn, inputEl, { surface: 'composer' });
            assert.equal(opened, true);
            assert.equal(picker.isOpen(), true);

            const panel = document.getElementById('emoji-picker-panel');
            assert.ok(panel);
            assert.equal(panel.classList.contains('hidden'), false);

            // Search for "thumbsup" and click it twice (verifies stay-open multi-pick + medium skin tone applied)
            const searchInput = document.getElementById('emoji-picker-search');
            searchInput.value = 'thumbsup';
            searchInput.dispatchEvent({ type: 'input' });
            const firstPick = panel.querySelector('[data-emoji-pick]');
            assert.ok(firstPick);
            assert.equal(firstPick.getAttribute('data-emoji-pick'), '👍🏽');
            assert.equal(firstPick._bubble.size, 0, '[S5] Emoji grid must delegate clicks rather than attaching per-button listeners');

            firstPick.dispatchEvent({ type: 'click' });
            assert.equal(picker.isOpen(), true, 'Picker must remain open for multi-pick');
            firstPick.dispatchEvent({ type: 'click' });
            assert.equal(inputEl.value, '👍🏽👍🏽');

            // Escape closes picker
            document.dispatchEvent({ type: 'keydown', key: 'Escape' });
            assert.equal(picker.isOpen(), false);
            assert.equal(panel.classList.contains('hidden'), true);

            // 7. Shortcode autocomplete trigger rules + Enter arbitration (must NOT send message!)
            assert.deepEqual(autoMod.findShortcodeTrigger(':fi', 3), { query: 'fi', start: 0, end: 3 });
            assert.deepEqual(autoMod.findShortcodeTrigger('hello :fire', 11), { query: 'fire', start: 6, end: 11 });
            assert.equal(autoMod.findShortcodeTrigger('10:30', 5), null);
            assert.equal(autoMod.findShortcodeTrigger('https://x', 9), null);
            assert.equal(autoMod.findShortcodeTrigger(':)', 2), null);

            const sendBtn = document.createElement('button');
            sendBtn.id = 'send-message-btn';
            body.appendChild(sendBtn);

            const fakeState = {
                activeConversationId: 'conv-1',
                switchEpoch: 1,
                currentUserId: 'user-a',
                activeMeta: { canSend: true, isBlocked: false },
                messagesById: new Map(),
                deleteDraft: () => {}
            };
            const autocomplete = autoMod.createEmojiAutocomplete({ getUserId: () => 'user-a' });
            const composer = composerMod.createComposer({
                state: fakeState,
                emojiPicker: picker,
                emojiAutocomplete: autocomplete
            });
            composer.init();
            composer.setLocked(false);

            inputEl.value = 'lit :fire';
            inputEl.selectionStart = 9;
            inputEl.selectionEnd = 9;
            inputEl.dispatchEvent({ type: 'input' });
            await new Promise((r) => setTimeout(r, 25));

            assert.equal(autocomplete.isOpen(), true);
            let enterEvent = { type: 'keydown', key: 'Enter', shiftKey: false };
            inputEl.dispatchEvent(enterEvent);
            assert.equal(enterEvent.defaultPrevented, true);
            assert.equal(autocomplete.isOpen(), false);
            assert.equal(inputEl.value, 'lit 🔥');

            // Locking composer disables emoji button and closes open picker
            await picker.toggle(triggerBtn, inputEl, { surface: 'composer' });
            assert.equal(picker.isOpen(), true);
            composer.setLocked(true);
            assert.equal(triggerBtn.disabled, true);
            assert.equal(picker.isOpen(), false);

            // 8. Renderer tiered bubble sizing & edit live class update
            const bubbleRow = renderer.createMessageBubbleElement(
                { id: 42, senderId: 'user-a', content: '🔥', createdAtUtc: '2026-11-23T12:00:00Z', isOutgoing: true },
                'user-a'
            );
            body.appendChild(bubbleRow);
            const textEl = bubbleRow.querySelector('.message-text-content');
            assert.ok(textEl.className.includes('emoji-jumbo-single'));
            assert.ok(textEl.className.includes('text-4xl'));

            renderer.applyMessageEditedToRow(42, 'now text 🔥', '2026-11-23T12:01:00Z');
            assert.equal(textEl.textContent, 'now text 🔥');
            assert.ok(textEl.className.includes('text-xs'));
            assert.equal(textEl.className.includes('emoji-jumbo-single'), false);

            composer.destroy();
            picker.destroy();
            autocomplete.destroy();
            console.log('ALL_EMOJI_CLIENT_CHECKS_PASSED');
            """;

        var (exitCode, output, error) = RunNodeModuleScript(script);
        Assert.True(exitCode == 0, $"Node verification failed (exit {exitCode}).\nSTDOUT:\n{output}\nSTDERR:\n{error}");
        Assert.Contains("ALL_EMOJI_CLIENT_CHECKS_PASSED", output);
    }
}
