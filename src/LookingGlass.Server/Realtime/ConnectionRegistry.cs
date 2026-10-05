using System.Collections.Concurrent;
using LookingGlass.Protocol;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Realtime;

/// <summary>
/// Who is online. One live connection per user; a new login replaces the old one.
/// <para>
/// Members who share a channel are told when each other come online (no connection before) and go
/// offline (their connection closes and no other replaced it), with <see cref="PresenceChanged"/>. Those
/// changes, and the online flags in the channel lists sent to clients, are made under one lock, so every
/// client receives them in the order they happened.
/// </para>
/// </summary>
public sealed class ConnectionRegistry(Database db, ILogger<ConnectionRegistry> logger) {
    private readonly ConcurrentDictionary<long, ClientConnection> _online = new();
    // Held while a user comes online or goes offline and their co-members are told, and while a
    // response's online flags are filled in and queued (see Respond).
    private readonly Lock _presence = new();

    public void SetOnline(long userId, ClientConnection connection) {
        ClientConnection? previous;
        lock (this._presence) {
            this._online.TryGetValue(userId, out previous);
            this._online[userId] = connection;
            if (previous == null) {
                this.AnnouncePresence(userId, true);
            }
        }

        if (previous != null && previous != connection) {
            previous.Abort("Logged in from another connection");
        }
    }

    public void SetOffline(long userId, ClientConnection connection) {
        lock (this._presence) {
            // Only if this is still their connection: one that was replaced by a newer login changes nothing.
            if (this._online.TryRemove(new KeyValuePair<long, ClientConnection>(userId, connection))) {
                this.AnnouncePresence(userId, false);
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
    /// </summary>
    public void AnnounceJoined(string channelId, long userId) {
        lock (this._presence) {
            if (!this._online.ContainsKey(userId)) {
                return;
            }

            var members = db.GetMembers(channelId).Where(member => !member.Forgotten).Select(member => member.User.UserId);
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

    /// <summary>Tells the online co-members (members of a channel the user is a member of) that the user came online or went offline.</summary>
    private void AnnouncePresence(long userId, bool online) {
        HashSet<long> coMembers;
        try {
            coMembers = db.GetCoMemberIds(userId);
        } catch (Exception ex) {
            // Presence is a courtesy: a database hiccup mustn't stop a login or a disconnect.
            logger.LogWarning(ex, "Couldn't tell the co-members of {User} that they are {State}", userId, online ? "online" : "offline");
            return;
        }

        this.SendToAll(coMembers, PresenceEvent(userId, online), except: userId);
    }

    private static Event PresenceEvent(long userId, bool online) => new() { PresenceChanged = new PresenceChanged { UserId = userId, Online = online } };
}
