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

        private FileSystemWatcher? _stagingWatcher;
        private bool _isLoadingProfile = false;
        private string _previousProfile = "Default";

        private readonly Dictionary<string, string> _vanillaFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ContextMenuStrip _profileContextMenu = new ContextMenuStrip();

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

        private void Form1_Load(object sender, EventArgs e)
        {
            txtGamePath.Text = GameDirectory;

            if (!Directory.Exists(StagingDirectory)) Directory.CreateDirectory(StagingDirectory);
            if (!Directory.Exists(ConfigsDirectory)) Directory.CreateDirectory(ConfigsDirectory);

            BuildVanillaFileMap();
            SetupStagingWatcher();
            LoadProfiles();
            LoadInstalledMods();
        }

        private readonly string _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vanilla_map.json");

        private void BuildVanillaFileMap()
        {
            _vanillaFileMap.Clear();

            // Use cached map if available to eliminate cold-start scanning delay
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
                catch { /* Fallback to fresh scan if cache is corrupt */ }
            }

            string gameDataDir = Path.Combine(GameDirectory, "NASCARHeat5_Data");
            if (!Directory.Exists(gameDataDir)) return;

            foreach (string filePath in Directory.GetFiles(gameDataDir, "*.*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(filePath);
                string relativePath = Path.GetRelativePath(GameDirectory, filePath);

                _vanillaFileMap.TryAdd(fileName, relativePath);
            }

            // Save cache for next run
            try
            {
                string json = JsonSerializer.Serialize(_vanillaFileMap, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(_cachePath, json);
            }
            catch { }
        }

        private string GetDestinationPathForFile(string profileFolder, string relativePath)
        {
            return Path.Combine(profileFolder, relativePath);
        }

        private void InitializeProfileContextMenu()
        {
            _profileContextMenu.Items.Clear();

            ToolStripMenuItem openFolderItem = new ToolStripMenuItem("Open Profile in File Explorer");
            openFolderItem.Click += (s, e) => OpenSelectedProfileFolder();

            ToolStripMenuItem deleteProfileItem = new ToolStripMenuItem("Delete Selected Profile");
            deleteProfileItem.Click += (s, e) => DeleteSelectedProfile();

            _profileContextMenu.Items.Add(openFolderItem);
            _profileContextMenu.Items.Add(deleteProfileItem);

            this.cmbProfiles.ContextMenuStrip = _profileContextMenu;
            this.lstMods.ContextMenuStrip = _profileContextMenu;
        }

        private void OpenSelectedProfileFolder()
        {
            string selectedProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
            string profilePath = Path.Combine(ConfigsDirectory, selectedProfile);

            if (!Directory.Exists(profilePath))
            {
                Directory.CreateDirectory(profilePath);
            }

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

                    if (!folderName.Equals("Vanilla_Backup", StringComparison.OrdinalIgnoreCase) &&
                        !folderName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        cmbProfiles.Items.Add(folderName);
                    }
                }
            }

            cmbProfiles.SelectedIndex = 0;
            _previousProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";
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
                if (item.Checked)
                {
                    activeMods.Add(item.Text);
                }
            }

            string jsonPath = GetProfileJsonPath(profileName);
            try
            {
                string json = JsonSerializer.Serialize(activeMods, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, json);
            }
            catch { }
        }

        private async void cmbProfiles_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_isLoadingProfile) return;

            string newProfile = cmbProfiles.SelectedItem?.ToString() ?? "Default";

            this.UseWaitCursor = true;
            this.Enabled = false;

            await Task.Run(() =>
            {
                SyncCurrentSaveDataToProfile(_previousProfile);
                SwapSaveDataForProfile(newProfile);
            });

            _previousProfile = newProfile;
            _isLoadingProfile = true;
            LoadInstalledMods();
            _isLoadingProfile = false;

            this.Enabled = true;
            this.UseWaitCursor = false;
        }

        private void LoadInstalledMods()
        {
            lstMods.ItemCheck -= lstMods_ItemCheck;
            lstMods.Items.Clear();

            if (!Directory.Exists(StagingDirectory))
                Directory.CreateDirectory(StagingDirectory);

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

            if (Directory.Exists(profileFolder))
            {
                // Normalize loose files in the profile root into NASCARHeat5_Data automatically
                NormalizeModDirectory(profileFolder);

                // 2. Scan pending loose assets or overriding files in target directories
                string profileDataFolder = Path.Combine(profileFolder, "NASCARHeat5_Data");
                if (Directory.Exists(profileDataFolder))
                {
                    string[] profileFiles = Directory.GetFiles(profileDataFolder, "*.*", SearchOption.AllDirectories);
                    foreach (string filePath in profileFiles)
                    {
                        string fileName = Path.GetFileName(filePath);
                        string relativePath = Path.GetRelativePath(profileFolder, filePath);
                        string ext = Path.GetExtension(filePath).ToLowerInvariant();

                        string tagType = ext switch
                        {
                            ".bank" => "[Audio]",
                            ".dll" => "[Core]",
                            ".bundle" or ".assets" => "[Asset]",
                            _ => "[Data]"
                        };

                        ListViewItem item = new ListViewItem($"{tagType} {relativePath}")
                        {
                            Tag = filePath,
                            Checked = true
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
            if (conflictFound)
            {
                statusText += " | ⚠️ Conflict Detected: Duplicate Active Mods!";
            }

            lblStatus.Text = statusText;
        }

        private string GetGameSaveDataPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NASCAR Heat 5", "SaveData");
        }

        private void SwapSaveDataForProfile(string profileName)
        {
            string activeSaveDir = GetGameSaveDataPath();
            if (!Directory.Exists(activeSaveDir))
            {
                Directory.CreateDirectory(activeSaveDir);
            }

            string profileSaveDir = Path.Combine(ConfigsDirectory, profileName, "SaveData");
            string vanillaSaveBackup = Path.Combine(ConfigsDirectory, "Vanilla_Backup", "SaveData");

            if (!Directory.Exists(vanillaSaveBackup))
            {
                this.Invoke(new Action(() => lblStatus.Text = "Creating initial SaveData backup..."));
                CopyDirectory(activeSaveDir, vanillaSaveBackup);
            }

            try
            {
                DirectoryInfo di = new DirectoryInfo(activeSaveDir);
                foreach (FileInfo file in di.GetFiles()) file.Delete();
                foreach (DirectoryInfo dir in di.GetDirectories()) dir.Delete(true);
            }
            catch { }

            string sourceSaveDir = Directory.Exists(profileSaveDir) ? profileSaveDir : vanillaSaveBackup;

            this.Invoke(new Action(() => lblStatus.Text = $"Loading save data for profile '{profileName}'..."));
            CopyDirectory(sourceSaveDir, activeSaveDir);
        }

        private void SyncCurrentSaveDataToProfile(string profileName)
        {
            string activeSaveDir = GetGameSaveDataPath();
            string profileSaveDir = Path.Combine(ConfigsDirectory, profileName, "SaveData");

            if (Directory.Exists(activeSaveDir))
            {
                CopyDirectory(activeSaveDir, profileSaveDir);
            }
        }

        private readonly string _deployedManifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeployedManifest.json");

        private void SwapDataFolderForProfile(string profileName)
        {
            string gameDataDir = Path.Combine(GameDirectory, "NASCARHeat5_Data");
            string profileDataDir = Path.Combine(ConfigsDirectory, profileName, "NASCARHeat5_Data");

            if (!Directory.Exists(gameDataDir)) return;

            // 1. Clean up previously deployed mod files using manifest (avoids wiping clean game files)
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
                            if (File.Exists(targetFile))
                            {
                                File.Delete(targetFile);
                            }
                        }
                    }
                }
                catch { }
            }

            var deployedFilesList = new List<string>();

            // 2. Deploy active profile overrides and record paths into new manifest
            if (profileName != "Default" && Directory.Exists(profileDataDir))
            {
                this.Invoke(() => lblStatus.Text = $"Applying {profileName} mod files...");

                foreach (string sourceFile in Directory.GetFiles(profileDataDir, "*.*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(profileDataDir, sourceFile);
                    string destFile = Path.Combine(gameDataDir, relativePath);

                    string? destDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                    File.Copy(sourceFile, destFile, overwrite: true);

                    // Track relative path to game directory
                    deployedFilesList.Add(Path.Combine("NASCARHeat5_Data", relativePath));
                }
            }

            // Write manifest
            File.WriteAllText(_deployedManifestPath, JsonSerializer.Serialize(deployedFilesList));
        }

        private string NormalizeModDirectory(string inputPath)
        {
            string targetDataFolder = Path.Combine(inputPath, "NASCARHeat5_Data");
            if (!Directory.Exists(targetDataFolder))
            {
                Directory.CreateDirectory(targetDataFolder);
            }

            // 1. Process loose modded files placed directly in the profile folder root
            string[] rootFiles = Directory.GetFiles(inputPath, "*.*", SearchOption.TopDirectoryOnly);
            foreach (string filePath in rootFiles)
            {
                string fileName = Path.GetFileName(filePath);

                // Check if this single loose file matches a known game file location
                if (_vanillaFileMap.TryGetValue(fileName, out string? relativePath))
                {
                    string destinationPath = Path.Combine(inputPath, relativePath);
                    string? destDir = Path.GetDirectoryName(destinationPath);

                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

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
            foreach (ListViewItem item in lstMods.Items)
            {
                if (item.Checked && (item.Tag?.ToString() == "DLL"))
                {
                    checkedDlls.Add(item.Text);
                }
            }

            await Task.Run(() =>
            {
                this.Invoke(new Action(() => lblStatus.Text = $"Syncing save data & deploying season mod for '{selectedProfile}'..."));
                SwapSaveDataForProfile(selectedProfile);
                SwapDataFolderForProfile(selectedProfile);

                this.Invoke(new Action(() => lblStatus.Text = "Deploying BepInEx plugins..."));
                string pluginsDir = Path.Combine(GameDirectory, "BepInEx", "plugins");
                if (!Directory.Exists(pluginsDir))
                {
                    Directory.CreateDirectory(pluginsDir);
                }

                foreach (string file in Directory.GetFiles(pluginsDir, "*.dll"))
                {
                    try { File.Delete(file); } catch { }
                }

                foreach (string dllName in checkedDlls)
                {
                    string sourcePath = Path.Combine(StagingDirectory, dllName);
                    string destPath = Path.Combine(pluginsDir, dllName);

                    if (File.Exists(sourcePath))
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                    }
                }
            });

            this.Enabled = true;
            this.UseWaitCursor = false;

            lblStatus.Text = $"Deployment Complete | Active Profile: {selectedProfile}";
            MessageBox.Show($"Successfully deployed profile '{selectedProfile}' and active plugins!", "Deployment Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (MessageBox.Show("Deployment complete! Launch NASCAR Heat 5 now?", "Launch Game", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                LaunchNASCARHeat5();
            }
        }

        private async void btnSaveProfile_Click(object? sender, EventArgs e)
        {
            string newProfile = Microsoft.VisualBasic.Interaction.InputBox("Enter a name for the new Mod Profile:", "Create Profile", "New Profile");
            string cleanProfileName = string.Concat(newProfile.Split(Path.GetInvalidFileNameChars())).Trim();

            if (string.IsNullOrWhiteSpace(cleanProfileName)) return;

            string targetDir = Path.Combine(ConfigsDirectory, cleanProfileName);
            string targetDataDir = Path.Combine(targetDir, "NASCARHeat5_Data");

            // Simply ensure the folder exists without copying stock game assets into it
            if (!Directory.Exists(targetDataDir))
            {
                Directory.CreateDirectory(targetDataDir);
            }

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
                if (!File.Exists(backupPath))
                {
                    File.Copy(dllPath, backupPath);
                }

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

            if (!Directory.Exists(profileFolder))
            {
                Directory.CreateDirectory(profileFolder);
            }

            this.UseWaitCursor = true;
            this.Enabled = false;

            await Task.Run(() =>
            {
                foreach (string path in paths)
                {
                    if (File.Exists(path))
                    {
                        string ext = Path.GetExtension(path).ToLowerInvariant();

                        // 1. Unpack ZIP archives directly into profile
                        if (ext == ".zip")
                        {
                            try
                            {
                                ZipFile.ExtractToDirectory(path, profileFolder, overwriteFiles: true);
                            }
                            catch { }
                        }
                        // 2. Stage BepInEx plugin DLLs into the Mods folder
                        else if (ext == ".dll" && !path.Contains("Managed", StringComparison.OrdinalIgnoreCase))
                        {
                            string fileName = Path.GetFileName(path);
                            string destPath = Path.Combine(StagingDirectory, fileName);
                            File.Copy(path, destPath, overwrite: true);
                        }
                        // 3. Import loose individual data/asset files
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

                // Run automatic structure normalization on profile folder
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

                string? destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destFile, overwrite: true);
            }
        }

        private void btnLaunchGame_Click(object? sender, EventArgs e)
        {
            LaunchNASCARHeat5();
        }

        private void LaunchNASCARHeat5()
        {
            try
            {
                // 1. Primary method: Launch via Steam protocol (handles achievements, overlays, and DRM cleanly)
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://rungameid/1258980",
                    UseShellExecute = true
                });

                lblStatus.Text = "Status: Launching NASCAR Heat 5 via Steam...";
            }
            catch (Exception ex)
            {
                // 2. Fallback: Launch direct executable if Steam protocol fails
                string exePath = Path.Combine(GameDirectory, "NASCARHeat5.exe");

                if (File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = GameDirectory,
                            UseShellExecute = true
                        });

                        lblStatus.Text = "Status: Launching NASCARHeat5.exe directly...";
                    }
                    catch (Exception innerEx)
                    {
                        MessageBox.Show($"Failed to launch executable directly:\n{innerEx.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    MessageBox.Show($"Could not launch game:\n{ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}