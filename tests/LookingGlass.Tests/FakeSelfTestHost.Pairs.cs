using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// The fake host's two-player part: channel members, invites, delivered lines (with their places in the taps), blocking,
/// the party and the other tester's distance. A test plays the other tester through <see cref="OnSent"/> and
/// <see cref="OnInvite"/> (see <see cref="ScriptedPartner"/>), or by delivering lines and invites itself.
/// </summary>
internal sealed partial class FakeSelfTestHost {
    private readonly List<(long Seq, ReceivedLine Line)> _received = [];

    public const long MyId = 1;
    public const string Me = "Alice Wonder@Lich";

    /// <summary>What <see cref="Pair"/> was told: the other tester, the code, the role.</summary>
    public (string? Other, string Code, SelfTestRole Role)? Paired { get; private set; }

    public void Pair(string? other, string code, SelfTestRole role) => this.Paired = (other, code, role);

    /// <summary>Each channel's members (and invitees): this character alone unless a test adds someone.</summary>
    public Dictionary<string, List<SelfTestMember>> Members { get; } = new();

    public List<SelfTestInvite> Invites { get; } = [];

    public List<string> Accepted { get; } = [];

    /// <summary>Who the lookups find: name@world to user ID.</summary>
    public Dictionary<string, long> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int UserLookups { get; private set; }

    public HashSet<long> Blocked { get; } = [];

    public List<(long UserId, bool Blocked)> BlockChanges { get; } = [];

    public Dictionary<string, string> CheckCodes { get; } = new();

    public (int Count, bool OtherIn) Party { get; set; } = (0, false);

    public (double? Distance, bool? Friend) Other { get; set; } = (5, true);

    public List<string> LocalSent { get; } = [];

    public List<(string Channel, string Text, string Link)> SentWithLink { get; } = [];

    public bool Busy { get; set; }

    public Dictionary<long, uint?> NameColours { get; } = new();

    /// <summary>A line sent to a channel (after it is recorded and delivered back as one's own): the other tester's turn.</summary>
    public Action<string, string>? OnSent { get; set; }

    /// <summary>An invite sent (channel, name, world): the other tester's turn. Without it, the one-player rule (<see cref="Registered"/>).</summary>
    public Action<string, string, string>? OnInvite { get; set; }

    /// <summary>Delivers a line, as the session would (a channel's, or local chat's with a null channel).</summary>
    public void Deliver(string? channelId, long senderId, string sender, string text, bool own = false, bool caughtUp = false, IReadOnlyList<ChatLink>? links = null) {
        lock (this._lock) {
            var seq = ++this._seq;
            this._received.Add((seq, new ReceivedLine(channelId, senderId, sender, own, text, links ?? [], caughtUp) { Seq = seq }));
        }
    }

    public IReadOnlyList<ReceivedLine> ReceivedSince(long mark) {
        lock (this._lock) {
            return this._received.Where(entry => entry.Seq > mark).Select(entry => entry.Line).ToList();
        }
    }

    public List<SelfTestMember> MembersOf(string channelId) {
        if (!this.Members.TryGetValue(channelId, out var members)) {
            members = [new SelfTestMember(MyId, Me, false, true)];
            this.Members[channelId] = members;
        }

        return members;
    }

    /// <summary>Someone joins (or is invited to) a channel; the channel's count follows.</summary>
    public void Join(string channelId, long userId, string who, bool invited = false) {
        var members = this.MembersOf(channelId);
        members.RemoveAll(member => member.UserId == userId);
        members.Add(new SelfTestMember(userId, who, invited, !invited));
        this.Count(channelId);
    }

    /// <summary>Someone leaves a channel.</summary>
    public void Part(string channelId, long userId) {
        this.MembersOf(channelId).RemoveAll(member => member.UserId == userId);
        this.Count(channelId);
    }

    /// <summary>Someone's member line shows them online or not.</summary>
    public void SetOnline(string channelId, long userId, bool online) {
        var members = this.MembersOf(channelId);
        var at = members.FindIndex(member => member.UserId == userId);
        if (at >= 0) {
            members[at] = members[at] with { Online = online };
        }
    }

    private void Count(string channelId) {
        var at = this.Channels.FindIndex(channel => channel.Id == channelId);
        if (at >= 0) {
            this.Channels[at] = this.Channels[at] with { Members = this.MembersOf(channelId).Count };
        }
    }

    public Task<IReadOnlyList<SelfTestInvite>> InvitesAsync() => Task.FromResult<IReadOnlyList<SelfTestInvite>>(this.Invites.ToList());

    /// <summary>The channel an accepted invite was to, by its name, as the session lists it once joined.</summary>
    public Task AcceptInviteAsync(string channelId) {
        var invite = this.Invites.First(found => found.ChannelId == channelId);
        this.Invites.Remove(invite);
        this.Accepted.Add(channelId);
        if (this.Channels.All(channel => channel.Id != channelId)) {
            this.Channels.Add(new SelfTestChannel(channelId, invite.ChannelName, 1));
        }

        this.Join(channelId, MyId, Me);
        return Task.CompletedTask;
    }

    public Task<long?> UserIdAsync(string name, string world) {
        this.UserLookups++;
        return Task.FromResult(this.Users.TryGetValue($"{name}@{world}", out var id) ? id : (long?) null);
    }

