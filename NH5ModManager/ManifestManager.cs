using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NH5ModManager
{
    public static class ManifestManager
    {
        private static readonly string ManifestPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NH5ModManager",
            "mods.json"
        );

        public static List<ModInfo> LoadManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return new List<ModInfo>();

                string json = File.ReadAllText(ManifestPath);
                return JsonSerializer.Deserialize<List<ModInfo>>(json) ?? new List<ModInfo>();
            }
            catch
            {
                return new List<ModInfo>();
            }
        }

        public static void SaveManifest(List<ModInfo> mods)
        {
            try
            {
                string dir = Path.GetDirectoryName(ManifestPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(mods, options);
                File.WriteAllText(ManifestPath, json);
            }
            catch
            {
                // Silently handle write errors for now
            }
        }
    }
}