# NASCAR Heat Mod Manager

A lightweight, mod management tool for **NASCAR Heat 5**.

---

## Features

* **Profile-Based Asset Management:** Create distinct mod profiles for different overhauls, physics tweaks, or custom paint schemes.
* **Automated DLC Unlocker:** Toggle **Auto-Unlock DLCs** to automatically apply the IL patch to `Assembly-CSharp.dll` every time you deploy mods.
* **Safe Reversion & Backups:** Automatic original vanilla file backups prevent dirty installs and make reverting to stock effortless.
* **Drag-and-Drop Support:** Drag `.zip` archives or uncompressed folders directly into the window to assign them to your active profile.
* **Conflict Detection:** Visual indicators highlight overlapping or duplicated active mod files.
* **Direct Steam Launching:** Option to launch NASCAR Heat 5 directly through Steam immediately after deployment.

---

## Installation

1. Download the latest release from the **Releases** tab.
2. Extract the archive to any directory on your system.
3. Launch `NH5ModManager.exe`.

---

## Usage Guide

1. **Set Game Folder:** Click **Browse** and select your root `NASCAR Heat 5` directory (e.g., `C:\Program Files (x86)\Steam\steamapps\common\NASCAR Heat 5`).
2. **Create / Select Profile:** Choose an existing profile from the dropdown or click **+ New Profile** to set up a new layout.
3. **Add Mods:** Drag and drop mod archives (`.zip`) or individual mod folders into the app list view to add them to the selected profile.
4. **Configure DLC Settings:** Check **Auto-Unlock DLCs** if you want the season pass check patched into your deployed assembly files.
5. **Deploy:** Click **Deploy Mods**. The manager will swap the active assets, run the DLC patch (if enabled), and prompt you to launch the game.

---

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
* **Framework:** .NET 8.0 Desktop Runtime
* **Game:** NASCAR Heat 5 (Steam)
