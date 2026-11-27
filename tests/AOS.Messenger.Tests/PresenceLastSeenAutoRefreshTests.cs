using Xunit;

namespace AOS.Messenger.Tests;

public sealed class PresenceLastSeenAutoRefreshTests
{
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

    [Fact]
    public async Task Presence_TransitionsLastSeenJustNowAutomaticallyAfter60Seconds_AndOnVisibilityResume()
    {
        var script = $$"""
            import assert from 'node:assert/strict';

            const docListeners = new Map();
            globalThis.document = {
                visibilityState: 'visible',
                addEventListener(type, fn) {
                    if (!docListeners.has(type)) docListeners.set(type, new Set());
                    docListeners.get(type).add(fn);
                },
                removeEventListener(type, fn) {
                    docListeners.get(type)?.delete(fn);
                },
                dispatchEvent(evt) {
                    for (const fn of [...(docListeners.get(evt.type) || [])]) fn(evt);
                }
            };

            function makeEl(id = '') {
                const attrs = new Map();
                return {
                    id,
                    className: '',
                    classList: { remove() {}, add() {} },
                    textContent: '',
                    children: [],
                    setAttribute(k, v) { attrs.set(k, String(v)); },
                    getAttribute(k) { return attrs.has(k) ? attrs.get(k) : null; },
                    querySelector(sel) {
                        for (const c of this.children) {
                            if (sel === `#${c.id}` || (sel === '[data-presence-dot]' && c.getAttribute('data-presence-dot')) || (sel === '[data-presence-text]' && c.getAttribute('data-presence-text'))) {
                                return c;
                            }
                        }
                        return null;
                    }
                };
            }

            // Capture scheduled timers deterministically
            let nextTimerId = 1;
            const scheduled = new Map();
            globalThis.setTimeout = (fn, delay) => {
                const id = nextTimerId++;
                scheduled.set(id, { fn, delay });
                return id;
            };
            globalThis.clearTimeout = (id) => {
                scheduled.delete(id);
            };

            const { setupPresence, computeNextLastSeenRefreshDelayMs } = await import('{{JsUrl("presence.js")}}');

            const header = makeEl('active-conversation-header');
            header.setAttribute('data-peer-user-id', 'peer-1');
            const dot = makeEl('header-presence-dot');
            dot.setAttribute('data-presence-dot', 'true');
            const text = makeEl('header-presence-text');
            text.setAttribute('data-presence-text', 'true');
            text.textContent = 'Online';
            header.children.push(dot, text);

            const baseMs = Date.UTC(2026, 10, 27, 14, 35, 0);
            let currentMs = baseMs + 5_000; // 5s after logoff

            const presence = setupPresence(null, null, header, {
                skipInitialFetch: true,
                nowFn: () => new Date(currentMs)
            });

            // 1. Peer logs off -> initially "Last seen just now" and schedules timer ~55.25s out
            const logoffIso = new Date(baseMs).toISOString();
            presence.handlePresenceChanged({
                userId: 'peer-1',
                isOnline: false,
                lastSeenAtUtc: logoffIso
            });
            assert.equal(text.textContent, 'Last seen just now');
            assert.equal(text.getAttribute('data-presence-online'), 'false');
            assert.equal(text.getAttribute('data-last-seen-at'), logoffIso);
            assert.equal(scheduled.size, 1);

            const [[timerId, timerEntry]] = [...scheduled.entries()];
            assert.ok(timerEntry.delay >= 55_000 && timerEntry.delay <= 56_000, `Unexpected delay: ${timerEntry.delay}`);

            // 2. Advance clock past 60s and fire the scheduled timer -> flips automatically to "Last seen today at ..."
            currentMs = baseMs + 61_000;
            scheduled.delete(timerId);
            timerEntry.fn();
            assert.ok(text.textContent.startsWith('Last seen today at '), `Expected 'Last seen today at ...', got '${text.textContent}'`);
            // Another timer should be scheduled for the next midnight boundary
            assert.equal(scheduled.size, 1);

            // 3. Advance clock to the next day and simulate returning to the tab (visibilitychange)
            currentMs = baseMs + 25 * 60 * 60 * 1000;
            document.visibilityState = 'visible';
            document.dispatchEvent({ type: 'visibilitychange' });
            assert.ok(text.textContent.startsWith('Last seen yesterday at '), `Expected 'Last seen yesterday at ...', got '${text.textContent}'`);

            // 4. Peer comes back Online -> clears timer & sets Online
            presence.handlePresenceChanged({ userId: 'peer-1', isOnline: true, lastSeenAtUtc: null });
            assert.equal(text.textContent, 'Online');
            assert.equal(text.getAttribute('data-presence-online'), 'true');
            assert.equal(text.getAttribute('data-last-seen-at'), '');
            assert.equal(scheduled.size, 0);

            // 5. Delay helper sanity checks (including past-year timestamps needing no further timer)
            assert.equal(computeNextLastSeenRefreshDelayMs(null), null);
            assert.equal(computeNextLastSeenRefreshDelayMs('not-a-date'), null);
            assert.equal(
                computeNextLastSeenRefreshDelayMs('2025-05-01T10:00:00Z', new Date(Date.UTC(2026, 10, 27, 14, 0, 0))),
                null
            );

            // 6. destroy() clears timers and document visibilitychange listener
            presence.handlePresenceChanged({ userId: 'peer-1', isOnline: false, lastSeenAtUtc: logoffIso });
            assert.equal(scheduled.size, 1);
            presence.destroy();
            assert.equal(scheduled.size, 0);
            assert.equal(docListeners.get('visibilitychange')?.size || 0, 0);
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node failed ({exitCode}): {stderr}\n{stdout}");
    }

    [Fact]
    public void ConversationView_And_MessageRenderer_PersistLastSeenAttributesForLiveRefresh()
    {
        var slnRoot = FindSolutionRoot();

        var convView = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml"));
        Assert.Contains("data-presence-online=", convView);
        Assert.Contains("data-last-seen-at=", convView);

        var rendererJs = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "message-renderer.js"));
        Assert.Contains("data-presence-online", rendererJs);
        Assert.Contains("data-last-seen-at", rendererJs);

        var chatShellJs = File.ReadAllText(Path.Combine(slnRoot, "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-shell.js"));
        Assert.Contains("presence.refreshHeaderLastSeen()", chatShellJs);
    }
}
