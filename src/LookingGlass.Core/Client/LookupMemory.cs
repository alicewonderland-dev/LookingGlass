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

        this._lookups[lookup] = (userId, now);
    }

    public void Forget(string lookup) => this._lookups.TryRemove(lookup, out _);

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
