using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Plugin.SelfTest;

/// <summary>
/// The host's two-player part: the other tester (held while the run lasts only, never logged), the delivered-messages tap,
/// invites accepted only from them to the run's own channels, their block, the party and how far they are. Everything
/// here refuses anyone but the paired tester, and any channel but the run's.
/// </summary>
internal sealed partial class SelfTestHost {
    private readonly List<(long Seq, ReceivedLine Line)> _received = [];
    private (string? Other, string Code, SelfTestRole Role)? _pair;
    private long _otherId;

    /// <summary>The other tester's name@world, while a two-player run lasts.</summary>
    private string? Other {
        get {
            lock (this._lock) {
                return this._pair?.Other;
            }
        }
    }

    public void Pair(string? other, string code, SelfTestRole role) {
        lock (this._lock) {
            this._pair = role == SelfTestRole.Solo ? null : (other?.Trim(), code, role);
            this._otherId = 0;
        }

        if (other != null) {
            // Never in the log, nor in the report.
            this.Forbid(other);
        }
    }

    private void StopPair() {
        lock (this._lock) {
            this._pair = null;
            this._otherId = 0;
            this._received.Clear();
        }
    }

    private bool IsOther(string? name, string? world) =>
        this.Other is { } other && name != null && world != null && string.Equals($"{name}@{world}", other, StringComparison.OrdinalIgnoreCase);

    /// <summary>The other tester's user ID: looked up by the run, or found among the run's channels' members.</summary>
    private long OtherUserId() {
        lock (this._lock) {
            if (this._otherId != 0 || this._pair == null) {
                return this._otherId;
            }
        }

        var found = this.Sessions.Snapshot.Channels.SelectMany(channel => channel.Members).Select(member => member.User)
            .FirstOrDefault(user => this.IsOther(user.Name, user.WorldName))?.UserId ?? 0;
        lock (this._lock) {
            this._otherId = this._otherId == 0 ? found : this._otherId;
            return this._otherId;
        }
    }

    private long OnlyTheOther(long userId) => userId != 0 && userId == this.OtherUserId()
        ? userId
        : throw new InvalidOperationException("Only the other tester.");

    private void OnDelivered(ReceivedLine line) {
        lock (this._lock) {
            if (this._pair != null && this._received.Count < MostLines) {
                var seq = ++this._seq;
                this._received.Add((seq, line with { Seq = seq }));
            }
        }
    }

    public IReadOnlyList<ReceivedLine> ReceivedSince(long mark) {
        lock (this._lock) {
            return this._received.Where(entry => entry.Seq > mark).Select(entry => entry.Line).ToList();
        }
    }

    public Task<IReadOnlyList<SelfTestInvite>> InvitesAsync() => Task.FromResult<IReadOnlyList<SelfTestInvite>>(this.Sessions.Snapshot.Invites
        .Select(invite => new SelfTestInvite(invite.ChannelId, invite.ChannelName, $"{invite.Inviter.Name}@{invite.Inviter.WorldName}", invite.Verified && !invite.InviterKeyChanged))
        .ToList());

    /// <summary>
    /// Accepts an invite, checked again here: from the other tester, verified, to a channel named for this run (the host's
    /// test channels, for a partner; the partner's one channel, for the host). Joined, it is the run's to leave.
    /// </summary>
    public async Task AcceptInviteAsync(string channelId) {
        (string? Other, string Code, SelfTestRole Role) pair;
        lock (this._lock) {
            pair = this._pair ?? throw new InvalidOperationException("Not two players.");
        }

        var invite = this.Sessions.Snapshot.Invites.FirstOrDefault(found => found.ChannelId == channelId) ?? throw new InvalidOperationException("No such invite.");
        var named = pair.Role == SelfTestRole.Partner
            ? PartnerSide.HostChannelIndex(invite.ChannelName, pair.Code) != null
            : invite.ChannelName == PartnerSide.OwnChannelName(pair.Code);
        if (!invite.Verified || invite.InviterKeyChanged || !this.IsOther(invite.Inviter.Name, invite.Inviter.WorldName) || !named) {
            throw new InvalidOperationException("Only the other tester's invite to this run's channel.");
        }

        await this.Session.RespondToInviteAsync(channelId, accept: true).ConfigureAwait(false);
    }

