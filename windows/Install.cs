using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NrgToMp3Win
{
    /// <summary>The app installs itself: it copies its .exe to the user's Programs folder, adds a Start-menu
    /// (and optionally desktop) shortcut, and an entry in Settings → Apps for uninstalling. No admin rights needed.</summary>
    static class Install
    {
        const string AppName = "NRG to MP3";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NRG to MP3";
        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        static readonly string InstalledExe = Path.Combine(Folder, AppName + ".exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        static string Me => Environment.ProcessPath;
        public static bool RunningInstalled => string.Equals(Path.GetFullPath(Me), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
        public static bool IsInstalled => File.Exists(InstalledExe);

        /// <summary>Copies this .exe into place (replacing an older installed copy) and adds the shortcuts.</summary>
        public static void Run(IWin32Window owner)
        {
            var answer = MessageBox.Show(owner,
                (IsInstalled ? "Replace the installed copy of NRG to MP3 with this one?\n\n" : "Install NRG to MP3 on this PC?\n\n")
                + "It's copied to your Programs folder and added to the Start menu, and it can be removed again in Settings → Apps.\n\n"
                + "Also put a shortcut on the desktop?",
                "Install", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            try
            {
                foreach (var p in Process.GetProcessesByName(AppName))
                    if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The installed copy is open. Close it first, then try again.");
                Directory.CreateDirectory(Folder);
                File.Copy(Me, InstalledExe, true);
                Shortcut(StartMenuLink);
                if (answer == DialogResult.Yes) Shortcut(DesktopLink);
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", AppName);
                    key.SetValue("DisplayVersion", $"1.0 (build {MainForm.Build})");
                    key.SetValue("Publisher", "George");
                    key.SetValue("DisplayIcon", InstalledExe);
                    key.SetValue("InstallLocation", Folder);
                    key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                    key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
                    key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024));
                }
                if (MessageBox.Show(owner, "Installed. You'll find NRG to MP3 in the Start menu.\n\nStart the installed copy now (and close this one)?", "Install",
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
                    Application.Exit();
                }
            }
            catch (Exception ex) { MessageBox.Show(owner, "Couldn't install: " + ex.Message, "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static void Shortcut(string link)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            dynamic s = shell.CreateShortcut(link);
            s.TargetPath = InstalledExe;
            s.WorkingDirectory = Folder;
            s.Description = "Convert, play, copy and burn CDs and disc images";
            s.IconLocation = InstalledExe + ",0";
            s.Save();
        }

        /// <summary>"NRG to MP3.exe --uninstall", run from Settings → Apps. Settings (folders, colour) are kept.</summary>
        public static void Uninstall()
        {
            if (MessageBox.Show("Remove NRG to MP3 from this PC?\n\nYour music, disc images and playlists are not touched.", "Uninstall",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            foreach (var link in new[] { StartMenuLink, DesktopLink }) try { File.Delete(link); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // a running .exe can't delete itself: remove the folder a moment after this closes
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 > nul & rmdir /s /q \"{Folder}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
            }
            catch { }
            MessageBox.Show("NRG to MP3 has been removed.", "Uninstall");
        }
    }
}
