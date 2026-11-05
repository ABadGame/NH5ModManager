using System.Text.Json;

namespace NH5ModManager;

// The live save stays where NASCAR Heat 5 expects it. Inactive profile saves are
// parked inside that mod profile folder (Profiles/<name>/Save) so each profile
// keeps its own progress next to its mods. Original + state.json live under
// Profiles/.SaveStore. Switches use directory renames on the same volume.
public sealed class SaveProfileManager
{
    public const string ProfileSaveFolderName = "Save";
    private const string StoreFolderName = ".SaveStore";

    private readonly string live;
    private readonly string profilesRoot;
    private readonly string store;
    private string StatePath => Path.Combine(store, "state.json");

    public SaveProfileManager(string saveDirectory, string profilesDirectory)
    {
        live = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveDirectory));
        _ = Path.GetDirectoryName(live)
            ?? throw new ArgumentException("Select the game's save folder, not a drive root.");
        profilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesDirectory));
        if (string.IsNullOrWhiteSpace(Path.GetFileName(profilesRoot)))
            throw new ArgumentException("Profiles directory cannot be a drive root.");
        store = Path.Combine(profilesRoot, StoreFolderName);
    }

    public sealed class SaveState
    {
        public int Version { get; set; } = 3;
        public string ActiveSlot { get; set; } = "Original";
        public string ActiveProfile { get; set; } = "Original";
        public Dictionary<string, string> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public PendingSwitch? Pending { get; set; }
    }

    public sealed class PendingSwitch
    {
        public string TargetSlot { get; set; } = "";

        // Old state files did not contain this property. The initializer keeps
        // their original behaviour during recovery.
        public bool HadOutgoingSave { get; set; } = true;
    }

    private void ValidateSlotId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id is "." or ".."
            || Path.IsPathRooted(id)
            || !string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.Equals("state.json", StringComparison.OrdinalIgnoreCase)
            || id.Equals("state.json.tmp", StringComparison.OrdinalIgnoreCase)
            || id.Equals(StoreFolderName, StringComparison.OrdinalIgnoreCase)
            || id.Equals(ProfileSaveFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Invalid save profile identifier. Save files have not been changed.");
        }
    }

    private string Slot(string id)
    {
        ValidateSlotId(id);
        if (id.Equals("Original", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(store, "Original");

        string path = Path.GetFullPath(Path.Combine(profilesRoot, id, ProfileSaveFolderName));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesRoot)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid save profile path. Save files have not been changed.");
        return path;
    }

    private static string? LegacyExternalStoreFor(string livePath)
    {
        string? parent = Path.GetDirectoryName(livePath);
        if (parent == null) return null;
        return Path.Combine(parent, "." + Path.GetFileName(livePath) + ".NH5ModManager");
    }

    private SaveState Load()
    {
        Directory.CreateDirectory(store);
        MigrateLegacyExternalStore();

        var state = File.Exists(StatePath)
            ? JsonSerializer.Deserialize<SaveState>(File.ReadAllText(StatePath))
                ?? throw new IOException("The save profile index is empty.")
            : new SaveState();

        state.Profiles = new Dictionary<string, string>(state.Profiles, StringComparer.OrdinalIgnoreCase);
        Slot(state.ActiveSlot);
        var usedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.Profiles)
        {
            Slot(pair.Value);
            if (pair.Value.Equals("Original", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Profile '{pair.Key}' incorrectly points to the Original backup.");
            if (!usedSlots.Add(pair.Value))
                throw new IOException("Two profiles point to the same save folder. Save files have not been changed.");
        }
        if (state.Pending != null) Slot(state.Pending.TargetSlot);
        SyncActiveProfile(state);
        return state;
    }

    private void SyncActiveProfile(SaveState state)
    {
        if (state.ActiveSlot.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            state.ActiveProfile = "Original";
            return;
        }

        string? name = state.Profiles.FirstOrDefault(pair =>
            pair.Value.Equals(state.ActiveSlot, StringComparison.OrdinalIgnoreCase)).Key;
        state.ActiveProfile = string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    private void Write(SaveState state)
    {
        state.Version = 3;
        SyncActiveProfile(state);
        Directory.CreateDirectory(store);
        string temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, StatePath, true);
    }

    private static bool HasSaveData(string path)
    {
        return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    }

    // Merges every file and subfolder from source into target. When the same
    // relative path exists on both sides the newer write time wins; the losing
    // file is deleted, so the source is emptied of everything merged and its
    // wrapper folder can be tidied away afterwards. Used when a legacy save
    // slot has to be folded into the plain profile slot that already holds a
    // parked save.
    private static void MergeDirectoryTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            string targetFile = Path.Combine(target, Path.GetFileName(file));
            if (Directory.Exists(targetFile))
                throw new IOException(
                    $"Cannot merge save folders: '{targetFile}' is a folder on one side and a file on the other. Save files have not been changed.");
            if (File.Exists(targetFile))
            {
                // Newer wins; the outdated copy (source or target) is removed.
                if (File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(targetFile))
                    File.Copy(file, targetFile, overwrite: true);
                File.Delete(file);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Move(file, targetFile);
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            string targetDir = Path.Combine(target, Path.GetFileName(dir));
            if (File.Exists(targetDir))
                throw new IOException(
                    $"Cannot merge save folders: '{targetDir}' is a folder on one side and a file on the other. Save files have not been changed.");
            MergeDirectoryTree(dir, targetDir);
        }
        // The source is now empty of everything that merged; delete stray
        // leftovers (empty directories) so the wrapper can be removed.
        foreach (string entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(entry)) File.Delete(entry);
            else Directory.Delete(entry, recursive: true);
        }
    }

    private static void RejectLinks(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Save folders cannot be symbolic links or junctions.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The save folder contains a symbolic link or junction.");
            if (Directory.Exists(entry)) RejectLinks(entry);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string CleanProfileName(string profile)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string clean = new(profile.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        clean = clean.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(clean)) clean = "Profile";
        if (clean.Equals(StoreFolderName, StringComparison.OrdinalIgnoreCase)
            || clean.Equals(ProfileSaveFolderName, StringComparison.OrdinalIgnoreCase)
            || clean.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            clean = "Profile";
        }
        if (clean.Length > 80) clean = clean[..80].TrimEnd();
        return clean;
    }

    private static string StripLegacySavePrefix(string slotId)
    {
        const string prefix = "Save - ";
        if (slotId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = slotId[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return slotId;
    }

    private string NewReadableSlotId(string profile, SaveState state, string? ignoreId = null, string? anticipatedPath = null)
    {
        string baseName = CleanProfileName(profile);
        string candidate = baseName;
        int suffix = 2;
        var used = new HashSet<string>(
            state.Profiles.Values.Where(id => !id.Equals(ignoreId, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        while (used.Contains(candidate)
               || candidate.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{baseName} ({suffix++})";
            ValidateSlotId(candidate);
        }

        // The plain name is always preferred. An existing Profiles/<name>/Save
        // is NOT a collision: that is this profile's own parked save (or files
        // imported next to its folder). Slot paths are guarded from real
        // conflicts by the 'used' set, because Load() rejects two profiles
        // pointing at the same save folder. Bumping to "Name (2)" here used to
        // create duplicate sibling folders alongside every profile folder.
        return candidate;
    }

    private void EnsureSlotParent(string slotPath)
    {
        string? parent = Path.GetDirectoryName(slotPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private void MoveToSlot(string source, string slotPath)
    {
        EnsureSlotParent(slotPath);
        MoveDirectoryTree(source, slotPath);
    }

    // After a slot relocation the old wrapper folder (e.g. "Name (2)") is empty.
    // Delete it when it holds nothing anymore so the Profiles area stays clean.
    // Never touches the store root, the Profiles root, or a live profile folder.
    private void TryDeleteEmptiedSlotParent(string slotPath)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(slotPath)));
        if (string.IsNullOrEmpty(parent)) return;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (full.Equals(store, StringComparison.OrdinalIgnoreCase)) return;
        if (full.Equals(profilesRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetFileName(full).StartsWith(".", StringComparison.Ordinal)) return;
        try
        {
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full, recursive: false);
        }
        catch
        {
            // Cosmetic cleanup only; never fail a migration over it.
        }
    }

    // Directory renames only work on one volume. The game save lives under the
    // user profile (usually C:) while Profiles — and every parked save slot —
    // live wherever the manager itself is installed (e.g. D:). Switching
    // therefore has to fall back to copy + delete when the sides differ.
    private static void MoveDirectoryTree(string source, string target)
    {
        string? sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        string? targetRoot = Path.GetPathRoot(Path.GetFullPath(target));
        bool sameVolume = !string.IsNullOrEmpty(sourceRoot)
            && sourceRoot.Equals(targetRoot, StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            Directory.Move(source, target);
            return;
        }

        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("The move destination already exists. Nothing was overwritten: " + target);

        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        try
        {
            CopyDirectory(source, target);
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            // Never leave a half-copied destination behind; the source is untouched.
            try { Directory.Delete(target, recursive: true); } catch { }
            throw;
        }
    }

    private void MigrateLegacyExternalStore()
    {
        if (File.Exists(StatePath)) return;
        string? legacyStore = LegacyExternalStoreFor(live);
        if (legacyStore == null || !Directory.Exists(legacyStore)) return;

        string legacyStatePath = Path.Combine(legacyStore, "state.json");
        if (!File.Exists(legacyStatePath)) return;

        var state = JsonSerializer.Deserialize<SaveState>(File.ReadAllText(legacyStatePath))
            ?? throw new IOException("The legacy save profile index is empty.");
        state.Profiles = new Dictionary<string, string>(state.Profiles, StringComparer.OrdinalIgnoreCase);

        string MapLegacyId(string oldId, string? profileName)
        {
            if (oldId.Equals("Original", StringComparison.OrdinalIgnoreCase)) return "Original";
            if (profileName != null) return CleanProfileName(profileName);
            if (Guid.TryParseExact(oldId, "N", out _)) return CleanProfileName("Profile");
            return CleanProfileName(StripLegacySavePrefix(oldId));
        }

        string? activeProfileName = state.Profiles.FirstOrDefault(p =>
            p.Value.Equals(state.ActiveSlot, StringComparison.OrdinalIgnoreCase)).Key;
        string? pendingProfileName = state.Pending == null
            ? null
            : state.Profiles.FirstOrDefault(p =>
                p.Value.Equals(state.Pending.TargetSlot, StringComparison.OrdinalIgnoreCase)).Key;

        var remapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.Profiles.ToArray())
        {
            string newId = MapLegacyId(pair.Value, pair.Key);
            string uniqueId = newId;
            int suffix = 2;
            while (!used.Add(uniqueId))
                uniqueId = $"{newId} ({suffix++})";

            string oldPath = Path.Combine(legacyStore, pair.Value);
            string newPath = Slot(uniqueId);
            if (Directory.Exists(oldPath))
            {
                if (Directory.Exists(newPath) || File.Exists(newPath))
                    throw new IOException($"Cannot migrate '{pair.Key}' because '{newPath}' already exists.");
                MoveToSlot(oldPath, newPath);
            }
            remapped[pair.Key] = uniqueId;
        }

        string oldOriginal = Path.Combine(legacyStore, "Original");
        string newOriginal = Slot("Original");
        if (Directory.Exists(oldOriginal))
        {
            if (Directory.Exists(newOriginal) || File.Exists(newOriginal))
                throw new IOException("Cannot migrate Original because the destination already exists.");
            MoveToSlot(oldOriginal, newOriginal);
        }

        string newActive = state.ActiveSlot.Equals("Original", StringComparison.OrdinalIgnoreCase)
            ? "Original"
            : remapped.GetValueOrDefault(activeProfileName ?? "", MapLegacyId(state.ActiveSlot, activeProfileName));
        if (state.Pending != null)
        {
            state.Pending.TargetSlot = pendingProfileName != null && remapped.TryGetValue(pendingProfileName, out string? pendingId)
                ? pendingId
                : MapLegacyId(state.Pending.TargetSlot, pendingProfileName);
        }

        state.Profiles = remapped;
        state.ActiveSlot = newActive;
        Write(state);

        try
        {
            string migrated = legacyStore + ".migrated";
            if (!Directory.Exists(migrated)) MoveDirectoryTree(legacyStore, migrated);
        }
        catch
        {
            // Migration of saves already succeeded; leaving the empty legacy folder is fine.
        }
    }

    private void RepairActiveLocation(SaveState state)
    {
        string archivedActive = Slot(state.ActiveSlot);
        if (File.Exists(archivedActive))
            throw new IOException("The active save backup path is a file. Save files have not been changed.");
        if (!Directory.Exists(archivedActive)) return;

        if (HasSaveData(live))
            throw new IOException($"The active profile '{state.ActiveProfile}' exists both live and in its backup folder. Nothing was overwritten.");

        if (Directory.Exists(live)) Directory.Delete(live, recursive: false);
        MoveDirectoryTree(archivedActive, live);
    }

    private void MigrateReadableSlots(SaveState state)
    {
        foreach (var pair in state.Profiles.ToArray())
        {
            string profile = pair.Key;
            string oldId = pair.Value;
            // "(n)" suffixed slots were only needed when the save could not live
            // next to the profile's own folder; they are legacy layout now too.
            bool isLegacyName = Guid.TryParseExact(oldId, "N", out _)
                || oldId.StartsWith("Save - ", StringComparison.OrdinalIgnoreCase)
                || !oldId.Equals(CleanProfileName(profile), StringComparison.OrdinalIgnoreCase);
            if (!isLegacyName) continue;

            string newId = NewReadableSlotId(profile, state, oldId);
            string? legacyStore = LegacyExternalStoreFor(live);
            string oldPath = Slot(oldId);
            if (!Directory.Exists(oldPath) && legacyStore != null)
            {
                string legacyPath = Path.Combine(legacyStore, oldId);
                if (Directory.Exists(legacyPath)) oldPath = legacyPath;
            }

            // Older builds parked "Save - Name" folders inside Profiles/.SaveStore.
            string storeLegacyPath = Path.Combine(store, oldId);
            if (!Directory.Exists(oldPath) && Directory.Exists(storeLegacyPath))
                oldPath = storeLegacyPath;

            string newPath = Slot(newId);
            bool moved = false;
            string oldActive = state.ActiveSlot;

            try
            {
                if (File.Exists(oldPath))
                    throw new IOException($"Legacy save slot '{oldId}' is a file instead of a folder.");
                if (Directory.Exists(oldPath)
                    && !oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(newPath))
                        throw new IOException($"Cannot migrate '{profile}' because '{newId}' already exists.");
                    if (Directory.Exists(newPath))
                    {
                        // The plain slot path already holds this profile's own
                        // parked save. Merge the legacy slot's save into it
                        // (newer file wins) and retire the legacy wrapper, so
                        // no sibling "(n)" folder survives the migration.
                        MergeDirectoryTree(oldPath, newPath);
                        moved = true;
                    }
                    else
                    {
                        MoveToSlot(oldPath, newPath);
                        moved = true;
                        // The old "Name (2)" wrapper folder is empty now;
                        // tidy it up so it stops showing in the Profiles area.
                    }
                    TryDeleteEmptiedSlotParent(oldPath);
                }

                state.Profiles[profile] = newId;
                if (state.ActiveSlot.Equals(oldId, StringComparison.OrdinalIgnoreCase))
                    state.ActiveSlot = newId;
                Write(state);
            }
            catch
            {
                state.Profiles[profile] = oldId;
                state.ActiveSlot = oldActive;
                if (moved && Directory.Exists(newPath) && !Directory.Exists(oldPath))
                    MoveDirectoryTree(newPath, oldPath);
                throw;
            }
        }
    }

    private void Rollback(SaveState state)
    {
        if (state.Pending == null) return;
        PendingSwitch pending = state.Pending;
        string previous = Slot(state.ActiveSlot);
        string target = Slot(pending.TargetSlot);

        if (Directory.Exists(previous))
        {
            if (Directory.Exists(live))
            {
                if (Directory.Exists(target))
                    throw new IOException("Both the target save and live save exist during recovery. Nothing was overwritten.");
                if (HasSaveData(live)) MoveToSlot(live, target);
                else Directory.Delete(live, recursive: false);
            }
            MoveDirectoryTree(previous, live);
        }
        else if (!pending.HadOutgoingSave)
        {
            // There was no outgoing save to restore. If the target was already
            // moved live, put it back; otherwise it is still in its slot.
            if (!Directory.Exists(target) && Directory.Exists(live))
            {
                if (HasSaveData(live)) MoveToSlot(live, target);
                else Directory.Delete(live, recursive: false);
            }
            Directory.CreateDirectory(live);
        }

        state.Pending = null;
        Write(state);
    }

    private bool IsInitialDefaultAdoption(SaveState state, string profile, bool isNewProfile)
    {
        return isNewProfile
            && state.Profiles.Count == 1
            && state.ActiveSlot.Equals("Original", StringComparison.OrdinalIgnoreCase)
            && (profile.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || profile.Equals("Vanilla", StringComparison.OrdinalIgnoreCase));
    }

    private void AdoptInitialDefault(SaveState state, string profile, string targetId, bool isNewProfile, Action deployAssets)
    {
        string original = Slot("Original");
        bool createdOriginal = false;
        string oldSlot = state.ActiveSlot;
        string oldProfile = state.ActiveProfile;

        try
        {
            Directory.CreateDirectory(live);
            if (HasSaveData(live))
            {
                if (Directory.Exists(original) || File.Exists(original))
                    throw new IOException("The Original save backup already exists. Nothing was overwritten.");
                EnsureSlotParent(original);
                CopyDirectory(live, original);
                createdOriginal = true;
            }

            state.ActiveSlot = targetId;
            state.ActiveProfile = profile;
            Write(state);
            deployAssets();
        }
        catch
        {
            state.ActiveSlot = oldSlot;
            state.ActiveProfile = oldProfile;
            if (isNewProfile) state.Profiles.Remove(profile);
            try { Write(state); } catch { }
            if (createdOriginal && Directory.Exists(original) && Directory.Exists(live))
            {
                try { Directory.Delete(original, recursive: true); } catch { }
            }
            throw;
        }
    }

    public void Deploy(string profile, Action deployAssets)
    {
        if (string.IsNullOrWhiteSpace(profile))
            throw new IOException("Choose a profile before deployment.");
        if (File.Exists(live))
            throw new IOException("The configured save folder is a file, not a folder.");

        RejectLinks(live);
        RejectLinks(store);
        var state = Load();
        foreach (string slotId in state.Profiles.Values)
            RejectLinks(Slot(slotId));
        RejectLinks(Slot(state.ActiveSlot));

        // A terminated manager leaves its journal behind. Restore the outgoing
        // save before retrying deployment; no save is silently discarded.
        Rollback(state);
        RepairActiveLocation(state);
        MigrateReadableSlots(state);

        bool isNewProfile = !state.Profiles.TryGetValue(profile, out string? targetId);
        if (isNewProfile)
        {
            targetId = NewReadableSlotId(profile, state);
            state.Profiles.Add(profile, targetId);
        }

        if (IsInitialDefaultAdoption(state, profile, isNewProfile))
        {
            AdoptInitialDefault(state, profile, targetId!, isNewProfile, deployAssets);
            return;
        }

        if (isNewProfile) Write(state);

        if (state.ActiveSlot.Equals(targetId, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.ActiveProfile.Equals(profile, StringComparison.Ordinal))
            {
                state.ActiveProfile = profile;
                Write(state);
            }
            deployAssets();
            return;
        }

        string previous = Slot(state.ActiveSlot);
        string target = Slot(targetId!);
        if (Directory.Exists(previous) || File.Exists(previous))
            throw new IOException("The outgoing save backup already exists. Nothing was overwritten; inspect state.json before retrying.");
        if (File.Exists(target))
            throw new IOException("The target save backup is a file. Nothing was overwritten.");

        bool hadOutgoingSave = HasSaveData(live);
        Directory.CreateDirectory(live);
        state.Pending = new PendingSwitch { TargetSlot = targetId!, HadOutgoingSave = hadOutgoingSave };
        Write(state);

        try
        {
            if (hadOutgoingSave) MoveToSlot(live, previous);
            else Directory.Delete(live, recursive: false);

            if (Directory.Exists(target)) MoveDirectoryTree(target, live);
            else Directory.CreateDirectory(live); // The game creates its own new save.

            deployAssets();

            PendingSwitch pending = state.Pending;
            string oldId = state.ActiveSlot;
            string oldProfile = state.ActiveProfile;
            state.Pending = null;
            state.ActiveSlot = targetId!;
            state.ActiveProfile = profile;
            try { Write(state); }
            catch
            {
                state.ActiveSlot = oldId;
                state.ActiveProfile = oldProfile;
                state.Pending = pending;
                throw;
            }
        }
        catch (Exception failure)
        {
            try { Rollback(state); }
            catch (Exception recovery)
            {
                throw new IOException($"Deployment failed: {failure.Message}\nSave recovery needs attention: {recovery.Message}\nBackups: {store}", failure);
            }
            throw;
        }
    }

    // Profile folder rename is handled by the UI (so mods + Save move together).
    // This only rewrites the save index to the new profile name.
    public void RenameProfile(string previousName, string newName)
    {
        RejectLinks(store);
        var state = Load();
        foreach (string slotId in state.Profiles.Values)
            RejectLinks(Slot(slotId));
        if (state.Pending != null)
            throw new IOException("Retry deployment to recover the interrupted save switch before renaming profiles.");

        RepairActiveLocation(state);
        MigrateReadableSlots(state);
        if (!state.Profiles.TryGetValue(previousName, out string? oldId)) return;
        if (state.Profiles.ContainsKey(newName))
            throw new IOException("That name already has saved progress. Choose a different profile name.");

        // If the UI already renamed Profiles/old -> Profiles/new, the save
        // folder moved with it and now lives at the new path; prefer that
        // layout over the legacy slot path so renames stay conflict-free.
        string renamedWithProfile = Path.Combine(profilesRoot, newName, ProfileSaveFolderName);
        string newId = NewReadableSlotId(newName, state, oldId, renamedWithProfile);
        string oldPath = Slot(oldId);
        string newPath = Slot(newId);
        bool moved = false;
        string oldActive = state.ActiveSlot;
        string oldActiveProfile = state.ActiveProfile;

        try
        {
            if (!oldId.Equals(newId, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(renamedWithProfile)
                    && !renamedWithProfile.Equals(newPath, StringComparison.OrdinalIgnoreCase)
                    && !Directory.Exists(newPath) && !File.Exists(newPath))
                {
                    MoveToSlot(renamedWithProfile, newPath);
                    moved = true;
                }
                else if (Directory.Exists(oldPath))
                {
                    if (File.Exists(newPath))
                        throw new IOException("The renamed save folder already exists. Nothing was overwritten.");
                    if (Directory.Exists(newPath))
                    {
                        // The target profile folder already parks this
                        // profile's own save; merge instead of failing.
                        MergeDirectoryTree(oldPath, newPath);
                        moved = true;
                    }
                    else
                    {
                        MoveToSlot(oldPath, newPath);
                        moved = true;
                    }
                    TryDeleteEmptiedSlotParent(oldPath);
                }
            }

            state.Profiles.Remove(previousName);
            state.Profiles.Add(newName, newId);
            if (state.ActiveSlot.Equals(oldId, StringComparison.OrdinalIgnoreCase))
                state.ActiveSlot = newId;
            if (state.ActiveProfile.Equals(previousName, StringComparison.OrdinalIgnoreCase)
                || state.ActiveSlot.Equals(newId, StringComparison.OrdinalIgnoreCase))
            {
                state.ActiveProfile = newName;
            }
            Write(state);
        }
        catch
        {
            state.Profiles.Remove(newName);
            state.Profiles[previousName] = oldId;
            state.ActiveSlot = oldActive;
            state.ActiveProfile = oldActiveProfile;
            if (moved && Directory.Exists(newPath) && !Directory.Exists(oldPath))
                MoveDirectoryTree(newPath, oldPath);
            throw;
        }
    }
}
