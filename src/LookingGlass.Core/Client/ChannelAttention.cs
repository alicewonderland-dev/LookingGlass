using System.Collections.Immutable;

namespace LookingGlass.Core.Client;

public enum AttentionLevel {
    None,
    /// <summary>Waiting for something that usually sorts itself out (a channel key).</summary>
    Pending,
    /// <summary>Something the user should look at: a membership warning, a changed key, someone who registered again.</summary>
    Warning,
}

/// <summary>
/// Whether a channel needs a look, and why, gathered from the warnings its details show. For the
/// channel list, which flags a channel without opening it. Fingerprints nobody has compared yet are
/// normal (trusted on first use), so they aren't flagged.
/// </summary>
public sealed record ChannelAttention(AttentionLevel Level, ImmutableArray<string> Reasons) {
    public static readonly ChannelAttention None = new(AttentionLevel.None, ImmutableArray<string>.Empty);

    /// <param name="advanced">In advanced mode's words; otherwise simple mode's (see <see cref="Wording"/>). The same warnings either way.</param>
    public static ChannelAttention Of(ChannelView channel, bool advanced = true) {
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (channel.WarningFor(advanced) is { } warning) {
            // Also carries removals the server didn't take the rekey for, and "your place belongs to your old key".
            warnings.Add(warning);
        }

        var changed = channel.Members.Where(member => member is { KeyChanged: true, KeyReplaced: false }).Select(NameOf).ToList();
        if (changed.Count > 0) {
            warnings.Add(PlainMessages.KeysChangedIn(string.Join(", ", changed)).For(advanced));
        }

        var replaced = channel.Members.Where(member => member.KeyReplaced).Select(NameOf).ToList();
        if (replaced.Count > 0) {
            warnings.Add(PlainMessages.RegisteredAgainIn(string.Join(", ", replaced)).For(advanced));
        }

        // No key is coming for a place that belongs to an old key: its warning says what to do instead.
        var pending = channel.OldKeyMembership ? null
            : channel.RekeyPending ? PlainMessages.NewKeyPending
            : !channel.HasKey ? PlainMessages.WaitingForKey
            : null;

        var level = warnings.Count > 0 ? AttentionLevel.Warning : pending != null ? AttentionLevel.Pending : AttentionLevel.None;
        if (level == AttentionLevel.None) {
            return None;
        }

        if (pending != null) {
            warnings.Add(pending.For(advanced));
        }

        return new ChannelAttention(level, warnings.ToImmutable());
    }

    private static string NameOf(MemberView member) => $"{member.User.Name}@{member.User.WorldName}";
}
