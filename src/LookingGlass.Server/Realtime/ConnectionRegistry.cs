using System.Collections.Concurrent;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Realtime;

/// <summary>
/// Who is online. One live connection per user; a new login replaces the old one.
/// <para>
/// Members who share a channel are told when each other come online (no connection before) and go
/// offline (their connection closes and no other replaced it), with <see cref="PresenceChanged"/>. Those
/// changes, and the online flags in the channel lists sent to clients, are decided and queued under one lock,
/// so every client receives them in the order they happened: never online twice, or offline twice, in a row.
/// </para>
/// <para>
/// Whom to tell is a database query, made before taking the lock, so a slow one holds up nobody else's login.
/// It can be out of date by then in one way that matters: someone joined a channel with the user meanwhile, and
/// their channel list showed the user's state before this change. Joins are counted (see
/// <see cref="AnnounceJoined"/>, which every join goes through before its channel list is sent), and a change
/// that sees the count move asks again, under the lock: rare, and then nobody misses it. A query out of date the
/// other way (someone left meanwhile) only tells them once more, as could happen before.
/// </para>
/// </summary>
public sealed class ConnectionRegistry(Database db, ILogger<ConnectionRegistry> logger) {
    private readonly ConcurrentDictionary<long, ClientConnection> _online = new();
    // Held while a user's coming online or going offline is decided and their co-members are told, while a
    // response's online flags are filled in and queued (see Respond), and while a join is counted.
    private readonly Lock _presence = new();
    // Members who joined a channel so far (see AnnounceJoined); changed under _presence.
    private long _joins;

    /// <summary>Runs after each query of whom to tell about a user's presence, so tests can hold it there.</summary>
    internal Action<long>? AfterCoMemberQueryForTests { get; set; }

    public void SetOnline(long userId, ClientConnection connection) {
        ClientConnection? previous;
        HashSet<long>? coMembers = null;
        long joinsSeen = 0;
        while (true) {
            lock (this._presence) {
                this._online.TryGetValue(userId, out previous);
                if (previous != null) {
                    // Another of their connections is online: this one replaces it, and nobody needs telling.
                    this._online[userId] = connection;
                    break;
                }

                if (coMembers != null) {
                    if (joinsSeen != this._joins) {
                        coMembers = this.CoMembersOf(userId);
                    }

                    this._online[userId] = connection;
                    this.SendToAll(coMembers, PresenceEvent(userId, true), except: userId);
                    break;
                }
            }

            // Coming online, so far: find out whom to tell, without the lock, then decide again under it. Joins are read
            // first, so any join that the query might have missed shows up as a changed count.
            joinsSeen = Interlocked.Read(ref this._joins);
            coMembers = this.CoMembersOf(userId);
        }

        if (previous != null && previous != connection) {
            previous.Abort("Logged in from another connection");
        }
    }

    public void SetOffline(long userId, ClientConnection connection) {
        // Only if this is still their connection: one that was replaced by a newer login changes nothing (and a connection
        // that isn't theirs now never becomes it again, so this needs no lock).
        if (!this._online.TryGetValue(userId, out var current) || current != connection) {
            return;
        }

        var joinsSeen = Interlocked.Read(ref this._joins);
        var coMembers = this.CoMembersOf(userId);
        lock (this._presence) {
            if (this._online.TryRemove(new KeyValuePair<long, ClientConnection>(userId, connection))) {
                if (joinsSeen != this._joins) {
                    coMembers = this.CoMembersOf(userId);
                }

                this.SendToAll(coMembers, PresenceEvent(userId, false), except: userId);
            }
        }
    }

    public bool IsOnline(long userId) => this._online.ContainsKey(userId);

    public int OnlineCount => this._online.Count;

    public void Send(long userId, Event ev) {
        if (this._online.TryGetValue(userId, out var connection)) {
            connection.SendEvent(ev);
        }
    }

