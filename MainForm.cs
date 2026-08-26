using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DimensionsModManager;

public class AppConfig
{
    [JsonPropertyName("gameDir")]
    public string GameDir { get; set; } = "";

    [JsonPropertyName("modsDir")]
    public string ModsDir { get; set; } = "";
}

public class MainForm : Form
{
    // The manager targets the RPCS3 (PS3) build of LEGO Dimensions.
    private const string Platform = "ps3";

    private static readonly string AppDir =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string ConfigPath = Path.Combine(AppDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly TextBox gameDirBox_ = new();
    private readonly Button browseGameButton_ = new();
    private readonly TextBox modsDirBox_ = new();
    private readonly Button browseModsButton_ = new();
    private readonly CheckedListBox modsList_ = new();
    private readonly Button upButton_ = new();
    private readonly Button downButton_ = new();
    private readonly TextBox descriptionBox_ = new();
    private readonly Button applyButton_ = new();
    private readonly Button restoreButton_ = new();
    private readonly Button refreshButton_ = new();
    private readonly Button openModsButton_ = new();
    private readonly Label statusLabel_ = new();

    private AppConfig config_ = new();

    public MainForm()
    {
        Text = "LEGO Dimensions Mod Manager (RPCS3)";
        MinimumSize = new Size(640, 540);
        Size = new Size(680, 580);
        StartPosition = FormStartPosition.CenterScreen;

        var gameDirLabel = new Label
        {
            Text = "Game folder:",
            AutoSize = true,
            Location = new Point(12, 16),
        };
        gameDirBox_.Location = new Point(100, 12);
        gameDirBox_.Width = 440;
        gameDirBox_.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        gameDirBox_.TextChanged += (_, _) => SaveGameDir();
        browseGameButton_.Text = "...";
        browseGameButton_.Location = new Point(548, 11);
        browseGameButton_.Width = 36;
        browseGameButton_.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browseGameButton_.Click += (_, _) => BrowseGameDir();

        var modsDirLabel = new Label
        {
            Text = "Mods folder:",
            AutoSize = true,
            Location = new Point(12, 48),
        };
        modsDirBox_.Location = new Point(100, 44);
        modsDirBox_.Width = 440;
        modsDirBox_.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        modsDirBox_.TextChanged += (_, _) => SaveModsDir();
        browseModsButton_.Text = "...";
        browseModsButton_.Location = new Point(548, 43);
        browseModsButton_.Width = 36;
        browseModsButton_.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browseModsButton_.Click += (_, _) => BrowseModsDir();

        var modsLabel = new Label
        {
            Text = "Mods (checked = enabled, top to bottom = load order, lower wins conflicts):",
            AutoSize = true,
            Location = new Point(12, 84),
        };
        modsList_.Location = new Point(12, 104);
        modsList_.Size = new Size(520, 236);
        modsList_.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right |
                           AnchorStyles.Bottom;
        modsList_.CheckOnClick = true;
        modsList_.SelectedIndexChanged += (_, _) => ShowDescription();

        upButton_.Text = "Up";
        upButton_.Location = new Point(544, 104);
        upButton_.Width = 60;
        upButton_.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        upButton_.Click += (_, _) => MoveSelected(-1);
        downButton_.Text = "Down";
        downButton_.Location = new Point(544, 136);
        downButton_.Width = 60;
        downButton_.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        downButton_.Click += (_, _) => MoveSelected(1);

        descriptionBox_.Location = new Point(12, 350);
        descriptionBox_.Size = new Size(592, 70);
        descriptionBox_.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        descriptionBox_.Multiline = true;
        descriptionBox_.ReadOnly = true;
        descriptionBox_.ScrollBars = ScrollBars.Vertical;

        applyButton_.Text = "Apply Mods";
        applyButton_.Location = new Point(12, 432);
        applyButton_.Size = new Size(120, 32);
        applyButton_.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        applyButton_.Click += (_, _) => Apply();
        restoreButton_.Text = "Restore Vanilla";
        restoreButton_.Location = new Point(140, 432);
        restoreButton_.Size = new Size(120, 32);
        restoreButton_.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        restoreButton_.Click += (_, _) => Restore();
        refreshButton_.Text = "Refresh";
        refreshButton_.Location = new Point(268, 432);
        refreshButton_.Size = new Size(90, 32);
        refreshButton_.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        refreshButton_.Click += (_, _) => RefreshMods();
        openModsButton_.Text = "Open Mods Folder";
        openModsButton_.Location = new Point(366, 432);
        openModsButton_.Size = new Size(130, 32);
        openModsButton_.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        openModsButton_.Click += (_, _) => OpenModsFolder();

        statusLabel_.Location = new Point(12, 474);
        statusLabel_.Size = new Size(592, 40);
        statusLabel_.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        Controls.AddRange(new Control[]
        {
            gameDirLabel, gameDirBox_, browseGameButton_,
            modsDirLabel, modsDirBox_, browseModsButton_,
            modsLabel, modsList_, upButton_, downButton_, descriptionBox_,
            applyButton_, restoreButton_, refreshButton_, openModsButton_,
            statusLabel_,
        });

        LoadConfig();
        gameDirBox_.Text = config_.GameDir;
        modsDirBox_.Text = config_.ModsDir;
    }

    private void LoadConfig()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                config_ = JsonSerializer.Deserialize<AppConfig>(
                              File.ReadAllText(ConfigPath), JsonOptions)
                          ?? new AppConfig();
            }
            catch (JsonException)
            {
                config_ = new AppConfig();
            }
        }
    }

    private void SaveConfig()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config_, JsonOptions));
    }

    private void SaveGameDir()
    {
        config_.GameDir = gameDirBox_.Text;
        SaveConfig();
        UpdateStatus();
    }

    private void SaveModsDir()
    {
        config_.ModsDir = modsDirBox_.Text;
        SaveConfig();
        RefreshMods();
    }

    private void BrowseGameDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the RPCS3 game folder (the one containing the game's .DAT files)",
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(gameDirBox_.Text))
        {
            dialog.SelectedPath = gameDirBox_.Text;
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            gameDirBox_.Text = dialog.SelectedPath;
        }
    }

    private void BrowseModsDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder that holds your mods (each mod is a subfolder)",
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(modsDirBox_.Text))
        {
            dialog.SelectedPath = modsDirBox_.Text;
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            modsDirBox_.Text = dialog.SelectedPath;
        }
    }

    private void RefreshMods()
    {
        var previouslyEnabled = modsList_.Items.Cast<ModInfo>()
            .Where((_, i) => modsList_.GetItemChecked(i))
            .Select(m => m.FolderPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        modsList_.Items.Clear();
        string modsRoot = modsDirBox_.Text.Trim();
        if (!string.IsNullOrEmpty(modsRoot) && Directory.Exists(modsRoot))
        {
            foreach (ModInfo mod in ModEngine.DiscoverMods(modsRoot, Platform))
            {
                modsList_.Items.Add(mod, previouslyEnabled.Contains(mod.FolderPath));
            }
        }
        UpdateStatus();
    }

    private void ShowDescription()
    {
        if (modsList_.SelectedItem is ModInfo mod)
        {
            string author = string.IsNullOrWhiteSpace(mod.Author)
                ? ""
                : $" by {mod.Author}";
            descriptionBox_.Text =
                $"{mod.Name} v{mod.Version}{author} [{mod.Platform}]\r\n{mod.Description}";
        }
    }

    private void MoveSelected(int delta)
    {
        int index = modsList_.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= modsList_.Items.Count)
        {
            return;
        }
        object item = modsList_.Items[index];
        bool wasChecked = modsList_.GetItemChecked(index);
        modsList_.Items.RemoveAt(index);
        modsList_.Items.Insert(target, item);
        modsList_.SetItemChecked(target, wasChecked);
        modsList_.SelectedIndex = target;
    }

    private bool WarnIfEmulatorRunning()
    {
        if (Process.GetProcessesByName("rpcs3").Length > 0)
        {
            return MessageBox.Show(
                       this,
                       "RPCS3 appears to be running. Modifying game files while " +
                       "the game is open can crash it.\n\nContinue anyway?",
                       "RPCS3 running",
                       MessageBoxButtons.YesNo,
                       MessageBoxIcon.Warning) == DialogResult.Yes;
        }
        return true;
    }

    private void Apply()
    {
        string gameDir = gameDirBox_.Text.Trim();
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            MessageBox.Show(this, "Set a valid game folder first.", "Mod Manager");
            return;
        }
        if (!WarnIfEmulatorRunning())
        {
            return;
        }
        var enabledMods = modsList_.Items.Cast<ModInfo>()
            .Where((_, i) => modsList_.GetItemChecked(i))
            .ToList();
        try
        {
            if (enabledMods.Count == 0)
            {
                int restored = ModEngine.RestoreVanilla(gameDir);
                statusLabel_.Text = $"No mods enabled - restored vanilla ({restored} files).";
            }
            else
            {
                AppliedState state = ModEngine.ApplyMods(gameDir, enabledMods);
                statusLabel_.Text =
                    $"Applied {enabledMods.Count} mod(s), " +
                    $"{state.Entries.Count} loose file(s), " +
                    $"{state.DatPatches.Count} DAT patch(es).";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Apply failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateStatus();
    }

    private void Restore()
    {
        string gameDir = gameDirBox_.Text.Trim();
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            MessageBox.Show(this, "Set a valid game folder first.", "Mod Manager");
            return;
        }
        if (!WarnIfEmulatorRunning())
        {
            return;
        }
        try
        {
            int restored = ModEngine.RestoreVanilla(gameDir);
            statusLabel_.Text = restored > 0
                ? $"Restored vanilla ({restored} files)."
                : "Nothing to restore - game is already vanilla.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateStatus();
    }

    private void OpenModsFolder()
    {
        string modsRoot = modsDirBox_.Text.Trim();
        if (string.IsNullOrEmpty(modsRoot))
        {
            MessageBox.Show(this, "Set a mods folder first.", "Mod Manager");
            return;
        }
        Directory.CreateDirectory(modsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = modsRoot,
            UseShellExecute = true,
        });
    }

    private void UpdateStatus()
    {
        string gameDir = gameDirBox_.Text.Trim();
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            Text = "LEGO Dimensions Mod Manager (RPCS3)";
            return;
        }
        AppliedState? state = ModEngine.GetAppliedState(gameDir);
        int modded = state == null ? 0 : state.Entries.Count + state.DatPatches.Count;
        Text = state == null
            ? "LEGO Dimensions Mod Manager (RPCS3) - vanilla"
            : $"LEGO Dimensions Mod Manager (RPCS3) - {modded} modded item(s)";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshMods();
    }
}
