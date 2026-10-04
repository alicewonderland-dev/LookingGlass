using System.Collections.Concurrent;
using WonderlandChat.Protocol;

namespace WonderlandChat.Server.Realtime;

/// <summary>Who is online. One live connection per user; a new login replaces the old one.</summary>
public sealed class ConnectionRegistry {
    private readonly ConcurrentDictionary<long, ClientConnection> _online = new();

    public void SetOnline(long userId, ClientConnection connection) {
        ClientConnection? previous = null;
        this._online.AddOrUpdate(userId, connection, (_, existing) => {
            previous = existing;
            return connection;
        });

        if (previous != null && previous != connection) {
            previous.Abort("Logged in from another connection");
        }
    }

    public void SetOffline(long userId, ClientConnection connection) {
        this._online.TryRemove(new KeyValuePair<long, ClientConnection>(userId, connection));
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
}
