namespace LookingGlass.Core.SelfTest;

/// <summary>The partner didn't answer in time, or has left: the step is skipped (and, once gone, every check left that needs it).</summary>
public sealed class PartnerSilentException(bool gone) : Exception(gone ? SelfTestWords.PartnerGone : SelfTestWords.PartnerSilent) {
    public bool Gone { get; } = gone;
}

/// <summary>The partner's plugin said no (past a cap, or not something it does): the step is skipped.</summary>
public sealed class PartnerRefusedException() : Exception(SelfTestWords.PartnerRefused);

/// <summary>A line with the run's prefix came from someone else than the two testers: the run stops (someone is meddling).</summary>
public sealed class PartnerMeddlingException() : Exception(SelfTestWords.Meddling);

/// <summary>
/// The host's side of two players (docs/self-test-plan.md, "Two players"): pairing (one lookup, one invite, then the
/// partner's hello within two minutes), and numbered requests to the partner's plugin over ordinary messages in the test
/// channel, each answered within a minute (ten for one that waits on the partner's player). A silence asks "still-there";
/// a second one takes the partner as gone, as does seeing them out of the test channel. A line with the run's prefix from
/// anyone but the two testers stops the run. The partner's name is the run file's only, while the run lasts.
/// </summary>
public sealed class PartnerLink {
    private readonly SelfTestRun _run;
    private int _silences;

    internal PartnerLink(SelfTestRun run) {
        this._run = run;
    }

    private ISelfTestHost Host => this._run.Host;

    private SelfTestRunData Data => this._run.Data;

    /// <summary>The partner, name@world: held while the run lasts only.</summary>
    public string Partner => this.Data.Partner ?? "";

    /// <summary>The partner joined and said hello (kept in the run file: a reload carries on paired).</summary>
    public bool Paired => this.Data.PartnerHello != null;

    /// <summary>The partner stopped answering (twice), or left the test channel, or was told bye: no more requests.</summary>
    public bool Gone { get; private set; }

    /// <summary>The partner's facts, as its hello said: its version, private place, ChatTwo, mode, place kind, data centre.</summary>
    public IReadOnlyDictionary<string, string> Hello => this.Data.PartnerHello ?? new Dictionary<string, string>();

    /// <summary>The test channel the partner joins first (the run's first): every request goes there.</summary>
    public string? ChannelId => this.Data.TestChannels.FirstOrDefault();

    /// <summary>The partner's user ID (looked up when pairing).</summary>
    public long UserId => this.Data.OtherUserId;

