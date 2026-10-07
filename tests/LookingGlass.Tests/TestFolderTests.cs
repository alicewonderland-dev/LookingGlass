namespace LookingGlass.Tests;

/// <summary>
/// Tests delete only folders tests make: "lgt-" folders directly in the temporary folder. A mistaken path (a user's data
/// folder, the temporary folder itself, a folder inside a test's) is refused, loudly, and left alone.
/// </summary>
public sealed class TestFolderTests {
    [Fact]
    public void OnlyTestFoldersInTheTemporaryFolderAreDeleted() {
        var temp = Path.GetTempPath();
        var ours = Path.Combine(temp, "lgt-guard-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(ours, "lgt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nested);
        try {
            foreach (var refused in new[] {
                nested, temp, Path.GetDirectoryName(Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar))!,
                Path.Combine(temp, "lgx-" + Guid.NewGuid().ToString("N")), Path.Combine(temp, "lgt-"),
                Path.Combine(temp, "LGT-" + Guid.NewGuid().ToString("N")), Path.Combine(ours, "..", "..", "lgt-" + Guid.NewGuid().ToString("N")),
                "", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            }) {
                Assert.Throws<InvalidOperationException>(() => Harness.DeleteDirectory(refused));
            }

            Assert.True(Directory.Exists(nested));

            // A test's own folder, given with a trailing separator or not, is deleted.
            Harness.DeleteDirectory(ours + Path.DirectorySeparatorChar);
            Assert.False(Directory.Exists(ours));
        } finally {
            Harness.DeleteDirectory(ours);
        }
    }
}
