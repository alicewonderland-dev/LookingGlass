using System.Collections.Concurrent;
using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Realtime;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The Lodestone code relay: a malicious server M starts a registration on an honest server B for a user's character,
/// with M's own key, and shows the user B's code as if it were its own. If the user puts it in their Lodestone profile,
/// M completes the registration on B and takes the character's account there. The code is derived from the server's
/// address, the identity key, the nonce and the character, so the user's client recomputes it from its own address and
/// key and refuses a code made for anything else; and B issues, and looks for, only the code for the key it registers.
/// </summary>
public sealed class RegistrationCodeTests {
    private const string BUrl = "wss://b.example/ws";
    private const string MUrl = "wss://m.example/ws";
    private const string Name = "Alice Lodestone";
    private const string World = "Gilgamesh";

    private static readonly Character Character = new() { Name = Name, WorldName = World };

    /// <summary>A real character registers as the plugin does it: the server's code is the one the client expects, and the Lodestone has it.</summary>
    [Fact]
    public async Task ARealCharacterRegistersWithTheCodeItsServerIssues() {
        var lodestone = new FakeLodestone { Name = Name, World = World };
        var server = new Harness(lodestone: lodestone, settings: ("LookingGlass:PublicUrls:0", BUrl));
        try {
            await using (server) {
                var alice = server.StartClient(Name, options: server.Options(serverUri: new Uri(BUrl)));
                await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);

                var challenge = await alice.Session.StartRegistrationAsync(Character, Ct);
                Assert.False(challenge.VerificationSkipped);
                Assert.Equal(lodestone.CharacterId, challenge.LodestoneId);
                using (var keys = alice.LoadIdentity()) {
                    Assert.Equal(LodestoneCode.Derive(ServerOrigin.FromUrl(BUrl)!, keys.SigningPublicKey, challenge.Nonce.Span, challenge.LodestoneId), challenge.Code);
                }

                Assert.Equal(challenge.Code, alice.Session.Snapshot.PendingChallenge?.Code);
                Assert.Contains(challenge.Code, alice.Session.Snapshot.StatusText);

                // The user pastes it (here in lower case, among other text), and verifies.
                lodestone.Profile = $"Hello! {challenge.Code.ToLowerInvariant()} Have a nice day.";
                await alice.Session.CompleteRegistrationAsync(Ct);
                await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
                Assert.Equal(lodestone.CharacterId, alice.UserId);
                Assert.Equal(1, lodestone.ProfileReads);
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// The relay. M (here a server whose answers a test rewrites) passes on B's code and nonce for the user's character,
    /// made for M's own key (which M could then register on B), or for the user's key (which M learns when the user
    /// registers with it). Either way the user's client recomputes the code from its own server address and key, finds
    /// it isn't that, and refuses it with a warning, showing nothing to put in the profile, so M can't complete on B.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheClientRefusesACodePassedOnFromAnotherServer(bool forUsersKey) {
        var lodestoneB = new FakeLodestone { Name = Name, World = World };
        var lodestoneM = new FakeLodestone { Name = Name, World = World };
        var b = new Harness(lodestone: lodestoneB, settings: ("LookingGlass:PublicUrls:0", BUrl));
        var m = new Harness(lodestone: lodestoneM, settings: ("LookingGlass:PublicUrls:0", MUrl));
        try {
            await using (b)
            await using (m) {
                // The user's identity key for M (the plugin keeps one per server address).
                using var userKeys = IdentityKeys.Generate();
                var store = new InMemorySecretStore();
                var (signing, agreement) = userKeys.ExportPrivateKeys();
                store.Save(new ClientSecrets { SigningPrivateKey = signing, AgreementPrivateKey = agreement });
                using var malloryKeys = IdentityKeys.Generate();

                // M, as a client of B, starts a registration of the user's character there, for B's address.
                await using var mallory = await b.ConnectRawAsync();
                var fromB = (await mallory.SendAsync(new ClientFrame {
                    StartRegistration = new StartRegistration {
                        Character = Character, Identity = (forUsersKey ? userKeys : malloryKeys).ToBundle(), ServerUrl = BUrl,
                    },
                })).RegistrationChallenge!;
                Assert.False(string.IsNullOrEmpty(fromB.Code));

                // And answers the user's own registration with B's challenge.
                var logs = new ConcurrentQueue<(NoticeLevel Level, string Text)>();
                var user = m.StartClient(Name, store, m.Options(serverUri: new Uri(MUrl), log: (level, text) => logs.Enqueue((level, text)),
                    wrap: inner => new RewritingWebSocket(inner, frame => {
                        if (frame.Response?.RegistrationChallenge != null) {
                            frame.Response.RegistrationChallenge = fromB.Clone();
                        }

                        return frame;
                    })));
                await WaitFor(() => user.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);

                RegistrationChallenge? shown = null;
                Exception? refused = null;
                try {
                    shown = await user.Session.StartRegistrationAsync(Character, Ct);
                } catch (Exception ex) when (ex is not ServerErrorException) {
                    refused = ex;
                }

                // A code the plugin shows, the user puts in their profile.
                if (shown != null) {
                    lodestoneB.Profile = lodestoneM.Profile = $"Registering on LookingGlass: {shown.Code}";
                }

                // M completes on B, for B's address, signed with its own key (when B's code was for it).
                if (!forUsersKey) {
                    var taken = await mallory.SendAsync(new ClientFrame {
                        CompleteRegistration = new CompleteRegistration {
                            ServerUrl = BUrl,
                            Signature = ByteString.CopyFrom(RegistrationProof.Sign(malloryKeys, fromB.Nonce.Span, fromB.LodestoneId, BUrl)),
                        },
                    });
                    Assert.Null(taken.RegistrationComplete);
                    Assert.Equal(ErrorCode.RegistrationFailed, taken.Error?.Code);
                }

                Assert.Null(b.Database.GetUser(lodestoneB.CharacterId));

                // The client refused the code, said why in words the user can act on, and logged it.
                Assert.Null(shown);
                Assert.NotNull(refused);
                Assert.Contains("doesn't belong to it", refused.Message);
                Assert.Contains("Don't put it in your Lodestone profile", refused.Message);
                Assert.Contains(logs, entry => entry.Level == NoticeLevel.Warning && entry.Text.Contains("registration code"));
                Assert.DoesNotContain(fromB.Code, lodestoneB.Profile);

                // It shows no code, and never completes the registration it refused.
                var snapshot = user.Session.Snapshot;
                Assert.Null(snapshot.PendingChallenge);
                Assert.DoesNotContain(fromB.Code, snapshot.StatusText ?? "");
                Assert.NotEqual(ConnectionState.Registering, snapshot.State);
                await Assert.ThrowsAsync<InvalidOperationException>(() => user.Session.CompleteRegistrationAsync(Ct));
                Assert.DoesNotContain(user.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteRegistration"));
            }
        } finally {
            DeleteDirectory(b.DataDirectory);
            DeleteDirectory(m.DataDirectory);
        }
    }

    /// <summary>
    /// The refused code is neither logged nor shown, anywhere: the warning names the address the client connected to and
    /// the character, which is enough to look into it, and nothing a user could paste into their profile.
    /// </summary>
    [Fact]
    public async Task ARefusedCodeIsNeitherLoggedNorShown() {
        await using var relay = Relay.Create();
        var fromB = await relay.StartOnBAsync(relay.MalloryKeys);
        relay.Rewrite = frame => {
            if (frame.Response?.RegistrationChallenge != null) {
                frame.Response.RegistrationChallenge = fromB.Clone();
            }

            return frame;
        };

        var user = await relay.StartUserAsync();
        var refused = await Assert.ThrowsAsync<RelayedRegistrationCodeException>(() => user.Session.StartRegistrationAsync(Character, Ct));

        var warning = Assert.Single(relay.Logs, entry => entry.Level == NoticeLevel.Warning && entry.Text.Contains("registration code"));
        Assert.Contains("wss://m.example:443", warning.Text);
        Assert.Contains(fromB.LodestoneId.ToString(), warning.Text);
        foreach (var text in relay.Logs.Select(entry => entry.Text).Concat(user.Notices.Select(notice => notice.Text))
                     .Append(refused.Message).Append(user.Session.Snapshot.StatusText ?? "")) {
            AssertNoCode(fromB.Code, text);
        }
    }

    /// <summary>Not <paramref name="code"/>, in any case, nor anything shaped like a code.</summary>
    private static void AssertNoCode(string code, string text) {
        Assert.DoesNotContain(code[LodestoneCode.Prefix.Length..], text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("(?i)LGC-[0-9A-Z]{4}"), text);
    }

    /// <summary>
    /// A plugin from before codes were derived sends StartRegistration without the server address it connected to, and
    /// is asked to update, for real characters and debug accounts alike, before the Lodestone is asked anything.
    /// </summary>
    [Theory]
    [InlineData(World)]
    [InlineData(ProtocolInfo.DebugWorldName)]
    public async Task AStartRegistrationWithoutTheServerAddressIsAskedToUpdate(string world) {
        var lodestone = new FakeLodestone { Name = Name, World = World };
        var server = new Harness(lodestone: lodestone, settings: ("LookingGlass:PublicUrls:0", BUrl));
        try {
            await using (server) {
                using var keys = IdentityKeys.Generate();
                await using var raw = await server.ConnectRawAsync();
                var old = await raw.SendAsync(new ClientFrame {
                    StartRegistration = new StartRegistration { Character = new Character { Name = Name, WorldName = world }, Identity = keys.ToBundle() },
                });

                Assert.Null(old.RegistrationChallenge);
                Assert.Equal(ErrorCode.RegistrationFailed, old.Error?.Code);
                Assert.Contains("update the plugin", old.Error!.Message);

                // Nothing was started: completing has nothing to complete.
                var complete = await raw.SendAsync(new ClientFrame {
                    CompleteRegistration = new CompleteRegistration {
                        ServerUrl = BUrl, Signature = ByteString.CopyFrom(RegistrationProof.Sign(keys, new byte[RegistrationProof.NonceSize], 0, BUrl)),
                    },
                });
                Assert.Contains("Start registration on this connection first", complete.Error?.Message);
                Assert.Equal(0, lodestone.ProfileReads);

                // The same request with the address goes ahead.
                var current = await raw.SendAsync(new ClientFrame {
                    StartRegistration = new StartRegistration { Character = new Character { Name = Name, WorldName = world }, Identity = keys.ToBundle(), ServerUrl = BUrl },
                });
                Assert.NotNull(current.RegistrationChallenge);
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// The relay, set up: an honest server B (at <see cref="BUrl"/>), a server M (at <see cref="MUrl"/>) whose answers
    /// to the user's client go through <see cref="Rewrite"/> (as a malicious M would change them), M's own client of B,
    /// and the user's identity key for M.
    /// </summary>
    private sealed class Relay : IAsyncDisposable {
        private RawConnection? _mallory;

        private Relay() {
            this.B = new Harness(lodestone: this.LodestoneB, settings: ("LookingGlass:PublicUrls:0", BUrl));
            this.M = new Harness(lodestone: this.LodestoneM, settings: ("LookingGlass:PublicUrls:0", MUrl));
            var (signing, agreement) = this.UserKeys.ExportPrivateKeys();
            this.Store.Save(new ClientSecrets { SigningPrivateKey = signing, AgreementPrivateKey = agreement });
        }

        public static Relay Create() => new();

        public FakeLodestone LodestoneB { get; } = new() { Name = Name, World = World };
        public FakeLodestone LodestoneM { get; } = new() { Name = Name, World = World };
        public Harness B { get; }
        public Harness M { get; }

        /// <summary>The user's identity key for M (the plugin keeps one per server address), in <see cref="Store"/>.</summary>
        public IdentityKeys UserKeys { get; } = IdentityKeys.Generate();

        public InMemorySecretStore Store { get; } = new();
        public IdentityKeys MalloryKeys { get; } = IdentityKeys.Generate();

        /// <summary>Everything the user's client logs (what the plugin writes to the Dalamud log).</summary>
        public ConcurrentQueue<(NoticeLevel Level, string Text)> Logs { get; } = new();

        /// <summary>What M does to each frame on its way to the user's client (by default, nothing).</summary>
        public Func<ServerFrame, ServerFrame> Rewrite { get; set; } = frame => frame;

        /// <summary>M, as a client of B, starts registering the user's character there with <paramref name="keys"/>, for B's address.</summary>
        public async Task<RegistrationChallenge> StartOnBAsync(IdentityKeys keys) {
            this._mallory ??= await this.B.ConnectRawAsync();
            var response = await this._mallory.SendAsync(new ClientFrame {
                StartRegistration = new StartRegistration { Character = Character, Identity = keys.ToBundle(), ServerUrl = BUrl },
            });
            var challenge = response.RegistrationChallenge!;
            Assert.False(string.IsNullOrEmpty(challenge.Code));
            return challenge;
        }

        /// <summary>The user's client, connected to M (through <see cref="Rewrite"/>), not registered yet.</summary>
        public async Task<TestClient> StartUserAsync() {
            var user = this.M.StartClient(Name, this.Store, this.M.Options(serverUri: new Uri(MUrl), log: (level, text) => this.Logs.Enqueue((level, text)),
                wrap: inner => new RewritingWebSocket(inner, frame => this.Rewrite(frame))));
            await WaitFor(() => user.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
            return user;
        }

        public async ValueTask DisposeAsync() {
            await this.M.DisposeAsync();
            await this.B.DisposeAsync();
            this.UserKeys.Dispose();
            this.MalloryKeys.Dispose();
            DeleteDirectory(this.B.DataDirectory);
            DeleteDirectory(this.M.DataDirectory);
        }
    }
}