    /// <summary>Is this the partner (a line's sender, name@world)?</summary>
    public bool IsPartner(string sender) => this.Partner.Length > 0 && string.Equals(sender, this.Partner, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the checks that need the partner can't run now, or null.</summary>
    public string? Lacks => this.Gone ? SelfTestWords.PartnerGone : !this.Paired ? this._unpaired ?? SelfTestWords.PartnerDidntJoin : null;

    // Why pairing didn't work, if it was tried.
    private string? _unpaired;

    // ================================================================ pairing

    /// <summary>
    /// Pairs: looks the partner up (they must be registered here, and not blocked), makes the test channel, invites them,
    /// and waits for their plugin to join and say hello (two minutes). Carried on after a reload, it asks for hello again.
    /// </summary>
    /// <param name="waiting">Told how long it waits still, for the window.</param>
    /// <returns>Why not, or null once paired.</returns>
    public async Task<string?> PairAsync(StepContext context, Action<TimeSpan>? waiting = null) {
        this._unpaired = await this.TryPairAsync(context, waiting).ConfigureAwait(false);
        return this._unpaired;
    }

    private async Task<string?> TryPairAsync(StepContext context, Action<TimeSpan>? waiting) {
        if (!SplitName(this.Partner, out var name, out var world)) {
            return SelfTestWords.NoPartnerName;
        }

        if (this.UserId == 0) {
            var userId = await context.ServerAsync(SelfTestAction.Lookup, () => this.Host.UserIdAsync(name, world)).ConfigureAwait(false);
            if (userId is not { } id) {
                return SelfTestWords.PartnerNotRegistered;
            }

            this.Data.OtherUserId = id;
            this.Data.OtherWasBlocked = await this.Host.IsBlockedAsync(id).ConfigureAwait(false);
        }

        if (this.Data.OtherWasBlocked) {
            // Their lines would be hidden: the run never unblocks someone the tester blocked.
            return SelfTestWords.PartnerWasBlocked;
        }

        var channel = await context.TestChannelAsync().ConfigureAwait(false);
        var members = await this.Host.MembersAsync(channel).ConfigureAwait(false);
        if (this.Paired && members.Any(member => member.UserId == this.UserId && !member.Invited)) {
            // Carried on (a reload): still there? Its answer is its hello.
            try {
                var hello = await this.AskAsync(context, PartnerVerb.Hello).ConfigureAwait(false);
                this.Data.PartnerHello = hello.Fields.ToDictionary();
                return null;
            } catch (PartnerSilentException) {
                return SelfTestWords.PartnerSilent;
            }
        }

        var mark = this.Host.Mark();
        if (members.All(member => member.UserId != this.UserId)) {
            await this.InviteAsync(context, channel).ConfigureAwait(false);
        }

        PartnerAnswer? found = null;
        var until = this.Host.Now + SelfTestProtocol.PairingWithin;
        await context.WaitUntilAsync(() => {
            waiting?.Invoke(until - this.Host.Now);
            found = this.AnswersSince(mark, channel).FirstOrDefault(answer => answer.Outcome == PartnerOutcome.Hello);
            return Task.FromResult(found != null);
        }, SelfTestProtocol.PairingWithin).ConfigureAwait(false);
        if (found == null) {
            return SelfTestWords.PartnerDidntJoin;
        }

        this.Data.PartnerHello = found.Fields.ToDictionary();
        this._silences = 0;
        return null;
    }

    /// <summary>Splits "Name Surname@World".</summary>
    public static bool SplitName(string? who, out string name, out string world) {
        var at = who?.IndexOf('@') ?? -1;
        name = at > 0 ? who![..at].Trim() : "";
        world = at > 0 ? who![(at + 1)..].Trim() : "";
        return name.Length > 0 && world.Length > 0;
    }

    /// <summary>Invites the partner (only them) to one of the run's test channels: a lookup and an invite, both paced.</summary>
    public async Task InviteAsync(StepContext context, string channelId) {
        if (!SplitName(this.Partner, out var name, out var world) || !this.Data.TestChannels.Contains(channelId)) {
            throw new InvalidOperationException("Only the partner, to the run's own test channel.");
        }

        // The invite looks them up (as the right-click menu does): both paced, a refusal stops the run.
        await context.ServerAsync(SelfTestAction.Lookup, () => Task.CompletedTask).ConfigureAwait(false);
        await context.ServerAsync(SelfTestAction.Invite, () => this.Host.InviteAsync(channelId, name, world)).ConfigureAwait(false);
    }

    /// <summary>Invites the partner to a channel and waits (a minute) for its plugin to accept: whether it did.</summary>
    public async Task<bool> InviteAndWaitAsync(StepContext context, string channelId) {
        await this.InviteAsync(context, channelId).ConfigureAwait(false);
        return await this.WaitMemberAsync(context, channelId, member: true).ConfigureAwait(false);
    }

    /// <summary>Waits (a minute) until the partner is a member of a channel (joined, not invited), or isn't any more.</summary>
    public Task<bool> WaitMemberAsync(StepContext context, string channelId, bool member) => context.WaitUntilAsync(async () =>
        (await this.Host.MembersAsync(channelId).ConfigureAwait(false)).Any(found => found.UserId == this.UserId && !found.Invited) == member, SelfTestProtocol.AnswerWithin);

    /// <summary>
    /// Accepts the partner's one invite to its one channel of its own ("LG self-test K7Q2 p", verified), waiting a minute,
    /// and no other invite: its ID, or null. The channel is the partner's: the clean-up only leaves it.
    /// </summary>
    public async Task<string?> AcceptOwnChannelAsync(StepContext context) {
        string? accepted = null;
        await context.WaitUntilAsync(async () => {
            foreach (var invite in await this.Host.InvitesAsync().ConfigureAwait(false)) {
                if (invite.Verified && this.IsPartner(invite.Inviter) && invite.ChannelName == PartnerSide.OwnChannelName(this._run.Code)) {
                    await this.Host.AcceptInviteAsync(invite.ChannelId).ConfigureAwait(false);
                    context.MarkJoined(invite.ChannelId);
                    accepted = invite.ChannelId;
                    return true;
                }
            }

            return false;
        }, SelfTestProtocol.AnswerWithin).ConfigureAwait(false);
        return accepted;
    }

    // ================================================================ requests

    /// <summary>The partner's answers in a channel since a mark; stops the run on a line with the prefix from anyone else.</summary>
    private IEnumerable<PartnerAnswer> AnswersSince(long mark, string channel) {
        foreach (var line in this.Host.ReceivedSince(mark).Where(line => !line.Own)) {
            var parsed = SelfTestProtocol.Parse(this._run.Code, line.Text);
            if (parsed is ProtocolLine.NotOurs) {
                continue;
            }

            if (!this.IsPartner(line.Sender) && (this.UserId == 0 || line.SenderId != this.UserId)) {
                throw new PartnerMeddlingException();
            }

            if (line.ChannelId == channel && parsed is ProtocolLine.Answer answer) {
                yield return answer.Value;
            }
        }
    }

    /// <summary>Sends a request to the partner, paced as a channel message: its number.</summary>
    public async Task<int> SendAsync(StepContext context, PartnerVerb verb, params string[] args) {
        if (this.Gone || !this.Paired || this.ChannelId is not { } channel) {
            throw new PartnerSilentException(gone: true);
        }

        if ((await this.Host.MembersAsync(channel).ConfigureAwait(false)).All(member => member.UserId != this.UserId)) {
            // Neither in the test channel nor invited to it: their run has ended.
            this.Gone = true;
            throw new PartnerSilentException(gone: true);
        }

        var number = ++this.Data.ProtocolNumber;
        var line = SelfTestProtocol.Write(this._run.Code, new PartnerRequest(number, verb, args));
        await context.ServerAsync(SelfTestAction.PartnerMessage, () => this.Host.SendAsync(channel, line)).ConfigureAwait(false);
        return number;
    }

    /// <summary>Waits for request <paramref name="number"/>'s answer, from a mark taken before it was sent: null if none came.</summary>
    public async Task<PartnerAnswer?> WaitAsync(StepContext context, long mark, int number, TimeSpan within) {
        PartnerAnswer? answer = null;
        var channel = this.ChannelId!;
        await context.WaitUntilAsync(() => {
            answer = this.AnswersSince(mark, channel).FirstOrDefault(found => found.Number == number);
            return Task.FromResult(answer != null);
        }, within).ConfigureAwait(false);
        return answer;
    }

    /// <summary>How long the host waits for a request's answer.</summary>
    public static TimeSpan WithinFor(PartnerVerb verb, IReadOnlyList<string> args) => verb switch {
        PartnerVerb.Ask or PartnerVerb.SendWhileAway or PartnerVerb.InviteMe => SelfTestProtocol.GuidedAnswerWithin,
        PartnerVerb.Disconnect => SelfTestProtocol.AnswerWithin + TimeSpan.FromSeconds(SelfTestProtocol.MostDisconnectSeconds),
        // A few lines at the pace of channel messages, or local chat's slower one.
        PartnerVerb.Send or PartnerVerb.Local => SelfTestProtocol.AnswerWithin + TimeSpan.FromSeconds(5 * SelfTestProtocol.MostPerSend),
        _ => SelfTestProtocol.AnswerWithin,
    };

    /// <summary>
    /// Asks the partner something and waits for the answer (see <see cref="WithinFor"/>). A silence asks "still-there"
    /// (this request is then skipped, and the run goes on); a second silence takes the partner as gone. A refusal throws
    /// <see cref="PartnerRefusedException"/>; silence <see cref="PartnerSilentException"/>.
    /// </summary>
    public async Task<PartnerAnswer> AskAsync(StepContext context, PartnerVerb verb, params string[] args) {
        var mark = this.Host.Mark();
        var number = await this.SendAsync(context, verb, args).ConfigureAwait(false);
        return await this.AnswerOfAsync(context, mark, number, verb, args).ConfigureAwait(false);
    }

    /// <summary>The answer to a request sent earlier (from the mark taken before it): as <see cref="AskAsync"/>.</summary>
    public async Task<PartnerAnswer> AnswerOfAsync(StepContext context, long mark, int number, PartnerVerb verb, params string[] args) {
        if (await this.WaitAsync(context, mark, number, WithinFor(verb, args)).ConfigureAwait(false) is { } answer) {
            this._silences = 0;
            return answer.Outcome == PartnerOutcome.Refused ? throw new PartnerRefusedException() : answer;
        }

        if (++this._silences >= 2 || verb == PartnerVerb.StillThere) {
            this.Gone = true;
            throw new PartnerSilentException(gone: true);
        }

        // Once silent: still there? If it is, only this request goes unanswered (skipped), and the run goes on.
        try {
            await this.AskAsync(context, PartnerVerb.StillThere).ConfigureAwait(false);
        } catch (PartnerSilentException) {
            this.Gone = true;
        }

        throw new PartnerSilentException(this.Gone);
    }

    /// <summary>Asks the partner's player to do something (a fixed step), and turns their answer into the step's result.</summary>
    public async Task<StepResult> AskPlayerAsync(StepContext context, PartnerAsk ask, string? confirmed = null) {
        var answer = await this.AskAsync(context, PartnerVerb.Ask, SelfTestProtocol.NameOf(ask)).ConfigureAwait(false);
        return answer.Outcome switch {
            PartnerOutcome.Yes or PartnerOutcome.Done => new StepResult(StepOutcome.Passed, Confirmed: confirmed),
            PartnerOutcome.Skipped => StepResult.Skip("your partner skipped it"),
            _ => StepResult.Fail("your partner said it didn't work"),
        };
    }

    // ================================================================ the end

    /// <summary>
    /// The clean-up's part for the partner: asks it to leave ("bye"), and waits up to a minute for it to go from every test
    /// channel, so the host leaves last (as the only member, that deletes each channel). If it doesn't go, the clean-up
    /// disbands them instead. Never throws.
    /// </summary>
    internal async Task ByeAsync() {
        if (!this.Paired || this.ChannelId is not { } channel) {
            return;
        }

        var wasGone = this.Gone;
        this.Gone = true;
        try {
            if (!wasGone) {
                var number = ++this.Data.ProtocolNumber;
                await this.Host.SendAsync(channel, SelfTestProtocol.Write(this._run.Code, new PartnerRequest(number, PartnerVerb.Bye, []))).ConfigureAwait(false);
            }

            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1); waited += TimeSpan.FromSeconds(1)) {
                var stillIn = false;
                foreach (var id in this.Data.TestChannels.ToList()) {
                    stillIn |= (await this.Host.MembersAsync(id).ConfigureAwait(false)).Any(member => member.UserId == this.UserId && !member.Invited);
                }

                if (!stillIn) {
                    return;
                }

                await this.Host.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            this.Host.Log($"[selftest] the partner couldn't be asked to leave ({ex.GetType().Name})");
        }
    }
}
