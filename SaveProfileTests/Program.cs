using NH5ModManager;
using System.Text.Json;

string root = Path.Combine(AppContext.BaseDirectory, "nh5-save-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

string ProfileSave(string profiles, string name) =>
    Path.Combine(profiles, name, SaveProfileManager.ProfileSaveFolderName);

string live = Path.Combine(root, "NASCAR Heat 5");
string profiles = Path.Combine(root, "Profiles");
string store = Path.Combine(profiles, ".SaveStore");
string statePath = Path.Combine(store, "state.json");
Directory.CreateDirectory(Path.Combine(live, "nested"));
File.WriteAllText(Path.Combine(live, "nested", "career.sav"), "original");
var manager = new SaveProfileManager(live, profiles);
manager.Deploy("2024", () => Check(!Directory.EnumerateFileSystemEntries(live).Any(), "New profile starts empty"));
Check(File.ReadAllText(Path.Combine(store, "Original", "nested", "career.sav")) == "original", "Original save preserved including subfolders");
File.WriteAllText(Path.Combine(live, "career.sav"), "2024 progress");
manager.Deploy("2025", () => Check(!Directory.EnumerateFileSystemEntries(live).Any(), "Second profile starts empty"));
Check(File.ReadAllText(Path.Combine(ProfileSave(profiles, "2024"), "career.sav")) == "2024 progress", "Inactive save is stored inside the profile folder");
File.WriteAllText(Path.Combine(live, "career.sav"), "2025 progress");
manager = new SaveProfileManager(live, profiles);
manager.Deploy("2024", () => { });
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2024 progress", "Switch back restores progress after manager restart");
manager.Deploy("2024", () => { });
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2024 progress", "Redeploying current profile preserves progress");
manager.RenameProfile("2024", "2024 renamed");
manager.Deploy("2024 RENAMED", () => { });
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2024 progress", "Rename and case changes preserve save identity");
try { manager.Deploy("2025", () => throw new IOException("simulated deploy failure")); }
catch (IOException ex) when (ex.Message == "simulated deploy failure") { }
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2024 progress", "Failed deployment restores outgoing save");
manager.Deploy("2025", () => { });
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2025 progress", "Failed deployment preserves target save too");

// Simulate abrupt termination at each directory-switch boundary.
foreach (int phase in new[] { 0, 1, 2 })
{
    var state = JsonSerializer.Deserialize<SaveProfileManager.SaveState>(File.ReadAllText(statePath))!;
    string target = state.Profiles["2024 renamed"];
    state.Pending = new SaveProfileManager.PendingSwitch { TargetSlot = target };
    File.WriteAllText(statePath, JsonSerializer.Serialize(state));
    if (phase >= 1)
    {
        string archived = ProfileSave(profiles, state.ActiveSlot);
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        Directory.Move(live, archived);
    }
    if (phase >= 2) Directory.Move(ProfileSave(profiles, target), live);
    manager = new SaveProfileManager(live, profiles);
    manager.Deploy("2025", () => { });
    Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2025 progress", $"Interrupted switch recovers at phase {phase}");
}
manager.Deploy("2024 renamed", () => { });
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2024 progress", "Recovery preserves the other profile");
File.WriteAllText(statePath, "broken JSON");
try { manager.Deploy("other", () => { }); throw new Exception("Expected invalid index failure"); }
catch (JsonException) { }
Check(File.ReadAllText(Path.Combine(live, "career.sav")) == "2024 progress", "Corrupt index stops without modifying live save");
string absent = Path.Combine(root, "First run");
string absentProfiles = Path.Combine(root, "First run Profiles");
new SaveProfileManager(absent, absentProfiles).Deploy("Brand new", () => { });
Check(Directory.Exists(absent) && !Directory.EnumerateFileSystemEntries(absent).Any(), "First run without an existing save works");

// Reproduce the reported real-world order: Default first, then year mods.
// The existing Default save must be adopted instead of shifting every label by one.
string reportedRoot = Path.Combine(root, "reported-default-1992-1997");
string reportedLive = Path.Combine(reportedRoot, "NASCAR Heat 5");
string reportedProfiles = Path.Combine(reportedRoot, "Profiles");
string reportedStore = Path.Combine(reportedProfiles, ".SaveStore");
Directory.CreateDirectory(reportedLive);
File.WriteAllText(Path.Combine(reportedLive, "career.sav"), "Default");
var reportedManager = new SaveProfileManager(reportedLive, reportedProfiles);
reportedManager.Deploy("Default", () =>
    Check(File.ReadAllText(Path.Combine(reportedLive, "career.sav")) == "Default", "First Default deploy adopts the existing live save"));
var reportedState = JsonSerializer.Deserialize<SaveProfileManager.SaveState>(File.ReadAllText(Path.Combine(reportedStore, "state.json")))!;
Check(reportedState.ActiveProfile == "Default" && reportedState.Profiles["Default"] == "Default", "Default state uses the profile folder as its save slot");
Check(File.ReadAllText(Path.Combine(reportedStore, "Original", "career.sav")) == "Default", "Initial Default save is also preserved as Original");

reportedManager.Deploy("1992", () =>
    Check(!Directory.EnumerateFileSystemEntries(reportedLive).Any(), "1992 starts with a clean save"));
File.WriteAllText(Path.Combine(reportedLive, "career.sav"), "1992");
reportedManager.Deploy("1997", () =>
    Check(!Directory.EnumerateFileSystemEntries(reportedLive).Any(), "1997 starts with a clean save"));
File.WriteAllText(Path.Combine(reportedLive, "career.sav"), "1997");
reportedState = JsonSerializer.Deserialize<SaveProfileManager.SaveState>(File.ReadAllText(Path.Combine(reportedStore, "state.json")))!;
Check(File.ReadAllText(Path.Combine(ProfileSave(reportedProfiles, "Default"), "career.sav")) == "Default", "Default save remains inside Profiles/Default/Save");
Check(File.ReadAllText(Path.Combine(ProfileSave(reportedProfiles, "1992"), "career.sav")) == "1992", "1992 save remains inside Profiles/1992/Save");
Check(reportedState.ActiveProfile == "1997" && File.ReadAllText(Path.Combine(reportedLive, "career.sav")) == "1997", "state.json clearly identifies 1997 as the live save");
reportedManager.Deploy("Default", () => { });
Check(File.ReadAllText(Path.Combine(reportedLive, "career.sav")) == "Default", "Switching back restores Default without overwrite");
Check(File.ReadAllText(Path.Combine(ProfileSave(reportedProfiles, "1997"), "career.sav")) == "1997", "Leaving 1997 archives it inside Profiles/1997/Save");

// Eleven profiles must yield eleven distinct saves: ten archived plus one live.
string manyRoot = Path.Combine(root, "eleven-profile-sequence");
string manyLive = Path.Combine(manyRoot, "NASCAR Heat 5");
string manyProfiles = Path.Combine(manyRoot, "Profiles");
string manyStore = Path.Combine(manyProfiles, ".SaveStore");
string[] manyProfileNames = ["Default", "1990", "1992", "1994", "1995", "1997", "1999", "2001", "2003", "2005", "2024"];
var manyManager = new SaveProfileManager(manyLive, manyProfiles);
foreach (string profileName in manyProfileNames)
{
    manyManager.Deploy(profileName, () => { });
    File.WriteAllText(Path.Combine(manyLive, "profile-note.txt"), profileName);
}
var manyState = JsonSerializer.Deserialize<SaveProfileManager.SaveState>(File.ReadAllText(Path.Combine(manyStore, "state.json")))!;
int archivedSaveCount = manyProfileNames.Count(name =>
    File.Exists(Path.Combine(ProfileSave(manyProfiles, name), "profile-note.txt")));
Check(archivedSaveCount == 10 && File.ReadAllText(Path.Combine(manyLive, "profile-note.txt")) == "2024", "Eleven profiles retain ten archived saves plus one live save");
Check(manyState.Profiles.Count == 11
      && manyState.Profiles.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 11
      && manyState.Profiles.All(pair => pair.Value.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)),
    "Eleven profiles have eleven unique profile-folder slot names");
