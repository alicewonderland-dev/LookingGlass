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

    public static ChannelAttention Of(ChannelView channel) {
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (channel.MembershipWarning is { } warning) {
            // Also carries removals the server didn't take the rekey for, and "your place belongs to your old key".
            warnings.Add(warning);
        }

        var changed = channel.Members.Where(member => member is { KeyChanged: true, KeyReplaced: false }).Select(NameOf).ToList();
        if (changed.Count > 0) {
            warnings.Add($"Key changed: {string.Join(", ", changed)}. Compare fingerprints.");
        }

        var replaced = channel.Members.Where(member => member.KeyReplaced).Select(NameOf).ToList();
        if (replaced.Count > 0) {
            warnings.Add($"Registered again: {string.Join(", ", replaced)}. Remove them and invite them again to let their new key in.");
        }

        string? pending = channel.RekeyPending ? "A new channel key is pending."
            : !channel.HasKey ? "Waiting for the channel key."
            : null;

        var level = warnings.Count > 0 ? AttentionLevel.Warning : pending != null ? AttentionLevel.Pending : AttentionLevel.None;
        if (level == AttentionLevel.None) {
            return None;
        }

        if (pending != null) {
            warnings.Add(pending);
        }

        return new ChannelAttention(level, warnings.ToImmutable());
    }

    private static string NameOf(MemberView member) => $"{member.User.Name}@{member.User.WorldName}";
}
