using System.Text.Json;

namespace WonderlandChat.Core.Client;

/// <summary>
/// Everything a client must keep private and persist between sessions. One
/// instance belongs to one character on one server.
/// </summary>
public sealed class ClientSecrets {
    public int Version { get; set; } = 1;
    public byte[]? SigningPrivateKey { get; set; }
    public byte[]? AgreementPrivateKey { get; set; }
    public string? DeviceToken { get; set; }
    public long? UserId { get; set; }

    /// <summary>Identity keys seen for other users (trust on first use).</summary>
    public Dictionary<long, PinnedIdentity> PinnedIdentities { get; set; } = new();

    /// <summary>Channel ID → epoch → raw epoch key.</summary>
    public Dictionary<string, Dictionary<ulong, byte[]>> EpochKeys { get; set; } = new();

    public ClientSecrets Clone() {
        return JsonSerializer.Deserialize<ClientSecrets>(JsonSerializer.SerializeToUtf8Bytes(this))!;
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static ClientSecrets Deserialize(byte[] data) {
        return JsonSerializer.Deserialize<ClientSecrets>(data) ?? new ClientSecrets();
    }
}

public sealed class PinnedIdentity {
    public byte[] SigningPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public uint KeyVersion { get; set; }
    public string Name { get; set; } = "";
    public string WorldName { get; set; } = "";

    /// <summary>The keys changed since they were first seen, and the user hasn't confirmed the new ones yet.</summary>
    public bool KeyChangeUnacknowledged { get; set; }
}

/// <summary>Where a client keeps its <see cref="ClientSecrets"/>.</summary>
public interface ISecretStore {
    /// <returns>The stored secrets, or a new empty instance if none exist.</returns>
    ClientSecrets Load();

    void Save(ClientSecrets secrets);
}

public sealed class InMemorySecretStore : ISecretStore {
    private byte[]? _data;

    public ClientSecrets Load() {
        var data = Volatile.Read(ref this._data);
        return data == null ? new ClientSecrets() : ClientSecrets.Deserialize(data);
    }

    public void Save(ClientSecrets secrets) {
        Volatile.Write(ref this._data, secrets.Serialize());
    }
}

/// <summary>
/// Plain JSON file. Only for the echo bot and tests: the plugin uses an
/// encrypted store instead.
/// </summary>
public sealed class FileSecretStore(string path) : ISecretStore {
    private readonly Lock _lock = new();

    public ClientSecrets Load() {
        lock (this._lock) {
            return File.Exists(path) ? ClientSecrets.Deserialize(File.ReadAllBytes(path)) : new ClientSecrets();
        }
    }

    public void Save(ClientSecrets secrets) {
        lock (this._lock) {
            AtomicFile.Write(path, secrets.Serialize());
        }
    }
}

public static class AtomicFile {
    /// <summary>Writes to a temporary file, then replaces the target, so a crash never leaves a half-written file.</summary>
    public static void Write(string path, byte[] data) {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null) {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }
}
