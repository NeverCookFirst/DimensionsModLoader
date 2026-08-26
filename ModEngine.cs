using System.Text.Json;
using System.Text.Json.Serialization;

namespace DimensionsModManager;

public class AppliedEntry
{
    [JsonPropertyName("relPath")]
    public string RelPath { get; set; } = "";

    [JsonPropertyName("hadOriginal")]
    public bool HadOriginal { get; set; }

    [JsonPropertyName("sourceMod")]
    public string SourceMod { get; set; } = "";
}

public class DatPatchEntry
{
    [JsonPropertyName("archive")]
    public string Archive { get; set; } = "";

    [JsonPropertyName("internalPath")]
    public string InternalPath { get; set; } = "";

    [JsonPropertyName("sourceMod")]
    public string SourceMod { get; set; } = "";

    [JsonPropertyName("entryIndex")]
    public int EntryIndex { get; set; } = -1;
}

public class DatOriginalEntry
{
    [JsonPropertyName("archive")]
    public string Archive { get; set; } = "";

    [JsonPropertyName("datLength")]
    public long DatLength { get; set; }
}

public class AppliedState
{
    [JsonPropertyName("appliedUtc")]
    public DateTime AppliedUtc { get; set; }

    [JsonPropertyName("entries")]
    public List<AppliedEntry> Entries { get; set; } = new();

    [JsonPropertyName("datPatches")]
    public List<DatPatchEntry> DatPatches { get; set; } = new();

    [JsonPropertyName("datOriginals")]
    public List<DatOriginalEntry> DatOriginals { get; set; } = new();
}

/// <summary>
/// Applies mod files onto a game folder with one-time backups of the
/// vanilla originals, and can restore everything back. Loose files are
/// plain file layering; DAT-packed files are injected in place into the
/// TT Games .DAT/.HDR archives (see DatArchive).
/// </summary>
public static class ModEngine
{
    public const string BackupDirName = ".vanilla_backup";
    public const string StateFileName = "modmanager_state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static List<ModInfo> DiscoverMods(string modsRoot, string platform)
    {
        var mods = new List<ModInfo>();
        if (!Directory.Exists(modsRoot))
        {
            return mods;
        }
        foreach (string folder in Directory.EnumerateDirectories(modsRoot)
                                           .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ModInfo? mod = ModInfo.Load(folder);
            if (mod != null && mod.MatchesPlatform(platform))
            {
                mods.Add(mod);
            }
        }
        return mods;
    }

