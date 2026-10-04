using System.Security.Cryptography;
using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Membership;

/// <summary>
/// How membership log entries are signed and hashed. These layouts are part of the
/// protocol: clients and the server must agree on them byte for byte.
/// </summary>
public static class MembershipEntries {
    public const int HashSize = 32;

    /// <summary>What the actor signs: every field of the entry except the signature.</summary>
    public static byte[] SigningPayload(MembershipEntry entry) {
        var payload = new SigningPayload(Domains.MembershipEntry)
            .Add(entry.ChannelId)
            .Add(entry.Seq)
            .Add(entry.PreviousHash.Span)
            .Add((long) entry.Kind)
            .Add(entry.ActorId)
            .Add(entry.ActorKeyHash.Span);

        // Length-prefixed fields and presence markers, so no two entries share a payload.
        if (entry.Subject == null) {
            payload.Add(0L);
        } else {
            payload.Add(1L).Add(entry.Subject.UserId).Add(entry.Subject.SigningPublicKey.Span).Add(entry.Subject.AgreementPublicKey.Span);
        }

        return payload
            .Add((long) entry.Rank)
            .Add(entry.TimestampUnixMs)
            .Add(entry.Invite)
            .ToArray();
    }

    /// <summary>The entry's hash, which the next entry chains to. Covers the signature too.</summary>
    public static byte[] Hash(MembershipEntry entry) {
        return SHA256.HashData(new SigningPayload(Domains.MembershipEntryHash)
            .Add(SigningPayload(entry))
            .Add(entry.Signature.Span)
            .ToArray());
    }

    public static LogPosition PositionOf(MembershipEntry entry) => new() { Seq = entry.Seq, Hash = ByteString.CopyFrom(Hash(entry)) };

    public static void Sign(MembershipEntry entry, IdentityKeys actor) {
        entry.Signature = ByteString.CopyFrom(actor.Sign(SigningPayload(entry)));
    }

    public static bool SamePosition(LogPosition? a, LogPosition? b) {
        return a != null && b != null && a.Seq == b.Seq && a.Hash.Span.SequenceEqual(b.Hash.Span);
    }

    public static string Describe(LogPosition? position) => position == null ? "none" : $"#{position.Seq}";
}
