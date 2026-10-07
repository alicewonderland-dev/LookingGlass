using System.Text.RegularExpressions;

namespace LookingGlass.Server.Services;

/// <summary>
/// What the game allows as a character's name, checked before a registration asks the Lodestone anything (a name that can't
/// exist would only cost a search of the shared Lodestone queue), and how it spells its worlds.
/// </summary>
public static partial class GameWorlds {
    /// <summary>
    /// The public worlds the Lodestone lists characters on, by data center, written from memory: only a hint for spelling a
    /// world as the game does, never a reason to refuse one (the plugin sends the game's own name for the home world, and this
    /// may lack a world opened since).
    /// </summary>
    public static readonly IReadOnlyList<string> Known = [
        // North America
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren", // Aether
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera", // Crystal
        "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph", // Dynamis
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros", // Primal
        // Europe
        "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan", // Chaos
        "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark", // Light
        "Innocence", "Pixie", "Titania", "Tycoon", // Shadow
        // Oceania
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan", // Materia
        // Japan
        "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon", // Elemental
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima", // Gaia
        "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan", // Mana
        "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus", // Meteor
    ];

    /// <summary>The world's name as the game spells it, if it is <see cref="Known"/> (in any case); else null.</summary>
    public static string? Find(string world) {
        var trimmed = world.Trim();
        return Known.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the game allows this as a character's name: a first and last name, one space between, each 2 to 15 letters
    /// (A to Z), apostrophes or hyphens, starting with a letter.
    /// </summary>
    public static bool IsCharacterName(string name) => CharacterName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z'\-]{1,14} [A-Za-z][A-Za-z'\-]{1,14}$")]
    private static partial Regex CharacterName();
}
