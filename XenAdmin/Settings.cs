﻿/* Copyright (c) Citrix Systems, Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using XenAdmin.Core;
using XenAdmin.Dialogs;
using XenAdmin.Dialogs.RestoreSession;
using XenAdmin.Network;
using System.Configuration;
using XenCenterLib;
using System.Linq;


namespace XenAdmin
{
    public static class Settings
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Used in place of the username, to indicate that a record is for a VM's VNC connection, rather
        /// than a connection to a server.
        /// </summary>
        private const string VNC_INDICATOR = "$VNC$";

        private const char SEPARATOR = '\x202f'; // narrow non-breaking space.
        /// <summary>
        /// Anybody who chooses this for their password deserves a crash:
        /// MODIFIER LETTER REVERSED GLOTTAL STOP, NKO SYMBOL GBAKURUNEN, CHAM PUNCTUATION DOUBLE DANDA, BOPOMOFO LETTER INNN
        /// </summary>
        private const string NO_PASSWORD = "\x02c1\x07f7\xaa5e\x31b3";
        private const string DISCONNECTED = "disconnected";
        private const string CONNECTED = "connected";

        /// <summary>
        /// vm_uuid -> password.  The password may be null if it's been deleted.
        /// </summary>
        private static Dictionary<string, string> VNCPasswords = new Dictionary<string, string>();

        /// <summary>
        /// MSDN info regarding the path to the user.config file is somewhat confusing. It turns out
        /// it is not in Application.UserAppDataPath as stated on http://msdn.microsoft.com/en-us/library/8eyb2ct1.aspx
        /// but rather in a location like BasePath\CompanyName\AppName_EvidenceType_EvidenceHash\Version as described on
        /// http://stackoverflow.com/questions/1075204/when-using-a-settings-settings-file-in-net-where-is-the-config-actually-stored
        /// and on http://msdn.microsoft.com/en-us/library/ms379611.aspx.
        /// Trying to retrieve the filename at the places where this method's caller caught the ConfigurationErrorsException
        /// returns null, so this one has to be called.
        /// </summary>
        /// <returns></returns>
        public static string GetUserConfigPath()
        {
            try
            {
                Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoaming);
                return config.FilePath;
            }
            catch (ConfigurationErrorsException exc)
            {
                return exc.Filename;
            }
        }

        public static void RestoreSession()
        {
            if (Program.RunInAutomatedTestMode)
            {
                Program.SkipSessionSave = true;
                Properties.Settings.Default.SaveSession = false;
                return;
            }

            if (!Registry.AllowCredentialSave)
            {
                Program.SkipSessionSave = true;
                Properties.Settings.Default.SaveSession = false;
                Properties.Settings.Default.RequirePass = false;
                RestoreSessionWithPassword(null, false);
                return;
            }

            Program.MasterPassword = null;

            if (Properties.Settings.Default.SaveSession || Properties.Settings.Default.RequirePass)
            {
                if (Properties.Settings.Default.ServerList != null && Properties.Settings.Default.ServerList.Length > 0 ||
                    Properties.Settings.Default.ServerAddressList != null && Properties.Settings.Default.ServerAddressList.Length > 0)
                {
                    if (!Properties.Settings.Default.RequirePass)
                    {
                        Program.SkipSessionSave = true;
                        RestoreSessionWithPassword(null, true);
                        return;
                    }

                    // close the splash screen before opening the password dialog (the main window closes the
                    // splash screen after this method is called, however, this cannot happen because the dialog
                    // is launched modally blocking the UI thread and is additionally behind the splash screen)
                    Program.CloseSplash();

                    string password = null;
                    do
                    {
                        using (var dialog = new LoadSessionDialog(password != null))
                            password = dialog.ShowDialog(Program.MainWindow) == DialogResult.OK
                                ? dialog.Password
                                : null;
                    } while (password != null && !RestoreSessionWithPassword(password, true));

                    Properties.Settings.Default.SaveSession = password != null;
                    Properties.Settings.Default.RequirePass = true;
                    Program.SkipSessionSave = true;

                    if (password == null)
                        RestoreSessionWithPassword(null, false); //if the user has cancelled start a new session
                    else
                        Program.MasterPassword = EncryptionUtils.ComputeHash(password);
                }
                else
                {
                    Properties.Settings.Default.RequirePass = false;
                    Properties.Settings.Default.SaveSession = false;
                }
            }
            else
            {
                Program.SkipSessionSave = true;
                RestoreSessionWithPassword(null, false);
            }
        }

        /// <summary>
        /// Tries to restore the session list using the given password as the key.
        /// Returns true if successful, false otherwise (usually due to
        /// a decryption failure, in turn due to a wrong password).
        /// </summary>
        private static bool RestoreSessionWithPassword(string password, bool useOriginalList)
        {
            string[] encServerList;

            if (useOriginalList)
            {
                // if we are resuming without a password or have a valid password use Settings.ServerList
                encServerList = Properties.Settings.Default.ServerList ?? new string[0];
            }
            else
            {
                // user has cancelled, use the ServerAddressList (no usernames or passwords)
                encServerList = Properties.Settings.Default.ServerAddressList ?? new string[0];
            }

            string[] decryptedList = new string[encServerList.Length];
            if (!Properties.Settings.Default.RequirePass || !useOriginalList)
            {
                int idx = 0;
                try
                {
                    foreach (string encEntry in encServerList)
                    {
                        decryptedList[idx] = EncryptionUtils.Unprotect(encEntry);
                        idx++;
                    }
                }
                catch (Exception exp)
                {
                    log.Warn("Could not unprotect session information", exp);
                    return false;
                }
            }
            else
            {
                int idx = 0;
                try
                {
                    foreach (string encEntry in encServerList)
                    {
                        decryptedList[idx] = EncryptionUtils.DecryptString(encEntry, password);
                        idx++;
                    }
                }
                catch (Exception exp)
                {
                    log.Warn("Could not decrypt session information", exp);
                    // Problem decrypting -> wrong password entered...
                    return false;
                }
            }

            // Validate the decrypted entries - the session entries may 
            // have been decrypted to nonsense...
            foreach (string entry in decryptedList)
            {
                string[] entryComps = entry.Split(SEPARATOR);
                if (entryComps.Length < 3 || entryComps.Length > 7)
                {
                    log.Warn("Did not decrypt session list to a valid entry...");
                    return false;
                }
            }

            foreach (string entry in decryptedList)
            {
                // $VNC$, VM UUID, unused, password
                // username, hostname, port, password
                // username, hostname, port, password, connected
                // username, hostname, port, password, connected, friendly name
                // username, hostname, port, password, connected, friendly name, pool members

                // If the user cancels the restore dialog, we use the ServerAddressList instead:
                // hostname, port, friendly name

                string[] entryComps = entry.Split(SEPARATOR);

                int port;
                if (!int.TryParse(entryComps[2], out port))
                {
                    port = ConnectionsManager.DEFAULT_XEN_PORT;
                }

                if (entryComps[0] == VNC_INDICATOR)
                {
                    string vm_uuid = entryComps[1];
                    VNCPasswords[vm_uuid] = entryComps[3];
                }
                else if (entryComps.Length == 3)
                {
                    if (!int.TryParse(entryComps[1], out port))
                    {
                        port = ConnectionsManager.DEFAULT_XEN_PORT;
                    }
                    IXenConnection connection = new XenConnection();
                    connection.Hostname = entryComps[0];
                    connection.Port = port;
                    connection.Password = null;
                    connection.SaveDisconnected = true;
                    connection.FriendlyName = entryComps[2] != "" ? entryComps[2] : entryComps[0];

                    AddConnection(connection);
                }
                else
                {
                    IXenConnection connection = new XenConnection();
                    connection.Username = entryComps[0];
                    connection.Hostname = entryComps[1];
                    connection.Port = port;
                    // If password is NO_PASSWORD, this indicates we didn't save a password for this connection
                    if (entryComps[3] == NO_PASSWORD)
                    {
                        connection.Password = null;
                        connection.ExpectPasswordIsCorrect = false;
                    }
                    else
                    {
                        connection.Password = entryComps[3];
                    }
                    connection.SaveDisconnected = entryComps.Length > 4 && entryComps[4] == DISCONNECTED;
                    connection.FriendlyName = entryComps.Length > 5 ? entryComps[5] : entryComps[1];

                    // We save a comma-separated list of hostnames of each of the slaves.
                    // This enables us to connect to a former slave in the event of master failover while the GUI isn't running.
                    if (entryComps.Length == 7 && entryComps[6] != "")
                    {
                        connection.PoolMembers = new List<string>(entryComps[6].Split(new char[] { ',' }));
                    }
                    else
                    {
                        connection.PoolMembers = new List<string>(new string[] { connection.Hostname });
                    }

                    AddConnection(connection);
                }
            }

            return true;
        }

        private static void AddConnection(IXenConnection connection)
        {
            if (ConnectionsManager.XenConnectionsContains(connection))
                return;

            XenConnection conn = connection as XenConnection;
            conn.LastConnectionFullName =
                string.IsNullOrEmpty(connection.FriendlyName) ?
                    connection.Hostname :
                    string.Format("{0} ({1})", connection.FriendlyName, connection.Hostname);

            lock (ConnectionsManager.ConnectionsLock)
            {
                ConnectionsManager.XenConnections.Add(connection);
            }
        }

        /// <summary>
        /// If an exception is thrown while saving the list, this method will notify the user
        /// and then call Application.Exit().
        /// </summary>
        public static void SaveServerList()
        {
            Program.AssertOnEventThread();

            // Check that there is something to do...
            if (ConnectionsManager.XenConnectionsCopy.Count == 0)
            {
                // Ensure the list is empty in the serialized settings file...
                Properties.Settings.Default.ServerList = new string[0];
                Properties.Settings.Default.ServerAddressList = new string[0];
                TrySaveSettings();
                return;
            }

            if (!Program.SkipSessionSave && Registry.AllowCredentialSave)
            {
                if (Program.RunInAutomatedTestMode)
                    log.Debug("In automated test mode: not showing save session dialog");
                else
                {
                    using (var dialog = new SaveAndRestoreDialog())
                        dialog.ShowDialog(Program.MainWindow);
                    Program.SkipSessionSave = true;
                }
            }

            EncryptServerList();
            TrySaveSettings();
        }

        /// <summary>
        /// Attempts to save the settings. On catching an exception will prompt the user and close XC.
        /// </summary>
        public static void TrySaveSettings()
        {
            try
            {
                Properties.Settings.Default.Save();
            }
            catch (ConfigurationErrorsException ex)
            {
                // Show a warning to the user and exit the application.
                using (var dlg = new ErrorDialog(string.Format(Messages.MESSAGEBOX_SAVE_CORRUPTED, Settings.GetUserConfigPath()))
                    {WindowTitle = Messages.MESSAGEBOX_SAVE_CORRUPTED_TITLE})
                {
                    dlg.ShowDialog(Program.MainWindow);
                }

                log.Error("Could not save settings. Exiting application.", ex);
                Application.Exit();
            }

            if (Registry.GetBrandOverride() == "XenCenter" || BrandManager.BrandConsole == "XenCenter")
                HealthCheck.SendMetadataToHealthCheck();
        }

        private static void EncryptServerList()
        {
            List<string> encServerList = new List<string>();
            List<string> encServerAddressList = new List<string>();

            try
            {
                foreach (IXenConnection connection in ConnectionsManager.XenConnectionsCopy)
                {
                    int port = connection.Port;
                    if (port <= 0)
                        port = ConnectionsManager.DEFAULT_XEN_PORT;

                    if (Properties.Settings.Default.SaveSession)
                    {
                        encServerList.Add(EncryptCredentials(connection.Hostname, port, connection.Username, connection.Password,
                            !connection.IsConnected, connection.FriendlyName, connection.PoolMembers));
                    }

                    // Save the address, port and friendly name in case the user clicks cancel on the resume password dialog
                    string entryAddress = ProtectCredentials(connection.Hostname, port, connection.FriendlyName);
                    encServerAddressList.Add(entryAddress);
                }

                if (Properties.Settings.Default.SaveSession)
                {
                    foreach (string vm_uuid in VNCPasswords.Keys)
                    {
                        string pwd = VNCPasswords[vm_uuid];
                        if (pwd != null)
                            encServerList.Add(EncryptCredentials(vm_uuid, -1, VNC_INDICATOR, pwd, false, "", null));
                    }
                }

                if (Properties.Settings.Default.SaveSession)
                    Properties.Settings.Default.ServerList = encServerList.ToArray();
                Properties.Settings.Default.ServerAddressList = encServerAddressList.ToArray();
            }
            catch (Exception exp)
            {
                // Problem encrypting
                log.Error("Could not encrypt session.", exp);

                // Ugly, but we need to warn the user...
                if (!Program.RunInAutomatedTestMode)
                    using (var dlg = new WarningDialog(Messages.MESSAGEBOX_SESSION_SAVE_UNABLE)
                        {WindowTitle = Messages.MESSAGEBOX_SESSION_SAVE_UNABLE_TITLE})
                    {
                        dlg.ShowDialog();
                    }
            }
        }

        private static string ProtectCredentials(String serverName, int port, string friendlyName)
        {
            string entryStr = string.Join(SEPARATOR.ToString(), new string[] { serverName, port.ToString(), friendlyName });
            return EncryptionUtils.Protect(entryStr);
        }

        private static String EncryptCredentials(String serverName, int port, String username, String password, bool saveDisconnected, string friendlyName, List<string> poolMembers)
        {
            if (password == null)
            {
                // We don't have a password saved for this connection: save a special marker value
                password = NO_PASSWORD;
            }
            string entryStr = string.Join(SEPARATOR.ToString(), new string[] { username, serverName, port.ToString(), password, (saveDisconnected ? DISCONNECTED : CONNECTED), friendlyName });
            if (poolMembers != null && poolMembers.Count > 0)
            {
                string members = string.Join(",", poolMembers.ToArray());
                entryStr += SEPARATOR.ToString();
                entryStr += members;
            }
            return Properties.Settings.Default.RequirePass && Program.MasterPassword != null ? EncryptionUtils.EncryptString(entryStr, Program.MasterPassword) : EncryptionUtils.Protect(entryStr);
        }

        public static AutoCompleteStringCollection GetServerHistory()
        {
            if (Properties.Settings.Default.ServerHistory == null)
                Properties.Settings.Default.ServerHistory = new AutoCompleteStringCollection();

            return Properties.Settings.Default.ServerHistory;
        }

        public static void UpdateServerHistory(string hostnameWithPort)
        {
            AutoCompleteStringCollection history = GetServerHistory();
            if (!history.Contains(hostnameWithPort))
            {
                while (history.Count >= 20)
                    history.RemoveAt(0);
                history.Add(hostnameWithPort);
                Properties.Settings.Default.ServerHistory = history;
                TrySaveSettings();
            }
        }

        public static AutoCompleteStringCollection GetVMwareServerHistory()
        {
            if (Properties.Settings.Default.VMwareServerHistory == null)
                Properties.Settings.Default.VMwareServerHistory = new AutoCompleteStringCollection();

            return Properties.Settings.Default.VMwareServerHistory;
        }

        public static void UpdateVMwareServerHistory(string server)
        {
            var history = GetVMwareServerHistory();
            if (history.Contains(server))
                return;

            while (history.Count >= 20)
                history.RemoveAt(0);

            history.Add(server);
            Properties.Settings.Default.VMwareServerHistory = history;
            TrySaveSettings();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="vm_uuid"></param>
        /// <returns>The password corresponding to the given VM, or null if no password is stored.</returns>
        public static char[] GetVNCPassword(string vm_uuid)
        {
            return
                VNCPasswords.ContainsKey(vm_uuid) && VNCPasswords[vm_uuid] != null ?
                    VNCPasswords[vm_uuid].ToCharArray() :
                    null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="vm_uuid"></param>
        /// <param name="password">May be null, to clear the password</param>
        public static void SetVNCPassword(string vm_uuid, char[] password)
        {
            VNCPasswords[vm_uuid] = password == null ? null : new string(password);
            SaveServerList();
        }

        public static void AddCertificate(string hashString, string hostname)
        {
            Dictionary<string, string> known_servers = KnownServers;
            known_servers.Add(hostname, hashString);
            KnownServers = known_servers;
        }

        public static Dictionary<string, string> KnownServers
        {
            get
            {
                var known = new Dictionary<string, string>();

                var knownServers = Properties.Settings.Default.KnownServers;
                if (knownServers == null)
                    return known;
                
                foreach (string knownHost in knownServers)
                {
                    string[] hostCert = knownHost.Split(' ');
                    if (hostCert.Length != 2)
                        continue;

                    known.Add(hostCert[0], hostCert[1]);
                }
                return known;
            }
            set
            {
                Properties.Settings.Default.KnownServers = value == null
                    ? new string[0]
                    : value.Select(kvp => string.Format("{0} {1}", kvp.Key, kvp.Value)).ToArray();

                TrySaveSettings();
            }
        }

        public static void ReplaceCertificate(string hostname, string hashString)
        {
            Dictionary<string, string> known_servers = KnownServers;
            known_servers[hostname] = hashString;
            KnownServers = known_servers;
        }

        public static void UpdateDisabledPluginsList(List<string> list)
        {
            Properties.Settings.Default.DisabledPlugins = list.ToArray();
            TrySaveSettings();
        }

        public static bool IsPluginEnabled(string name, string org)
        {
            string id = $"{org}::{name}";
            return Properties.Settings.Default.DisabledPlugins.All(s => s != id);
            //returns true for empty collection, which is correct
        }

        public static void Log()
        {
            log.Info("Tools Options Settings -");

            log.Info($"=== ProxySetting: {Properties.Settings.Default.ProxySetting}");
            log.Info($"=== ProxyAddress: {Properties.Settings.Default.ProxyAddress}");
            log.Info($"=== ProxyPort: {Properties.Settings.Default.ProxyPort}");
            log.Info($"=== ByPassProxyForServers: {Properties.Settings.Default.BypassProxyForServers}");
            log.Info($"=== ProvideProxyAuthentication: {Properties.Settings.Default.ProvideProxyAuthentication}");
            log.Info($"=== ProxyAuthenticationMethod: {Properties.Settings.Default.ProxyAuthenticationMethod}");
            log.Info($"=== ConnectionTimeout: {Properties.Settings.Default.ConnectionTimeout}");

            log.Info($"=== FullScreenShortcutKey: {Properties.Settings.Default.FullScreenShortcutKey}");
            log.Info($"=== DockShortcutKey: {Properties.Settings.Default.DockShortcutKey}");
            log.Info($"=== UncaptureShortcutKey: {Properties.Settings.Default.UncaptureShortcutKey}");
            log.Info($"=== ClipboardAndPrinterRedirection: {Properties.Settings.Default.ClipboardAndPrinterRedirection}");
            log.Info($"=== WindowsShortcuts: {Properties.Settings.Default.WindowsShortcuts}");
            log.Info($"=== ReceiveSoundFromRDP: {Properties.Settings.Default.ReceiveSoundFromRDP}");
            log.Info($"=== AutoSwitchToRDP: {Properties.Settings.Default.AutoSwitchToRDP}");
            log.Info($"=== ConnectToServerConsole: {Properties.Settings.Default.ConnectToServerConsole}");
            log.Info($"=== PreserveScaleWhenUndocked: {Properties.Settings.Default.PreserveScaleWhenUndocked}");
            log.Info($"=== PreserveScaleWhenSwitchBackToVNC: {Properties.Settings.Default.PreserveScaleWhenSwitchBackToVNC}");

            log.Info($"=== WarnUnrecognizedCertificate: {Properties.Settings.Default.WarnUnrecognizedCertificate}");
            log.Info($"=== WarnChangedCertificate: {Properties.Settings.Default.WarnChangedCertificate}");
            log.Info($"=== RemindChangePassword: {Properties.Settings.Default.RemindChangePassword}");

            if (!Helpers.CommonCriteriaCertificationRelease)
            {
                log.Info($"=== AllowXenCenterUpdates: {Properties.Settings.Default.AllowXenCenterUpdates}");
                log.Info($"=== AllowXenServerUpdates: {Properties.Settings.Default.AllowXenServerUpdates}");
            }
            
            log.Info($"=== FillAreaUnderGraphs: {Properties.Settings.Default.FillAreaUnderGraphs}");
            log.Info($"=== RememberLastSelectedTab: {Properties.Settings.Default.RememberLastSelectedTab}");

            log.Info($"=== SaveSession: {Properties.Settings.Default.SaveSession}");
            log.Info($"=== RequirePass: {Properties.Settings.Default.RequirePass}");

            var disabledPlugins = Properties.Settings.Default.DisabledPlugins.Length == 0
                ? "<None>"
                : string.Join(", ", Properties.Settings.Default.DisabledPlugins);
            log.InfoFormat($"=== DisabledPlugins: {disabledPlugins}");

            log.Info($"=== DoNotConfirmDismissAlerts: {Properties.Settings.Default.DoNotConfirmDismissAlerts}");
            log.Info($"=== DoNotConfirmDismissUpdates: {Properties.Settings.Default.DoNotConfirmDismissUpdates}" );
            log.Info($"=== DoNotConfirmDismissEvents: {Properties.Settings.Default.DoNotConfirmDismissEvents}" );
            log.Info($"=== IgnoreOvfValidationWarnings: {Properties.Settings.Default.IgnoreOvfValidationWarnings}");
        }
    }
}