    /// <summary>Whether a user is online on a connection that agreed to local chat (so a local message would reach them).</summary>
    public bool AcceptsLocal(long userId) => this._online.TryGetValue(userId, out var connection) && connection.LocalChatAgreed;

    /// <summary>
    /// Passes a local message to a user, if they are online on a connection that agreed to local chat and isn't too slow to
    /// take it (see <see cref="ClientConnection.TrySendDroppable"/>); otherwise it goes nowhere, as nothing is kept.
    /// </summary>
    /// <returns>Whether it was queued for them.</returns>
    public bool SendLocal(long userId, Event ev) =>
        this._online.TryGetValue(userId, out var connection) && connection.LocalChatAgreed && connection.TrySendDroppable(ev);

    public void SendToAll(IEnumerable<long> userIds, Event ev, long? except = null) {
        foreach (var userId in userIds) {
            if (userId != except) {
                this.Send(userId, ev);
            }
        }
    }

    public void Disconnect(long userId, string reason) {
        if (this._online.TryGetValue(userId, out var connection)) {
            connection.Abort(reason);
        }
    }

    /// <summary>
    /// Tells a channel's online members that <paramref name="userId"/>, who just joined it, is online, if
    /// they still are. Members who already shared a channel with them knew; the others couldn't have. Not those whose place
    /// there was removed from their list: they're told nothing about the channel.
    /// <para>
    /// Every join comes here after it is stored and before the joiner's channel list is sent, and is counted, so a
    /// presence change whose query of whom to tell was made before the join asks again (see the class summary).
    /// </para>
    /// </summary>
    public void AnnounceJoined(string channelId, long userId) {
        var members = db.GetMembers(channelId).Where(member => !member.Forgotten).Select(member => member.User.UserId).ToList();
        lock (this._presence) {
            Interlocked.Increment(ref this._joins);
            if (!this._online.ContainsKey(userId)) {
                return;
            }

            this.SendToAll(members, PresenceEvent(userId, true), except: userId);
        }
    }

    /// <summary>
    /// Sets each member's online flag in a channel's info (never an invitee's: their presence isn't shared).
    /// </summary>
    public void MarkOnline(ChannelInfo info) {
        foreach (var member in info.Members) {
            member.Online = member.Rank >= Rank.Member && member.User != null && this._online.ContainsKey(member.User.UserId);
        }
    }

    /// <summary>
    /// Queues a response. One carrying channel info gets its online flags filled in again first, under the
    /// presence lock: every <see cref="PresenceChanged"/> queued before it is already reflected in it, and
    /// every one after it is newer, so the client can apply both in the order they arrive.
    /// </summary>
    public void Respond(ClientConnection connection, Response response) {
        IEnumerable<ChannelInfo> channels = response.ResultCase switch {
            Response.ResultOneofCase.Channel => [response.Channel],
            Response.ResultOneofCase.ChannelList => response.ChannelList.Channels,
            _ => [],
        };

        var infos = channels.ToList();
        if (infos.Count == 0) {
            connection.SendResponse(response);
            return;
        }

        lock (this._presence) {
            foreach (var info in infos) {
                this.MarkOnline(info);
            }

            connection.SendResponse(response);
        }
    }

    /// <summary>The members of channels the user is a member of (see <see cref="Database.GetCoMemberIds"/>): whom to tell about the user's presence.</summary>
    private HashSet<long> CoMembersOf(long userId) {
        HashSet<long> coMembers;
        try {
            coMembers = db.GetCoMemberIds(userId);
        } catch (Exception ex) {
            // Presence is a courtesy: a database hiccup mustn't stop a login or a disconnect.
            logger.LogWarning(ex, "Couldn't look up whom to tell that {User} came online or went offline", userId);
            return [];
        }

        this.AfterCoMemberQueryForTests?.Invoke(userId);
        return coMembers;
    }

    private static Event PresenceEvent(long userId, bool online) => new() { PresenceChanged = new PresenceChanged { UserId = userId, Online = online } };
}
