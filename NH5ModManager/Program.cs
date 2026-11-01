namespace NH5ModManager
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            using var instanceLock = new System.Threading.Mutex(true, "NH5ModManager.SaveProfiles", out bool firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("NH5 Mod Manager is already running. Use the existing window to switch profiles.");
                return;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}