    public async Task<long?> UserIdAsync(string name, string world) {
        if (!this.IsOther(name, world)) {
            throw new InvalidOperationException("Only the other tester is looked up.");
        }

        var user = await this.Session.FindPlayerAsync(name, world).ConfigureAwait(false);
        if (user != null) {
            lock (this._lock) {
                this._otherId = user.UserId;
            }
        }

        return user?.UserId;
    }

    public Task SetBlockedAsync(long userId, bool blocked) => OnFramework(() => {
        var other = this.OnlyTheOther(userId);
        if (blocked) {
            this.Session.BlockUser(other);
        } else {
            this.Session.UnblockUser(other);
        }
    });

    public Task<bool> IsBlockedAsync(long userId) => Task.FromResult(this.Sessions.Snapshot.BlockedUsers.Any(user => user.UserId == userId));

    public Task<string?> CheckCodeAsync(string channelId) => Task.FromResult(this.Sessions.Snapshot.FindChannel(this.Own(channelId))?.CheckCode);

    public Task<IReadOnlyList<SelfTestMember>> MembersAsync(string channelId) => Task.FromResult<IReadOnlyList<SelfTestMember>>(
        this.Sessions.Snapshot.FindChannel(channelId)?.Members.Select(member => new SelfTestMember(member.User.UserId, $"{member.User.Name}@{member.User.WorldName}",
            member.Rank == Protocol.Rank.Invited, member.Online)).ToList() ?? []);

    public Task<(int Count, bool OtherIn)> PartyAsync() => OnFramework(() => {
        var count = Services.Party.Length;
        var otherIn = Services.Party.Any(member => this.IsOther(member.Name.TextValue, member.World.ValueNullable?.Name.ExtractText()));
        return (count, otherIn);
    });

    public Task<(double? Distance, bool? Friend)> OtherAsync() => OnFramework<(double?, bool?)>(() => {
        var near = LocalChatGame.Read().Players.FirstOrDefault(player => this.IsOther(player.Name, player.WorldName));
        return near == null ? (null, null) : (near.Distance, near.IsFriend);
    });

    public Task SendLocalAsync(string text) => OnFramework(() => {
        // As /lgl <text> would: its own checks (accepted, connected, who is near), never game chat.
        this._parts.Local.Send(TypedLine.Plain(text));
    });

    /// <summary>A fixed link for each kind the protocol names: a Potion, a flag in Limsa Lominsa's upper decks, Well Fed.</summary>
    private static (ChatLink Link, string Label)? LinkOf(string kind) => kind switch {
        "item" => (new ChatLink.Item(4551), "[Potion]"),
        "map" => (new ChatLink.MapFlag(128, 11, 0, 0), "[Limsa Lominsa Upper Decks]"),
        "status" => (new ChatLink.Status(48), "[Well Fed]"),
        _ => null,
    };

    public Task SendWithLinkAsync(string channelId, string text, string linkKind) {
        if (LinkOf(linkKind) is not var (link, label)) {
            throw new InvalidOperationException("Not a link the self-test sends.");
        }

        // Before ", please ignore", so the line stays a test line.
        var at = text.LastIndexOf(", please ignore", StringComparison.Ordinal);
        at = at < 0 ? text.Length : at;
        var linked = new LinkedText($"{text[..at]} {label}{text[at..]}", [new MessageLink(at + 1, label.Length, link)]);
        return this.Session.SendAsync(this.Own(channelId), linked);
    }

    public Task<uint?> NameColourOfAsync(long userId) => Task.FromResult(this.Sessions.NameColourOf(
        this.Sessions.Snapshot.Channels.SelectMany(channel => channel.Members).Select(member => member.User).FirstOrDefault(user => user.UserId == userId)));

    public Task<bool> BusyAsync() => OnFramework(() => Services.Condition[ConditionFlag.InCombat] || Services.Condition[ConditionFlag.OccupiedInCutSceneEvent]
                                                       || Services.Condition[ConditionFlag.WatchingCutscene] || Services.Condition[ConditionFlag.WatchingCutscene78]
                                                       || Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]);

    /// <summary>The player targeted now, as name@world (the window's "Use my target"), or null. Framework thread.</summary>
    public static string? TargetName() => Services.Targets.Target is IPlayerCharacter player && player.HomeWorld.ValueNullable is { } world
        ? $"{player.Name.TextValue}@{world.Name.ExtractText()}"
        : null;
}
