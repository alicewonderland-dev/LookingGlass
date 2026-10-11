using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;
using LookingGlass.Server.Services;
using OperatorViewMessage = LookingGlass.Protocol.OperatorView;

namespace LookingGlass.Server.Realtime;

/// <summary>
/// The plugin's operator panel (capability "operator.v1"; see "The plugin's operator panel" in docs/operator-tools-plan.md):
/// an operator character's login is told it is one, reads the flags and bans (addresses shortened), and bans or lifts bans on
/// characters, each signed by its current identity key. Every check is the server's: who is an operator is decided afresh
/// for every request (the character listed, its account's current keys the pinned ones), and what may be done is
/// <see cref="OperatorActions"/>' (the plugin's row of <see cref="OperatorPolicy"/>), which records every attempt.
/// </summary>
public sealed partial class RequestHandler {
    /// <summary>How long an operator view's nonce (<see cref="OperatorViewMessage.Nonce"/>) can be signed and sent back.</summary>
    internal static readonly TimeSpan OperatorNonceLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The most operator nonces a connection holds at once (the newest).</summary>
    private const int MaxOperatorNonces = 4;

    /// <summary>The most lines of each list one operator view carries (newest first); the web page has them all.</summary>
    internal const int OperatorListLines = 10;

    /// <summary>What anyone who isn't an operator (now) is told, whatever they asked: nothing about who is.</summary>
    internal const string OperatorsOnly = "Only this server's operators can do that.";

    /// <summary>The name refusals of operator requests by a non-operator are counted under, towards flagging (there is no setting).</summary>
    internal const string OperatorRequestsLimit = "OperatorRequests";

    /// <summary>The name the operator lists' own limit is counted under.</summary>
    internal const string OperatorViewsLimit = "OperatorViews";

    // The operator's lists (and the fresh lists each ban or lift is answered with): 20 at once, then 1 every 3 seconds.
    private readonly UserRateLimits _operatorViews = new(perSecond: 1.0 / 3, burst: 20, time);

    /// <summary>
    /// Says in a login's answer how its character stands as an operator, when the connection agreed to "operator.v1": a
    /// character the settings don't list is told nothing more than "not listed", as everyone else.
    /// </summary>
    private void TellOperatorStatus(ClientConnection connection, UserRow user, AuthenticateOk ok) {
        if (!connection.OperatorAgreed || operators == null) {
            return;
        }

        try {
            switch (operators.StatusOf(user.UserId)) {
                case OperatorStatus.Operator:
                    ok.OperatorStatus = OperatorLoginStatus.Operator;
                    break;
                case OperatorStatus.KeysChanged:
                    ok.OperatorStatus = OperatorLoginStatus.KeysChanged;
                    if (db.GetOperatorPin(user.UserId) is { } pin) {
                        ok.OperatorPinnedKeyHash = ByteString.CopyFrom(pin.KeyHash);
                    }

                    break;
                case OperatorStatus.NotPinned:
                    ok.OperatorStatus = OperatorLoginStatus.NotPinned;
                    break;
            }
        } catch (Exception ex) {
            // The login stands; the plugin just shows no Operator section this time.
            logger.LogWarning(ex, "Couldn't tell whether user {User} is an operator", user.UserId);
        }
    }

    /// <summary>The flags and bans as an operator's plugin sees them, with a nonce for one signed ban or lift.</summary>
    private Response GetOperatorView(ClientConnection connection, GetOperatorView request) {
        var me = this.RequireOperator(connection);
        this.TakeOperatorRead(me);
        return new Response { OperatorView = this.OperatorViewFor(connection, me, request.AboutUserId) };
    }

