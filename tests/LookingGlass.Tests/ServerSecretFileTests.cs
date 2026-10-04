using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// A character's secrets are kept per server address: the file is named after a hash of the address, and holds the
/// address too, so a file is never used for an address it wasn't made for (say, one whose hash collides with it).
/// </summary>
public sealed class ServerSecretFileTests : IDisposable {
    private const ulong ContentId = 0x0040_0000_1234_5678;
    private const string Url = "ws://LookingGlassChat:5180/ws";
    private const string OtherUrl = "wss://evil.example/ws";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => DeleteDirectory(this._directory);

    private ISecretStore Open(string url, Action<string>? log = null) {
        return ServerSecretFiles.Open(this._directory, ContentId, url, path => new FileSecretStore(path), log);
    }

    private string PathFor(string url) => Path.Combine(this._directory, ServerSecretFiles.FileName(ContentId, url));

    private string LegacyPathFor(string url) => Path.Combine(this._directory, ServerSecretFiles.LegacyFileName(ContentId, url));

    private static ClientSecrets Registered(string token) {
        using var keys = IdentityKeys.Generate();
        var (signing, agreement) = keys.ExportPrivateKeys();
        return new ClientSecrets { SigningPrivateKey = signing, AgreementPrivateKey = agreement, DeviceToken = token, UserId = 1234 };
    }

    [Fact]
    public void FilesAreNamedAfter128BitsOfTheAddressHash() {
        var name = ServerSecretFiles.FileName(ContentId, Url);
        Assert.Matches("^secrets-0040000012345678-[0-9A-F]{32}\\.bin$", name);
        // The same address however it's typed (case, spaces), and a different one for another address.
        Assert.Equal(name, ServerSecretFiles.FileName(ContentId, "  ws://lookingglasschat:5180/ws "));
        Assert.NotEqual(name, ServerSecretFiles.FileName(ContentId, OtherUrl));
        // The old names kept 48 bits.
        Assert.Matches("^secrets-0040000012345678-[0-9A-F]{12}\\.bin$", ServerSecretFiles.LegacyFileName(ContentId, Url));
    }

    [Fact]
    public void SavedSecretsNameTheirAddress() {
        this.Open(Url).Save(Registered("token"));

        var saved = new FileSecretStore(this.PathFor(Url)).Load();
        Assert.Equal("ws://lookingglasschat:5180/ws", saved.ServerUrl);
        Assert.Equal("ws://lookingglasschat:5180", saved.ServerOrigin);
        Assert.Equal("token", this.Open(Url).Load().DeviceToken);
    }

    /// <summary>A file under another address's name (as a hash collision would put it) is refused, not used.</summary>
    [Fact]
    public void AFileForAnotherAddressIsRefused() {
        this.Open(Url).Save(Registered("victim's token"));
        Directory.CreateDirectory(this._directory);
        File.Copy(this.PathFor(Url), this.PathFor(OtherUrl));

        var error = Assert.Throws<SecretsServerMismatchException>(() => this.Open(OtherUrl).Load());
        Assert.Contains("lookingglasschat", error.Message);
        Assert.Contains("evil.example", error.Message);
        // Nor is it overwritten by a save for the other address.
        Assert.Throws<SecretsServerMismatchException>(() => this.Open(OtherUrl).Save(new ClientSecrets()));
        Assert.Equal("victim's token", this.Open(Url).Load().DeviceToken);
    }

    /// <summary>
    /// Files from before the address was stored only have the old, short name. The first open for their address
    /// moves them to the new name, stamped with the address, and keeps the old file as a backup.
    /// </summary>
    [Fact]
    public void AnOldFileIsMovedToTheNewNameAndKept() {
        var old = Registered("old token");
        new FileSecretStore(this.LegacyPathFor(Url)).Save(old);
        var log = new List<string>();

        var loaded = this.Open(Url, log.Add).Load();
        Assert.Equal("old token", loaded.DeviceToken);
        Assert.Equal(old.SigningPrivateKey, loaded.SigningPrivateKey);
        Assert.Equal("ws://lookingglasschat:5180/ws", loaded.ServerUrl);
        Assert.True(File.Exists(this.PathFor(Url)));
        Assert.Single(log);

        // The old file is still there, with the same keys and login, and now names its address too.
        Assert.True(File.Exists(this.LegacyPathFor(Url)));
        var kept = new FileSecretStore(this.LegacyPathFor(Url)).Load();
        Assert.Equal("old token", kept.DeviceToken);
        Assert.Equal(old.SigningPrivateKey, kept.SigningPrivateKey);
        Assert.Equal("ws://lookingglasschat:5180/ws", kept.ServerUrl);

        // From now on the new file is the one used: it moves only once.
        this.Open(Url).Save(Registered("new token"));
        Assert.Equal("new token", this.Open(Url).Load().DeviceToken);
        Assert.Equal("old token", new FileSecretStore(this.LegacyPathFor(Url)).Load().DeviceToken);
    }

