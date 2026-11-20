using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        public MusicBeeApiInterface mbApiInterface;
        private readonly PluginInfo about = new PluginInfo();
        private SpotySyncBridge bridge;
        private ToolStripItem menuItem;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "SpotySync Bridge";
            about.Description = "Safe local MusicBee adapter for the portable SpotySync application.";
            about.Author = "SpotySync contributors";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 4;
            about.VersionMinor = 8;
            about.Revision = 0;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.StartupOnly;
            about.ConfigurationPanelHeight = 0;

            menuItem = mbApiInterface.MB_AddMenuItem("mnuTools/SpotySync", "", OnMenuClick);
            try
            {
                bridge = new SpotySyncBridge(mbApiInterface, ResolveMusicBeeUiInvoker, Trace);
                bridge.Start();
            }
            catch (Exception ex) { Trace("Bridge startup failed: " + ex); }
            return about;
        }

        public bool Configure(IntPtr panelHandle) { return false; }
        public void SaveSettings() { }
        public void Uninstall() { }
        public void ReceiveNotification(string sourceFileUrl, NotificationType type) { }
        public string[] GetProviders() { return new string[0]; }
        public string RetrieveLyrics(string sourceFileUrl, string artist, string trackTitle, string album, bool synchronisedPreferred, string provider) { return null; }
        public string RetrieveArtwork(string sourceFileUrl, string albumArtist, string album, string provider) { return null; }

        public void Close(PluginCloseReason reason)
        {
            try { if (bridge != null) bridge.Dispose(); } catch { }
            bridge = null;
        }

        private Control ResolveMusicBeeUiInvoker()
        {
            try
            {
                ToolStrip owner = menuItem == null ? null : menuItem.Owner ?? menuItem.GetCurrentParent();
                if (owner != null && !owner.IsDisposed && owner.IsHandleCreated) return owner;
            }
            catch { }
            try
            {
                IntPtr handle = mbApiInterface.MB_GetWindowHandle();
                Control control = handle == IntPtr.Zero ? null : Control.FromHandle(handle);
                if (control != null && !control.IsDisposed) return control;
            }
            catch { }
            throw new InvalidOperationException("MusicBee's UI dispatcher is unavailable.");
        }

        private void OnMenuClick(object sender, EventArgs args)
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService");
            string pointer = Path.Combine(data, "CompanionPath.txt");
            try
            {
                string executable = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : string.Empty;
                if (File.Exists(executable))
                {
                    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                    return;
                }
            }
            catch { }
            MessageBox.Show("Start SpotySync.exe once so MusicBee can remember its portable location.\r\n\r\nBridge status: listening on 127.0.0.1:51983.", "SpotySync", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Trace(string message)
        {
            try { mbApiInterface.MB_Trace("SpotySync: " + message); } catch { }
        }
    }
}
