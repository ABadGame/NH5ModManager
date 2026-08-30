using System;

namespace NH5ModManager
{
    public class ModInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string ModType { get; set; } = "Plugin"; // "Plugin", "Texture", "Asset"
        public DateTime InstalledDate { get; set; } = DateTime.Now;
    }
}