    /// <summary>Bans a character, signed by the operator character's current identity key; answered with a fresh view.</summary>
    private Response OperatorBan(ClientConnection connection, OperatorBan request) {
        var me = this.RequireOperator(connection);
        this.TakeOperatorRead(me);
        this.CheckOperatorAction(connection, me, OperatorActionProof.Actions.Ban, request.UserId, request.Days, request.Reason, request.ServerUrl,
            request.Nonce, request.Signature);
        var days = request.Days == 0 ? (int?) null : (int) Math.Min(request.Days, int.MaxValue);
        var result = operators!.Ban(OperatorActorOf(connection, me), request.UserId.ToString(CultureInfo.InvariantCulture), days, request.Reason);
        if (!result.Done) {
            throw OperatorRefused(result);
        }

        logger.LogInformation("Operator user {User} banned user {Target} from the plugin", me.UserId, request.UserId);
        return new Response { OperatorView = this.OperatorViewFor(connection, me, request.UserId) };
    }

    /// <summary>Lifts the ban on a character, signed as a ban is; answered with a fresh view.</summary>
    private Response OperatorUnban(ClientConnection connection, OperatorUnban request) {
        var me = this.RequireOperator(connection);
        this.TakeOperatorRead(me);
        this.CheckOperatorAction(connection, me, OperatorActionProof.Actions.Lift, request.UserId, 0, "", request.ServerUrl, request.Nonce, request.Signature);
        var result = operators!.Lift(OperatorActorOf(connection, me), request.UserId.ToString(CultureInfo.InvariantCulture));
        if (!result.Done) {
            throw OperatorRefused(result);
        }

        logger.LogInformation("Operator user {User} lifted the ban on user {Target} from the plugin", me.UserId, request.UserId);
        return new Response { OperatorView = this.OperatorViewFor(connection, me, request.UserId) };
    }

    /// <summary>
    /// The account behind an operator request, as it is now, if it is an operator now: logged in, on a connection that agreed
    /// to "operator.v1", its character listed and its account's current keys the pinned ones (checked again at every
    /// request, so keys registered since, even while this request was on its way, end it). Anyone else is refused, in the same
    /// words whatever the reason, and the refusal counts towards flagging, as one by a limit does.
    /// </summary>
    private UserRow RequireOperator(ClientConnection connection) {
        var me = RequireUser(connection);
        if (!connection.OperatorAgreed || operators == null) {
            // As a server without the capability answers.
            throw new RequestException(ErrorCode.InvalidRequest, "Unknown request.");
        }

        if (operators.StatusOf(me.UserId) != OperatorStatus.Operator || db.GetUser(me.UserId) is not { } user) {
            throw new RequestException(ErrorCode.Forbidden, OperatorsOnly) { Limit = OperatorRequestsLimit };
        }

        return user;
    }

    private void TakeOperatorRead(UserRow me) {
        if (!this._operatorViews.TryTake(me.UserId, out var wait)) {
            throw new RequestException(ErrorCode.RateLimited, $"You're asking for the operator lists too often; try again in {AboutHowLong(wait)}.") {
                Limit = OperatorViewsLimit,
            };
        }
    }

