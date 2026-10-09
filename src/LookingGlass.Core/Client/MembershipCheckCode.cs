using System.Buffers.Binary;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// A short code for the newest membership log entry a client verified in a channel: its number, then five groups of five
/// digits cut from its hash ("#12 48213 90412 33187 00921 55102"). Two members whose clients verified the same log show the same
/// code once both have the same newest entry; a different code at the same number means they were shown different
/// memberships, even where the member lists read the same (a key recovered entry moves someone to other keys, not another
/// name). Shown in the channel's member list, and in the warning that someone sees a different member list, so people
/// compare it over /tell. See "Log heads in messages" in docs/design.md.
/// </summary>
public static class MembershipCheckCode {
    private const int Groups = 5;

    /// <returns>Null without a verified entry.</returns>
    public static string? Of(LogPosition? head) {
        if (head == null || head.Hash.Length < Groups * 4) {
            return null;
        }

        var hash = head.Hash.Span;
        var groups = new string[Groups];
        for (var i = 0; i < Groups; i++) {
            groups[i] = (BinaryPrimitives.ReadUInt32LittleEndian(hash.Slice(i * 4, 4)) % 100_000).ToString("D5");
        }

        return $"#{head.Seq} {string.Join(' ', groups)}";
    }

    /// <summary>The code of entry <paramref name="seq"/>, whose hash is <paramref name="hash"/>; null if it isn't known.</summary>
    public static string? Of(ulong seq, byte[]? hash) => hash == null ? null : Of(new LogPosition { Seq = seq, Hash = Google.Protobuf.ByteString.CopyFrom(hash) });
}
