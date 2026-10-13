using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>The self-test's private-area rule (docs/self-test-plan.md, "The private-area check"): every row, failing closed.</summary>
public sealed class SelfTestPrivateAreaTests {
    private static PlaceFacts Inn(uint territory = 177) => new(territory, 2, 2, HousingKind.None, ulong.MaxValue, -1, 0, false);

    private static PlaceFacts Indoor(uint territory = 339, int plot = 5, int room = 0, ulong house = 0x1234) =>
        new(territory, 14, 14, HousingKind.Indoor, house, plot, room, false);

    [Fact]
    public void EveryInnRoomCounts() {
        foreach (var territory in PrivateArea.InnRooms) {
            var verdict = PrivateArea.Judge(Inn(territory));
            Assert.Equal(PlaceKind.InnRoom, verdict.Kind);
            Assert.True(verdict.Private);
            Assert.False(verdict.NeedsNobodyNear);
        }
    }

    [Fact]
    public void AHouseItsChambersAndItsWorkshopCount() {
        Assert.Equal(PlaceKind.House, PrivateArea.Judge(Indoor(room: 0)).Kind);
        Assert.Equal(PlaceKind.Chambers, PrivateArea.Judge(Indoor(room: 3)).Kind);
        Assert.Equal(PlaceKind.Workshop, PrivateArea.Judge(Indoor() with { Housing = HousingKind.Workshop }).Kind);
        Assert.Equal(PlaceKind.Workshop, PrivateArea.Judge(Indoor() with { InWorkshop = true }).Kind);
        // A new interior keeps use 14: the rule goes by use and the housing manager, not a list of territories.
        Assert.Equal(PlaceKind.House, PrivateArea.Judge(Indoor(territory: 1375)).Kind);
        foreach (var facts in new[] { Indoor(), Indoor(room: 3), Indoor() with { Housing = HousingKind.Workshop } }) {
            var verdict = PrivateArea.Judge(facts);
            Assert.True(verdict.Private);
            Assert.True(verdict.NeedsNobodyNear);
        }
    }

    [Fact]
    public void AnApartmentCountsButNotItsLobby() {
        Assert.Equal(PlaceKind.Apartment, PrivateArea.Judge(Indoor(territory: 608, plot: -128, room: 12)).Kind);
        Assert.Equal(PlaceKind.Apartment, PrivateArea.Judge(Indoor(territory: 608, plot: -127, room: 1)).Kind);
        Assert.True(PrivateArea.Judge(Indoor(territory: 608, plot: -127, room: 1)).NeedsNobodyNear);

        // By room 0, and by the lobby's territory whatever else it says.
        Assert.Equal(PlaceKind.ApartmentLobby, PrivateArea.Judge(Indoor(territory: 608, plot: -128, room: 0)).Kind);
        foreach (var lobby in PrivateArea.ApartmentLobbies) {
            var verdict = PrivateArea.Judge(Indoor(territory: lobby, plot: -128, room: 5));
            Assert.Equal(PlaceKind.ApartmentLobby, verdict.Kind);
            Assert.False(verdict.Private);
        }
    }

    [Fact]
    public void WardsYardsAndEverywhereElseDont() {
        Assert.Equal(PlaceKind.Outdoor, PrivateArea.Judge(new PlaceFacts(339, 13, 13, HousingKind.Outdoor, null, 5, 0, false)).Kind);
        // A yard by either fact.
        Assert.Equal(PlaceKind.Outdoor, PrivateArea.Judge(new PlaceFacts(339, 13, 13, HousingKind.None, null, null, null, false)).Kind);
        Assert.Equal(PlaceKind.Elsewhere, PrivateArea.Judge(new PlaceFacts(129, 0, 0, HousingKind.None, null, null, null, false)).Kind);
        Assert.False(PrivateArea.Judge(new PlaceFacts(129, 0, 0, HousingKind.None, null, null, null, false)).Private);
    }

    [Fact]
    public void ALoadingScreenIsNotPrivate() {
        var verdict = PrivateArea.Judge(Inn() with { Territory = 0 });
        Assert.Equal(PlaceKind.Loading, verdict.Kind);
        Assert.False(verdict.Private);
    }

    [Fact]
    public void TheSheetAndTheGameDisagreeingFailsClosed() {
        Assert.Equal(PlaceKind.Unknown, PrivateArea.Judge(Inn() with { SheetUse = 14 }).Kind);
        // Indoors by the sheet, a yard or nothing by the housing manager.
        Assert.Equal(PlaceKind.Outdoor, PrivateArea.Judge(Indoor() with { Housing = HousingKind.Outdoor }).Kind);
        Assert.Equal(PlaceKind.Unknown, PrivateArea.Judge(Indoor() with { Housing = HousingKind.None }).Kind);
        // An inn by the sheet, inside housing by the housing manager.
        Assert.Equal(PlaceKind.Unknown, PrivateArea.Judge(Inn() with { Housing = HousingKind.Indoor }).Kind);
    }

    [Fact]
    public void AnythingUnreadableFailsClosed() {
        var unreadable = new[] {
            Inn() with { IntendedUse = null },
            Inn() with { SheetUse = null },
            Inn() with { Housing = null },
            Indoor() with { HouseId = null },
            Indoor() with { HouseId = 0 },
            Indoor() with { HouseId = ulong.MaxValue },
            Indoor() with { Plot = null },
            Indoor() with { Room = null },
            Indoor() with { Room = -1 },
            Indoor(plot: -3),
        };

        foreach (var facts in unreadable) {
            Assert.False(PrivateArea.Judge(facts).Private, $"Counted as private: {facts}");
        }

        Assert.False(PrivateArea.Judge(null).Private);
        Assert.False(PrivateArea.Judge(PlaceFacts.Unreadable).Private);
    }

    [Fact]
    public void APlaceCountsOnlyOnceItHasSettled() {
        var watch = new PrivateAreaWatch();
        var start = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

        Assert.False(watch.Observe(Inn(), start));
        Assert.False(watch.Observe(Inn(), start.AddSeconds(1.9)));
        Assert.True(watch.Observe(Inn(), start.AddSeconds(2)));

        // A zone change starts it again, even to another private place.
        Assert.False(watch.Observe(Inn(178), start.AddSeconds(3)));
        Assert.True(watch.Observe(Inn(178), start.AddSeconds(5)));

        // So does the answer changing in the same territory (the housing facts catching up).
        Assert.False(watch.Observe(Indoor(plot: -128, room: 0), start.AddSeconds(10)));
        Assert.False(watch.Observe(Indoor(plot: -128, room: 4), start.AddSeconds(11)));
        Assert.Equal(TimeSpan.FromSeconds(1), watch.UntilSettled(start.AddSeconds(12)));
        Assert.True(watch.Observe(Indoor(plot: -128, room: 4), start.AddSeconds(13)));

        // Not private never settles into private.
        Assert.False(watch.Observe(Indoor(plot: -128, room: 0), start.AddSeconds(14)));
        Assert.False(watch.Observe(Indoor(plot: -128, room: 0), start.AddSeconds(60)));
        Assert.Equal(PlaceKind.ApartmentLobby, watch.Last.Kind);
    }
}
