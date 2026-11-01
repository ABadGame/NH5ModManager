# NASCAR Heat Mod Manager

A lightweight, mod management tool for **NASCAR Heat 5**.

---

## Features

* **Per-profile saves:** Deploying a named profile parks the outgoing save and restores that profile's progress. New mod profiles start empty so the game generates compatible saves. Starting with Default/Vanilla adopts the existing live save instead of shifting profile ownership by one. The original save is kept separately.
* **Profile-Based Asset Management:** Create distinct mod profiles for different overhauls, physics tweaks, or custom paint schemes.
* **Automated DLC Unlocker:** Toggle **Auto-Unlock DLCs** to automatically apply the IL patch to `Assembly-CSharp.dll` every time you deploy mods.
* **Safe Reversion & Backups:** Automatic original vanilla file backups prevent dirty installs and make reverting to stock effortless.
* **Drag-and-Drop Support:** Drag `.zip` archives or uncompressed folders directly into the window to assign them to your active profile.
* **Conflict Detection:** Visual indicators highlight overlapping or duplicated active mod files.
* **Direct Steam Launching:** Option to launch NASCAR Heat 5 directly through Steam immediately after deployment.

---

## Installation

NOTE: Please make sure your current game files are in a CLEAN unmodified state otherwise, disabling of mods will not work correctly.

1. Download the latest release from the **Releases** tab.
2. Extract the archive to any directory on your system.
3. Launch `NH5ModManager.exe`.

For any issues, please visit our [Discord server](https://discord.gg/3KQ49uEVj2)

---

## Usage Guide

1. **Set Game Folder:** Click **Browse** and select your root `NASCAR Heat 5` directory (e.g., `C:\Program Files (x86)\Steam\steamapps\common\NASCAR Heat 5`).
2. **Create / Select Profile:** Choose an existing profile from the dropdown or click **+ New Profile** to set up a new layout.
3. **Add Mods:** Drag and drop mod archives (`.zip`) or individual mod folders into the app list view to add them to the selected profile.
4. **Configure DLC Settings:** Check **Auto-Unlock DLCs** if you want the season pass check patched into your deployed assembly files.
5. **Deploy:** Click **Deploy Mods**. The manager will swap the active assets, run the DLC patch (if enabled), and prompt you to launch the game.

---

## Save profiles

Create a separate named profile for each incompatible mod/driver roster, then use **Deploy Mods** to switch. Selecting a dropdown item alone does not switch game assets or saves. Deploying the same profile again preserves its save. If you replace a roster inside an existing profile, create a new profile for it first.

The live save folder (where NASCAR Heat 5 reads/writes) defaults to `%USERPROFILE%\NASCAR Heat 5`. To change it, right-click the profile dropdown and select **Choose game save folder...**. Choose the save folder, not the Steam installation directory.

Inactive profile saves are stored **inside each mod profile** by default:

* `Profiles/<profile name>/Save/` holds that profile's progress while another profile is active.
* `Profiles/.SaveStore/Original/` preserves the save that existed before the first managed deployment.
* `Profiles/.SaveStore/state.json` maps profiles to those folders and records `ActiveProfile`.
* The active profile's save remains in the game's normal save folder, so eleven profiles normally mean ten `Profiles/*/Save` folders plus one live save.
* Empty profiles are not archived as misleading empty save folders. Copy/export of a profile skips its `Save/` folder so packages stay mod-only.
* Older installs that used `%USERPROFILE%\.NASCAR Heat 5.NH5ModManager` are migrated automatically into `Profiles/` without changing profile ownership.

For a genuinely clean retest, close the game and manager, then rename the live `NASCAR Heat 5` save folder and the manager `Profiles` folder (or at least `Profiles/.SaveStore`) to backup names. Clearing only the live folder is not a clean reset because the previous profile mappings remain in `state.json`.

Close the game before deployment. The manager blocks deployment while NASCAR Heat 5 is running, and only offers to launch after deployment succeeds. Interrupted switches are recovered on the next deployment. If recovery cannot proceed, the manager stops and reports the backup location.

Renaming a profile retains its save identity. Copied profiles start with a separate fresh save; save files are never included in exported mod packages. Deleting a mod profile retains its save archive; reusing the exact profile name reconnects that progress. There is one shared save per named profile, not per individual checked asset file.

To manually recover the pre-manager save: close the game and manager, copy `Original` to a separate safe location, move the current live save folder aside, then copy `Original` to the normal game save path. Do not delete the archive or index. Redeploy a profile before using automatic switching again.

## Technical Details & Project Structure

```text
├── Profiles/                      # Saved profile configs and asset structures
│   ├── Default/
│   └── <ProfileName>/
│       └── NASCARHeat5_Data/
├── NH5ModManager_Data/
│   └── Vanilla_Backup/            # Original unmodified game backups
├── app_settings.json              # App configuration (e.g., Auto-Unlock DLC state)
├── DeployedManifest.json          # Active deployment tracking
└── vanilla_map.json               # Indexed vanilla directory structure cache

```

---

## Requirements

* **OS:** Windows 10 / 11 (64-bit)
* **Framework:** .NET 10.0 Desktop Runtime (not required for the self-contained build)
* **Game:** NASCAR Heat 5 (Steam)