    public Task SetBlockedAsync(long userId, bool blocked) {
        this.BlockChanges.Add((userId, blocked));
        if (blocked) {
            this.Blocked.Add(userId);
        } else {
            this.Blocked.Remove(userId);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsBlockedAsync(long userId) => Task.FromResult(this.Blocked.Contains(userId));

    public Task<string?> CheckCodeAsync(string channelId) => Task.FromResult(this.CheckCodes.TryGetValue(channelId, out var code) ? code : null);

    public Task<IReadOnlyList<SelfTestMember>> MembersAsync(string channelId) => Task.FromResult<IReadOnlyList<SelfTestMember>>(this.MembersOf(channelId).ToList());

    public Task<(int Count, bool OtherIn)> PartyAsync() => Task.FromResult(this.Party);

    public Task<(double? Distance, bool? Friend)> OtherAsync() => Task.FromResult(this.Other);

    public Task SendLocalAsync(string text) {
        this.LocalSent.Add(text);
        this.Deliver(null, MyId, Me, text, own: true);
        return Task.CompletedTask;
    }

    public Task SendWithLinkAsync(string channelId, string text, string linkKind) {
        this.SentWithLink.Add((channelId, text, linkKind));
        return this.SendAsync(channelId, text);
    }

    public Task<uint?> NameColourOfAsync(long userId) => Task.FromResult(this.NameColours.TryGetValue(userId, out var colour) ? colour : null);

    public Task<bool> BusyAsync() => Task.FromResult(this.Busy);
}

/// <summary>
/// A partner's plugin as a host test needs it: joins when invited (and says hello), answers each request as told (by
/// default "done"), leaves when asked, and goes on "bye". Everything happens inside the host's own flow, so it is
/// deterministic.
/// </summary>
internal sealed class ScriptedPartner {
    public const long Id = 42;
    public const string Who = "Bob Hatter@Lich";

    private readonly FakeSelfTestHost _host;

    public ScriptedPartner(FakeSelfTestHost host) {
        this._host = host;
        host.Users[Who] = Id;
        host.OnInvite = this.Invited;
        host.OnSent = this.Sent;
    }

    private string Code => this._host.Paired?.Code ?? "";

    public bool JoinsOnInvite { get; set; } = true;

    public bool SaysHello { get; set; } = true;

    /// <summary>Doesn't answer requests at all.</summary>
    public bool Silent { get; set; }

    /// <summary>Requests it doesn't answer.</summary>
    public Func<PartnerRequest, bool>? Ignore { get; set; }

    /// <summary>Leaves every channel when told bye (by default).</summary>
    public bool LeavesOnBye { get; set; } = true;

    /// <summary>The partner's one channel of its own (invite-me).</summary>
    public const string OwnChannel = "partner-channel";

    /// <summary>Its hello's fields.</summary>
    public Dictionary<string, string> Facts { get; } = new() { ["v"] = "0.2.17", ["private"] = "yes", ["place"] = "house", ["chattwo"] = "off", ["mode"] = "simple", ["dc"] = "1" };

    /// <summary>The answer to a request, or null for none: by default "done" (hello and still-there with its facts).</summary>
    public Func<PartnerRequest, string, PartnerAnswer?>? Answer { get; set; }

    public List<PartnerRequest> Requests { get; } = [];

    public int Invites { get; private set; }

    private void Invited(string channelId, string name, string world) {
        this.Invites++;
        if (!this.JoinsOnInvite) {
            this._host.Join(channelId, Id, Who, invited: true);
            return;
        }

        this._host.Join(channelId, Id, Who);
        if (this.SaysHello && this.Invites == 1) {
            this.Say(channelId, new PartnerAnswer(0, PartnerOutcome.Hello, this.Facts));
        }
    }

    public void Say(string channelId, PartnerAnswer answer) => this._host.Deliver(channelId, Id, Who, SelfTestProtocol.Write(this.Code, answer));

    private void Sent(string channelId, string text) {
        if (SelfTestProtocol.Parse(this.Code, text) is not ProtocolLine.Request { Value: var request }) {
            return;
        }

        this.Requests.Add(request);
        if (request.Verb == PartnerVerb.Bye) {
            if (this.LeavesOnBye) {
                foreach (var channel in this._host.Members.Keys.ToList()) {
                    this._host.Part(channel, Id);
                }
            }

            return;
        }

        if (this.Silent || this.Ignore?.Invoke(request) == true) {
            return;
        }

        if (request.Verb == PartnerVerb.InviteMe) {
            // Its one channel of its own, and its one invite: to the host.
            this._host.Join(OwnChannel, Id, Who);
            this._host.Invites.Add(new SelfTestInvite(OwnChannel, PartnerSide.OwnChannelName(this.Code), Who, true));
        }

        var answer = this.Answer?.Invoke(request, channelId) ?? Default(request, this.Facts);
        if (answer != null) {
            this.Say(channelId, answer);
        }

        if (request.Verb == PartnerVerb.Leave) {
            this._host.Part(request.Option("in") is { } letter ? this.ChannelOf(letter) ?? channelId : channelId, Id);
        }
    }

    /// <summary>The host's test channel a letter names ("b": the run's second), by its name.</summary>
    public string? ChannelOf(string letter) =>
        this._host.Channels.FirstOrDefault(channel => channel.Name == SelfTestRun.ChannelName(this.Code, letter[0] - 'a'))?.Id;

    public static PartnerAnswer Default(PartnerRequest request, IReadOnlyDictionary<string, string> facts) =>
        new(request.Number, PartnerOutcome.Done, request.Verb is PartnerVerb.Hello or PartnerVerb.StillThere ? facts : new Dictionary<string, string>());
}
