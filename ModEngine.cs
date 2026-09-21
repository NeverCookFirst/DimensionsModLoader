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

    /// <summary>
    /// Where the archive actually is. Empty in state written before DLC
    /// archives outside the game folder could be patched.
    /// </summary>
    [JsonPropertyName("datPath")]
    public string DatPath { get; set; } = "";
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
    /// <summary>
    /// Finds the .DAT (or .DAT2) for an archive name. The game folder comes
    /// first; then each search folder and its immediate subfolders, which is
    /// how the Xbox 360 build keeps its DLC: one package folder per DLC under
    /// the content root, holding DLCnn.DAT2/.HDR2.
    /// </summary>
    public static string? ResolveArchivePath(
        string gameDir, string archive, IReadOnlyList<string>? searchDirs)
    {
        IEnumerable<string> Candidates(string dir)
        {
            yield return Path.Combine(dir, archive + ".DAT");
            yield return Path.Combine(dir, archive + ".DAT2");
        }
        foreach (string c in Candidates(gameDir))
        {
            if (File.Exists(c)) return c;
        }
        foreach (string dir in searchDirs ?? Array.Empty<string>())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string c in Candidates(dir))
            {
                if (File.Exists(c)) return c;
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                foreach (string c in Candidates(sub))
                {
                    if (File.Exists(c)) return c;
                }
            }
        }
        return null;
    }

    public static AppliedState ApplyMods(string gameDir, IReadOnlyList<ModInfo> mods,
                                         IReadOnlyList<string>? archiveSearchDirs = null)
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
            string? datPath = ResolveArchivePath(gameDir, group.Key, archiveSearchDirs);
            if (datPath == null)
            {
                // A DLC the user does not own: the rest of the mod still applies.
                // The update archive is different - without it nothing works.
                if (group.Key.StartsWith("DLC", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        $"warning: {group.Key} not installed, skipping its {group.Count()} file(s)");
                    continue;
                }
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
                DatPath = Path.GetFullPath(datPath),
            });
            // In-place injections first, additions after. An added entry is
            // inserted at its sorted CRC position and shifts every index past
            // it, while the state file records in-place indices for restore,
            // which replays them against the vanilla .HDR - so those indices
            // must be taken before anything moves.
            var ordered = group
                .OrderBy(kv => archive.FindEntry(kv.Key.internalPath) < 0 ? 1 : 0)
                .ToList();
            foreach (((_, string internalPath), (string sourceFile, string modName)) in ordered)
            {
                int index = archive.FindEntry(internalPath);
                if (index < 0)
                {
                    // The archive has no such file, so the mod is bringing a new
                    // one. Some mods cannot work any other way: a character is
                    // built from the abilities cached for it, and adding one to
                    // that cache is the only way to give it something it never
                    // had. Restore puts the original .HDR back and trims the
                    // .DAT, which takes the added entry with it.
                    index = archive.AddEntry(internalPath, File.ReadAllBytes(sourceFile));
                    state.DatPatches.Add(new DatPatchEntry
                    {
                        Archive = group.Key,
                        InternalPath = internalPath,
                        SourceMod = modName,
                        EntryIndex = index,
                    });
                    continue;
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
            // Older state files only name the archive; those always lived in
            // the game folder itself.
            string datPath = !string.IsNullOrEmpty(original.DatPath)
                ? original.DatPath
                : Path.Combine(gameDir, original.Archive + ".DAT");
            string hdrPath = DatArchive.HdrPathFor(datPath);
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
