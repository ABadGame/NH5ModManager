using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mono.Cecil;
using Mono.Cecil.Cil;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using DefaultAssemblyResolver = Mono.Cecil.DefaultAssemblyResolver;
using ILProcessor = Mono.Cecil.Cil.ILProcessor;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using ReaderParameters = Mono.Cecil.ReaderParameters;
using TypeDefinition = Mono.Cecil.TypeDefinition;

namespace NH5ModManager
{
    public partial class Form1 : Form
    {
        private const string CustomServerUrl = "http://72.39.41.141:8000/";

        public string GameDirectory { get; private set; } = @"C:\Program Files (x86)\Steam\steamapps\common\NASCAR Heat 5";
        private string ConfigsDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
        private string VanillaBackupDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NH5ModManager_Data", "Vanilla_Backup");

        private string _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NASCAR Heat 5");
        private bool _isLoadingSettings;
        private SaveProfileManager SaveProfiles => new SaveProfileManager(_saveDirectory, ConfigsDirectory);

        private static void EnsureGameClosed()
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.ProcessName.Contains("NASCARHeat5", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Contains("NASCAR Heat 5", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Close NASCAR Heat 5 before deploying mods or switching saves.");
                }
            }
        }

        private void ChooseSaveFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = @"Select the SAVE folder (normally C:\Users\<your user>\NASCAR Heat 5), not the Steam game installation.",
                SelectedPath = _saveDirectory,
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            string chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.SelectedPath));
            if (Directory.Exists(Path.Combine(chosen, "NASCARHeat5_Data")))
            {
                MessageBox.Show("Select the game's save folder, not its installation folder.", "Save Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string? rejection = DescribeUnsafeSaveFolder(chosen);
            if (rejection != null)
            {
                MessageBox.Show(rejection, "Save Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _saveDirectory = chosen;
            SaveSettings();
            lblStatus.Text = $"Save folder: {_saveDirectory}";
        }

        private static bool SameFolderPath(string a, string b)
            => string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase);

        private static bool IsInside(string candidate, string root)
            => candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // Returns null when the chosen folder is safe as the live save folder;
        // otherwise a user-facing reason why it must not be used. The save
        // switcher moves the live folder itself, so the live folder must never
        // sit inside, or above, the profile folders that it will be moved into.
        private static string? DescribeUnsafeSaveFolder(string chosen)
        {
            if (string.IsNullOrEmpty(Path.GetDirectoryName(chosen)))
                return "A drive root cannot be the save folder. Choose the game's save folder (normally C:\\Users\\<your user>\\NASCAR Heat 5).";

            string? baseRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
            string profilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles")));

            if (IsInside(chosen, profilesRoot) || SameFolderPath(chosen, profilesRoot))
                return "That folder is inside the manager's Profiles area. Profile saves are moved into it automatically; picking it as the live save folder would nest folders into themselves.\n\nUse the game's save folder (normally C:\\Users\\<your user>\\NASCAR Heat 5).";

            if (IsInside(profilesRoot, chosen) || SameFolderPath(chosen, baseRoot))
                return "That folder contains this manager's Profiles (or the manager itself). The save switcher moves profile saves under here, which would nest folders into themselves.\n\nChoose a location that does not contain the manager, e.g. C:\\Users\\<your user>\\NASCAR Heat 5.";

            return null;
        }

        private bool _isLoadingProfile = false;
        private string _activeProfileName = "Default";

        private readonly Dictionary<string, string> _vanillaFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ContextMenuStrip _profileContextMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _modListContextMenu = new ContextMenuStrip();
        private readonly string _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vanilla_map.json");
        private readonly string _deployedManifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeployedManifest.json");
        private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.json");
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pObserved);
        private const uint WM_DROPFILES = 0x0233;
        private const uint WM_COPYDATA = 0x004A;
        private const uint MSGFLT_ALLOW = 1;


        public Form1()
        {
            InitializeComponent();

            this.lstMods.UseCompatibleStateImageBehavior = false;
            this.AllowDrop = true;
            this.lstMods.AllowDrop = true;

            // Wire DragEnter, DragOver, and DragDrop to both the Form and ListView
            this.lstMods.DragEnter += Form1_DragEnter;
            this.lstMods.DragOver += Form1_DragOver;
            this.lstMods.DragDrop += Form1_DragDrop;
            this.DragEnter += Form1_DragEnter;
            this.DragOver += Form1_DragOver;
            this.DragDrop += Form1_DragDrop;

            this.lstMods.ItemCheck += lstMods_ItemCheck;
            this.cmbProfiles.SelectedIndexChanged += cmbProfiles_SelectedIndexChanged;
            this.chkUnlockDLC.CheckedChanged += chkSettings_CheckedChanged;
            this.chkCustomServer.CheckedChanged += chkSettings_CheckedChanged;
            this.chkUnlockSetups.CheckedChanged += chkSettings_CheckedChanged;
            this.chkFiveGear.CheckedChanged += chkSettings_CheckedChanged;

            InitializeContextMenus();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            txtGamePath.Text = GameDirectory;

            if (!Directory.Exists(ConfigsDirectory)) Directory.CreateDirectory(ConfigsDirectory);
            if (!Directory.Exists(VanillaBackupDirectory)) Directory.CreateDirectory(VanillaBackupDirectory);

            LoadSettings();
            BuildVanillaFileMap();
            LoadProfiles();
            LoadInstalledMods();
        }

        private class AppSettings
        {
            public string? SaveDirectory { get; set; }
            public bool RememberGameLocation { get; set; } = true;
            public string? GameDirectory { get; set; }
            public bool UnlockDLC { get; set; } = false;
            public bool EnableCustomServer { get; set; } = false;
            public bool UnlockCustomSetups { get; set; } = false;
            public bool AddFifthGear { get; set; } = false;
        }

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        if (!string.IsNullOrWhiteSpace(settings.SaveDirectory))
                        {
                            string persisted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.SaveDirectory));
                            // A persisted value predating the folder-picker guard
                            // could still be unsafe (e.g. inside the manager's own
                            // folder); fall back to the default instead of using it.
                            if (DescribeUnsafeSaveFolder(persisted) == null
                                && !Directory.Exists(Path.Combine(persisted, "NASCARHeat5_Data")))
                            {
                                _saveDirectory = persisted;
                            }
                        }
                        chkRememberPath.Checked = settings.RememberGameLocation;
                        if (settings.RememberGameLocation && !string.IsNullOrWhiteSpace(settings.GameDirectory))
                        {
                            GameDirectory = settings.GameDirectory;
                            txtGamePath.Text = GameDirectory;
                        }
                        chkUnlockDLC.Checked = settings.UnlockDLC;
                        chkCustomServer.Checked = settings.EnableCustomServer;
                        chkUnlockSetups.Checked = settings.UnlockCustomSetups;
                        chkFiveGear.Checked = settings.AddFifthGear;
                    }
                }
                catch { }
            }
            _isLoadingSettings = false;
        }

        private void SaveSettings()
        {
            if (_isLoadingSettings) return;
            try
            {
                var settings = new AppSettings
                {
                    SaveDirectory = _saveDirectory,
                    RememberGameLocation = chkRememberPath.Checked,
                    // Only persist the game folder when "Remember Game Location"
                    // is ticked; unchecking deliberately forgets it.
                    GameDirectory = chkRememberPath.Checked ? GameDirectory : null,
                    UnlockDLC = chkUnlockDLC.Checked,
                    EnableCustomServer = chkCustomServer.Checked,
                    UnlockCustomSetups = chkUnlockSetups.Checked,
                    AddFifthGear = chkFiveGear.Checked
                };
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        private void chkSettings_CheckedChanged(object? sender, EventArgs e)
        {
            SaveSettings();
        }

        private void chkRememberPath_CheckedChanged(object? sender, EventArgs e)
        {
            SaveSettings();
        }

        private void BuildVanillaFileMap()
        {
            _vanillaFileMap.Clear();

            if (File.Exists(_cachePath))
            {
                try
                {
                    string json = File.ReadAllText(_cachePath);
                    var cachedMap = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (cachedMap != null)
                    {
                        foreach (var kvp in cachedMap) _vanillaFileMap[kvp.Key] = kvp.Value;
                        return;
                    }
                }
                catch { }
            }

            string gameDataDir = Path.Combine(GameDirectory, "NASCARHeat5_Data");
            if (!Directory.Exists(gameDataDir)) return;

            foreach (string filePath in Directory.GetFiles(gameDataDir, "*.*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(filePath);
                string relativePath = Path.GetRelativePath(GameDirectory, filePath);

                _vanillaFileMap.TryAdd(fileName, relativePath);
            }

            try
            {
                string json = JsonSerializer.Serialize(_vanillaFileMap, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(_cachePath, json);
            }
            catch { }
        }

        private void InitializeContextMenus()
        {
            // Profile Dropdown Context Menu
            _profileContextMenu.Items.Clear();

            ToolStripMenuItem renameProfileItem = new ToolStripMenuItem("Rename Profile");
            renameProfileItem.Click += (s, e) => RenameSelectedProfile();

            ToolStripMenuItem copyProfileItem = new ToolStripMenuItem("Duplicate / Copy Profile");
            copyProfileItem.Click += (s, e) => CopySelectedProfile();

            ToolStripMenuItem exportProfileItem = new ToolStripMenuItem("Export Profile Package (.nh5prof)");
            exportProfileItem.Click += (s, e) => ExportSelectedProfile();

            ToolStripMenuItem openFolderItem = new ToolStripMenuItem("Open Profile in File Explorer");
            openFolderItem.Click += (s, e) => OpenSelectedProfileFolder();

            ToolStripMenuItem deleteProfileItem = new ToolStripMenuItem("Delete Selected Profile");
            deleteProfileItem.Click += (s, e) => DeleteSelectedProfile();

            _profileContextMenu.Items.Add(renameProfileItem);
            _profileContextMenu.Items.Add(copyProfileItem);
            _profileContextMenu.Items.Add(exportProfileItem);
            _profileContextMenu.Items.Add(new ToolStripSeparator());
            _profileContextMenu.Items.Add(openFolderItem);
            _profileContextMenu.Items.Add(deleteProfileItem);

            _profileContextMenu.Items.Add(new ToolStripSeparator());
            _profileContextMenu.Items.Add("Choose game save folder...", null, (s, e) => ChooseSaveFolder());
            this.cmbProfiles.ContextMenuStrip = _profileContextMenu;

            // Mod ListView Context Menu
            _modListContextMenu.Items.Clear();

            ToolStripMenuItem enableAllItem = new ToolStripMenuItem("Check All Mods");
            enableAllItem.Click += (s, e) => SetAllModItemsState(true);

            ToolStripMenuItem disableAllItem = new ToolStripMenuItem("Uncheck All Mods");
            disableAllItem.Click += (s, e) => SetAllModItemsState(false);

            ToolStripMenuItem deleteModItem = new ToolStripMenuItem("Delete Selected Mod File(s)");
            deleteModItem.Click += (s, e) => DeleteSelectedModFiles();

            _modListContextMenu.Items.Add(enableAllItem);
            _modListContextMenu.Items.Add(disableAllItem);
            _modListContextMenu.Items.Add(new ToolStripSeparator());
            _modListContextMenu.Items.Add(deleteModItem);

            this.lstMods.ContextMenuStrip = _modListContextMenu;
        }

        private void RenameSelectedProfile()
        {
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            if (selectedProfile.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase) ||
                selectedProfile.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The Vanilla baseline profile cannot be renamed.", "Action Restricted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string newName = Microsoft.VisualBasic.Interaction.InputBox("Enter new name for profile:", "Rename Profile", selectedProfile);
            string cleanName = string.Concat(newName.Split(Path.GetInvalidFileNameChars())).Trim();

            if (string.IsNullOrWhiteSpace(cleanName) || cleanName.Equals(selectedProfile, StringComparison.OrdinalIgnoreCase)) return;

            string sourceDir = Path.Combine(ConfigsDirectory, selectedProfile);
            string targetDir = Path.Combine(ConfigsDirectory, cleanName);

            string sourceJson = GetProfileJsonPath(selectedProfile);
            string targetJson = GetProfileJsonPath(cleanName);

            if (Directory.Exists(targetDir))
            {
                MessageBox.Show("A profile with that name already exists.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Move the profile folder first so its Save/ folder travels with the mods.
                if (Directory.Exists(sourceDir)) Directory.Move(sourceDir, targetDir);
                try
                {
                    SaveProfiles.RenameProfile(selectedProfile, cleanName);
                }
                catch
                {
                    if (Directory.Exists(targetDir) && !Directory.Exists(sourceDir))
                        Directory.Move(targetDir, sourceDir);
                    throw;
                }
                if (File.Exists(sourceJson)) File.Move(sourceJson, targetJson);

                LoadProfiles();
                cmbProfiles.SelectedItem = cleanName;
                lblStatus.Text = $"Renamed profile to '{cleanName}'.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to rename profile: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CopySelectedProfile()
        {
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            string defaultNewName = $"{selectedProfile} - Copy";

            string newName = Microsoft.VisualBasic.Interaction.InputBox("Enter name for the copied profile:", "Copy Profile", defaultNewName);
            string cleanName = string.Concat(newName.Split(Path.GetInvalidFileNameChars())).Trim();

            if (string.IsNullOrWhiteSpace(cleanName)) return;

            string sourceDir = Path.Combine(ConfigsDirectory, selectedProfile);
            string targetDir = Path.Combine(ConfigsDirectory, cleanName);

            string sourceJson = GetProfileJsonPath(selectedProfile);
            string targetJson = GetProfileJsonPath(cleanName);

            if (Directory.Exists(targetDir))
            {
                MessageBox.Show("A profile with that name already exists.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                if (Directory.Exists(sourceDir)) CopyDirectory(sourceDir, targetDir, excludeProfileSave: true);
                if (File.Exists(sourceJson)) File.Copy(sourceJson, targetJson, overwrite: true);

                LoadProfiles();
                cmbProfiles.SelectedItem = cleanName;
                lblStatus.Text = $"Created profile copy '{cleanName}'.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy profile: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportSelectedProfile()
        {
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";

            if (selectedProfile.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase) ||
                selectedProfile.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The vanilla backup profile does not need to be exported.", "Export Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string sourceDir = Path.Combine(ConfigsDirectory, selectedProfile);
            try
            {
                // "Default" is always listed, even before it contains any mods.
                // Materialize its normal profile structure so an empty profile can
                // still be exported and imported as a valid package.
                Directory.CreateDirectory(Path.Combine(sourceDir, "NASCARHeat5_Data"));
                SaveActiveModsForProfile(selectedProfile);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to prepare profile '{selectedProfile}' for export:\n{ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "NH5 Profile Package (*.nh5prof)|*.nh5prof|Zip Archive (*.zip)|*.zip",
                FileName = $"{selectedProfile}.nh5prof",
                Title = "Export Profile Package"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);

                    string tempFolder = Path.Combine(Path.GetTempPath(), $"nh5_export_{Guid.NewGuid()}");
                    Directory.CreateDirectory(tempFolder);

                    string targetProfileDir = Path.Combine(tempFolder, selectedProfile);
                    CopyDirectory(sourceDir, targetProfileDir, excludeProfileSave: true);

                    string jsonPath = GetProfileJsonPath(selectedProfile);
                    if (File.Exists(jsonPath))
                    {
                        File.Copy(jsonPath, Path.Combine(tempFolder, Path.GetFileName(jsonPath)), overwrite: true);
                    }

                    ZipFile.CreateFromDirectory(tempFolder, sfd.FileName);
                    Directory.Delete(tempFolder, true);

                    lblStatus.Text = $"Exported profile '{selectedProfile}' successfully.";
                    MessageBox.Show($"Successfully exported profile '{selectedProfile}'!", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export profile:\n{ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportProfilePackage()
        {
            using OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "NH5 Profile Package (*.nh5prof;*.zip)|*.nh5prof;*.zip",
                Title = "Import Profile Package"
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    string tempExtractDir = Path.Combine(Path.GetTempPath(), $"nh5_import_{Guid.NewGuid()}");
                    ZipFile.ExtractToDirectory(ofd.FileName, tempExtractDir);

                    string[] extractedDirs = Directory.GetDirectories(tempExtractDir);
                    if (extractedDirs.Length == 0)
                    {
                        MessageBox.Show("Invalid profile package structure.", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Directory.Delete(tempExtractDir, true);
                        return;
                    }

                    string sourceProfileFolder = extractedDirs[0];
                    string importedName = Path.GetFileName(sourceProfileFolder);
                    string targetDir = Path.Combine(ConfigsDirectory, importedName);

                    if (Directory.Exists(targetDir))
                    {
                        var result = MessageBox.Show(
                            $"A profile named '{importedName}' already exists. Overwrite it?",
                            "Profile Exists",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question
                        );

                        if (result == DialogResult.No)
                        {
                            Directory.Delete(tempExtractDir, true);
                            return;
                        }

                        Directory.Delete(targetDir, true);
                    }

                    CopyDirectory(sourceProfileFolder, targetDir, excludeProfileSave: true);

                    string[] jsonFiles = Directory.GetFiles(tempExtractDir, "*.json");
                    foreach (string jsonFile in jsonFiles)
                    {
                        string destJson = Path.Combine(ConfigsDirectory, Path.GetFileName(jsonFile));
                        File.Copy(jsonFile, destJson, overwrite: true);
                    }

                    Directory.Delete(tempExtractDir, true);

                    LoadProfiles();
                    cmbProfiles.SelectedItem = importedName;

                    lblStatus.Text = $"Imported profile '{importedName}' successfully.";
                    MessageBox.Show($"Successfully imported profile '{importedName}'!", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to import profile:\n{ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DeleteSelectedModFiles()
        {
            if (lstMods.SelectedItems.Count == 0) return;

            if (MessageBox.Show($"Are you sure you want to delete {lstMods.SelectedItems.Count} selected file(s) from this profile?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                foreach (ListViewItem item in lstMods.SelectedItems)
                {
                    if (item.Tag != null && File.Exists(item.Tag.ToString()))
                    {
                        try
                        {
                            File.Delete(item.Tag.ToString()!);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to delete file {item.Text}: {ex.Message}", "Delete Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }

                string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
                if (selectedProfile.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase) ||
                    selectedProfile.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("The Vanilla baseline profile cannot be deleted.", "Action Restricted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                LoadInstalledMods();
                SaveActiveModsForProfile(selectedProfile);
            }
        }

        private void SetAllModItemsState(bool isChecked)
        {
            lstMods.BeginUpdate();
            foreach (ListViewItem item in lstMods.Items)
            {
                item.Checked = isChecked;
            }
            lstMods.EndUpdate();

            UpdateStatusAndConflicts();
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            SaveActiveModsForProfile(selectedProfile);
        }

        private void OpenSelectedProfileFolder()
        {
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            string profilePath = Path.Combine(ConfigsDirectory, selectedProfile);

            if (!Directory.Exists(profilePath)) Directory.CreateDirectory(profilePath);

            Process.Start(new ProcessStartInfo
            {
                FileName = profilePath,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private void DeleteSelectedProfile()
        {
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            if (selectedProfile.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The Default profile cannot be deleted.", "Action Restricted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show($"Are you sure you want to delete profile '{selectedProfile}'?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                string profilePath = Path.Combine(ConfigsDirectory, selectedProfile);
                string jsonPath = GetProfileJsonPath(selectedProfile);

                try
                {
                    if (Directory.Exists(profilePath)) Directory.Delete(profilePath, true);
                    if (File.Exists(jsonPath)) File.Delete(jsonPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting profile: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                LoadProfiles();
            }
        }

        private void btnBrowse_Click(object? sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select NASCAR Heat 5 Root Folder";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    GameDirectory = fbd.SelectedPath;
                    txtGamePath.Text = GameDirectory;
                    // Persist the choice immediately when remembering is on,
                    // so it survives even a crash before the next save point.
                    SaveSettings();
                    BuildVanillaFileMap();
                    LoadProfiles();
                    LoadInstalledMods();
                }
            }
        }

        private void btnRefresh_Click(object? sender, EventArgs e)
        {
            string currentSelection = cmbProfiles.SelectedItem?.ToString() ?? "Default";

            BuildVanillaFileMap();
            LoadProfiles();

            if (cmbProfiles.Items.Contains(currentSelection))
            {
                cmbProfiles.SelectedItem = currentSelection;
            }

            LoadInstalledMods();
            lblStatus.Text = "Refreshed vanilla map and profiles.";
        }

        private void LoadProfiles()
        {
            _isLoadingProfile = true;
            cmbProfiles.Items.Clear();
            cmbProfiles.Items.Add("Default");

            if (Directory.Exists(ConfigsDirectory))
            {
                // Folders that exist only to hold a parked save (a lone "Save"
                // subfolder, e.g. legacy "Name (2)" slots) are not profiles;
                // keep them out of the menu.
                HashSet<string> registeredProfileNames = ReadRegisteredProfileNames();
                HashSet<string> slotOnlyFolders = CollectSaveOnlyFolders(registeredProfileNames);
                // Settings jsons can also be orphaned by the old "(2)" slot
                // layout: a "<slotName>.json" whose slot is not a real profile
                // is stale (its mod state was never reachable) — prune it.
                foreach (string slotName in slotOnlyFolders)
                {
                    string orphanJson = GetProfileJsonPath(slotName);
                    try
                    {
                        if (File.Exists(orphanJson)) File.Delete(orphanJson);
                    }
                    catch
                    {
                        // Cleanup only; never block the profile list.
                    }
                }
                foreach (string dir in Directory.GetDirectories(ConfigsDirectory))
                {
                    string folderName = Path.GetFileName(dir);
                    if (folderName.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                    if (folderName.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (slotOnlyFolders.Contains(folderName)) continue;
                    cmbProfiles.Items.Add(folderName);
                }
            }

            cmbProfiles.SelectedIndex = 0;
            _isLoadingProfile = false;
        }

        private HashSet<string> ReadRegisteredProfileNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string statePath = Path.Combine(ConfigsDirectory, ".SaveStore", "state.json");
                if (File.Exists(statePath))
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
                    if (document.RootElement.TryGetProperty("Profiles", out JsonElement profiles)
                        && profiles.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in profiles.EnumerateObject())
                        {
                            names.Add(property.Name);
                            // Slot ids are the VALUES ("Name", "Name (2)", …).
                            // A folder named after a slot id only parks a save;
                            // it must never appear as a profile in the menu.
                            if (property.Value.ValueKind == JsonValueKind.String
                                && property.Value.GetString() is { Length: > 0 } slotId)
                            {
                                names.Add(slotId);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best effort only; the menu still lists profile folders.
            }
            return names;
        }

        private HashSet<string> CollectSaveOnlyFolders(HashSet<string>? registeredProfileNames)
        {
            var slotOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string dir in Directory.GetDirectories(ConfigsDirectory))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                    string savePath = Path.Combine(dir, SaveProfileManager.ProfileSaveFolderName);
                    if (!Directory.Exists(savePath)) continue;
                    bool hasAnythingElse = Directory
                        .EnumerateFileSystemEntries(dir)
                        .Any(entry => !entry.Equals(savePath, StringComparison.OrdinalIgnoreCase));
                    if (hasAnythingElse) continue;

                    // A folder registered as its own profile key is a real
                    // (modless) profile; anything else only parks a save.
                    if (registeredProfileNames != null && !registeredProfileNames.Contains(name)) continue;
                    slotOnly.Add(name);
                }
            }
            catch
            {
                // Menu hygiene only; never block profile listing.
            }
            return slotOnly;
        }

        private string GetProfileJsonPath(string profileName)
        {
            string cleanName = string.Join("_", profileName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(ConfigsDirectory, $"{cleanName}.json");
        }

        private HashSet<string> LoadActiveModsForProfile(string profileName)
        {
            string jsonPath = GetProfileJsonPath(profileName);
            if (File.Exists(jsonPath))
            {
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null) return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                }
                catch { }
            }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveActiveModsForProfile(string profileName)
        {
            if (_isLoadingProfile) return;

            var activeMods = new List<string>();
            foreach (ListViewItem item in lstMods.Items)
            {
                if (item.Checked) activeMods.Add(item.Text);
            }

            string jsonPath = GetProfileJsonPath(profileName);
            try
            {
                string json = JsonSerializer.Serialize(activeMods, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, json);
            }
            catch { }
        }

        private void cmbProfiles_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_isLoadingProfile) return;

            _isLoadingProfile = true;
            LoadInstalledMods();
            _isLoadingProfile = false;
        }

        private void LoadInstalledMods()
        {
            lstMods.ItemCheck -= lstMods_ItemCheck;
            lstMods.Items.Clear();

            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            HashSet<string> activeForProfile = LoadActiveModsForProfile(selectedProfile);
            string profileFolder = Path.Combine(ConfigsDirectory, selectedProfile);

            if (Directory.Exists(profileFolder))
            {
                NormalizeModDirectory(profileFolder);

                string profileDataFolder = Path.Combine(profileFolder, "NASCARHeat5_Data");
                if (Directory.Exists(profileDataFolder))
                {
                    string jsonPath = GetProfileJsonPath(selectedProfile);
                    bool hasSavedState = File.Exists(jsonPath);

                    string[] profileFiles = Directory.GetFiles(profileDataFolder, "*.*", SearchOption.AllDirectories);
                    foreach (string filePath in profileFiles)
                    {
                        string relativePath = Path.GetRelativePath(profileFolder, filePath);
                        string ext = Path.GetExtension(filePath).ToLowerInvariant();

                        string tagType = ext switch
                        {
                            ".bank" => "[Audio]",
                            ".dll" => "[Core]",
                            ".bundle" or ".assets" => "[Asset]",
                            _ => "[Data]"
                        };

                        string itemLabel = $"{tagType} {relativePath}";
                        ListViewItem item = new ListViewItem(itemLabel)
                        {
                            Tag = filePath,
                            Checked = hasSavedState ? activeForProfile.Contains(itemLabel) : true
                        };
                        lstMods.Items.Add(item);
                    }
                }

                string profileDlcFolder = Path.Combine(profileFolder, "DLC");
                // "Default"/"Vanilla" represent the clean baseline: no DLC entries there.
                bool isBaselineProfile = selectedProfile.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    || selectedProfile.Equals("Vanilla", StringComparison.OrdinalIgnoreCase)
                    || selectedProfile.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase);
                if (!isBaselineProfile && Directory.Exists(profileDlcFolder))
                {
                    string jsonPath = GetProfileJsonPath(selectedProfile);
                    bool hasSavedState = File.Exists(jsonPath);
                    bool dlcNeverSeen = hasSavedState &&
                        !activeForProfile.Any(a => a.StartsWith("[DLC] ", StringComparison.OrdinalIgnoreCase));

                    string[] dlcFiles = Directory.GetFiles(profileDlcFolder, "*.*", SearchOption.AllDirectories);
                    foreach (string filePath in dlcFiles)
                    {
                        string relativePath = Path.GetRelativePath(profileFolder, filePath);
                        string itemLabel = $"[DLC] {relativePath}";
                        // A profile saved before DLC support has no [DLC] entries:
                        // treat its first appearance as "enabled by default".
                        bool checkedState = !hasSavedState
                            || dlcNeverSeen
                            || activeForProfile.Contains(itemLabel);

                        ListViewItem item = new ListViewItem(itemLabel)
                        {
                            Tag = filePath,
                            Checked = checkedState
                        };
                        lstMods.Items.Add(item);
                    }
                }
            }

            UpdateStatusAndConflicts();
            lstMods.ItemCheck += lstMods_ItemCheck;
        }

        private void lstMods_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            this.BeginInvoke(new Action(() =>
            {
                UpdateStatusAndConflicts();
                string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
                SaveActiveModsForProfile(selectedProfile);
            }));
        }

        private void UpdateStatusAndConflicts()
        {
            int activeCount = 0;
            int disabledCount = 0;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool conflictFound = false;

            foreach (ListViewItem item in lstMods.Items)
            {
                if (item.Checked)
                {
                    activeCount++;
                    string cleanName = item.Text;

                    if (seenNames.Contains(cleanName))
                    {
                        item.ForeColor = Color.Red;
                        item.UseItemStyleForSubItems = false;
                        conflictFound = true;
                    }
                    else
                    {
                        seenNames.Add(cleanName);
                        item.ForeColor = Color.Black;
                    }
                }
                else
                {
                    disabledCount++;
                    item.ForeColor = Color.Gray;
                }
            }

            string statusText = $"Loaded {activeCount} active, {disabledCount} disabled mod(s).";
            if (conflictFound) statusText += " | ⚠️ Conflict Detected: Duplicate Active Mods!";
            lblStatus.Text = statusText;
        }

        private bool SwapDataFolderForProfile(string profileName, HashSet<string> enabledAssetPaths)
        {
            string gameDataDir = Path.Combine(GameDirectory, "NASCARHeat5_Data");
            if (!Directory.Exists(gameDataDir)) return false;

            try
            {
                if (File.Exists(_deployedManifestPath))
                {
                    try
                    {
                        var previousManifest = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_deployedManifestPath));
                        if (previousManifest != null)
                        {
                            foreach (string relativeFile in previousManifest)
                            {
                                string targetFile = Path.Combine(GameDirectory, relativeFile);
                                string backupFile = Path.Combine(VanillaBackupDirectory, relativeFile);

                                if (File.Exists(backupFile))
                                {
                                    string? destDir = Path.GetDirectoryName(targetFile);
                                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                                    File.Copy(backupFile, targetFile, overwrite: true);
                                }
                                else if (File.Exists(targetFile))
                                {
                                    File.Delete(targetFile);
                                }
                            }
                        }
                    }
                    catch { }
                }

                var newlyDeployedFiles = new List<string>();

                bool isVanillaProfile = profileName.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase) ||
                                         profileName.Equals("Vanilla", StringComparison.OrdinalIgnoreCase);

                string profileDataDir = Path.Combine(ConfigsDirectory, profileName, "NASCARHeat5_Data");

                if (!isVanillaProfile && Directory.Exists(profileDataDir))
                {
                    this.Invoke(() => lblStatus.Text = $"Applying active mod files for '{profileName}'...");

                    foreach (string sourceFile in Directory.GetFiles(profileDataDir, "*.*", SearchOption.AllDirectories))
                    {
                        if (!enabledAssetPaths.Contains(sourceFile)) continue;

                        string relativePath = Path.GetRelativePath(profileDataDir, sourceFile);
                        string relativeToGame = Path.Combine("NASCARHeat5_Data", relativePath);
                        string destFile = Path.Combine(gameDataDir, relativePath);
                        string backupFile = Path.Combine(VanillaBackupDirectory, relativeToGame);

                        if (File.Exists(destFile) && !File.Exists(backupFile))
                        {
                            string? backupDir = Path.GetDirectoryName(backupFile);
                            if (!string.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
                            File.Copy(destFile, backupFile, overwrite: true);
                        }

                        string? destDir = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                        File.Copy(sourceFile, destFile, overwrite: true);
                        newlyDeployedFiles.Add(relativeToGame);
                    }
                }

                // Profile DLC packs: NASCAR Heat 5 reads the "<game>\DLC" folder
                // for bonus scenarios (name.scenario), extra car bundles
                // (challenge_*.rab) and the Unlock_*.txt entitlement stubs.
                // Deploy every file in the profile's DLC folder the same way the
                // NASCARHeat5_Data files are deployed: vanilla backup first,
                // tracked in DeployedManifest.json so switching profiles (or
                // restoring) removes/restores exactly what this profile added.
                string profileDlcDir = Path.Combine(ConfigsDirectory, profileName, "DLC");
                bool dlcBaselineProfile = profileName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    || profileName.Equals("Vanilla", StringComparison.OrdinalIgnoreCase)
                    || profileName.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase);
                if (!isVanillaProfile && !dlcBaselineProfile && Directory.Exists(profileDlcDir))
                {
                    this.Invoke(() => lblStatus.Text = $"Applying DLC files for '{profileName}'...");

                    string gameDlcDir = Path.Combine(GameDirectory, "DLC");
                    foreach (string sourceFile in Directory.GetFiles(profileDlcDir, "*.*", SearchOption.AllDirectories))
                    {
                        string dlcRelativePath = Path.GetRelativePath(profileDlcDir, sourceFile);
                        string relativeToGame = Path.Combine("DLC", dlcRelativePath);
                        string destFile = Path.Combine(gameDlcDir, dlcRelativePath);
                        if (!enabledAssetPaths.Contains(sourceFile)) continue;

                        string backupFile = Path.Combine(VanillaBackupDirectory, relativeToGame);

                        if (File.Exists(destFile) && !File.Exists(backupFile))
                        {
                            string? backupDir = Path.GetDirectoryName(backupFile);
                            if (!string.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
                            File.Copy(destFile, backupFile, overwrite: true);
                        }

                        string? destDir = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                        File.Copy(sourceFile, destFile, overwrite: true);
                        newlyDeployedFiles.Add(relativeToGame);
                    }
                }

                File.WriteAllText(_deployedManifestPath, JsonSerializer.Serialize(newlyDeployedFiles));
                return true;
            }
            catch (Exception ex)
            {
                this.Invoke(() => MessageBox.Show($"Failed to deploy profile assets:\n{ex.Message}", "Deployment Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
                return false;
            }
        }

        private string NormalizeModDirectory(string inputPath)
        {
            string targetDataFolder = Path.Combine(inputPath, "NASCARHeat5_Data");
            if (!Directory.Exists(targetDataFolder)) Directory.CreateDirectory(targetDataFolder);

            string[] rootFiles = Directory.GetFiles(inputPath, "*.*", SearchOption.TopDirectoryOnly);
            foreach (string filePath in rootFiles)
            {
                string fileName = Path.GetFileName(filePath);
                if (_vanillaFileMap.TryGetValue(fileName, out string? relativePath))
                {
                    string destinationPath = Path.Combine(inputPath, relativePath);
                    string? destDir = Path.GetDirectoryName(destinationPath);

                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    if (File.Exists(destinationPath)) File.Delete(destinationPath);

                    File.Move(filePath, destinationPath);
                }
            }

            return targetDataFolder;
        }

        private async void btnDeploy_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(GameDirectory) || !Directory.Exists(GameDirectory))
            {
                MessageBox.Show("Please select a valid NASCAR Heat 5 game directory first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            SaveActiveModsForProfile(selectedProfile);

            this.UseWaitCursor = true;
            this.Enabled = false;

            var checkedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ListViewItem item in lstMods.Items)
            {
                if (item.Checked && item.Tag != null && File.Exists(item.Tag.ToString()))
                {
                    checkedAssetPaths.Add(item.Tag.ToString()!);
                }
            }

            bool unlockDlcRequested = chkUnlockDLC.Checked;
            bool customServerRequested = chkCustomServer.Checked;
            bool unlockSetupsRequested = chkUnlockSetups.Checked;
            bool addFifthGearRequested = chkFiveGear.Checked;

            bool deploymentSuccess = false;

            try
            {
                await Task.Run(() =>
                {
                    EnsureGameClosed();
                    if (Directory.Exists(Path.Combine(_saveDirectory, "NASCARHeat5_Data")))
                        throw new IOException("The save folder points to a game installation. Choose the save folder from the profile menu.");
                    SaveProfiles.Deploy(selectedProfile, () =>
                    {
                        this.Invoke(new Action(() => lblStatus.Text = $"Deploying mods and save for '{selectedProfile}'..."));
                        if (!SwapDataFolderForProfile(selectedProfile, checkedAssetPaths))
                            throw new IOException("Profile assets could not be deployed.");
                        if (unlockDlcRequested) DeployDlcFixFiles();
                        // Always run: this applies enabled patches AND restores
                        // the vanilla bodies of disabled ones (full reversibility).
                        ApplyAssemblyPatches(unlockDlcRequested, customServerRequested, unlockSetupsRequested, addFifthGearRequested);
                    });
                    deploymentSuccess = true;
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Deployment stopped: {ex.Message}", "Deployment Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Enabled = true;
                this.UseWaitCursor = false;
            }

            if (!deploymentSuccess)
            {
                lblStatus.Text = $"Deployment Failed | Profile: {selectedProfile}";
                MessageBox.Show("Mod deployment encountered errors. Assembly patching was aborted to prevent corruption.", "Deployment Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            lblStatus.Text = $"Deployment Complete | Active Profile: {selectedProfile}";
            MessageBox.Show($"Successfully deployed profile '{selectedProfile}'!", "Deployment Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (MessageBox.Show("Deployment complete! Launch NASCAR Heat 5 now?", "Launch Game", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                LaunchNASCARHeat5();
            }
        }

        private void ApplyAssemblyPatches(bool enableDlc, bool enableCustomServer, bool unlockSetups, bool addFifthGear)
        {
            string gameDir = GameDirectory.Trim();
            string managedDir = Path.Combine(gameDir, "NASCARHeat5_Data", "Managed");
            string dllPath = Path.Combine(managedDir, "Assembly-CSharp.dll");
            string backupPath = dllPath + ".nh5final-backup";
            string tempOutputPath = dllPath + ".tmp-seturl";

            if (!File.Exists(dllPath)) return;

            try
            {
                // 1. Create clean baseline backup if absent
                if (!File.Exists(backupPath))
                {
                    File.Copy(dllPath, backupPath, overwrite: true);
                }

                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(managedDir);
                var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

                using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(dllPath, readerParameters))
                {
                    ModuleDefinition module = asm.MainModule;

                    // --- Patch 1: Unlock DLC ---
                    if (enableDlc)
                    {
                        TypeDefinition? dlcType = module.Types.FirstOrDefault(t => t.Name == "SteamPlatformDLCLoader");
                        MethodDefinition? seasonPassMethod = dlcType?.Methods.FirstOrDefault(m => m.Name == "do_they_own_the_season_pass");

                        if (seasonPassMethod != null)
                        {
                            ILProcessor il = seasonPassMethod.Body.GetILProcessor();
                            seasonPassMethod.Body.Instructions.Clear();
                            il.Append(il.Create(OpCodes.Ldc_I4_1));
                            il.Append(il.Create(OpCodes.Ret));
                        }
                    }

                    // --- Patch 2: Conditional Custom Server URL ---
                    if (enableCustomServer)
                    {
                        TypeDefinition? ngUtilType = module.Types.FirstOrDefault(t => t.Name == "NGUtil");
                        MethodDefinition? getBaseUrlMethod = ngUtilType?.Methods.FirstOrDefault(m => m.Name == "GetBaseURL" && m.Parameters.Count == 0 && m.HasBody);

                        if (getBaseUrlMethod != null)
                        {
                            // Check if stock URLs exist in the current body
                            bool hasStockUrls = getBaseUrlMethod.Body.Instructions.Any(i =>
                                i.OpCode == OpCodes.Ldstr &&
                                (string.Equals(i.Operand as string, "https://n2020.mgrsys.com/", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(i.Operand as string, "https://n2022.mgrsys.com/", StringComparison.OrdinalIgnoreCase)));

                            if (hasStockUrls)
                            {
                                // Path A: In-place string replacement (Safe for existing mods)
                                foreach (var inst in getBaseUrlMethod.Body.Instructions)
                                {
                                    if (inst.OpCode == OpCodes.Ldstr && inst.Operand is string str)
                                    {
                                        if (string.Equals(str, "https://n2020.mgrsys.com/", StringComparison.OrdinalIgnoreCase))
                                            inst.Operand = "http://72.39.41.141:8000/n2020/";
                                        else if (string.Equals(str, "https://n2022.mgrsys.com/", StringComparison.OrdinalIgnoreCase))
                                            inst.Operand = "http://72.39.41.141:8000/n2022/";
                                    }
                                }
                            }
                            else
                            {
                                // Path B: Scan existing assembly for pre-resolved method references
                                MethodReference? globalMethod = FindMethodRef(module, mr => mr.Name == "Global" && mr.DeclaringType.Name == "InputSys");
                                MethodReference? debugGetKeyMethod = FindMethodRef(module, mr => mr.Name == "Debug_GetKeyDown");
                                MethodReference? getInstanceMethod = FindMethodRef(module, mr => mr.Name == "get_Instance" && mr.DeclaringType.FullName.Contains("LazySingleton") && mr.DeclaringType.FullName.Contains("GameYearManager"));
                                MethodReference? isFutureYearMethod = FindMethodRef(module, mr => mr.Name == "IsFutureYearActive" && mr.DeclaringType.Name == "GameYearManager");

                                if (globalMethod != null && debugGetKeyMethod != null && getInstanceMethod != null && isFutureYearMethod != null)
                                {
                                    getBaseUrlMethod.Body.ExceptionHandlers.Clear();
                                    getBaseUrlMethod.Body.Variables.Clear();
                                    getBaseUrlMethod.Body.Instructions.Clear();
                                    getBaseUrlMethod.Body.InitLocals = false;
                                    getBaseUrlMethod.Body.MaxStackSize = 8;

                                    ILProcessor il = getBaseUrlMethod.Body.GetILProcessor();

                                    Instruction targetGetInstance = il.Create(OpCodes.Call, getInstanceMethod);
                                    Instruction targetN2020 = il.Create(OpCodes.Ldstr, "http://72.39.41.141:8000/n2020/");
                                    Instruction targetRet = il.Create(OpCodes.Ret);

                                    // 1. if (InputSys.Global().Debug_GetKeyDown((MGIKeyCode)42)) [42 = LEFT_SHIFT]
                                    il.Emit(OpCodes.Call, globalMethod);
                                    il.Emit(OpCodes.Ldc_I4_S, (sbyte)42);
                                    il.Emit(OpCodes.Callvirt, debugGetKeyMethod);
                                    il.Emit(OpCodes.Brfalse, targetGetInstance);

                                    // 2. return debug URL
                                    il.Emit(OpCodes.Ldstr, "http://192.168.1.132:8000/");
                                    il.Emit(OpCodes.Ret);

                                    // 3. LazySingleton<GameYearManager>.Instance.IsFutureYearActive()
                                    il.Append(targetGetInstance);
                                    il.Emit(OpCodes.Callvirt, isFutureYearMethod);
                                    il.Emit(OpCodes.Brfalse, targetN2020);

                                    // 4. Return n2022 URL
                                    il.Emit(OpCodes.Ldstr, "http://72.39.41.141:8000/n2022/");
                                    il.Emit(OpCodes.Br, targetRet);

                                    // 5. Return n2020 URL
                                    il.Append(targetN2020);
                                    il.Append(targetRet);
                                }
                            }
                        }
                    }

                    // --- Patch 3: Unlock Custom Setups ---
                    // NASCAR Heat 5 locks the full garage behind the Hard/Expert
                    // driving-aids preset: VSChangeSetup.IsGarageButtonActive()
                    // compares the preset against VSDrivingAids.Preset.HARD and
                    // below that only the simplified tighten/loosen slider is
                    // offered (this is what kills custom setups on the 2022
                    // side). In addition GarageSetupManager
                    // .get_custom_setups_are_allowed() can refuse custom slots
                    // for some sessions. Patch both gates open.
                    if (unlockSetups)
                    {
                        TypeDefinition? changeSetupType = module.Types.FirstOrDefault(t => t.Name == "VSChangeSetup");
                        MethodDefinition? garageButtonMethod = changeSetupType?.Methods
                            .FirstOrDefault(m => m.Name == "IsGarageButtonActive" && m.HasBody);

                        if (garageButtonMethod != null)
                        {
                            garageButtonMethod.Body.ExceptionHandlers.Clear();
                            garageButtonMethod.Body.Instructions.Clear();
                            garageButtonMethod.Body.Variables.Clear();
                            garageButtonMethod.Body.InitLocals = false;
                            ILProcessor garageIl = garageButtonMethod.Body.GetILProcessor();
                            // return true;
                            garageIl.Emit(OpCodes.Ldc_I4_1);
                            garageIl.Emit(OpCodes.Ret);
                        }
                        else
                        {
                            // A silently-skipped unlock would make deploys lie
                            // to the user; fail loudly instead.
                            throw new InvalidOperationException("Patch 3: VSChangeSetup.IsGarageButtonActive was not found.");
                        }

                        TypeDefinition? setupMgrType = module.Types.FirstOrDefault(t => t.Name == "GarageSetupManager");
                        MethodDefinition? customAllowedMethod = setupMgrType?.Methods
                            .FirstOrDefault(m => m.Name == "get_custom_setups_are_allowed" && m.HasBody);

                        if (customAllowedMethod != null)
                        {
                            customAllowedMethod.Body.ExceptionHandlers.Clear();
                            customAllowedMethod.Body.Instructions.Clear();
                            customAllowedMethod.Body.Variables.Clear();
                            customAllowedMethod.Body.InitLocals = false;
                            ILProcessor setupsIl = customAllowedMethod.Body.GetILProcessor();
                            // return true;
                            setupsIl.Emit(OpCodes.Ldc_I4_1);
                            setupsIl.Emit(OpCodes.Ret);
                        }
                        else
                        {
                            throw new InvalidOperationException("Patch 3: GarageSetupManager.get_custom_setups_are_allowed was not found.");
                        }

                        // --- Patch 3E: chooser un-gate ---
                        // Applied AFTER the Patch 6 restore block below: that
                        // block unconditionally restores the chooser method
                        // bodies (overwriting anything written into them
                        // here), so the year-gate removal must be re-added
                        // last. See the RemoveFutureYearChooserGates call at
                        // the end of ApplyAssemblyPatches.
                    }

                    // --- Patch 5: Cup 5-speed gearbox ---
                    // The Cup chassis builds every Cup car's transmission with
                    // num_gears = 4 and the garage keeps the 4th-gear row
                    // locked to a fixed 1:1 ratio. Broaden the Cup gearbox to
                    // 5 and make the 4th row adjustable. The 5th gear uses
                    // the per-track default ratio already carried by the
                    // track data (gearbox_4). There is no runtime flag that
                    // separates the 2020/2022 content sides (both Cup sides
                    // share this one chassis class), so this applies to Cup
                    // racing on both sides; Xfinity, Truck and dirt chassis
                    // remain stock.
                    if (addFifthGear)
                    {
                        TypeDefinition? cupChassisType = module.Types.FirstOrDefault(t => t.Name == "SprintCupChassis");
                        MethodDefinition? makeTransmission = cupChassisType?.Methods
                            .FirstOrDefault(m => m.HasBody && m.Name.EndsWith(".MakeTransmission"));

                        if (makeTransmission == null)
                        {
                            throw new InvalidOperationException("Patch 5: SprintCupChassis.MakeTransmission was not found.");
                        }

                        var makeTransInstrs = makeTransmission.Body.Instructions.ToList();
                        int numGearsIdx = makeTransInstrs.FindIndex(ins =>
                            ins.OpCode == OpCodes.Stfld && (ins.Operand as FieldReference)?.Name == "num_gears");

                        if (numGearsIdx > 0 && makeTransInstrs[numGearsIdx - 1].OpCode == OpCodes.Ldc_I4_4)
                        {
                            ILProcessor transIl = makeTransmission.Body.GetILProcessor();
                            transIl.Replace(makeTransInstrs[numGearsIdx - 1], transIl.Create(OpCodes.Ldc_I4_5));
                        }
                        else if (!(numGearsIdx > 0 && makeTransInstrs[numGearsIdx - 1].OpCode == OpCodes.Ldc_I4_5))
                        {
                            throw new InvalidOperationException("Patch 5: Cup gear-count pattern not found in MakeTransmission.");
                        }

                        TypeDefinition? garageListType = module.Types.FirstOrDefault(t => t.Name == "GarageOptionListController");
                        MethodDefinition? gearsDefaults = garageListType?.Methods
                            .FirstOrDefault(m => m.HasBody && m.Name == "apply_gears_options_defaults");

                        if (gearsDefaults == null)
                        {
                            throw new InvalidOperationException("Patch 5: GarageOptionListController.apply_gears_options_defaults was not found.");
                        }

                        var gearRows = gearsDefaults.Body.Instructions.ToList();
                        int gearbox3Idx = gearRows.FindIndex(ins =>
                            ins.OpCode == OpCodes.Ldfld && (ins.Operand as FieldReference)?.Name == "gearbox_3");

                        if (gearbox3Idx < 0)
                        {
                            throw new InvalidOperationException("Patch 5: 4th-gear row lookup (gearbox_3) not found.");
                        }

                        // The non-selectable flag is the ldc.i4.0 immediately
                        // preceding the SetValue call for the 4th-gear row.
                        // Abort the scan at SetValue (unexpected layout) or at
                        // ldc.i4.1 (already unlocked by a previous deploy).
                        Instruction? fourthGearFlag = null;
                        for (int j = gearbox3Idx + 1; j < gearRows.Count; j++)
                        {
                            Instruction ins = gearRows[j];
                            if (ins.OpCode == OpCodes.Ldc_I4_0)
                            {
                                fourthGearFlag = ins;
                                break;
                            }
                            if (ins.OpCode == OpCodes.Ldc_I4_1 ||
                                (ins.OpCode == OpCodes.Callvirt && (ins.Operand as MethodReference)?.Name == "SetValue"))
                            {
                                break;
                            }
                        }

                        if (fourthGearFlag != null)
                        {
                            ILProcessor gearIl = gearsDefaults.Body.GetILProcessor();
                            gearIl.Replace(fourthGearFlag, gearIl.Create(OpCodes.Ldc_I4_1));
                        }
                    }

                    // --- Patch 6: restore disabled features from the vanilla backup ---
                    // Unticking "Unlock Custom Setups" or "Add 5th Gear (Cup)"
                    // and deploying rolls the corresponding game methods back
                    // to their original bodies (from the vanilla Assembly-CSharp
                    // backup), so all recent game mods are individually reversible.
                    if (File.Exists(backupPath))
                    {
                        using AssemblyDefinition backup = AssemblyDefinition.ReadAssembly(backupPath, readerParameters);
                        ModuleDefinition backupModule = backup.MainModule;

                        if (!unlockSetups)
                        {
                            RestoreVanillaMethod(backupModule, module, "VSChangeSetup", m => m.Name == "IsGarageButtonActive");
                            RestoreVanillaMethod(backupModule, module, "GarageSetupManager", m => m.Name == "get_custom_setups_are_allowed");
                        }

                        // Always restore the pre-garage chooser and its helper
                        // methods: the retired chooser bypass used to rewrite
                        // MakePreGarageSelectPresetOrCustom and its finders in
                        // older manager builds, and Patch 3E rewrites
                        // VSGarageSetupChooser's bodies on every deploy.
                        // Restoring from vanilla first makes repeated deploys
                        // idempotent.
                        RestoreVanillaMethod(backupModule, module, "GarageHelper", m => m.Name == "MakePreGarageSelectPresetOrCustom");
                        RestoreVanillaMethod(backupModule, module, "GarageHelper", m => m.Name == "find_pregaragecustomorrecommended");
                        RestoreVanillaMethod(backupModule, module, "GarageHelper", m => m.Name == "find_pregarageselector");
                        RestoreVanillaMethod(backupModule, module, "GarageHelper", m => m.Name == "make_garage");
                        RestoreVanillaMethod(backupModule, module, "GarageHelper", m => m.Name == "make_garage_default_selector");
                        RestoreVanillaMethod(backupModule, module, "VSGarageSetupChooser", m => m.Name == "InitText");
                        // ButtonPress/LeavingScreen must also be restored: the
                        // first Patch 3E build (deletion-based) removed the
                        // future-year gates from the live DLL, and without
                        // these restores a later deploy would find 0 gates
                        // and fail (or skip the patch silently).
                        RestoreVanillaMethod(backupModule, module, "VSGarageSetupChooser", m => m.Name == "ButtonPress");
                        RestoreVanillaMethod(backupModule, module, "VSGarageSetupChooser", m => m.Name == "LeavingScreen");
                        RestoreVanillaMethod(backupModule, module, "VSGarageSetupCustomOrRecommendedChooser", m => m.Name == "InitText");
                        RestoreVanillaMethod(backupModule, module, "VSGarageSetupCustomOrRecommendedBars", m => m.Name == "GarageCustomOrRecommendedEditting");
                        RestoreVanillaMethod(backupModule, module, "VSGarageSetupDisplayBars", m => m.Name == "OnEnable");

                        if (!addFifthGear)
                        {
                            RestoreVanillaMethod(backupModule, module, "SprintCupChassis", m => m.Name.EndsWith(".MakeTransmission"));
                            RestoreVanillaMethod(backupModule, module, "GarageOptionListController", m => m.Name == "apply_gears_options_defaults");
                        }
                    }

                    // --- Patch 3E (re-apply): future-year chooser un-gate ---
                    // Must run AFTER the unconditional chooser restores
                    // above; those wipes would otherwise overwrite the
                    // de-gated bodies before the module is written.
                    if (unlockSetups)
                    {
                        // --- Patch 3E: future-year chooser un-gate ---
                        // The live game ships a per-year simplification inside
                        // VSGarageSetupChooser: InitText/LeavingScreen skip the
                        // Custom/Recommended bars (the 3-row chooser) and
                        // ButtonPress blocks UP/DOWN row navigation when the
                        // online year is FUTURE (2022) — the reason the 2020
                        // side gets the chooser and the 2022 side doesn't.
                        // Remove those gates so both sides behave the same.
                        RemoveFutureYearChooserGates(module);
                    }

                    // Verify requested patches actually landed on the module
                    // before writing it. A requested-but-missing patch means
                    // the game will run unmodified while the manager claims
                    // success — fail loudly so that can never happen again.
                    bool unlockApplied = false;
                    TypeDefinition? verifySetupType = module.Types.FirstOrDefault(t => t.Name == "VSChangeSetup");
                    MethodDefinition? verifyGate = verifySetupType?.Methods
                        .FirstOrDefault(m => m.HasBody && m.Name == "IsGarageButtonActive");
                    if (verifyGate != null && verifyGate.Body.Instructions.Count == 2 &&
                        verifyGate.Body.Instructions[0].OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_1 &&
                        verifyGate.Body.Instructions[1].OpCode == OpCodes.Ret)
                    {
                        unlockApplied = true;
                    }
                    if (unlockSetups && !unlockApplied)
                    {
                        throw new InvalidOperationException("Patch 3 verification failed: IsGarageButtonActive was not rewritten.");
                    }
                    bool yearGateRemoved = false;
                    TypeDefinition? verifyChooserType = module.Types.FirstOrDefault(t => t.Name == "VSGarageSetupChooser");
                    if (verifyChooserType != null)
                    {
                        yearGateRemoved = !verifyChooserType.Methods
                            .Where(m => m.HasBody &&
                                        (m.Name == "InitText" || m.Name == "ButtonPress" || m.Name == "LeavingScreen"))
                            .SelectMany(m => m.Body.Instructions)
                            .Any(ins => (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                                        ins.Operand is MethodReference mr && mr.Name == "IsFutureYearActive");
                    }
                    if (unlockSetups && !yearGateRemoved)
                    {
                        throw new InvalidOperationException("Patch 3E verification failed: future-year gates remain in VSGarageSetupChooser.");
                    }

                    asm.Write(tempOutputPath);

                    // Record exactly what this deploy applied so post-deploy
                    // analysis never has to guess about checkbox state again.
                    WriteDeployStateLog(tempOutputPath, enableCustomServer, unlockSetups, addFifthGear, unlockApplied, yearGateRemoved);
                }

                if (File.Exists(tempOutputPath))
                {
                    File.Copy(tempOutputPath, dllPath, overwrite: true);
                    File.Delete(tempOutputPath);
                }
            }
            catch (Exception ex)
            {
                if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);
                throw new IOException("Failed to patch Assembly-CSharp.dll: " + ex.Message, ex);
            }
        }

        // Appends one line to deploy_log.txt next to the manager exe,
        // recording which patches were requested this deploy and whether
        // they were verified in the written assembly, plus the patched
        // DLL hash. Keeps post-deploy analysis from having to guess
        // about UI checkbox state ever again.
        private static void WriteDeployStateLog(string patchedDllPath, bool enableCustomServer,
            bool unlockSetups, bool addFifthGear, bool unlockApplied, bool yearGateRemoved)
        {
            try
            {
                string md5 = "";
                try
                {
                    using System.Security.Cryptography.MD5 alg = System.Security.Cryptography.MD5.Create();
                    byte[] hash = alg.ComputeHash(File.ReadAllBytes(patchedDllPath));
                    md5 = BitConverter.ToString(hash).Replace("-", "");
                }
                catch { }
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | CustomServer={enableCustomServer} UnlockSetups={unlockSetups} FifthGear={addFifthGear} | verified: gate_patch={(unlockApplied ? "YES" : "no")} yearGate_removed={(yearGateRemoved ? "YES" : "no")} | dll md5={md5}";
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deploy_log.txt"),
                    line + Environment.NewLine);
            }
            catch { }
        }

        // Copies a method body (IL, locals, exception handlers) from the
        // vanilla backup assembly onto the live module, undoing any manager
        // patch applied to it. All metadata operands are imported into the
        // live module so the written assembly stays self-contained.
        private static void RestoreVanillaMethod(
            ModuleDefinition backupModule,
            ModuleDefinition liveModule,
            string typeName,
            Func<MethodDefinition, bool> methodMatch)
        {
            TypeDefinition? backupType = backupModule.Types.FirstOrDefault(t => t.Name == typeName);
            MethodDefinition? backupMethod = backupType?.Methods
                .FirstOrDefault(m => m.HasBody && methodMatch(m));
            TypeDefinition? liveType = liveModule.Types.FirstOrDefault(t => t.Name == typeName);
            MethodDefinition? liveMethod = liveType?.Methods
                .FirstOrDefault(m => m.HasBody && methodMatch(m));

            if (backupMethod == null || liveMethod == null)
            {
                throw new InvalidOperationException(
                    $"Restore failed: '{typeName}' method not found (backup={(backupMethod != null)}, live={(liveMethod != null)}).");
            }

            var src = backupMethod.Body;
            var dst = liveMethod.Body;
            ILProcessor il = dst.GetILProcessor();

            dst.ExceptionHandlers.Clear();
            dst.Instructions.Clear();
            dst.Variables.Clear();
            dst.InitLocals = src.InitLocals;
            dst.MaxStackSize = src.MaxStackSize;

            var variables = new VariableDefinition[src.Variables.Count];
            for (int i = 0; i < variables.Length; i++)
            {
                variables[i] = new VariableDefinition(liveModule.ImportReference(src.Variables[i].VariableType));
                dst.Variables.Add(variables[i]);
            }

            var srcInstrs = src.Instructions.ToList();
            var newInstrs = new Instruction[srcInstrs.Count];
            for (int i = 0; i < srcInstrs.Count; i++)
            {
                object o = srcInstrs[i].Operand;
                switch (o)
                {
                    case null:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode);
                        break;
                    case Instruction target:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, target);
                        break;
                    case Instruction[] table:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, table);
                        break;
                    case TypeReference typeRef:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, liveModule.ImportReference(typeRef));
                        break;
                    case MethodReference methodRef:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, liveModule.ImportReference(methodRef));
                        break;
                    case FieldReference fieldRef:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, liveModule.ImportReference(fieldRef));
                        break;
                    case VariableDefinition variable:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, variables[variable.Index]);
                        break;
                    case ParameterDefinition param:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, liveMethod.Parameters[param.Index]);
                        break;
                    case sbyte asSbyte:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asSbyte);
                        break;
                    case byte asByte:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asByte);
                        break;
                    case int asInt:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asInt);
                        break;
                    case long asLong:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asLong);
                        break;
                    case float asFloat:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asFloat);
                        break;
                    case double asDouble:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asDouble);
                        break;
                    case string asString:
                        newInstrs[i] = il.Create(srcInstrs[i].OpCode, asString);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Restore: unsupported operand {o?.GetType().Name ?? "null"} in {typeName}.");
                }
            }

            foreach (Instruction created in newInstrs)
            {
                dst.Instructions.Add(created);
            }

            // Re-point branch targets and switch tables at the new instructions.
            var indexMap = new Dictionary<Instruction, int>(srcInstrs.Count);
            for (int i = 0; i < srcInstrs.Count; i++)
            {
                indexMap[srcInstrs[i]] = i;
            }
            for (int i = 0; i < newInstrs.Length; i++)
            {
                object o = srcInstrs[i].Operand;
                if (o is Instruction target)
                {
                    newInstrs[i].Operand = newInstrs[indexMap[target]];
                }
                else if (o is Instruction[] table)
                {
                    var mapped = new Instruction[table.Length];
                    for (int k = 0; k < table.Length; k++)
                    {
                        mapped[k] = newInstrs[indexMap[table[k]]];
                    }
                    newInstrs[i].Operand = mapped;
                }
            }

            // Copy exception handlers (none exist in the methods we patch,
            // but keep the generic path for safety).
            foreach (ExceptionHandler eh in src.ExceptionHandlers)
            {
                var copy = new ExceptionHandler(eh.HandlerType)
                {
                    CatchType = eh.CatchType != null ? liveModule.ImportReference(eh.CatchType) : null,
                    TryStart = newInstrs[indexMap[eh.TryStart]],
                    TryEnd = newInstrs[indexMap[eh.TryEnd]],
                    HandlerStart = newInstrs[indexMap[eh.HandlerStart]],
                    HandlerEnd = newInstrs[indexMap[eh.HandlerEnd]]
                };
                if (eh.FilterStart != null)
                {
                    copy.FilterStart = newInstrs[indexMap[eh.FilterStart]];
                }
                dst.ExceptionHandlers.Add(copy);
            }
        }

        // ---------- end chooser patch helpers ----------

        // Patch 3E: removes the vanilla FUTURE-year simplification gates
        // inside VSGarageSetupChooser. The live game already branches per
        // year: InitText/LeavingScreen skip the CustomOrRecommended bars
        // (the 3-row chooser slide on/off) and ButtonPress blocks UP/DOWN
        // row navigation when the active online year is FUTURE (2022).
        // Removing those gates makes the 2022 side behave like the 2020
        // side. Reversibility: gated behind "Unlock Custom Setups", and
        // the un-gated methods are always restored from the vanilla
        // backup before every deploy (same hygiene as the chooser restores).
        // Pattern per gate: call get_Instance; callvirt IsFutureYearActive;
        // brtrue/brtrue.s <skip> — the three instructions disappear and
        // the previously-skipped block becomes the fall-through.
        private static void RemoveFutureYearChooserGates(ModuleDefinition module)
        {
            TypeDefinition? chooserType = module.Types.FirstOrDefault(t => t.Name == "VSGarageSetupChooser");
            if (chooserType == null)
            {
                throw new InvalidOperationException("Patch 3E: VSGarageSetupChooser was not found.");
            }

            int gatesRemoved = 0;
            foreach (string methodName in new[] { "InitText", "ButtonPress", "LeavingScreen" })
            {
                MethodDefinition? method = chooserType.Methods
                    .FirstOrDefault(m => m.HasBody && m.Name == methodName);
                if (method == null)
                {
                    throw new InvalidOperationException("Patch 3E: " + methodName + " was not found.");
                }

                var instructions = method.Body.Instructions.ToList();
                int gateCount = 0;
                for (int i = 1; i < instructions.Count - 1; i++)
                {
                    bool gateShape =
                        (instructions[i].OpCode == OpCodes.Callvirt &&
                         instructions[i].Operand is MethodReference future &&
                         future.Name == "IsFutureYearActive" &&
                         (instructions[i + 1].OpCode == OpCodes.Brtrue ||
                          instructions[i + 1].OpCode == OpCodes.Brtrue_S) &&
                         instructions[i - 1].OpCode == OpCodes.Call &&
                         instructions[i - 1].Operand is MethodReference lazy &&
                         lazy.Name == "get_Instance");
                    if (gateShape)
                    {
                        // In-place NOP conversion instead of removal: the
                        // gate instructions can be the targets of other
                        // branches (ButtonPress's message switch jumps
                        // directly at the get_Instance call), and deleting
                        // them left dangling jump-table references that
                        // crashed Mono. NOPs keep object identity, offsets
                        // and stack balance intact.
                        instructions[i - 1].OpCode = OpCodes.Nop;
                        instructions[i - 1].Operand = null;
                        instructions[i].OpCode = OpCodes.Nop;
                        instructions[i].Operand = null;
                        instructions[i + 1].OpCode = OpCodes.Nop;
                        instructions[i + 1].Operand = null;
                        gateCount++;
                        i++; // skip the branch we just consumed
                    }
                }

                int expectedGates = methodName == "ButtonPress" ? 2 : 1;
                if (gateCount != expectedGates)
                {
                    throw new InvalidOperationException(
                        $"Patch 3E: {methodName} had {gateCount} future-year gates, expected {expectedGates}.");
                }

                bool stillGated = method.Body.Instructions.Any(ins =>
                    (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                    ins.Operand is MethodReference mr && mr.Name == "IsFutureYearActive");
                if (stillGated)
                {
                    throw new InvalidOperationException(
                        "Patch 3E: " + methodName + " still calls IsFutureYearActive after removal.");
                }

                // Only counted once the method passed its per-method checks.
                gatesRemoved++;
            }

            if (gatesRemoved != 3)
            {
                throw new InvalidOperationException(
                    "Patch 3E: expected 3 de-gated methods, got " + gatesRemoved + ".");
            }
        }

        // Helper method to locate existing MethodReferences anywhere in the assembly
        private MethodReference? FindMethodRef(ModuleDefinition module, Func<MethodReference, bool> match)
        {
            foreach (TypeDefinition type in module.Types)
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (method.HasBody)
                    {
                        foreach (Instruction inst in method.Body.Instructions)
                        {
                            if (inst.Operand is MethodReference mr && match(mr))
                            {
                                return mr;
                            }
                        }
                    }
                }
            }
            return null;
        }

        private void LaunchNASCARHeat5()
        {
            try
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/1265860") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch game: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnSaveProfile_Click(object? sender, EventArgs e)
        {
            string newProfile = Microsoft.VisualBasic.Interaction.InputBox("Enter a name for the new Mod Profile:", "Create Profile", "New Profile");
            string cleanProfileName = string.Concat(newProfile.Split(Path.GetInvalidFileNameChars())).Trim();

            if (string.IsNullOrWhiteSpace(cleanProfileName)) return;

            string targetDir = Path.Combine(ConfigsDirectory, cleanProfileName);
            string targetDataDir = Path.Combine(targetDir, "NASCARHeat5_Data");

            if (!Directory.Exists(targetDataDir)) Directory.CreateDirectory(targetDataDir);

            LoadProfiles();
            cmbProfiles.SelectedItem = cleanProfileName;
            lblStatus.Text = $"Profile '{cleanProfileName}' created successfully.";
        }

        private void Form1_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void Form1_DragOver(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        // Where a dropped file lands: files registered in the vanilla map are
        // copied to the profile root (NormalizeModDirectory then files them
        // into NASCARHeat5_Data). DLC-flavored files — mod scenarios, challenge
        // .rab bundles and Unlock_*.txt stubs that the game reads from the
        // "<game>\DLC" folder — are routed into the profile's DLC folder so
        // they appear as [DLC] entries in the mod list.
        private string GetDropDestinationPath(string sourceFile, string profileFolder)
        {
            string fileName = Path.GetFileName(sourceFile);
            if (_vanillaFileMap.ContainsKey(fileName)) return Path.Combine(profileFolder, fileName);

            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            bool dlcFlavored = ext == ".rab"
                || ext == ".scenario"
                || fileName.StartsWith("Unlock_", StringComparison.OrdinalIgnoreCase);
            return dlcFlavored
                ? Path.Combine(profileFolder, "DLC", fileName)
                : Path.Combine(profileFolder, fileName);
        }

        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            string[]? paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;

            string currentProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            string profileFolder = Path.Combine(ConfigsDirectory, currentProfile);

            if (!Directory.Exists(profileFolder)) Directory.CreateDirectory(profileFolder);

            this.UseWaitCursor = true;
            this.Enabled = false;

            await Task.Run(() =>
            {
                foreach (string path in paths)
                {
                    if (File.Exists(path))
                    {
                        string ext = Path.GetExtension(path).ToLowerInvariant();

                        if (ext == ".zip")
                        {
                            string tempExtractDir = Path.Combine(Path.GetTempPath(), $"nh5_zip_{Guid.NewGuid()}");
                            try
                            {
                                ZipFile.ExtractToDirectory(path, tempExtractDir);

                                foreach (string file in Directory.GetFiles(tempExtractDir, "*.*", SearchOption.AllDirectories))
                                {
                                    string destPath = GetDropDestinationPath(file, profileFolder);
                                    string? destDir = Path.GetDirectoryName(destPath);
                                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                                    File.Copy(file, destPath, overwrite: true);
                                }
                            }
                            catch (Exception ex)
                            {
                                this.Invoke(() => MessageBox.Show($"Failed to extract ZIP '{Path.GetFileName(path)}':\n{ex.Message}", "ZIP Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
                            }
                            finally
                            {
                                if (Directory.Exists(tempExtractDir))
                                    Directory.Delete(tempExtractDir, true);
                            }
                        }
                        else
                        {
                            string destPath = GetDropDestinationPath(path, profileFolder);
                            string? destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                            File.Copy(path, destPath, overwrite: true);
                        }
                    }
                    else if (Directory.Exists(path))
                    {
                        string[] filesInsideFolder = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);

                        foreach (string file in filesInsideFolder)
                        {
                            string destPath = GetDropDestinationPath(file, profileFolder);
                            string? destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                            File.Copy(file, destPath, overwrite: true);
                        }
                    }
                }

                NormalizeModDirectory(profileFolder);
            });

            this.Enabled = true;
            this.UseWaitCursor = false;

            this.Invoke(new Action(() =>
            {
                LoadInstalledMods();
                lblStatus.Text = $"Import complete for profile: '{currentProfile}'";
            }));
        }

        private static bool IsInsideProfileSaveFolder(string relativePath)
        {
            string saveName = SaveProfileManager.ProfileSaveFolderName;
            if (relativePath.Equals(saveName, StringComparison.OrdinalIgnoreCase)) return true;
            return relativePath.StartsWith(saveName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(saveName + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyDirectory(string sourceDir, string destinationDir, bool excludeProfileSave = false)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDir, file);
                if (excludeProfileSave && IsInsideProfileSaveFolder(relativePath)) continue;

                string destFile = Path.Combine(destinationDir, relativePath);
                string? destFolder = Path.GetDirectoryName(destFile);

                if (!string.IsNullOrEmpty(destFolder)) Directory.CreateDirectory(destFolder);
                File.Copy(file, destFile, overwrite: true);
            }
        }

        private void btnExportProfile_Click(object? sender, EventArgs e)
        {
            ExportSelectedProfile();
        }

        private void btnImportProfile_Click(object? sender, EventArgs e)
        {
            ImportProfilePackage();
        }

        private void DeployDlcFixFiles()
        {
            string sourceFixDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DLC_Fix");
            if (!Directory.Exists(sourceFixDir)) return;

            string targetDlcDir = Path.Combine(GameDirectory, "DLC");
            if (!Directory.Exists(targetDlcDir)) Directory.CreateDirectory(targetDlcDir);

            foreach (string sourceFile in Directory.GetFiles(sourceFixDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(sourceFile);
                string destFile = Path.Combine(targetDlcDir, fileName);
                string relativeToGame = Path.Combine("DLC", fileName);
                string backupFile = Path.Combine(VanillaBackupDirectory, relativeToGame);

                // Backup existing vanilla/current file if not already backed up
                if (File.Exists(destFile) && !File.Exists(backupFile))
                {
                    string? backupDir = Path.GetDirectoryName(backupFile);
                    if (!string.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
                    File.Copy(destFile, backupFile, overwrite: true);
                }

                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }
    }
}