    /// <summary>
    /// Checks a signed ban or lift, in this order: the address it was signed for is this server's, it is signed by the
    /// account's current identity key for this connection's login, and its nonce is one of this connection's, unexpired and
    /// unused (used up now). The policy comes after, in <see cref="OperatorActions"/>.
    /// </summary>
    private void CheckOperatorAction(ClientConnection connection, UserRow me, string action, long target, long days, string reason, string serverUrl,
        ByteString nonce, ByteString signature) {
        if (connection.DeviceTokenHash is not { } tokenHash) {
            throw new RequestException(ErrorCode.NotAuthenticated, "Log in first.");
        }

        // Before the signature, as for key login: one made for another server is what a relay would bring.
        if (this.NotThisServer(connection, serverUrl) is { } elsewhere) {
            logger.LogWarning("Operator {Action} by user {User} from {Address} refused: {Reason}", action, me.UserId, connection.RemoteAddress, elsewhere);
            throw new RequestException(ErrorCode.Forbidden,
                this.WrongAddressMessage(connection, serverUrl) + " Nothing was done: connect through an address this server accepts, then try again.");
        }

        if (!OperatorActionProof.Verify(me.SigningKey, action, me.UserId, target, days, reason, tokenHash, serverUrl, nonce.Span, signature.Span)) {
            // A copy of the login without the key, or a plugin with keys the account no longer has: worth the operator's notice.
            logger.LogWarning("Operator {Action} by user {User} from {Address} refused: not signed by the account's identity key for this login", action, me.UserId,
                connection.RemoteAddress);
            throw new RequestException(ErrorCode.Forbidden, "That isn't signed with this character's identity key for this login, so nothing was done.");
        }

        // Single use: found or not, it can't be named again.
        var now = this._time.GetUtcNow();
        var given = connection.OperatorNonces.FindIndex(entry => nonce.Span.SequenceEqual(entry.Nonce));
        var good = given >= 0 && now < connection.OperatorNonces[given].Expires;
        if (given >= 0) {
            connection.OperatorNonces.RemoveAt(given);
        }

        if (!good) {
            throw new RequestException(ErrorCode.InvalidRequest, "Refresh the operator lists, then try again.");
        }
    }

    private static OperatorActor OperatorActorOf(ClientConnection connection, UserRow me) =>
        new(OperatorTool.Plugin, $"{me.Name}@{me.WorldName}", connection.RemoteAddress, me.UserId);

    /// <summary>A ban or lift <see cref="OperatorActions"/> refused, as the plugin is told: why, in its words for any tool.</summary>
    private static RequestException OperatorRefused(OperatorResult result) => new(result.Refusal switch {
        OperatorRefusal.NotValid or OperatorRefusal.NotATarget => ErrorCode.InvalidRequest,
        OperatorRefusal.NothingToLift or OperatorRefusal.NoSuchCharacter => ErrorCode.NotFound,
        OperatorRefusal.HourLimit => ErrorCode.LimitReached,
        _ => ErrorCode.Forbidden,
    }, result.Why);

    /// <summary>The operator's lists, at most <see cref="OperatorListLines"/> lines of each, never a full address, with a fresh nonce.</summary>
    private OperatorViewMessage OperatorViewFor(ClientConnection connection, UserRow me, long aboutUserId) {
        var seen = operators!.See(OperatorTool.Plugin);
        var flags = seen.Flags.OrderByDescending(flag => flag.Flag.LastRefusedAt).ToList();
        var inForce = seen.InForce.OrderByDescending(ban => ban.Ban.CreatedAt).ThenByDescending(ban => ban.Ban.BanId).ToList();
        var history = seen.History.OrderByDescending(ban => ban.Ban.LiftedAt ?? ban.Ban.ExpiresAt ?? 0).ThenByDescending(ban => ban.Ban.BanId).ToList();
        var listed = operators.Operators();

        var shownFlags = flags.Take(OperatorListLines).ToList();
        var shownInForce = inForce.Take(OperatorListLines).ToList();
        var shownHistory = history.Take(OperatorListLines).ToList();
        var aboutBan = aboutUserId == 0 ? null : inForce.FirstOrDefault(ban => ban.Ban.UserId == aboutUserId);
        var aboutFlag = aboutUserId == 0 ? null : flags.FirstOrDefault(flag => flag.Flag.UserId == aboutUserId);
        var ids = shownFlags.Select(flag => flag.Flag.UserId).Concat(shownInForce.Concat(shownHistory).Select(ban => ban.Ban.UserId)).OfType<long>()
            .Append(aboutUserId).Where(id => id != 0).Distinct();
        var users = db.GetUsers(ids).ToDictionary(user => user.UserId);

        var view = new OperatorViewMessage {
            FlagsTotal = (uint) flags.Count,
            InForceTotal = (uint) inForce.Count,
            HistoryTotal = (uint) history.Count,
            BlockedAutomatically = (uint) inForce.Count(ban => ban.Ban.Automatic),
            PageUrl = options.Value.Admin.PageLink ?? "",
            BansLeftThisHour = (uint) (operators.BansLeftThisHour(OperatorActorOf(connection, me)) ?? 0),
            Nonce = ByteString.CopyFrom(this.NewOperatorNonce(connection)),
            FlagHours = (uint) seen.FlagHours,
            HistoryDays = (uint) seen.HistoryDays,
        };
        view.Flags.AddRange(shownFlags.Select(flag => FlagLine(flag, users)));
        view.InForce.AddRange(shownInForce.Select(ban => BanLine(ban, users)));
        view.History.AddRange(shownHistory.Select(ban => BanLine(ban, users)));
        view.Operators.AddRange(listed.Where(entry => entry.User != null).Select(entry => entry.User!.ToProto()));
        if (aboutUserId != 0) {
            view.About = new OperatorSubject {
                UserId = aboutUserId,
                User = users.GetValueOrDefault(aboutUserId)?.ToProto(),
                ListedOperator = operators.IsListed(aboutUserId),
                Ban = aboutBan == null ? null : BanLine(aboutBan, users),
                Flag = aboutFlag == null ? null : FlagLine(aboutFlag, users),
            };
        }

        return view;
    }

