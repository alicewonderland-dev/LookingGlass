using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using LookingGlass.Core.Client;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace LookingGlass.Plugin;

/// <summary>
/// Reads what local chat needs from the game (see <see cref="LocalChat"/>): the players near the player, from the object
/// table, and which of them are friends. A player counts as a friend if the game marks them so (Dalamud's
/// StatusFlags.Friend, read from the character; whether the game sets it before its list is loaded is to be checked in
/// game) or the game's friends list has them, by content ID or by name and home world (a request still waiting for an
/// answer doesn't count). The game may only fill its friends
/// list once the Friends window has been opened in a session; until then it counts as not loaded, and so does an empty one:
/// this plugin can't tell a list with nobody on it from one not filled in yet. Game thread only.
/// </summary>
internal static unsafe class LocalChatGame {
    /// <summary>What is around the player now, or <see cref="LocalSurroundings.Nobody"/> if they aren't in the world.</summary>
    public static LocalSurroundings Read() {
        if (Services.Objects.LocalPlayer is not { } me) {
            return LocalSurroundings.Nobody;
        }

        var (friendIds, friendNames, loaded) = FriendsList();
        var players = new List<NearbyPlayer>();
        foreach (var player in Services.Objects.PlayerObjects.OfType<IPlayerCharacter>()) {
            if (player.Address == me.Address || !player.IsValid()) {
                continue;
            }

            var name = player.Name.TextValue;
            var world = player.HomeWorld;
            if (name.Length == 0 || !world.IsValid) {
                continue;
            }

            var contentId = ((GameCharacter*) player.Address)->ContentId;
            var friend = player.StatusFlags.HasFlag(StatusFlags.Friend)
                         || (contentId != 0 && friendIds.Contains(contentId))
                         || friendNames.Contains((name.ToUpperInvariant(), world.RowId));
            players.Add(new NearbyPlayer(name, world.Value.Name.ExtractText(), Vector3.Distance(me.Position, player.Position), friend));
        }

        return new LocalSurroundings(players, loaded);
    }

    /// <summary>The game's friends list as it holds it now: content IDs, names (upper case) with home world IDs, and whether it has anyone.</summary>
    private static (HashSet<ulong> Ids, HashSet<(string, uint)> Names, bool Loaded) FriendsList() {
        var ids = new HashSet<ulong>();
        var names = new HashSet<(string, uint)>();
        var list = InfoProxyFriendList.Instance();
        if (list == null || list->EntryCount == 0) {
            return (ids, names, false);
        }

        foreach (var entry in list->CharDataSpan) {
            if (entry.WaitingForFriendListApproval) {
                continue;
            }

            if (entry.ContentId != 0) {
                ids.Add(entry.ContentId);
            }

            if (entry.NameString is { Length: > 0 } name) {
                names.Add((name.ToUpperInvariant(), entry.HomeWorld));
            }
        }

        return (ids, names, true);
    }
}
