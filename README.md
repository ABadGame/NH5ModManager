# NASCAR Heat 5 Mod Manager

A lightweight, profile-based mod management tool for **NASCAR Heat 5**. Built with C# and WinForms, this utility allows you to organize mod profiles, stage custom game files, bypass DLC checks, and launch the game cleanly via Steam.

---

## Features

* **Profile Management:** Create and switch between distinct mod profiles without corrupting your base game files.
* **Manifest-Based Deployment:** Safely deploys loose asset overrides (`NASCARHeat5_Data`) and tracks installed files to prevent residual mod clutter.
* **DLC Pass Unlocker:** Integrated `Mono.Cecil` assembly patcher that safely overrides `Assembly-CSharp.dll` to unlock season pass content (includes automatic `.bak` backup creation).
* **BepInEx Plugin Staging:** Automatically syncs and stages target `.dll` plugins into `BepInEx/plugins`.
* **Direct Steam Integration:** One-click launch via Steam protocol (`steam://rungameid/1258980`) with executable fallback.
* **Drag-and-Drop Installation:** Drag loose files directly into the manager for instant staging.

---

## Installation

1. Download the latest release from the **Releases** tab.
2. Extract `NH5ModManager.exe` into a folder of your choice (or directly into your NASCAR Heat 5 root directory).
3. Run `NH5ModManager.exe`.

---

## Usage

### 1. Setting Up the Game Directory
Upon first launch, click **Browse** and select your NASCAR Heat 5 installation directory (typically: `C:\Program Files (x86)\Steam\steamapps\common\NASCAR Heat 5`).

### 2. Creating & Managing Profiles
* Click **+ New Profile** to set up a target mod loadout.
* Drag loose mod files (e.g., sound banks, modified levels, textures) directly into the installed mods view.

### 3. Unlocking Season Pass Content
* Click **Unlock DLC Pass** in the top-right toolbar.
* The manager will back up your original `Assembly-CSharp.dll` to `Assembly-CSharp.dll.bak` and patch the DLC verification check in memory.

### 4. Deploying & Playing
* Select your desired profile from the drop-down menu.
* Ensure your target mods are checked in the list view.
* Click **Deploy Mods** to apply the overrides.
* Click **Launch Game** to start NASCAR Heat 5 via Steam.

---

## Troubleshooting & Safety

* **Restoring Vanilla Assembly:** If game updates break your patched assembly, navigate to `NASCARHeat5_Data/Managed/`, delete `Assembly-CSharp.dll`, and rename `Assembly-CSharp.dll.bak` back to `Assembly-CSharp.dll`.
* **Game Running Warning:** Always close NASCAR Heat 5 before deploying mods or switching profiles to avoid file-lock errors.

---

## Built With

* **.NET / C#** - Desktop Application Framework
* **Mono.Cecil** - IL Assembly Inspection & Modification