    public static AppliedState? GetAppliedState(string gameDir)
    {
        string statePath = Path.Combine(gameDir, StateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AppliedState>(
                File.ReadAllText(statePath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the given mods in list order (later mods win file conflicts).
    /// If mods are already applied, restores vanilla first so re-applying
    /// with a different selection is always correct.
    /// </summary>
    public static AppliedState ApplyMods(string gameDir, IReadOnlyList<ModInfo> mods)
    {
        if (!Directory.Exists(gameDir))
        {
            throw new DirectoryNotFoundException($"Game folder not found: {gameDir}");
        }
        if (GetAppliedState(gameDir) != null)
        {
            RestoreVanilla(gameDir);
        }

        // Later mods in the list override earlier ones per file.
        var fileMap = new Dictionary<string, (string sourceFile, string modName)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ModInfo mod in mods)
        {
            if (!Directory.Exists(mod.FilesPath))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(
                         mod.FilesPath, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(mod.FilesPath, file);
                fileMap[relPath] = (file, mod.Name);
            }
        }

        string backupDir = Path.Combine(gameDir, BackupDirName);
        var state = new AppliedState { AppliedUtc = DateTime.UtcNow };
        foreach ((string relPath, (string sourceFile, string modName)) in fileMap)
        {
            string targetPath = Path.Combine(gameDir, relPath);
            bool hadOriginal = File.Exists(targetPath);
            if (hadOriginal)
            {
                string backupPath = Path.Combine(backupDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                if (!File.Exists(backupPath))
                {
                    File.Copy(targetPath, backupPath);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourceFile, targetPath, overwrite: true);
            state.Entries.Add(new AppliedEntry
            {
                RelPath = relPath,
                HadOriginal = hadOriginal,
                SourceMod = modName,
            });
        }

        // DAT-injected files: datfiles\ARCHIVE\internal\path, later mods win.
        var datMap = new Dictionary<(string archive, string internalPath),
                                    (string sourceFile, string modName)>();
        foreach (ModInfo mod in mods)
        {
            if (!Directory.Exists(mod.DatFilesPath))
            {
                continue;
            }
            foreach (string archiveDir in Directory.EnumerateDirectories(mod.DatFilesPath))
            {
                string archive = Path.GetFileName(archiveDir).ToUpperInvariant();
                foreach (string file in Directory.EnumerateFiles(
                             archiveDir, "*", SearchOption.AllDirectories))
                {
                    string internalPath = Path.GetRelativePath(archiveDir, file)
                        .Replace('/', '\\').ToUpperInvariant();
                    datMap[(archive, internalPath)] = (file, mod.Name);
                }
            }
        }
        foreach (var group in datMap.GroupBy(kv => kv.Key.archive))
        {
            string datPath = Path.Combine(gameDir, group.Key + ".DAT");
            if (!File.Exists(datPath))
            {
                throw new FileNotFoundException(
                    $"Archive {group.Key}.DAT not found in the game folder.");
            }
            var archive = new DatArchive(datPath);
            // Tiny backup: the original HDR bytes + the original DAT length.
            string hdrBackup = Path.Combine(backupDir, group.Key + ".HDR");
            Directory.CreateDirectory(backupDir);
            if (!File.Exists(hdrBackup))
            {
                File.Copy(archive.HdrPath, hdrBackup);
            }
            state.DatOriginals.Add(new DatOriginalEntry
            {
                Archive = group.Key,
                DatLength = new FileInfo(datPath).Length,
            });
            foreach (((_, string internalPath), (string sourceFile, string modName)) in group)
            {
                int index = archive.FindEntry(internalPath);
                if (index < 0)
                {
                    throw new InvalidDataException(
                        $"{group.Key}.DAT has no file named {internalPath} " +
                        $"(mod \"{modName}\").");
                }
                // In-place injection overwrites vanilla bytes, so save the
                // entry's original data once (keyed by archive + entry index).
                string blobDir = Path.Combine(backupDir, "datbytes");
                string blobPath = Path.Combine(blobDir, $"{group.Key}_{index}.bin");
                if (!File.Exists(blobPath))
                {
                    Directory.CreateDirectory(blobDir);
                    File.WriteAllBytes(blobPath, archive.ReadEntryData(index));
                }
                archive.InjectFile(index, File.ReadAllBytes(sourceFile));
                state.DatPatches.Add(new DatPatchEntry
                {
                    Archive = group.Key,
                    InternalPath = internalPath,
                    SourceMod = modName,
                    EntryIndex = index,
                });
            }
            archive.SaveHdr();
        }

        File.WriteAllText(Path.Combine(gameDir, StateFileName),
                          JsonSerializer.Serialize(state, JsonOptions));
        return state;
    }

    /// <summary>Puts every vanilla file back and removes added files.</summary>
    public static int RestoreVanilla(string gameDir)
    {
        AppliedState? state = GetAppliedState(gameDir);
        if (state == null)
        {
            return 0;
        }
        string backupDir = Path.Combine(gameDir, BackupDirName);
        int restored = 0;
        foreach (AppliedEntry entry in state.Entries)
        {
            string targetPath = Path.Combine(gameDir, entry.RelPath);
            if (entry.HadOriginal)
            {
                string backupPath = Path.Combine(backupDir, entry.RelPath);
                if (File.Exists(backupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(backupPath, targetPath, overwrite: true);
                    restored++;
                }
            }
            else if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                restored++;
            }
        }
        // Undo DAT injections: restore the original HDR, truncate the DAT
        // back to its pre-mod length, then write the backed-up original
        // bytes over any in-place injections (using the restored HDR's
        // offsets, which are vanilla again at this point).
        foreach (DatOriginalEntry original in state.DatOriginals)
        {
            string hdrBackup = Path.Combine(backupDir, original.Archive + ".HDR");
            string datPath = Path.Combine(gameDir, original.Archive + ".DAT");
            string hdrPath = Path.ChangeExtension(datPath, ".HDR");
            if (File.Exists(hdrBackup))
            {
                File.Copy(hdrBackup, hdrPath, overwrite: true);
                restored++;
            }
            if (File.Exists(datPath))
            {
                using (var dat = new FileStream(datPath, FileMode.Open, FileAccess.Write))
                {
                    if (dat.Length > original.DatLength)
                    {
                        dat.SetLength(original.DatLength);
                    }
                }
                var archive = new DatArchive(datPath);
                foreach (DatPatchEntry patch in state.DatPatches.Where(
                             p => p.Archive == original.Archive && p.EntryIndex >= 0))
                {
                    string blobPath = Path.Combine(
                        backupDir, "datbytes", $"{patch.Archive}_{patch.EntryIndex}.bin");
                    if (!File.Exists(blobPath))
                    {
                        continue;
                    }
                    byte[] blob = File.ReadAllBytes(blobPath);
                    var (offset, zsize, _) = archive.GetEntry(patch.EntryIndex);
                    if (blob.Length == zsize)
                    {
                        using var dat = new FileStream(
                            datPath, FileMode.Open, FileAccess.Write);
                        dat.Position = offset;
                        dat.Write(blob, 0, blob.Length);
                        restored++;
                    }
                }
            }
        }
        File.Delete(Path.Combine(gameDir, StateFileName));
        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }
        return restored;
    }
}
