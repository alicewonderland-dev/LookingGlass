using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using LookingGlass.Core.SelfTest;
using Lumina.Excel.Sheets;
using GameMain = FFXIVClientStructs.FFXIV.Client.Game.GameMain;

namespace LookingGlass.Plugin.SelfTest;

/// <summary>
/// Reads where the player is, for the self-test's private-area check (<see cref="PrivateArea"/>, which decides): the
/// territory, its intended use as the game holds it and as its sheet says, and the housing manager's facts. Each fact it
/// can't read is null, and the rule fails closed on it. Game thread only. Nothing read here is logged.
/// </summary>
internal static unsafe class PrivateAreaReader {
    public static PlaceFacts Read() {
        if (!Services.ClientState.IsLoggedIn) {
            return PlaceFacts.Unreadable;
        }

        uint territory;
        try {
            territory = Services.ClientState.TerritoryType;
        } catch (Exception) {
            return PlaceFacts.Unreadable;
        }

        if (territory == 0) {
            return PlaceFacts.Unreadable;
        }

        uint? use = null;
        uint? sheetUse = null;
        string? name = null;
        try {
            var main = GameMain.Instance();
            if (main != null) {
                use = (uint) main->CurrentTerritoryIntendedUseId;
            }
        } catch (Exception) {
            // Unknown: fails closed.
        }

        try {
            if (Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory) is { } row) {
                sheetUse = row.TerritoryIntendedUse.RowId;
                name = row.PlaceName.ValueNullable?.Name.ExtractText();
            }
        } catch (Exception) {
            // Unknown: fails closed.
        }

        HousingKind? housing = null;
        ulong? house = null;
        int? plot = null;
        int? room = null;
        bool? workshop = null;
        try {
            var manager = HousingManager.Instance();
            if (manager != null) {
                housing = manager->GetCurrentHousingTerritoryType() switch {
                    HousingTerritoryType.None => HousingKind.None,
                    HousingTerritoryType.Outdoor => HousingKind.Outdoor,
                    HousingTerritoryType.Indoor => HousingKind.Indoor,
                    HousingTerritoryType.Workshop => HousingKind.Workshop,
                    _ => null,
                };
                house = (ulong) manager->GetCurrentIndoorHouseId();
                plot = manager->GetCurrentPlot();
                room = manager->GetCurrentRoom();
                workshop = manager->IsInWorkshop();
            }
        } catch (Exception) {
            // What was read stays; the rest is unknown.
        }

        return new PlaceFacts(territory, use, sheetUse, housing, house, plot, room, workshop, name);
    }

    /// <summary>
    /// How many player characters other than the tester are within <paramref name="yalms"/> (and not
    /// <paramref name="partner"/>, two players): any failure counts as someone.
    /// </summary>
    public static int OthersNear(double yalms, string? partner = null) {
        try {
            if (Services.Objects.LocalPlayer is not { } me) {
                return 1;
            }

            var count = 0;
            foreach (var player in Services.Objects.PlayerObjects.OfType<IPlayerCharacter>()) {
                if (player.Address == me.Address) {
                    continue;
                }

                if (partner != null && $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}" == partner) {
                    continue;
                }

                if (System.Numerics.Vector3.Distance(me.Position, player.Position) <= yalms) {
                    count++;
                }
            }

            return count;
        } catch (Exception) {
            return 1;
        }
    }
}