bool allMarkersRestored = true;
foreach (string profileName in manyProfileNames)
{
    manyManager.Deploy(profileName, () => { });
    allMarkersRestored &= File.ReadAllText(Path.Combine(manyLive, "profile-note.txt")) == profileName;
}
Check(allMarkersRestored, "All eleven profile markers restore under the correct profile");

// Switching away before a game has created a save must not leave fake empty archives.
string emptyRoot = Path.Combine(root, "empty-profile-sequence");
string emptyLive = Path.Combine(emptyRoot, "NASCAR Heat 5");
string emptyProfiles = Path.Combine(emptyRoot, "Profiles");
string emptyStore = Path.Combine(emptyProfiles, ".SaveStore");
var emptyManager = new SaveProfileManager(emptyLive, emptyProfiles);
emptyManager.Deploy("Default", () => { });
emptyManager.Deploy("1992", () => { });
Check(!Directory.Exists(Path.Combine(emptyStore, "Original"))
      && !Directory.Exists(ProfileSave(emptyProfiles, "Default")),
    "Empty outgoing profiles do not create misleading empty save folders");

// Existing GUID-based releases migrate without changing which save belongs to which profile.
string legacyRoot = Path.Combine(root, "legacy-guid-migration");
string legacyLive = Path.Combine(legacyRoot, "NASCAR Heat 5");
string legacyProfiles = Path.Combine(legacyRoot, "Profiles");
string legacyExternal = Path.Combine(legacyRoot, ".NASCAR Heat 5.NH5ModManager");
string legacyStore = Path.Combine(legacyProfiles, ".SaveStore");
string legacy1992 = Guid.NewGuid().ToString("N");
string legacy1997 = Guid.NewGuid().ToString("N");
Directory.CreateDirectory(legacyLive);
Directory.CreateDirectory(Path.Combine(legacyExternal, legacy1992));
File.WriteAllText(Path.Combine(legacyLive, "career.sav"), "1997");
File.WriteAllText(Path.Combine(legacyExternal, legacy1992, "career.sav"), "1992");
File.WriteAllText(Path.Combine(legacyExternal, "state.json"), JsonSerializer.Serialize(new SaveProfileManager.SaveState
{
    Version = 1,
    ActiveSlot = legacy1997,
    ActiveProfile = "1997",
    Profiles = new Dictionary<string, string> { ["1992"] = legacy1992, ["1997"] = legacy1997 }
}));
var legacyManager = new SaveProfileManager(legacyLive, legacyProfiles);
legacyManager.Deploy("1992", () => { });
var legacyState = JsonSerializer.Deserialize<SaveProfileManager.SaveState>(File.ReadAllText(Path.Combine(legacyStore, "state.json")))!;
Check(File.ReadAllText(Path.Combine(legacyLive, "career.sav")) == "1992"
      && File.ReadAllText(Path.Combine(ProfileSave(legacyProfiles, "1997"), "career.sav")) == "1997",
    "Legacy GUID saves migrate without swapping profile ownership");
