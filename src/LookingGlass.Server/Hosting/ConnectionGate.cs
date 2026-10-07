using LookingGlass.Server.Services;

namespace LookingGlass.Server.Hosting;

/// <summary>Why <see cref="ConnectionGate.TryAdmit"/> refused a connection.</summary>
public enum ConnectionRefusal {
    None,

    /// <summary>The address has too many new connections this minute.</summary>
    TooManyNew,

    /// <summary>The address has as many connections open as it may.</summary>
    AddressFull,

    /// <summary>The address has as many connections that haven't logged in as it may.</summary>
    TooManyNotLoggedIn,

    /// <summary>The server has as many connections as it takes, and every one has logged in.</summary>
    ServerFull,
}

/// <summary>
/// Which WebSocket connections the server takes. Per address (see <see cref="ClientAddresses.ConnectionLimitKey"/>): so many
/// new ones a minute, so many open, and only a few that haven't logged in yet (a plugin with a saved login logs in
/// within milliseconds; registering takes longer, but one at a time). In all, at most <c>max</c>; at that cap a new
/// connection is still taken, and the oldest that hasn't logged in is closed to make room (one that isn't registering,
/// if there is one; see <see cref="ChooseToMakeRoomLocked"/> for which registering one), so connections that never log in
/// can't fill the server and keep out plugins reconnecting. Only when every connection has logged in is a new one refused.
/// </summary>
public sealed class ConnectionGate(int max, int perAddress, int notLoggedInPerAddress, int newPerMinute, TimeProvider? time = null) {
    private readonly Lock _lock = new();
    private readonly WindowCounter _new = new(newPerMinute, TimeSpan.FromMinutes(1), time);
    private readonly Dictionary<string, Counts> _addresses = new();
    // Oldest first.
    private readonly LinkedList<Ticket> _notLoggedIn = new();
    private int _open;

    /// <summary>Connections open now.</summary>
    public int Open {
        get {
            lock (this._lock) {
                return this._open;
            }
        }
    }

    /// <summary>Connections open now that haven't logged in.</summary>
    public int NotLoggedIn {
        get {
            lock (this._lock) {
                return this._notLoggedIn.Count;
            }
        }
    }

    /// <summary>Takes a new connection from <paramref name="address"/>, as one that hasn't logged in, if it may be.</summary>
    /// <returns>Its ticket (dispose it when the connection closes), or why it was refused.</returns>
    public (Ticket? Ticket, ConnectionRefusal Refusal) TryAdmit(string address) {
        if (!this._new.TryAdd(address)) {
            return (null, ConnectionRefusal.TooManyNew);
        }

        Ticket? evicted = null;
        Ticket ticket;
        lock (this._lock) {
            var counts = this._addresses.GetValueOrDefault(address);
            if (counts.Open >= perAddress) {
                return (null, ConnectionRefusal.AddressFull);
            }

            if (counts.NotLoggedIn >= notLoggedInPerAddress) {
                return (null, ConnectionRefusal.TooManyNotLoggedIn);
            }

            if (this._open >= max) {
                evicted = this.ChooseToMakeRoomLocked(address);
                if (evicted == null) {
                    return (null, ConnectionRefusal.ServerFull);
                }

                this.ReleaseLocked(evicted);
                evicted.Evicted = true;
            }

            ticket = new Ticket(this, address);
            this._open++;
            ticket.Node = this._notLoggedIn.AddLast(ticket);
            this._addresses[address] = new Counts(counts.Open + 1, counts.NotLoggedIn + 1);
        }

        evicted?.Evict();
        return (ticket, ConnectionRefusal.None);
    }

