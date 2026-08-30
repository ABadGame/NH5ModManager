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
using System.Net.Http;

// Disambiguate Mono.Cecil types from System.Reflection
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
        public string GameDirectory { get; private set; } = @"C:\Program Files (x86)\Steam\steamapps\common\NASCAR Heat 5";
        private string StagingDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods");
        private string ConfigsDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
        private string VanillaBackupDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NH5ModManager_Data", "Vanilla_Backup");

        private FileSystemWatcher? _stagingWatcher;
        private bool _isLoadingProfile = false;
        private string _activeProfileName = "Default";

        private readonly Dictionary<string, string> _vanillaFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ContextMenuStrip _profileContextMenu = new ContextMenuStrip();
        private readonly string _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vanilla_map.json");
        private readonly string _deployedManifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeployedManifest.json");

        public Form1()
        {
            InitializeComponent();

            this.lstMods.UseCompatibleStateImageBehavior = false;
            this.AllowDrop = true;
            this.DragEnter += Form1_DragEnter;
            this.DragDrop += Form1_DragDrop;
            this.lstMods.ItemCheck += lstMods_ItemCheck;
            this.cmbProfiles.SelectedIndexChanged += cmbProfiles_SelectedIndexChanged;

            InitializeProfileContextMenu();
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            txtGamePath.Text = GameDirectory;

            if (!Directory.Exists(StagingDirectory)) Directory.CreateDirectory(StagingDirectory);
            if (!Directory.Exists(ConfigsDirectory)) Directory.CreateDirectory(ConfigsDirectory);
            if (!Directory.Exists(VanillaBackupDirectory)) Directory.CreateDirectory(VanillaBackupDirectory);

            BuildVanillaFileMap();
            SetupStagingWatcher();
            LoadProfiles();
            LoadInstalledMods();
            await EnsureBepInExInstalledAsync();
        }

        private string GetGameSaveDataPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "NASCAR Heat 5", "SaveData");
        }

        private async Task EnsureBepInExInstalledAsync()
        {
            string gameDir = txtGamePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;

            string bepinexDll = Path.Combine(gameDir, "winhttp.dll");
            string bepinexFolder = Path.Combine(gameDir, "BepInEx");

            if (File.Exists(bepinexDll) && Directory.Exists(bepinexFolder)) return;

            DialogResult result = MessageBox.Show(
                "BepInEx core files were not found in your NASCAR Heat 5 directory.\n\n" +
                "Would you like to automatically download and install BepInEx v5.4.22 (x64)?",
                "BepInEx Not Detected",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result != DialogResult.Yes) return;

            string downloadUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_x64_5.4.22.0.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), "BepInEx_x64_temp.zip");

            try
            {
                lblStatus.Text = "Downloading BepInEx...";
                btnDeploy.Enabled = false;

                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("NH5ModManager");
                    byte[] fileBytes = await client.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(tempZipPath, fileBytes);
                }

                lblStatus.Text = "Extracting BepInEx to game folder...";
                ZipFile.ExtractToDirectory(tempZipPath, gameDir, overwriteFiles: true);

                MessageBox.Show("BepInEx has been successfully installed to your game directory!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to download or extract BepInEx automatically:\n{ex.Message}", "Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                lblStatus.Text = "Ready";
                btnDeploy.Enabled = true;
            }
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

        private void InitializeProfileContextMenu()
        {
            _profileContextMenu.Items.Clear();

            ToolStripMenuItem enableAllItem = new ToolStripMenuItem("Check All Mods");
            enableAllItem.Click += (s, e) => SetAllModItemsState(true);

            ToolStripMenuItem disableAllItem = new ToolStripMenuItem("Uncheck All Mods");
            disableAllItem.Click += (s, e) => SetAllModItemsState(false);

            _profileContextMenu.Items.Add(enableAllItem);
            _profileContextMenu.Items.Add(disableAllItem);
            _profileContextMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openFolderItem = new ToolStripMenuItem("Open Profile in File Explorer");
            openFolderItem.Click += (s, e) => OpenSelectedProfileFolder();

            ToolStripMenuItem deleteProfileItem = new ToolStripMenuItem("Delete Selected Profile");
            deleteProfileItem.Click += (s, e) => DeleteSelectedProfile();

            _profileContextMenu.Items.Add(openFolderItem);
            _profileContextMenu.Items.Add(deleteProfileItem);

            this.cmbProfiles.ContextMenuStrip = _profileContextMenu;
            this.lstMods.ContextMenuStrip = _profileContextMenu;
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

        private void SetupStagingWatcher()
        {
            _stagingWatcher = new FileSystemWatcher(StagingDirectory)
            {
                Filter = "*.*",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName
            };

            _stagingWatcher.Created += OnStagingFolderChanged;
            _stagingWatcher.Deleted += OnStagingFolderChanged;
            _stagingWatcher.Renamed += OnStagingFolderChanged;
            _stagingWatcher.EnableRaisingEvents = true;
        }

        private void OnStagingFolderChanged(object sender, FileSystemEventArgs e)
        {
            if (this.IsHandleCreated)
            {
                this.BeginInvoke(new Action(() =>
                {
                    if (!_isLoadingProfile) LoadInstalledMods();
                }));
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
                    LoadInstalledMods();
                }
            }
        }

        private void btnRefresh_Click(object? sender, EventArgs e)
        {
            BuildVanillaFileMap();
            LoadInstalledMods();
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

            if (!Directory.Exists(StagingDirectory)) Directory.CreateDirectory(StagingDirectory);

            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            HashSet<string> activeForProfile = LoadActiveModsForProfile(selectedProfile);
            string profileFolder = Path.Combine(ConfigsDirectory, selectedProfile);

            // 1. Staged DLL plugins (BepInEx)
            string[] stagedFiles = Directory.GetFiles(StagingDirectory, "*.dll", SearchOption.AllDirectories);
            foreach (string file in stagedFiles)
            {
                string fileName = Path.GetFileName(file);
                ListViewItem item = new ListViewItem(fileName)
                {
                    Tag = "DLL",
                    Checked = activeForProfile.Contains(fileName)
                };
                lstMods.Items.Add(item);
            }

            // 2. Scan pending asset files in profile directory
            if (Directory.Exists(profileFolder))
            {
                NormalizeModDirectory(profileFolder);

                string profileDataFolder = Path.Combine(profileFolder, "NASCARHeat5_Data");
                if (Directory.Exists(profileDataFolder))
                {
                    // Check if this profile has an explicit saved JSON state on disk
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
                            // If saved state exists, respect it. If it's a brand new profile without a JSON file, default ALL to true.
                            Checked = hasSavedState ? activeForProfile.Contains(itemLabel) : true
                        };
                        lstMods.Items.Add(item);
                    }
                }
            }
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

        private void SwapDataFolderForProfile(string profileName, HashSet<string> enabledAssetPaths)
        {
            string gameDataDir = Path.Combine(GameDirectory, "NASCARHeat5_Data");
            if (!Directory.Exists(gameDataDir)) return;

            // 1. Revert all previously deployed files from manifest back to vanilla
            if (File.Exists(_deployedManifestPath))
            {
                try
                {
                    var previousManifest = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_deployedManifestPath));
                    if (previousManifest != null)
                    {
                        foreach (string relativeFile in previousManifest)
                        {
                            // Target file inside GameDirectory (e.g., NASCARHeat5_Data\...)
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
                                // ONLY delete if it was a custom added file that didn't exist in original vanilla
                                File.Delete(targetFile);
                            }
                        }
                    }
                }
                catch { }
            }

            var newlyDeployedFiles = new List<string>();
            string profileDataDir = Path.Combine(ConfigsDirectory, profileName, "NASCARHeat5_Data");

            // 2. Deploy ONLY actively checked mod files and back up original vanilla versions
            if (profileName != "Default" && Directory.Exists(profileDataDir))
            {
                this.Invoke(() => lblStatus.Text = $"Applying active mod files for '{profileName}'...");

                foreach (string sourceFile in Directory.GetFiles(profileDataDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!enabledAssetPaths.Contains(sourceFile)) continue;

                    string relativePath = Path.GetRelativePath(profileDataDir, sourceFile);
                    string relativeToGame = Path.Combine("NASCARHeat5_Data", relativePath);
                    string destFile = Path.Combine(gameDataDir, relativePath);
                    string backupFile = Path.Combine(VanillaBackupDirectory, relativeToGame);

                    // If vanilla file exists and isn't backed up yet, back it up now
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

            // Save active manifest for future clean reversal
            File.WriteAllText(_deployedManifestPath, JsonSerializer.Serialize(newlyDeployedFiles));
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

            var checkedDlls = new List<string>();
            var checkedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ListViewItem item in lstMods.Items)
            {
                if (item.Checked)
                {
                    if (item.Tag?.ToString() == "DLL")
                    {
                        checkedDlls.Add(item.Text);
                    }
                    else if (item.Tag != null && File.Exists(item.Tag.ToString()))
                    {
                        checkedAssetPaths.Add(item.Tag.ToString()!);
                    }
                }
            }

            await Task.Run(() =>
            {
                // Save data sync removed — using native/unified game save location for all profiles

                this.Invoke(new Action(() => lblStatus.Text = $"Deploying mods for '{selectedProfile}'..."));
                SwapDataFolderForProfile(selectedProfile, checkedAssetPaths);

                this.Invoke(new Action(() => lblStatus.Text = "Deploying BepInEx plugins..."));
                string pluginsDir = Path.Combine(GameDirectory, "BepInEx", "plugins");
                if (!Directory.Exists(pluginsDir)) Directory.CreateDirectory(pluginsDir);

                // Clear out existing plugins to allow clean uninstallation of unchecked plugins
                foreach (string file in Directory.GetFiles(pluginsDir, "*.dll"))
                {
                    try { File.Delete(file); } catch { }
                }

                foreach (string dllName in checkedDlls)
                {
                    string sourcePath = Path.Combine(StagingDirectory, dllName);
                    string destPath = Path.Combine(pluginsDir, dllName);

                    if (File.Exists(sourcePath)) File.Copy(sourcePath, destPath, overwrite: true);
                }
            });

            this.Enabled = true;
            this.UseWaitCursor = false;

            lblStatus.Text = $"Deployment Complete | Active Profile: {selectedProfile}";
            MessageBox.Show($"Successfully deployed profile '{selectedProfile}'!", "Deployment Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (MessageBox.Show("Deployment complete! Launch NASCAR Heat 5 now?", "Launch Game", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                LaunchNASCARHeat5();
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
            string targetSaveDir = Path.Combine(targetDir, "SaveData");

            if (!Directory.Exists(targetDataDir)) Directory.CreateDirectory(targetDataDir);
            if (!Directory.Exists(targetSaveDir)) Directory.CreateDirectory(targetSaveDir);

            LoadProfiles();
            cmbProfiles.SelectedItem = cleanProfileName;
            lblStatus.Text = $"Profile '{cleanProfileName}' created successfully.";
        }

        private void btnUnlockSeasonPass_Click(object? sender, EventArgs e)
        {
            string gameDir = txtGamePath.Text.Trim();
            string managedDir = Path.Combine(gameDir, "NASCARHeat5_Data", "Managed");
            string dllPath = Path.Combine(managedDir, "Assembly-CSharp.dll");
            string backupPath = dllPath + ".bak";
            string tempOutputPath = Path.Combine(managedDir, "Assembly-CSharp.dll.tmp");

            if (!File.Exists(dllPath))
            {
                MessageBox.Show("Could not find Assembly-CSharp.dll in the selected directory.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                if (!File.Exists(backupPath)) File.Copy(dllPath, backupPath);

                using (var resolver = new DefaultAssemblyResolver())
                {
                    resolver.AddSearchDirectory(managedDir);
                    var readerParameters = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = true };

                    using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(dllPath, readerParameters))
                    {
                        ModuleDefinition module = assembly.MainModule;
                        TypeDefinition? ngUtilType = module.Types.FirstOrDefault(t => t.Namespace == "MGI.Platform.Steam" && t.Name == "SteamPlatformDLCLoader");

                        if (ngUtilType == null)
                        {
                            MessageBox.Show("Could not locate SteamPlatformDLCLoader in Assembly-CSharp.dll.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        MethodDefinition? seasonPassMethod = ngUtilType.Methods.FirstOrDefault(m => m.Name == "do_they_own_the_season_pass");
                        if (seasonPassMethod != null)
                        {
                            ILProcessor il = seasonPassMethod.Body.GetILProcessor();
                            seasonPassMethod.Body.Instructions.Clear();
                            il.Append(il.Create(OpCodes.Ldc_I4_1)); // Force return true
                            il.Append(il.Create(OpCodes.Ret));
                        }

                        assembly.Write(tempOutputPath);
                    }
                }

                File.Move(tempOutputPath, dllPath, overwrite: true);

                lblStatus.Text = "Status: Season Pass unlocked!";
                MessageBox.Show("Season Pass check bypassed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);
                MessageBox.Show($"An error occurred while unlocking Season Pass:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
                        else if (ext == ".dll" && !path.Contains("Managed", StringComparison.OrdinalIgnoreCase))
                        {
                            string fileName = Path.GetFileName(path);
                            string destPath = Path.Combine(StagingDirectory, fileName);
                            File.Copy(path, destPath, overwrite: true);
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
    }
}