    /// <summary>
    /// Once moved, the old file names its address, so an address whose short hash collides with it (which can be
    /// found offline) can't take it over later.
    /// </summary>
    [Fact]
    public void AMovedOldFileCannotBeTakenOverByACollidingAddress() {
        new FileSecretStore(this.LegacyPathFor(Url)).Save(Registered("victim's token"));
        this.Open(Url).Load();

        // A colliding address finds the old file under its own short name.
        File.Copy(this.LegacyPathFor(Url), this.LegacyPathFor(OtherUrl));
        Assert.Throws<SecretsServerMismatchException>(() => this.Open(OtherUrl).Load());
        Assert.False(File.Exists(this.PathFor(OtherUrl)));
    }

    /// <summary>
    /// Once moved, the old file is a backup: if the new file (and its own backup) go missing later, the old one is never
    /// moved again by itself, which would bring back an old login, old channel keys, or a key replaced since. It is only
    /// offered, and restored when asked. Nothing is deleted.
    /// </summary>
    [Fact]
    public void AMovedOldFileIsNeverRestoredSilently() {
        new FileSecretStore(this.LegacyPathFor(Url)).Save(Registered("old token"));
        Assert.Equal("old token", this.Open(Url).Load().DeviceToken);
        this.Open(Url).Save(Registered("newer token"));

        // The new file and its backup are lost.
        File.Delete(this.PathFor(Url));
        File.Delete(this.PathFor(Url) + ".bak");

        var log = new List<string>();
        var loaded = this.Open(Url, log.Add).Load();
        Assert.Null(loaded.DeviceToken);
        Assert.Null(loaded.SigningPrivateKey);
        Assert.Empty(log);
        Assert.Equal(0, ServerSecretFiles.MigrateAll(this._directory, Url, path => new FileSecretStore(path)));
        Assert.False(File.Exists(this.PathFor(Url)));
        Assert.True(File.Exists(this.LegacyPathFor(Url)));

        // Offered instead, dated, and restored only when asked; the old file stays.
        var backup = ServerSecretFiles.FindBackup(this._directory, ContentId, Url, path => new FileSecretStore(path));
        Assert.NotNull(backup);
        Assert.Equal(this.LegacyPathFor(Url), backup.Path);
        Assert.Equal(File.GetLastWriteTimeUtc(this.LegacyPathFor(Url)), backup.SavedAt.UtcDateTime);
        ServerSecretFiles.RestoreBackup(this._directory, ContentId, Url, path => new FileSecretStore(path));
        var restored = this.Open(Url).Load();
        Assert.Equal("old token", restored.DeviceToken);
        Assert.Equal("ws://lookingglasschat:5180/ws", restored.ServerUrl);
        Assert.True(File.Exists(this.LegacyPathFor(Url)));
        Assert.Null(ServerSecretFiles.FindBackup(this._directory, ContentId, Url, path => new FileSecretStore(path)));
    }