    /// <summary>
    /// Which connection closes to make room for a new one from <paramref name="newcomer"/>: the oldest that hasn't logged in
    /// and isn't registering. Failing that, one registering, from the address that would hold the most of them counting the
    /// new connection (its oldest; between addresses holding as many, the oldest of all). Someone registering keeps one
    /// connection open, so an address holding several (4 at most) is likelier to be crowding the server; and a new one from
    /// an address already registering makes room from its own. None that the server already takes as logged in, though the
    /// gate hasn't been told yet. Under the lock.
    /// </summary>
    private Ticket? ChooseToMakeRoomLocked(string newcomer) {
        List<Ticket>? registering = null;
        foreach (var candidate in this._notLoggedIn) {
            if (candidate.IsLoggedIn) {
                continue;
            }

            if (!candidate.IsRegistering) {
                return candidate;
            }

            (registering ??= []).Add(candidate);
        }

        if (registering == null) {
            return null;
        }

        var perAddress = new Dictionary<string, int>();
        foreach (var candidate in registering) {
            perAddress[candidate.Address] = perAddress.GetValueOrDefault(candidate.Address) + 1;
        }

        if (perAddress.TryGetValue(newcomer, out var own)) {
            perAddress[newcomer] = own + 1;
        }

        var most = perAddress.Values.Max();
        return registering.First(candidate => perAddress[candidate.Address] == most);
    }

    private void SetLoggedIn(Ticket ticket, bool loggedIn) {
        lock (this._lock) {
            if (ticket.Released || (ticket.Node == null) == loggedIn) {
                return;
            }

            var counts = this._addresses[ticket.Address];
            if (loggedIn) {
                this._notLoggedIn.Remove(ticket.Node!);
                ticket.Node = null;
                this._addresses[ticket.Address] = counts with { NotLoggedIn = counts.NotLoggedIn - 1 };
            } else {
                ticket.Node = this._notLoggedIn.AddLast(ticket);
                this._addresses[ticket.Address] = counts with { NotLoggedIn = counts.NotLoggedIn + 1 };
            }
        }
    }

    private void Release(Ticket ticket) {
        lock (this._lock) {
            this.ReleaseLocked(ticket);
        }
    }

    private void ReleaseLocked(Ticket ticket) {
        if (ticket.Released) {
            return;
        }

        ticket.Released = true;
        this._open--;
        var counts = this._addresses[ticket.Address];
        if (ticket.Node != null) {
            this._notLoggedIn.Remove(ticket.Node);
            ticket.Node = null;
            counts = counts with { NotLoggedIn = counts.NotLoggedIn - 1 };
        }

        counts = counts with { Open = counts.Open - 1 };
        if (counts.Open <= 0) {
            this._addresses.Remove(ticket.Address);
        } else {
            this._addresses[ticket.Address] = counts;
        }
    }

    private readonly record struct Counts(int Open, int NotLoggedIn);

    /// <summary>One connection's place. Dispose it when the connection closes.</summary>
    public sealed class Ticket : IDisposable {
        private readonly ConnectionGate _gate;
        private Func<bool>? _isRegistering;
        private Func<bool>? _isLoggedIn;
        private Action? _evict;
        private int _evicted;

        internal Ticket(ConnectionGate gate, string address) {
            this._gate = gate;
            this.Address = address;
        }

        public string Address { get; }

        // Under the gate's lock.
        internal LinkedListNode<Ticket>? Node { get; set; }
        internal bool Released { get; set; }
        internal bool Evicted { get; set; }

        internal bool IsRegistering => this._isRegistering?.Invoke() == true;

        internal bool IsLoggedIn => this._isLoggedIn?.Invoke() == true;

        /// <summary>
        /// Says how to tell whether the connection is registering (it keeps its place before others), and how to close it to
        /// make room. If it was already chosen to make room, it is closed now.
        /// </summary>
        /// <param name="isLoggedIn">
        /// Whether the server already takes the connection as logged in, before <see cref="SetLoggedIn"/> (called when the
        /// answer to its login goes out): it isn't closed to make room then.
        /// </param>
        public void Attach(Func<bool> isRegistering, Action evict, Func<bool>? isLoggedIn = null) {
            bool evicted;
            lock (this) {
                this._isRegistering = isRegistering;
                this._isLoggedIn = isLoggedIn;
                this._evict = evict;
                evicted = this._evicted == 1;
            }

            if (evicted) {
                evict();
            }
        }

        /// <summary>The connection logged in (or, logged out again, no longer is).</summary>
        public void SetLoggedIn(bool loggedIn) => this._gate.SetLoggedIn(this, loggedIn);

        internal void Evict() {
            Action? evict;
            lock (this) {
                this._evicted = 1;
                evict = this._evict;
            }

            evict?.Invoke();
        }

        public void Dispose() => this._gate.Release(this);
    }
}