Check(legacyState.Profiles["1992"] == "1992"
      && legacyState.Profiles["1997"] == "1997"
      && !Directory.Exists(Path.Combine(legacyExternal, legacy1992)),
    "Legacy GUID folders migrate into Profiles/<name>/Save");

// A corrupt index that maps two profiles to one slot must stop before any move.
string duplicateRoot = Path.Combine(root, "duplicate-slot-index");
string duplicateLive = Path.Combine(duplicateRoot, "NASCAR Heat 5");
string duplicateProfiles = Path.Combine(duplicateRoot, "Profiles");
string duplicateStore = Path.Combine(duplicateProfiles, ".SaveStore");
string duplicateId = "shared";
Directory.CreateDirectory(duplicateLive);
Directory.CreateDirectory(duplicateStore);
File.WriteAllText(Path.Combine(duplicateLive, "career.sav"), "untouched");
File.WriteAllText(Path.Combine(duplicateStore, "state.json"), JsonSerializer.Serialize(new SaveProfileManager.SaveState
{
    ActiveSlot = "Original",
    Profiles = new Dictionary<string, string> { ["1992"] = duplicateId, ["1997"] = duplicateId }
}));
try { new SaveProfileManager(duplicateLive, duplicateProfiles).Deploy("1992", () => { }); throw new Exception("Expected duplicate slot failure"); }
catch (IOException ex) when (ex.Message.Contains("same save folder", StringComparison.OrdinalIgnoreCase)) { }
Check(File.ReadAllText(Path.Combine(duplicateLive, "career.sav")) == "untouched", "Duplicate slot mappings stop before touching the live save");

Console.WriteLine($"All {checks} checks passed. Test fixtures: {root}");