    /// <summary>A ban as the plugin is shown it: the address only as <see cref="ShownBan.ShownAddress"/> (shortened), and only the plugin's maker named.</summary>
    private static OperatorBanLine BanLine(ShownBan shown, IReadOnlyDictionary<long, UserRow> users) {
        var ban = shown.Ban;
        return new OperatorBanLine {
            UserId = ban.UserId ?? 0,
            User = ban.UserId is { } id ? users.GetValueOrDefault(id)?.ToProto() : null,
            Address = shown.ShownAddress ?? "",
            Reason = ban.Reason,
            CreatedUnix = ban.CreatedAt,
            UntilUnix = ban.ExpiresAt ?? 0,
            Automatic = ban.Automatic,
            MadeVia = ban.Automatic && ban.MadeVia == null ? BanTools.Automatic : ban.MadeVia ?? "",
            // As in Discord: only the plugin's operator character is named, never a Unix user or a Tailscale login.
            MadeBy = ban.MadeVia == BanTools.Plugin ? ban.MadeBy ?? "" : "",
            LiftedUnix = ban.LiftedAt ?? 0,
            LiftedVia = ban.LiftedVia ?? "",
            LiftedBy = ban.LiftedVia == BanTools.Plugin ? ban.LiftedBy ?? "" : "",
            Replaced = ban.ReplacedBy != null,
        };
    }

    /// <summary>A flag as the plugin is shown it: the address only as <see cref="ShownFlag.ShownAddress"/> (shortened).</summary>
    private static OperatorFlag FlagLine(ShownFlag shown, IReadOnlyDictionary<long, UserRow> users) {
        var flag = shown.Flag;
        return new OperatorFlag {
            UserId = flag.UserId ?? 0,
            User = flag.UserId is { } id ? users.GetValueOrDefault(id)?.ToProto() : null,
            Address = shown.ShownAddress ?? "",
            Why = flag.Why,
            Limits = flag.Limits,
            FirstUnix = flag.FirstFlaggedAt,
            LastUnix = flag.LastRefusedAt,
            Banned = shown.Banned,
        };
    }

    /// <summary>A fresh nonce for one signed ban or lift on this connection; the last few given stay good, for a few minutes.</summary>
    private byte[] NewOperatorNonce(ClientConnection connection) {
        var nonce = RandomNumberGenerator.GetBytes(OperatorActionProof.NonceSize);
        var now = this._time.GetUtcNow();
        connection.OperatorNonces.RemoveAll(entry => now >= entry.Expires);
        if (connection.OperatorNonces.Count >= MaxOperatorNonces) {
            connection.OperatorNonces.RemoveAt(0);
        }

        connection.OperatorNonces.Add((nonce, now + OperatorNonceLifetime));
        return nonce;
    }
}
