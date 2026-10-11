using System.Globalization;
using System.Text.RegularExpressions;

namespace LookingGlass.Server.Admin;

/// <summary>
/// Times as the operator web page shows them: in the owner's time zone (<c>LookingGlass:Admin:TimeZone</c>), each with the
/// zone's abbreviation ("2026-10-10 14:02 BST"), so a time copied from the page is never ambiguous. Where the system gives no
/// abbreviation for the zone (Windows, or a zone whose name is only an offset), the offset from UTC stands in ("UTC+1").
/// </summary>
public static partial class PageTime {
    /// <summary>"2026-10-10 14:02 BST": the date and time in <paramref name="zone"/>, and the zone's abbreviation then.</summary>
    public static string Format(DateTimeOffset when, TimeZoneInfo zone) {
        var local = TimeZoneInfo.ConvertTime(when, zone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + Abbreviation(zone, when);
    }

    /// <summary>As <see cref="Format(DateTimeOffset, TimeZoneInfo)"/>, for Unix seconds.</summary>
    public static string Format(long unixSeconds, TimeZoneInfo zone) => Format(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone);

    /// <summary>The date alone, in <paramref name="zone"/>: "2026-10-10".</summary>
    public static string Date(long unixSeconds, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The zone's abbreviation at <paramref name="when"/> ("BST" in a London summer, "GMT" in its winter, "UTC" for UTC); where
    /// the system has none, the offset from UTC then ("UTC+1", "UTC-3:30").
    /// </summary>
    public static string Abbreviation(TimeZoneInfo zone, DateTimeOffset when) {
        if (zone.Id == TimeZoneInfo.Utc.Id || zone.Id is "Etc/UTC" or "UTC" or "Etc/UCT" or "Etc/Universal" or "Etc/Zulu") {
            return "UTC";
        }

        var name = zone.IsDaylightSavingTime(when) ? zone.DaylightName : zone.StandardName;
        if (Letters().IsMatch(name)) {
            // Linux, without the globalization data (the server runs so), names each zone by the abbreviation its zone file gives.
            return name;
        }

        return Offset(zone.GetUtcOffset(when));
    }

    /// <summary>"UTC+1", "UTC-3:30", "UTC+0".</summary>
    public static string Offset(TimeSpan offset) {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"UTC{sign}{abs.Hours.ToString(CultureInfo.InvariantCulture)}"
            : $"UTC{sign}{abs.Hours.ToString(CultureInfo.InvariantCulture)}:{abs.Minutes.ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>"Times are Europe/London (BST)." or "Times are UTC.": the page's last line.</summary>
    public static string Legend(TimeZoneInfo zone, DateTimeOffset now) {
        var abbreviation = Abbreviation(zone, now);
        return abbreviation == "UTC" && zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime
            ? "Times are UTC."
            : $"Times are {zone.Id} ({abbreviation} now).";
    }

    // An abbreviation as zone files give them: two to six capital letters (GMT, BST, CEST, AEDT).
    [GeneratedRegex("^[A-Z]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex Letters();
}
