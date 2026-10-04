using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WonderlandChat.Server.Services;

public sealed record LodestoneCharacter(long Id, string Name, string WorldName);

public enum ProfileCheck {
    CodeFound,
    CodeNotFound,
    ProfileUnavailable,
}

/// <summary>
/// Looks characters up on the Lodestone. All requests go through one queue
/// with a minimum gap between them, and search results are cached, so the
/// server never floods the Lodestone.
/// </summary>
public sealed partial class LodestoneClient(HttpClient http, IOptions<ServerOptions> options, ILogger<LodestoneClient> logger) {
    private static readonly TimeSpan SearchCacheTime = TimeSpan.FromHours(1);
    private const int MaxSearchPages = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (LodestoneCharacter? Result, DateTimeOffset Expires)> _searchCache = new();
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    private LodestoneOptions Options => options.Value.Lodestone;

    /// <summary>Finds the character with exactly this name on this home world.</summary>
    public async Task<LodestoneCharacter?> FindCharacterAsync(string name, string worldName, CancellationToken ct) {
        var cacheKey = $"{name.ToLowerInvariant()}@{worldName.ToLowerInvariant()}";
        if (this._searchCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow) {
            return cached.Result;
        }

        LodestoneCharacter? found = null;
        for (var page = 1; page <= MaxSearchPages && found == null; page++) {
            var url = $"{this.Options.BaseUrl}/lodestone/character/?q={Uri.EscapeDataString($"\"{name}\"")}&worldname={Uri.EscapeDataString(worldName)}&page={page}";
            var html = await this.GetAsync(url, ct);
            if (html == null) {
                break;
            }

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

        this._searchCache[cacheKey] = (found, DateTimeOffset.UtcNow + SearchCacheTime);
        return found;
    }

    /// <summary>Checks whether the character's profile text contains <paramref name="code"/>.</summary>
    public async Task<ProfileCheck> ProfileContainsAsync(long characterId, string code, CancellationToken ct) {
        var html = await this.GetAsync($"{this.Options.BaseUrl}/lodestone/character/{characterId}/", ct);
        if (html == null) {
            return ProfileCheck.ProfileUnavailable;
        }

        var intro = IntroductionPattern().Match(html);
        if (!intro.Success) {
            return ProfileCheck.ProfileUnavailable;
        }

        var text = WebUtility.HtmlDecode(TagPattern().Replace(intro.Groups["text"].Value, " "));
        return text.Contains(code, StringComparison.OrdinalIgnoreCase) ? ProfileCheck.CodeFound : ProfileCheck.CodeNotFound;
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct) {
        await this._gate.WaitAsync(ct);
        try {
            var wait = this._lastRequest + TimeSpan.FromSeconds(this.Options.MinDelaySeconds) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait, ct);
            }

            this._lastRequest = DateTimeOffset.UtcNow;
            using var response = await http.GetAsync(url, ct);
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
}
