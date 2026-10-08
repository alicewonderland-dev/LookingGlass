using System.Collections.Concurrent;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// Whom a "Name@World" was looked up as, and when, so inviting one friend to many channels looks them up once (see
/// <see cref="ClientSession.InviteAsync(string, string, string, CancellationToken)"/>). Only their user ID is kept: what is
/// used is the identity the session has for them now, and only while that still has the name looked up.
/// </summary>
public sealed class LookupMemory(TimeProvider time) {
    /// <summary>How long a lookup is reused.</summary>
    public static readonly TimeSpan ReusedFor = TimeSpan.FromMinutes(10);

    /// <summary>The most remembered at once: past it, those that have expired are forgotten, and if none had, all.</summary>
    public const int MaxRemembered = 200;

    private readonly ConcurrentDictionary<string, (long UserId, DateTimeOffset At)> _lookups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many are remembered now, for tests.</summary>
    internal int Count => this._lookups.Count;

    /// <summary>Remembers that <paramref name="lookup"/> ("Name@World") was found to be <paramref name="userId"/>, now.</summary>
    public void Remember(string lookup, long userId) {
        var now = time.GetUtcNow();
        if (this._lookups.Count >= MaxRemembered && !this._lookups.ContainsKey(lookup)) {
            foreach (var (key, value) in this._lookups) {
                if (now - value.At >= ReusedFor) {
                    this._lookups.TryRemove(new KeyValuePair<string, (long, DateTimeOffset)>(key, value));
                }
            }

            if (this._lookups.Count >= MaxRemembered) {
                this._lookups.Clear();
            }
        }

        this._missing.TryRemove(lookup, out _);
        this._lookups[lookup] = (userId, now);
    }

    public void Forget(string lookup) {
        this._lookups.TryRemove(lookup, out _);
        this._missing.TryRemove(lookup, out _);
    }

    // Names the server said nobody is registered as, and when: local chat doesn't look a friend who doesn't use LookingGlass
    // up again with every message (see ClientSession.SendLocalAsync). Invites always look such a name up again.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers that the server knows nobody by <paramref name="lookup"/> ("Name@World"), now.</summary>
    public void RememberMissing(string lookup) {
        var now = time.GetUtcNow();
        if (this._missing.Count >= MaxRemembered && !this._missing.ContainsKey(lookup)) {
            foreach (var (key, at) in this._missing) {
                if (now - at >= ReusedFor) {
                    this._missing.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, at));
                }
            }

            if (this._missing.Count >= MaxRemembered) {
                this._missing.Clear();
            }
        }

        this._lookups.TryRemove(lookup, out _);
        this._missing[lookup] = now;
    }

    /// <summary>Whether the server said, within <see cref="ReusedFor"/>, that nobody is registered as <paramref name="lookup"/>.</summary>
    public bool IsMissing(string lookup) {
        if (!this._missing.TryGetValue(lookup, out var at)) {
            return false;
        }

        if (time.GetUtcNow() - at < ReusedFor) {
            return true;
        }

        this._missing.TryRemove(new KeyValuePair<string, DateTimeOffset>(lookup, at));
        return false;
    }

    /// <summary>
    /// The identity <paramref name="current"/> gives for whom <paramref name="lookup"/> was found to be within
    /// <see cref="ReusedFor"/>, if it still has that name and world; else null (look them up).
    /// </summary>
    public UserIdentity? Recall(string lookup, Func<long, UserIdentity?> current) {
        if (!this._lookups.TryGetValue(lookup, out var found)) {
            return null;
        }

        if (time.GetUtcNow() - found.At >= ReusedFor) {
            this._lookups.TryRemove(new KeyValuePair<string, (long, DateTimeOffset)>(lookup, found));
            return null;
        }

        var identity = current(found.UserId);
        return identity?.User != null && string.Equals($"{identity.User.Name}@{identity.User.WorldName}", lookup, StringComparison.OrdinalIgnoreCase)
            ? identity
            : null;
    }
}
