using System.Text.Json;
using System.Text.Json.Serialization;

namespace DimensionsModManager;

/// <summary>
/// A mod on disk: a folder under mods/ containing mod.json and a files/
/// subfolder that mirrors the game folder layout.
/// </summary>
public class ModInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>"x360", "ps3" or "any".</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "any";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonIgnore]
    public string FolderPath { get; set; } = "";

    [JsonIgnore]
    public string FilesPath => Path.Combine(FolderPath, "files");

    /// <summary>
    /// datfiles\ARCHIVE\internal\path.ext - files injected into the game's
    /// ARCHIVE.DAT/.HDR pair instead of being copied loose.
    /// </summary>
    [JsonIgnore]
    public string DatFilesPath => Path.Combine(FolderPath, "datfiles");

    [JsonIgnore]
    public bool Enabled { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static ModInfo? Load(string modFolder)
    {
        string manifestPath = Path.Combine(modFolder, "mod.json");
        ModInfo mod;
        if (File.Exists(manifestPath))
        {
            try
            {
                mod = JsonSerializer.Deserialize<ModInfo>(
                          File.ReadAllText(manifestPath), JsonOptions)
                      ?? new ModInfo();
            }
            catch (JsonException)
            {
                mod = new ModInfo { Description = "(invalid mod.json)" };
            }
        }
        else
        {
            mod = new ModInfo();
        }
        if (string.IsNullOrWhiteSpace(mod.Name))
        {
            mod.Name = Path.GetFileName(modFolder);
        }
        mod.FolderPath = modFolder;
        // A mod must actually ship files (loose and/or DAT-injected).
        bool hasLoose = Directory.Exists(mod.FilesPath) &&
            Directory.EnumerateFiles(mod.FilesPath, "*", SearchOption.AllDirectories).Any();
        bool hasDat = Directory.Exists(mod.DatFilesPath) &&
            Directory.EnumerateFiles(mod.DatFilesPath, "*", SearchOption.AllDirectories).Any();
        if (!hasLoose && !hasDat)
        {
            return null;
        }
        return mod;
    }

    public bool MatchesPlatform(string platform)
    {
        return Platform.Equals("any", StringComparison.OrdinalIgnoreCase) ||
               Platform.Equals(platform, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Name} v{Version}";
}