    /// <summary>
    /// Only an old file that was never moved (it names no address) is moved by itself, and only when the file itself is
    /// there: one whose own backup is all that is left may be what a move kept, so it is offered instead.
    /// </summary>
    [Fact]
    public void AnOldFileWithOnlyItsBackupLeftIsOfferedNotMoved() {
        var legacy = this.LegacyPathFor(Url);
        new FileSecretStore(legacy).Save(Registered("older"));
        new FileSecretStore(legacy).Save(Registered("old"));
        File.Delete(legacy);

        Assert.Null(this.Open(Url).Load().DeviceToken);
        Assert.False(File.Exists(this.PathFor(Url)));
        var backup = ServerSecretFiles.FindBackup(this._directory, ContentId, Url, path => new FileSecretStore(path));
        Assert.Equal(legacy + ".bak", backup?.Path);
        ServerSecretFiles.RestoreBackup(this._directory, ContentId, Url, path => new FileSecretStore(path));
        Assert.Equal("older", this.Open(Url).Load().DeviceToken);
    }

    /// <summary>
    /// A backup is only offered (or restored) for an address with no identity and no login of its own, and only if it
    /// holds an identity: it never replaces one, and one with nothing in it (say, scrubbed by a reset) isn't worth it.
    /// </summary>
    [Fact]
    public void ABackupNeverReplacesAnIdentity() {
        new FileSecretStore(this.LegacyPathFor(Url)).Save(Registered("old"));
        this.Open(Url).Load();
        this.Open(Url).Save(Registered("current"));
        Assert.Null(ServerSecretFiles.FindBackup(this._directory, ContentId, Url, path => new FileSecretStore(path)));
        Assert.Throws<InvalidOperationException>(() => ServerSecretFiles.RestoreBackup(this._directory, ContentId, Url, path => new FileSecretStore(path)));
        Assert.Equal("current", this.Open(Url).Load().DeviceToken);

        // A backup without an identity in it isn't offered.
        var empty = this.Open(Url).Load();
        empty.SigningPrivateKey = null;
        empty.AgreementPrivateKey = null;
        empty.DeviceToken = null;
        var legacy = new ServerBoundSecretStore(new FileSecretStore(this.LegacyPathFor(Url)), Url, this.LegacyPathFor(Url));
        legacy.Save(empty);
        File.Delete(this.PathFor(Url));
        File.Delete(this.PathFor(Url) + ".bak");
        Assert.Null(ServerSecretFiles.FindBackup(this._directory, ContentId, Url, path => new FileSecretStore(path)));
    }

    [Fact]
    public void TheNewFileWinsOverAnOldOne() {
        this.Open(Url).Save(Registered("new"));
        new FileSecretStore(this.LegacyPathFor(Url)).Save(Registered("old"));
        Assert.Equal("new", this.Open(Url).Load().DeviceToken);
    }

    [Fact]
    public void OldFilesForTheCurrentAddressAreMovedUpFront() {
        new FileSecretStore(this.LegacyPathFor(Url)).Save(Registered("first character"));
        var other = Path.Combine(this._directory, ServerSecretFiles.LegacyFileName(0x0040_0000_0000_0001, Url));
        new FileSecretStore(other).Save(Registered("second character"));
        // Another address's old file is left for when that address is used.
        new FileSecretStore(this.LegacyPathFor(OtherUrl)).Save(Registered("elsewhere"));

        Assert.Equal(2, ServerSecretFiles.MigrateAll(this._directory, Url, path => new FileSecretStore(path)));
        Assert.True(File.Exists(this.PathFor(Url)));
        Assert.True(File.Exists(Path.Combine(this._directory, ServerSecretFiles.FileName(0x0040_0000_0000_0001, Url))));
        Assert.False(File.Exists(this.PathFor(OtherUrl)));
        Assert.Equal(0, ServerSecretFiles.MigrateAll(this._directory, Url, path => new FileSecretStore(path)));
    }

    /// <summary>A whole session through the bound store: it registers, and what it saves names the address.</summary>
    [Fact]
    public async Task ASessionSavesItsAddressWithItsSecrets() {
        await using var server = new Harness();
        try {
            var url = server.ServerUri.AbsoluteUri;
            var store = ServerSecretFiles.Open(this._directory, ContentId, url, path => new FileSecretStore(path));
            var alice = await server.RegisterAsync("Alice Filed", store);
            await alice.Session.DisposeAsync();

            var saved = new FileSecretStore(Path.Combine(this._directory, ServerSecretFiles.FileName(ContentId, url))).Load();
            Assert.Equal(ServerSecretFiles.NormaliseUrl(url), saved.ServerUrl);
            Assert.NotNull(saved.DeviceToken);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }
}
