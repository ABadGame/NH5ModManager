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

using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using DefaultAssemblyResolver = Mono.Cecil.DefaultAssemblyResolver;
using ReaderParameters = Mono.Cecil.ReaderParameters;
using ILProcessor = Mono.Cecil.Cil.ILProcessor;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace NH5ModManager
{
    public partial class Form1 : Form
    {
        private const string CustomServerUrl = "http://72.39.41.141:8000/";

        public string GameDirectory { get; private set; } = @"C:\Program Files (x86)\Steam\steamapps\common\NASCAR Heat 5";
        private string ConfigsDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
        private string VanillaBackupDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NH5ModManager_Data", "Vanilla_Backup");

        private bool _isLoadingProfile = false;
        private string _activeProfileName = "Default";

        private readonly Dictionary<string, string> _vanillaFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ContextMenuStrip _profileContextMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _modListContextMenu = new ContextMenuStrip();
        private readonly string _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vanilla_map.json");
        private readonly string _deployedManifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeployedManifest.json");
        private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.json");

        public Form1()
        {
            InitializeComponent();

            this.lstMods.UseCompatibleStateImageBehavior = false;
            this.AllowDrop = true;
            this.DragEnter += Form1_DragEnter;
            this.DragDrop += Form1_DragDrop;
            this.lstMods.ItemCheck += lstMods_ItemCheck;
            this.cmbProfiles.SelectedIndexChanged += cmbProfiles_SelectedIndexChanged;
            this.chkUnlockDLC.CheckedChanged += chkSettings_CheckedChanged;
            this.chkCustomServer.CheckedChanged += chkSettings_CheckedChanged;

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
            public bool UnlockDLC { get; set; } = false;
            public bool EnableCustomServer { get; set; } = false;
        }

        private void LoadSettings()
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        chkUnlockDLC.Checked = settings.UnlockDLC;
                        chkCustomServer.Checked = settings.EnableCustomServer;
                    }
                }
                catch { }
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new AppSettings
                {
                    UnlockDLC = chkUnlockDLC.Checked,
                    EnableCustomServer = chkCustomServer.Checked
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
                if (Directory.Exists(sourceDir)) Directory.Move(sourceDir, targetDir);
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
                if (Directory.Exists(sourceDir)) CopyDirectory(sourceDir, targetDir);
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
            if (!Directory.Exists(sourceDir))
            {
                MessageBox.Show($"Profile folder '{selectedProfile}' was not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SaveActiveModsForProfile(selectedProfile);

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
                    CopyDirectory(sourceDir, targetProfileDir);

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

                    CopyDirectory(sourceProfileFolder, targetDir);

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
                foreach (string dir in Directory.GetDirectories(ConfigsDirectory))
                {
                    string folderName = Path.GetFileName(dir);
                    if (!folderName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        cmbProfiles.Items.Add(folderName);
                    }
                }
            }

            cmbProfiles.SelectedIndex = 0;
            _isLoadingProfile = false;
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

            bool deploymentSuccess = false;

            await Task.Run(() =>
            {
                this.Invoke(new Action(() => lblStatus.Text = $"Deploying mods for '{selectedProfile}'..."));
                deploymentSuccess = SwapDataFolderForProfile(selectedProfile, checkedAssetPaths);

                if (deploymentSuccess)
                {
                    if (unlockDlcRequested || customServerRequested)
                    {
                        this.Invoke(new Action(() => lblStatus.Text = "Mods verified. Applying Assembly Patches (DLC/Server)..."));
                        ApplyAssemblyPatches(unlockDlcRequested, customServerRequested);
                    }
                }
            });

            this.Enabled = true;
            this.UseWaitCursor = false;

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

        private void ApplyAssemblyPatches(bool enableDlc, bool enableCustomServer)
        {
            string gameDir = GameDirectory.Trim();
            string managedDir = Path.Combine(gameDir, "NASCARHeat5_Data", "Managed");
            string dllPath = Path.Combine(managedDir, "Assembly-CSharp.dll");
            string backupPath = dllPath + ".bak";
            string tempOutputPath = Path.Combine(managedDir, "Assembly-CSharp.dll.tmp");

            if (!File.Exists(dllPath)) return;

            try
            {
                if (!File.Exists(backupPath)) File.Copy(dllPath, backupPath, overwrite: true);

                byte[] assemblyBytes = File.ReadAllBytes(dllPath);

                using (var resolver = new DefaultAssemblyResolver())
                {
                    resolver.AddSearchDirectory(managedDir);
                    var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

                    using (MemoryStream ms = new MemoryStream(assemblyBytes))
                    using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(ms, readerParameters))
                    {
                        ModuleDefinition module = assembly.MainModule;

                        if (enableDlc)
                        {
                            TypeDefinition? dlcType = module.Types.FirstOrDefault(t => t.Namespace == "MGI.Platform.Steam" && t.Name == "SteamPlatformDLCLoader");
                            MethodDefinition? seasonPassMethod = dlcType?.Methods.FirstOrDefault(m => m.Name == "do_they_own_the_season_pass");

                            if (seasonPassMethod != null)
                            {
                                ILProcessor il = seasonPassMethod.Body.GetILProcessor();
                                seasonPassMethod.Body.Instructions.Clear();
                                il.Append(il.Create(OpCodes.Ldc_I4_1));
                                il.Append(il.Create(OpCodes.Ret));
                            }
                        }

                        if (enableCustomServer)
                        {
                            TypeDefinition? ngUtilType = module.Types.FirstOrDefault(t => t.Namespace == "MGI.NG" && t.Name == "NGUtil");
                            MethodDefinition? getBaseUrlMethod = ngUtilType?.Methods.FirstOrDefault(m => m.Name == "GetBaseURL");

                            if (getBaseUrlMethod != null)
                            {
                                ILProcessor il = getBaseUrlMethod.Body.GetILProcessor();
                                getBaseUrlMethod.Body.Instructions.Clear();
                                il.Append(il.Create(OpCodes.Ldstr, CustomServerUrl));
                                il.Append(il.Create(OpCodes.Ret));
                            }
                        }

                        assembly.Write(tempOutputPath);
                    }
                }

                File.Move(tempOutputPath, dllPath, overwrite: true);
            }
            catch (Exception ex)
            {
                if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);
                this.Invoke(() => MessageBox.Show($"An error occurred while patching Assembly-CSharp.dll:\n{ex.Message}", "Patch Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
            }
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

        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            string[]? paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return;

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
                            try { ZipFile.ExtractToDirectory(path, profileFolder, overwriteFiles: true); }
                            catch { }
                        }
                        else
                        {
                            string fileName = Path.GetFileName(path);
                            string destPath = Path.Combine(profileFolder, fileName);
                            File.Copy(path, destPath, overwrite: true);
                        }
                    }
                    else if (Directory.Exists(path))
                    {
                        string destFolder = Path.Combine(profileFolder, Path.GetFileName(path));
                        CopyDirectory(path, destFolder);
                    }
                }

                NormalizeModDirectory(profileFolder);
            });

            this.Enabled = true;
            this.UseWaitCursor = false;

            LoadInstalledMods();
            lblStatus.Text = $"Import complete for profile: '{currentProfile}'";
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDir, file);
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
    }
}