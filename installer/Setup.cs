// MacBar installer / uninstaller. MacBar.exe is embedded as a resource at build time.
// Installs per-user to %LOCALAPPDATA%\Programs\MacBar (no admin rights needed).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("MacBar Setup")]
[assembly: AssemblyProduct("MacBar")]
[assembly: AssemblyVersion("1.3.2.0")]
[assembly: AssemblyFileVersion("1.3.2.0")]

static class Setup
{
    const string AppName = "MacBar";
    const string Version = "1.3.2";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacBar";
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\MacBar");
    static readonly string ExePath = Path.Combine(InstallDir, "MacBar.exe");
    static readonly string UninstallerPath = Path.Combine(InstallDir, "Uninstall MacBar.exe");
    static readonly string MenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacBar");

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        bool quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/S", StringComparison.Ordinal));
        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
            return Uninstall(quiet);
        if (quiet) { Install(true, false); return 0; }
        Application.Run(new SetupForm());
        return 0;
    }

    class SetupForm : Form
    {
        readonly CheckBox startup = new CheckBox { Text = "Start MacBar when I sign in", Checked = true, AutoSize = true };
        readonly CheckBox launch = new CheckBox { Text = "Launch MacBar when setup finishes", Checked = true, AutoSize = true };
        readonly Button install = new Button { Text = "Install", Width = 100, Height = 30 };
        readonly Button cancel = new Button { Text = "Cancel", Width = 100, Height = 30 };

        public SetupForm()
        {
            Text = "MacBar " + Version + " Setup";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 290);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            var title = new Label { Text = "MacBar", Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Location = new Point(24, 18) };
            var desc = new Label
            {
                Text = "A macOS-style menu bar and dock for Windows.\n\n" +
                       "MacBar hides the Windows taskbar while it runs and brings it back when you quit. " +
                       "If anything ever goes wrong, use \"Restore Windows Taskbar\" in the Start menu.\n\n" +
                       "Installs to: " + InstallDir,
                Location = new Point(26, 64), Size = new Size(420, 120)
            };
            startup.Location = new Point(28, 190);
            launch.Location = new Point(28, 214);
            install.Location = new Point(244, 246);
            cancel.Location = new Point(352, 246);
            if (File.Exists(ExePath)) install.Text = "Update";
            AcceptButton = install; CancelButton = cancel;
            install.Click += delegate
            {
                install.Enabled = false; cancel.Enabled = false; UseWaitCursor = true;
                try
                {
                    Install(startup.Checked, launch.Checked);
                    UseWaitCursor = false;
                    MessageBox.Show(this, "MacBar was installed.\n\nYou can open it any time from the Start menu, and change options from the system menu at the top-left of the screen.", "MacBar Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                }
                catch (Exception ex)
                {
                    UseWaitCursor = false;
                    MessageBox.Show(this, "Setup failed:\n\n" + ex.Message, "MacBar Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    install.Enabled = true; cancel.Enabled = true;
                }
            };
            cancel.Click += delegate { Close(); };
            Controls.AddRange(new Control[] { title, desc, startup, launch, install, cancel });
        }
    }

    /// <summary>Stops any running copy and makes sure the Windows taskbar is back before files are touched.</summary>
    static void StopRunning()
    {
        foreach (var p in Process.GetProcessesByName("MacBar"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        if (File.Exists(ExePath))
        {
            try { var r = Process.Start(ExePath, "--restore"); r.WaitForExit(5000); } catch { }
        }
    }

    static void Install(bool startAtLogin, bool launchNow)
    {
        StopRunning();
        Directory.CreateDirectory(InstallDir);
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("MacBar.exe"))
        using (var f = File.Create(ExePath))
            res.CopyTo(f);
        string self = Assembly.GetExecutingAssembly().Location;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, UninstallerPath, true);

        Directory.CreateDirectory(MenuDir);
        Shortcut(Path.Combine(MenuDir, "MacBar.lnk"), ExePath, "", "macOS-style menu bar and dock");
        Shortcut(Path.Combine(MenuDir, "Restore Windows Taskbar.lnk"), ExePath, "--restore", "Bring back the normal Windows taskbar (use after quitting MacBar if it is still hidden)");
        Shortcut(Path.Combine(MenuDir, "Uninstall MacBar.lnk"), UninstallerPath, "/uninstall", "Remove MacBar");

        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startAtLogin) k.SetValue(AppName, "\"" + ExePath + "\"");
            else k.DeleteValue(AppName, false);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "MacBar");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "MacBar");
            k.SetValue("DisplayIcon", ExePath);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", "\"" + UninstallerPath + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + UninstallerPath + "\" /uninstall /quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024) + 200, RegistryValueKind.DWord);
        }
        if (launchNow) Process.Start(ExePath);
    }

    static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove MacBar and restore the Windows taskbar?", "Uninstall MacBar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return 1;
        StopRunning();
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(AppName, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { if (Directory.Exists(MenuDir)) Directory.Delete(MenuDir, true); } catch { }
        try { File.Delete(ExePath); } catch { }

        bool removeSettings = !quiet && MessageBox.Show("Also delete your MacBar settings (pinned apps, weather location)?", "Uninstall MacBar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        if (removeSettings)
        {
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacBar"), true); } catch { }
        }

        // The uninstaller can't delete itself while running; let cmd remove it (and the folder) a moment later.
        Process.Start(new ProcessStartInfo("cmd.exe",
            "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + UninstallerPath + "\" & rmdir \"" + InstallDir + "\"")
        { CreateNoWindow = true, UseShellExecute = false });
        if (!quiet) MessageBox.Show("MacBar was removed.", "Uninstall MacBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void Shortcut(string lnk, string target, string args, string description)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { ExePath + ",0" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        Marshal.ReleaseComObject(sc);
        Marshal.ReleaseComObject(sh);
    }
}
