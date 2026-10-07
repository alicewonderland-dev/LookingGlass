using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using LookingGlass.Core.Crypto;

namespace LookingGlass.Server.Services;

public sealed record LodestoneCharacter(long Id, string Name, string WorldName);

public sealed class LodestoneUnavailableException() : Exception("The Lodestone couldn't be reached.");

/// <summary>Too many Lodestone requests are already queued; the caller should try again later.</summary>
public sealed class LodestoneBusyException() : Exception("Too many Lodestone requests are waiting.");

public enum ProfileCheck {
    CodeFound,
    CodeNotFound,

    /// <summary>The character's page has no profile text to read (and doesn't say it is private).</summary>
    ProfileUnavailable,

    /// <summary>The character's page says their profile is private.</summary>
    ProfilePrivate,

    /// <summary>The Lodestone couldn't be reached, or didn't answer with the page.</summary>
    LodestoneUnavailable,
}

/// <summary>
/// Looks characters up on the Lodestone. All requests go through one queue
/// with a minimum gap between them, and characters found are cached, so the
/// server never floods the Lodestone. A search that finds nobody isn't cached:
/// someone who fixes a typo, or makes their profile public, is looked up again
/// at once (failed lookups are limited per address instead, by the caller).
/// </summary>
/// <param name="time">The clock cached results expire by (tests move it).</param>
public sealed partial class LodestoneClient(HttpClient http, IOptions<ServerOptions> options, ILogger<LodestoneClient> logger, TimeProvider? time = null) {
    private static readonly TimeSpan SearchCacheTime = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);
    private const int MaxSearchPages = 10;

    /// <summary>
    /// Requests allowed to wait for the queue. With a gap of seconds between
    /// requests, more would only time out, and each waiter holds a connection.
    /// </summary>
    public const int MaxWaitingRequests = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (LodestoneCharacter Result, DateTimeOffset Expires)> _searchCache = new();
    private readonly Lock _sweeping = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _waiting;

    /// <summary>
    /// Most searches kept cached. Every registration attempt names a character (any name: at most 5 per address an hour, but
    /// addresses are many), so expired results are dropped every 10 minutes, and past this the soonest to expire go.
    /// </summary>
    internal int MaxCachedSearches { get; init; } = 10_000;

    /// <summary>How many searches are cached now, for tests.</summary>
    internal int CachedSearches => this._searchCache.Count;

    private LodestoneOptions Options => options.Value.Lodestone;

    /// <summary>Finds the character with exactly this name on this home world.</summary>
    /// <exception cref="LodestoneUnavailableException">The Lodestone couldn't be reached; nothing is cached.</exception>
    /// <exception cref="LodestoneBusyException">Too many requests are already waiting.</exception>
    public async Task<LodestoneCharacter?> FindCharacterAsync(string name, string worldName, CancellationToken ct) {
        var cacheKey = $"{name.ToLowerInvariant()}@{worldName.ToLowerInvariant()}";
        if (this._searchCache.TryGetValue(cacheKey, out var cached) && cached.Expires > this._time.GetUtcNow()) {
            return cached.Result;
        }

        LodestoneCharacter? found = null;
        for (var page = 1; page <= MaxSearchPages && found == null; page++) {
            var url = $"{this.Options.BaseUrl}/lodestone/character/?q={Uri.EscapeDataString($"\"{name}\"")}&worldname={Uri.EscapeDataString(worldName)}&page={page}";
            // A failed request is not "not found": don't cache it, and tell the caller.
            var html = await this.GetAsync(url, ct) ?? throw new LodestoneUnavailableException();

            foreach (Match entry in EntryPattern().Matches(html)) {
                var entryName = WebUtility.HtmlDecode(entry.Groups["name"].Value).Trim();
                var entryWorld = WebUtility.HtmlDecode(entry.Groups["world"].Value).Trim();
                if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entryWorld, worldName, StringComparison.OrdinalIgnoreCase)) {
                    found = new LodestoneCharacter(long.Parse(entry.Groups["id"].Value), entryName, entryWorld);
                    break;
                }
            }

            var pager = PagerPattern().Match(html);
            if (!pager.Success || int.Parse(pager.Groups["current"].Value) >= int.Parse(pager.Groups["total"].Value)) {
                break;
            }
        }

        // Only characters found: a miss (a typo, a character too new, a private profile) is asked again next time, so a
        // retry right after fixing it works.
        if (found != null) {
            var now = this._time.GetUtcNow();
            this._searchCache[cacheKey] = (found, now + SearchCacheTime);
            this.TrimSearchCache(now);
        }

        return found;
    }

    /// <summary>Drops expired searches every 10 minutes, and keeps at most <see cref="MaxCachedSearches"/>, the soonest to expire going first.</summary>
    private void TrimSearchCache(DateTimeOffset now) {
        var due = now - this._lastSweep >= SweepEvery;
        if ((!due && this._searchCache.Count <= this.MaxCachedSearches) || !this._sweeping.TryEnter()) {
            return;
        }

        try {
            this._lastSweep = now;
            foreach (var (key, value) in this._searchCache) {
                if (value.Expires <= now) {
                    this._searchCache.TryRemove(new KeyValuePair<string, (LodestoneCharacter, DateTimeOffset)>(key, value));
                }
            }

            var excess = this._searchCache.Count - this.MaxCachedSearches * 9 / 10;
            if (this._searchCache.Count > this.MaxCachedSearches && excess > 0) {
                foreach (var (key, value) in this._searchCache.OrderBy(pair => pair.Value.Expires).Take(excess).ToList()) {
                    this._searchCache.TryRemove(new KeyValuePair<string, (LodestoneCharacter, DateTimeOffset)>(key, value));
                }
            }
        } finally {
            this._sweeping.Exit();
        }
    }

    /// <summary>
    /// Checks whether the character's profile text contains <paramref name="code"/>, a registration code, as
    /// <see cref="LodestoneCode.AppearsIn"/> reads it (in any case, forgiving O for 0 and I or L for 1). The page is read
    /// afresh every time (asking for no cached copy), in the queue like any request: the user has just changed it.
    /// </summary>
    /// <exception cref="LodestoneBusyException">Too many requests are already waiting.</exception>
    public async Task<ProfileCheck> ProfileContainsAsync(long characterId, string code, CancellationToken ct) {
        var html = await this.GetAsync($"{this.Options.BaseUrl}/lodestone/character/{characterId}/", ct, fresh: true);
        if (html == null) {
            return ProfileCheck.LodestoneUnavailable;
        }

        var intro = IntroductionPattern().Match(html);
        if (!intro.Success) {
            // A private profile's page shows no profile text, and says it is private ("This character's profile is private.").
            return PrivatePattern().IsMatch(TagPattern().Replace(html, " ")) ? ProfileCheck.ProfilePrivate : ProfileCheck.ProfileUnavailable;
        }

        var text = WebUtility.HtmlDecode(TagPattern().Replace(intro.Groups["text"].Value, " "));
        return LodestoneCode.AppearsIn(text, code) ? ProfileCheck.CodeFound : ProfileCheck.CodeNotFound;
    }

    /// <param name="fresh">Ask for no cached copy (of a page the user has just changed).</param>
    /// <exception cref="LodestoneBusyException">Too many requests are already waiting.</exception>
    private async Task<string?> GetAsync(string url, CancellationToken ct, bool fresh = false) {
        if (Interlocked.Increment(ref this._waiting) > MaxWaitingRequests) {
            Interlocked.Decrement(ref this._waiting);
            throw new LodestoneBusyException();
        }

        try {
            await this._gate.WaitAsync(ct);
        } finally {
            Interlocked.Decrement(ref this._waiting);
        }

        try {
            var wait = this._lastRequest + TimeSpan.FromSeconds(this.Options.MinDelaySeconds) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait, ct);
            }

            this._lastRequest = DateTimeOffset.UtcNow;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (fresh) {
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            }

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) {
                logger.LogInformation("Lodestone returned {Status} for {Url}", (int) response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        } catch (HttpRequestException ex) {
            logger.LogWarning(ex, "Lodestone request failed");
            return null;
        } finally {
            this._gate.Release();
        }
    }

    [GeneratedRegex("""<a href="/lodestone/character/(?<id>\d+)/" class="entry__link">.*?<p class="entry__name">(?<name>[^<]*)</p>.*?<p class="entry__world">(?:<i[^>]*></i>)?(?<world>[^<\[]*)""", RegexOptions.Singleline)]
    private static partial Regex EntryPattern();

    [GeneratedRegex("""class="btn__pager__current">Page (?<current>\d+) of (?<total>\d+)""")]
    private static partial Regex PagerPattern();

    [GeneratedRegex("""<div class="character__selfintroduction">(?<text>.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex IntroductionPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    // "Private" as a word ("Privacy" in a page's links isn't it), in a page's text.
    [GeneratedRegex(@"\bprivate\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrivatePattern();
}
