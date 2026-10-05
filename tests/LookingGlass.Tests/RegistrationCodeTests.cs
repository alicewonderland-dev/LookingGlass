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
                var sent = new ConcurrentQueue<ClientFrame>();
                var alice = server.StartClient(Name, options: server.Options(serverUri: new Uri(BUrl),
                    wrap: inner => new RewritingWebSocket(inner, frame => frame, sent.Enqueue)));
                await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);

                var challenge = await alice.Session.StartRegistrationAsync(Character, Ct);
                Assert.False(challenge.VerificationSkipped);
                Assert.Equal(lodestone.CharacterId, challenge.LodestoneId);
                var clientNonce = Assert.Single(sent, frame => frame.StartRegistration != null).StartRegistration.ClientNonce;
                Assert.Equal(LodestoneCode.ClientNonceSize, clientNonce.Length);
                using (var keys = alice.LoadIdentity()) {
                    Assert.Equal(LodestoneCode.Derive(ServerOrigin.FromUrl(BUrl)!, keys.SigningPublicKey, challenge.Nonce.Span, clientNonce.Span, challenge.LodestoneId),
                        challenge.Code);
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
                        Character = Character, Identity = (forUsersKey ? userKeys : malloryKeys).ToBundle(), ServerUrl = BUrl, ClientNonce = NewClientNonce(),
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
        AssertNotShown(code, text);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("(?i)LGC-[0-9A-Z]{4}"), text);
    }

    /// <summary>
    /// Not <paramref name="code"/> as the Lodestone check would read it: in any case, O as 0, I or L as 1, and with
    /// invisible characters (which a display or a copy may drop) left out.
    /// </summary>
    private static void AssertNotShown(string code, string text) {
        var read = new string(text
            .Where(c => char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control))
            .Select(c => char.ToUpperInvariant(c) switch {
                'O' => '0',
                'I' or 'L' => '1',
                var upper => upper,
            })
            .ToArray());
        Assert.DoesNotContain(code[LodestoneCode.Prefix.Length..], read);
    }

    /// <summary>The code as a server could write it so that a plain search doesn't find it: in lower case, with O, I and an invisible space.</summary>
    private static string Disguised(string code) {
        var body = code[LodestoneCode.Prefix.Length..].ToLowerInvariant().Replace('0', 'o').Replace('1', 'I');
        return $"lgc-{body[..3]}\u200B{body[3..]}";
    }

    /// <summary>
    /// The relay without a code to relay: M answers registering honestly, but writes B's code into what it says (its
    /// announcement, the error when verifying fails), as "LGC-... isn't in your Lodestone profile yet", hoping the user
    /// pastes that one. The client removes every code from what the server says, wherever it is shown or logged, except
    /// its own, checked one, which it still shows where it should.
    /// </summary>
    [Fact]
    public async Task AnotherCodeInTheServersWordsIsNeverShown() {
        await using var relay = Relay.Create();
        var other = (await relay.StartOnBAsync(relay.MalloryKeys)).Code;
        string? mine = null;
        relay.Rewrite = frame => {
            if (frame.Response?.Welcome is { } welcome) {
                welcome.Announcement = $"Welcome! Registering? Your code is {other}.";
            }

            if (frame.Response?.Error is { } error) {
                error.Message = $"{other} isn't in your Lodestone profile yet ({Disguised(other)}). Not {mine} but the other one.";
            }

            return frame;
        };

        var user = await relay.StartUserAsync();
        var challenge = await user.Session.StartRegistrationAsync(Character, Ct);
        mine = challenge.Code;
        // The code checked as this server's for this key: shown, as before.
        Assert.Equal(mine, user.Session.Snapshot.PendingChallenge?.Code);
        Assert.Contains(mine, user.Session.Snapshot.StatusText);

        var failed = await Assert.ThrowsAsync<ServerErrorException>(() => user.Session.CompleteRegistrationAsync(Ct));
        Assert.Equal(ErrorCode.RegistrationFailed, failed.Code);
        Assert.StartsWith($"{LodestoneCode.Removed} isn't in your Lodestone profile yet ({LodestoneCode.Removed}).", failed.ServerMessage);
        Assert.Contains($"Not {mine} but", failed.ServerMessage);

        var notice = Assert.Single(user.Notices, notice => notice.Text.StartsWith("Welcome!"));
        Assert.Equal($"Welcome! Registering? Your code is {LodestoneCode.Removed}.", notice.Text);
        foreach (var text in relay.Logs.Select(entry => entry.Text).Concat(user.Notices.Select(n => n.Text))
                     .Append(failed.Message).Append(failed.ServerMessage).Append(user.Session.Snapshot.StatusText ?? "")) {
            AssertNotShown(other, text);
        }
    }

    /// <summary>
    /// Wherever else a server's text reaches the user: announcements sent later, and names (here the user's own, as the
    /// server says it), with no registration under way, so nothing is kept.
    /// </summary>
    [Fact]
    public async Task ACodeInAnnouncementsOrNamesIsNeverShown() {
        var server = new Harness();
        try {
            await using (server) {
                using var keys = IdentityKeys.Generate();
                var code = LodestoneCode.Derive(ServerOrigin.FromUrl(BUrl)!, keys.SigningPublicKey, new byte[RegistrationProof.NonceSize], new byte[LodestoneCode.ClientNonceSize], 1);
                var logs = new ConcurrentQueue<string>();
                var user = await server.RegisterAsync("Code Name", options: server.Options(log: (_, text) => logs.Enqueue(text), wrap: inner => new RewritingWebSocket(inner, frame => {
                    if (frame.Response?.AuthenticateOk is { } ok) {
                        ok.User.Name = Disguised(code);
                    }

                    return frame;
                })));

                await server.SendAndSettleAsync(user, new Event { Announcement = new Announcement { Text = $"Maintenance at 10. Also, {code}" } });
                Assert.Contains(user.Notices, notice => notice.Text == $"Maintenance at 10. Also, {LodestoneCode.Removed}");
                Assert.Equal(LodestoneCode.Removed, user.Session.Snapshot.Me?.Name);
                foreach (var text in logs.Concat(user.Notices.Select(notice => notice.Text)).Append(user.Session.Snapshot.StatusText ?? "")) {
                    AssertNotShown(code, text);
                }
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
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
                    StartRegistration = new StartRegistration { Character = new Character { Name = Name, WorldName = world }, Identity = keys.ToBundle(), ClientNonce = NewClientNonce() },
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
                    StartRegistration = new StartRegistration {
                        Character = new Character { Name = Name, WorldName = world }, Identity = keys.ToBundle(), ServerUrl = BUrl, ClientNonce = NewClientNonce(),
                    },
                });
                Assert.NotNull(current.RegistrationChallenge);
            }
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// The client's nonce: 32 random bytes, fresh for each StartRegistration, which the code is derived from too. A server
    /// can't know it before the client asks, so it can't have looked for inputs of its own whose code matches one another
    /// server issued ahead of time; a code made for any other client nonce (here one M prepared, honest in every other
    /// way: its own address, the user's key) is refused like any other relayed code.
    /// </summary>
    [Fact]
    public async Task TheCodeIsForTheClientsFreshNonce() {
        await using var relay = Relay.Create();
        var sent = new ConcurrentQueue<ClientFrame>();
        var prepared = System.Security.Cryptography.RandomNumberGenerator.GetBytes(LodestoneCode.ClientNonceSize);
        var precompute = false;
        relay.Rewrite = frame => {
            if (precompute && frame.Response?.RegistrationChallenge is { } challenge) {
                challenge.Code = LodestoneCode.Derive(ServerOrigin.FromUrl(MUrl)!, relay.UserKeys.SigningPublicKey, challenge.Nonce.Span, prepared, challenge.LodestoneId);
            }

            return frame;
        };

        var user = relay.M.StartClient(Name, relay.Store, relay.M.Options(serverUri: new Uri(MUrl), log: (level, text) => relay.Logs.Enqueue((level, text)),
            wrap: inner => new RewritingWebSocket(inner, frame => relay.Rewrite(frame), sent.Enqueue)));
        await WaitFor(() => user.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);

        // Two starts, two fresh nonces of 32 bytes.
        await user.Session.StartRegistrationAsync(Character, Ct);
        await user.Session.StartRegistrationAsync(Character, Ct);
        var nonces = sent.Where(frame => frame.StartRegistration != null).Select(frame => frame.StartRegistration.ClientNonce).ToList();
        Assert.Equal(2, nonces.Count);
        Assert.All(nonces, nonce => Assert.Equal(LodestoneCode.ClientNonceSize, nonce.Length));
        Assert.NotEqual(nonces[0], nonces[1]);

        precompute = true;
        await Assert.ThrowsAsync<RelayedRegistrationCodeException>(() => user.Session.StartRegistrationAsync(Character, Ct));
        Assert.Null(user.Session.Snapshot.PendingChallenge);
    }

    /// <summary>
    /// How long a registration challenge lives (LookingGlass:Lodestone:ChallengeMinutes) bounds how long a server passing
    /// on another's code can hold it, so it is 1 to 60 minutes: a server set otherwise doesn't start, and says why.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("61")]
    [InlineData("1440")]
    public async Task AServerWithAChallengeLifetimeOutsideOneToSixtyMinutesDoesntStart(string minutes) {
        var logs = new CapturingLoggerProvider();
        var exitCode = Environment.ExitCode;
        var directory = Path.Combine(Path.GetTempPath(), "lgt-" + Guid.NewGuid().ToString("N"));
        try {
            Exception? failed = null;
            try {
                await using var server = new Harness(directory, logs: logs, settings: ("LookingGlass:Lodestone:ChallengeMinutes", minutes));
                await using var raw = await server.ConnectRawAsync();
            } catch (Exception ex) {
                failed = ex;
            }

            Assert.NotNull(failed);
            var critical = Assert.Single(logs.AtLeast(Microsoft.Extensions.Logging.LogLevel.Critical));
            Assert.Contains("LookingGlass:Lodestone:ChallengeMinutes", critical);
            Assert.Contains("1 to 60", critical);
            Assert.Equal(1, Environment.ExitCode);
        } finally {
            Environment.ExitCode = exitCode;
            DeleteDirectory(directory);
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
                StartRegistration = new StartRegistration { Character = Character, Identity = keys.ToBundle(), ServerUrl = BUrl, ClientNonce = NewClientNonce() },
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
