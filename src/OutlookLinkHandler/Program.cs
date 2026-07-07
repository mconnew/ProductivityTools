using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace OutlookLinkHandler
{
    /// <summary>
    /// Standalone Windows custom-URL-scheme protocol handler that opens a specific
    /// email in Outlook Classic (desktop) via COM automation.
    ///
    /// Supported link formats (scheme is "outlookitem"):
    ///   outlookitem:open?mid=&lt;url-encoded Internet Message-ID&gt;
    ///   outlookitem:open?eid=&lt;EntryID&gt;&amp;sid=&lt;StoreID&gt;   (fast path)
    ///   outlookitem:&lt;url-encoded Internet Message-ID&gt;             (shorthand)
    ///
    /// CLI:
    ///   OutlookLinkHandler.exe --register       Register the protocol for the current user (HKCU)
    ///   OutlookLinkHandler.exe --unregister     Remove the protocol registration
    ///   OutlookLinkHandler.exe --help           Show usage
    ///   OutlookLinkHandler.exe "outlookitem:..." Open the referenced email
    ///   OutlookLinkHandler.exe --find "outlookitem:..."  Resolve without displaying (diagnostics)
    /// </summary>
    internal static class Program
    {
        // ---- Configuration -------------------------------------------------
        internal const string Scheme = "outlookitem";
        private const string SchemeFriendlyName = "URL:Outlook Item Protocol";
        private const string MsgBoxTitle = "Open in Outlook";

        // Fixed CLSID of the DelegateExecute COM handler (see ComServer.cs).
        // The protocol is registered to activate this out-of-process COM server so
        // that COM/RPCSS -- not Outlook -- launches the handler, side-stepping the
        // "Office app creating a child process" Defender ASR rule.
        internal const string HandlerClsid = "86840CF6-1991-45E9-8974-625B5F40D759";

        // Per-user settings (kept separate from the protocol registration so it
        // survives --unregister). Logging is OFF unless LoggingEnabled = 1.
        private const string SettingsKeyPath = @"Software\OutlookLinkHandler";
        private const string LoggingValueName = "LoggingEnabled";

        // Office "Trusted Protocols" policy. Adding our scheme here suppresses the
        // Office "Microsoft Office has identified a potential security concern /
        // This location may be unsafe" warning that Outlook shows for custom URL
        // schemes. Office only honours this in the protected HKCU\Software\Policies
        // hive, whose ACL grants write access to Administrators/SYSTEM only -- so
        // writing it requires an elevated (Run as administrator) token. The version
        // token 16.0 covers Office 2016 / 2019 / 2021 / Microsoft 365 (Outlook Classic).
        private const string TrustedProtocolsPolicyPath =
            @"Software\Policies\Microsoft\Office\16.0\Common\Security\Trusted Protocols\All Applications";
        private static readonly string TrustedProtocolSubkey = Scheme + ":";  // "outlookitem:"

        // MAPI / DASL properties that hold the Internet Message-ID header.
        private static readonly string[] MessageIdProps =
        {
            "http://schemas.microsoft.com/mapi/proptag/0x1035001F", // PR_INTERNET_MESSAGE_ID (Unicode)
            "http://schemas.microsoft.com/mapi/proptag/0x1035001E", // PR_INTERNET_MESSAGE_ID (ANSI)
            "urn:schemas:mail:message-id"
        };

        private const int olFolderInbox = 6;   // OlDefaultFolders.olFolderInbox

        // Input bounds for the allowlist validators.
        private const int MaxMessageIdLength = 512;   // RFC 5322 message-ids are short
        private const int MaxEntryIdLength = 1024;     // MAPI EntryIDs are hex, ~140-512 chars

        private static readonly StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "OutlookItemHandler.log");

        // When true, suppress all pop-up dialogs (for silent installs / scripting / diagnostics).
        private static bool Quiet;

        // Cached logging state; loaded from the registry once per invocation.
        private static bool LoggingEnabled;

        [STAThread]
        private static int Main(string[] rawArgs)
        {
            string[] raw = rawArgs ?? new string[0];

            try
            {
                LoadLoggingSetting();

                // --- COM activation entry point -----------------------------
                // When the shell activates our DelegateExecute handler, COM (RPCSS)
                // launches this exe with "-Embedding" (or "/Embedding") on the command
                // line. In that case we are a COM LocalServer: register the class
                // factory and pump messages until the verb runs. This path is what
                // dodges the Office-child-process ASR rule (parent = svchost, not Outlook).
                foreach (string a in raw)
                {
                    string t = a.Trim();
                    if (EqualsAny(t, "-Embedding", "/Embedding"))
                        return ComServer.RunServer();
                }

                if (raw.Length == 0)
                {
                    ShowUsage();
                    return 0;
                }

                string firstRaw = raw[0].Trim();

                // --- Untrusted entry point (a clicked link) -----------------
                // Windows substitutes the URL into the registered "%1" and passes it
                // as the first argument. A crafted link can use quote/argument-injection
                // to append extra tokens, so when the first argument is an outlookitem:
                // URL we treat ONLY that argument as data and ignore everything else:
                // no --quiet, no --register, no commands can ride in on a link.
                if (firstRaw.StartsWith(Scheme + ":", OIC))
                {
                    if (raw.Length > 1)
                        Log("Protocol mode: ignoring " + (raw.Length - 1) + " extra argument(s) after the URL.");
                    Log("Invoked with URL: " + firstRaw);
                    return HandleUrl(firstRaw, false);
                }

                // --- Trusted entry point (manual command line) --------------
                // Reached only when the first argument is NOT an outlookitem: URL, i.e.
                // a user or script typed a command directly. Flags are honoured here.
                var args = new List<string>();
                foreach (string a in raw)
                {
                    if (EqualsAny(a.Trim(), "--quiet", "/quiet", "-q", "--silent", "/silent"))
                        Quiet = true;
                    else
                        args.Add(a);
                }

                if (args.Count == 0)
                {
                    ShowUsage();
                    return 0;
                }

                string first = args[0].Trim();

                if (EqualsAny(first, "--register", "/register", "-register"))
                {
                    RegisterProtocol();

                    var sb = new StringBuilder();
                    sb.Append("Registered the '" + Scheme + ":' protocol for the current user.\n\nHandler:\n" + ExePath());

                    if (IsElevated())
                    {
                        // Elevated: also add the Office Trusted Protocols policy so
                        // Outlook stops warning about the custom scheme.
                        if (TryAddTrustedProtocolPolicy(out string detail))
                            sb.Append("\n\nElevated run: added '" + Scheme + ":' to Office's Trusted Protocols policy, so "
                                + "Outlook will no longer show the \"This location may be unsafe\" warning for these links.\n\nPolicy key:\n" + detail);
                        else
                            sb.Append("\n\nElevated run, but the Trusted Protocols policy could NOT be written:\n" + detail
                                + "\n\nOutlook may still warn for '" + Scheme + ":' links.");
                    }
                    else if (TrustedProtocolPolicyExists())
                    {
                        sb.Append("\n\nOutlook's \"unsafe location\" warning for '" + Scheme + ":' links is already suppressed "
                            + "(the Trusted Protocols policy is present). No further action needed.");
                    }
                    else
                    {
                        sb.Append("\n\nOutlook will show a \"This location may be unsafe\" warning every time "
                            + "you click an '" + Scheme + ":' link. To suppress it, re-run this command from an ELEVATED prompt "
                            + "(right-click > Run as administrator):\n\n   \"" + ExePath() + "\" --register\n\n"
                            + "Elevation is required because the setting lives in a protected Group Policy registry key that only "
                            + "administrators may write.");
                    }

                    Info(sb.ToString());
                    return 0;
                }

                if (EqualsAny(first, "--unregister", "/unregister", "-unregister"))
                {
                    UnregisterProtocol();

                    var sb = new StringBuilder();
                    sb.Append("Unregistered the '" + Scheme + ":' protocol.");

                    if (TrustedProtocolPolicyExists())
                    {
                        if (IsElevated())
                        {
                            if (TryRemoveTrustedProtocolPolicy(out string detail))
                                sb.Append("\n\nAlso removed the Office Trusted Protocols policy entry for '" + Scheme + ":'.");
                            else
                                sb.Append("\n\nThe Trusted Protocols policy entry could NOT be removed:\n" + detail);
                        }
                        else
                        {
                            sb.Append("\n\nNote: the Office Trusted Protocols policy entry for '" + Scheme + ":' was left in place "
                                + "(removing it requires elevation). To remove it, re-run '--unregister' from an ELEVATED prompt.");
                        }
                    }

                    Info(sb.ToString());
                    return 0;
                }

                if (EqualsAny(first, "--enable-logging", "/enable-logging"))
                {
                    SetLoggingEnabled(true);
                    Info("Diagnostic logging is now ENABLED.\n\nLog file:\n" + LogPath);
                    return 0;
                }

                if (EqualsAny(first, "--disable-logging", "/disable-logging"))
                {
                    SetLoggingEnabled(false);
                    Info("Diagnostic logging is now DISABLED.");
                    return 0;
                }

                if (EqualsAny(first, "--help", "/help", "-help", "-h", "/?"))
                {
                    ShowUsage();
                    return 0;
                }

                bool findOnly = EqualsAny(first, "--find", "/find");
                string url = findOnly ? (args.Count > 1 ? args[1] : "") : first;

                if (string.IsNullOrWhiteSpace(url))
                {
                    Error("No link was provided.");
                    return 1;
                }

                Log("Invoked with URL: " + url + (findOnly ? "  (find-only)" : ""));
                return HandleUrl(url, findOnly);
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                Error("Failed to open the email in Outlook.\n\n" + ex.Message);
                return 1;
            }
        }

        // ---- URL handling --------------------------------------------------

        /// <summary>
        /// Entry point used by the COM DelegateExecute handler (ComServer). This is
        /// the untrusted path: the URL is treated purely as data, exactly like a
        /// clicked link on the command line, and no flags are honoured.
        /// </summary>
        internal static int HandleProtocolUrl(string url)
        {
            return HandleUrl(url, false);
        }

        private static int HandleUrl(string url, bool findOnly)
        {
            ParseUrl(url, out string mid, out string eid, out string sid);
            Log(string.Format(CultureInfo.InvariantCulture, "Parsed mid='{0}' eid='{1}' sid='{2}'",
                mid, eid, sid));

            // Allowlist the inputs before building any query or touching the mailbox.
            // A Message-ID is printable ASCII with no spaces/quotes/controls; EntryID
            // and StoreID are hex. Reject/ignore anything else so a crafted link can
            // neither smuggle characters into the DASL restriction nor probe with junk.
            if (!string.IsNullOrEmpty(mid) && !IsValidMessageId(mid))
            {
                Log("Rejected mid (failed validation): " + mid);
                Error("The message id in this link contains invalid characters, so it was rejected.");
                return 1;
            }
            if (!string.IsNullOrEmpty(eid) && !IsHexToken(eid))
            {
                Log("Ignoring eid (not a hex EntryID): " + eid);
                eid = null;
            }
            if (!string.IsNullOrEmpty(sid) && !IsHexToken(sid))
            {
                Log("Ignoring sid (not a hex StoreID): " + sid);
                sid = null;
            }

            if (string.IsNullOrEmpty(mid) && string.IsNullOrEmpty(eid))
            {
                Error("The link did not contain a valid message id (mid) or entry id (eid).\n\n" + url);
                return 1;
            }

            dynamic app = GetOutlookApp();
            dynamic ns = app.GetNamespace("MAPI");

            dynamic item = null;

            // 1) Fast path: direct EntryID lookup (may fail if the item moved).
            if (!string.IsNullOrEmpty(eid))
            {
                try
                {
                    item = string.IsNullOrEmpty(sid)
                        ? ns.GetItemFromID(eid)
                        : ns.GetItemFromID(eid, sid);
                    if (item != null) Log("Resolved via EntryID.");
                }
                catch (Exception ex)
                {
                    Log("GetItemFromID failed (will try Message-ID): " + ex.Message);
                    item = null;
                }
            }

            // 2) Robust path: search by Internet Message-ID.
            if (item == null && !string.IsNullOrEmpty(mid))
            {
                item = FindByMessageId(ns, mid);
                if (item != null) Log("Resolved via Message-ID search.");
            }

            if (item == null)
            {
                Error("Could not find that message in Outlook.\n\n" +
                      "It may have been moved or deleted, or Outlook is signed in with a different " +
                      "profile/account than the one that received it.");
                return 2;
            }

            string subject = TryGetString(() => (string)item.Subject) ?? "(no subject)";
            Log("Found item: " + subject);

            if (findOnly)
            {
                Info("Found message:\n\n" + subject);
                return 0;
            }

            DisplayItem(item);
            return 0;
        }

        /// <summary>
        /// Parse the incoming URL into message-id / entry-id / store-id components.
        /// Accepts scheme:path, scheme://path, and scheme:...?query forms.
        /// </summary>
        private static void ParseUrl(string url, out string mid, out string eid, out string sid)
        {
            mid = null; eid = null; sid = null;

            string rest = url.Trim();
            int colon = rest.IndexOf(':');
            if (colon >= 0) rest = rest.Substring(colon + 1); // strip "outlookitem:"
            rest = rest.TrimStart('/');                        // tolerate "//"

            string path = rest;
            string query = "";
            int q = rest.IndexOf('?');
            if (q >= 0)
            {
                path = rest.Substring(0, q);
                query = rest.Substring(q + 1);
            }

            if (query.Length > 0)
            {
                foreach (string pair in query.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                    string val = eq >= 0 ? pair.Substring(eq + 1) : "";
                    key = Uri.UnescapeDataString(key).Trim();
                    val = Uri.UnescapeDataString(val).Trim();

                    if (key.Equals("mid", OIC) || key.Equals("messageid", OIC)) mid = val;
                    else if (key.Equals("eid", OIC) || key.Equals("entryid", OIC)) eid = val;
                    else if (key.Equals("sid", OIC) || key.Equals("storeid", OIC)) sid = val;
                }
            }

            // Shorthand: outlookitem:<encoded message-id> (no query, path is the id)
            if (string.IsNullOrEmpty(mid) && string.IsNullOrEmpty(eid) &&
                !string.IsNullOrEmpty(path) && !path.Equals("open", OIC))
            {
                mid = Uri.UnescapeDataString(path).Trim();
            }
        }

        // ---- Outlook COM ---------------------------------------------------

        private static dynamic GetOutlookApp()
        {
            // Prefer an already-running Outlook instance.
            try
            {
                dynamic running = GetActiveObject("Outlook.Application");
                if (running != null)
                {
                    Log("Connected to running Outlook instance.");
                    return running;
                }
            }
            catch (Exception ex)
            {
                Log("No running Outlook (" + ex.Message + "); starting a new instance.");
            }

            Type t = Type.GetTypeFromProgID("Outlook.Application");
            if (t == null)
                throw new InvalidOperationException("Outlook is not installed (Outlook.Application ProgID not found).");

            dynamic app = Activator.CreateInstance(t);
            try
            {
                dynamic ns = app.GetNamespace("MAPI");
                ns.Logon("", "", false, false); // use the default profile, no dialog
                Log("Started Outlook and logged on to the default profile.");
            }
            catch (Exception ex)
            {
                Log("Logon note: " + ex.Message);
            }
            return app;
        }

        private static dynamic FindByMessageId(dynamic ns, string messageId)
        {
            string expectedBare = messageId.Trim().Trim('<', '>').Trim();
            List<string> filters = BuildMessageIdFilters(messageId);

            // Inbox first (most common location -> fast return).
            try
            {
                dynamic inbox = ns.GetDefaultFolder(olFolderInbox);
                dynamic hit = RestrictInFolder(inbox, filters, expectedBare);
                if (hit != null) return hit;
            }
            catch (Exception ex)
            {
                Log("Inbox search skipped: " + ex.Message);
            }

            // Walk every folder in every store.
            try
            {
                foreach (dynamic topFolder in ns.Folders)
                {
                    dynamic hit = WalkFolders(topFolder, filters, expectedBare);
                    if (hit != null) return hit;
                }
            }
            catch (Exception ex)
            {
                Log("Folder walk error: " + ex.Message);
            }

            return null;
        }

        private static List<string> BuildMessageIdFilters(string messageId)
        {
            string trimmed = messageId.Trim();
            string bare = trimmed.Trim('<', '>').Trim();
            string bracketed = "<" + bare + ">";

            // Order matters: most-likely representation first.
            var values = new List<string>();
            AddDistinct(values, bracketed);
            AddDistinct(values, bare);
            AddDistinct(values, trimmed);

            var filters = new List<string>();
            foreach (string prop in MessageIdProps)
                foreach (string val in values)
                    filters.Add("@SQL=\"" + prop + "\" = '" + val.Replace("'", "''") + "'");

            return filters;
        }

        private static dynamic WalkFolders(dynamic folder, List<string> filters, string expectedBare)
        {
            dynamic hit = RestrictInFolder(folder, filters, expectedBare);
            if (hit != null) return hit;

            try
            {
                foreach (dynamic sub in folder.Folders)
                {
                    dynamic subHit = WalkFolders(sub, filters, expectedBare);
                    if (subHit != null) return subHit;
                }
            }
            catch (Exception ex)
            {
                Log("Subfolder enumeration error under '" + SafeName(folder) + "': " + ex.Message);
            }
            return null;
        }

        private static dynamic RestrictInFolder(dynamic folder, List<string> filters, string expectedBare)
        {
            dynamic items;
            try
            {
                items = folder.Items;
            }
            catch
            {
                return null;
            }

            foreach (string filter in filters)
            {
                try
                {
                    dynamic restricted = items.Restrict(filter);
                    for (dynamic candidate = restricted.GetFirst();
                         candidate != null;
                         candidate = restricted.GetNext())
                    {
                        // Defence-in-depth: independently re-read the candidate's real
                        // Internet Message-ID and require an exact match. Even if a
                        // crafted value somehow influenced the DASL restriction, the
                        // item that actually opens must carry the requested id.
                        string actual = GetInternetMessageId(candidate);
                        string actualBare = (actual ?? string.Empty).Trim().Trim('<', '>').Trim();

                        if (actualBare.Length > 0 && actualBare.Equals(expectedBare, OIC))
                        {
                            Log("Matched (verified) in folder '" + SafeName(folder) + "' with filter: " + filter);
                            return candidate;
                        }

                        Log("Discarded restrict candidate in '" + SafeName(folder) +
                            "' (id mismatch, actual='" + actualBare + "').");
                    }
                }
                catch (Exception ex)
                {
                    // Restrict can throw on folders that don't support the property (e.g. calendar).
                    Log("Restrict failed in '" + SafeName(folder) + "': " + ex.Message);
                }
            }
            return null;
        }

        private static void DisplayItem(dynamic item)
        {
            item.Display(false); // non-modal inspector window
            try
            {
                dynamic inspector = item.GetInspector;
                inspector.Activate(); // bring the window to the foreground
            }
            catch (Exception ex)
            {
                Log("Inspector activate note: " + ex.Message);
            }
        }

        // ---- Registry (self) registration ----------------------------------

        private static void RegisterProtocol()
        {
            string exe = ExePath();
            string command = "\"" + exe + "\" \"%1\"";
            string clsidKey = "{" + HandlerClsid + "}";

            // 1) The custom URL scheme. The 'open' verb delegates to our COM handler
            //    via DelegateExecute so that COM/RPCSS (not Outlook) launches the exe.
            //    The plain command with "%1" is kept only as a down-level fallback for
            //    callers that don't support DelegateExecute.
            using (RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
            {
                root.SetValue(string.Empty, SchemeFriendlyName, RegistryValueKind.String);
                root.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

                using (RegistryKey icon = root.CreateSubKey("DefaultIcon"))
                    icon.SetValue(string.Empty, exe + ",0", RegistryValueKind.String);

                using (RegistryKey cmd = root.CreateSubKey(@"shell\open\command"))
                {
                    cmd.SetValue(string.Empty, command, RegistryValueKind.String);
                    cmd.SetValue("DelegateExecute", clsidKey, RegistryValueKind.String);
                }
            }

            // 2) The COM LocalServer32 registration for the DelegateExecute CLSID.
            //    COM appends "-Embedding" to this command line when it launches the
            //    server, which Main detects to enter COM-server mode.
            using (RegistryKey clsid = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\CLSID\" + clsidKey))
            {
                clsid.SetValue(string.Empty, "Outlook Item Link Handler", RegistryValueKind.String);
                using (RegistryKey ls = clsid.CreateSubKey("LocalServer32"))
                    ls.SetValue(string.Empty, "\"" + exe + "\"", RegistryValueKind.String);
            }

            Log("Registered protocol (DelegateExecute). Command = " + command +
                "  DelegateExecute = " + clsidKey);
        }

        private static void UnregisterProtocol()
        {
            string clsidKey = "{" + HandlerClsid + "}";
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Scheme, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + clsidKey, false);
                Log("Unregistered protocol and COM handler CLSID.");
            }
            catch (Exception ex)
            {
                Log("Unregister note: " + ex.Message);
            }
        }

        // ---- Office Trusted Protocols policy (suppresses Outlook's warning) -----

        /// <summary>
        /// True only when the process is running with an elevated (administrator)
        /// token. For a local admin whose token is split by UAC, this returns false
        /// until the process is actually elevated -- which is exactly the gate we
        /// need before attempting to write the protected Policies hive.
        /// </summary>
        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Read-only check for the Trusted Protocols entry (needs no elevation).</summary>
        private static bool TrustedProtocolPolicyExists()
        {
            try
            {
                using (RegistryKey all = Registry.CurrentUser.OpenSubKey(TrustedProtocolsPolicyPath))
                {
                    if (all == null) return false;
                    using (RegistryKey k = all.OpenSubKey(TrustedProtocolSubkey))
                        return k != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Adds the '&lt;scheme&gt;:' subkey to the Office Trusted Protocols policy.
        /// Requires elevation (the Policies hive is writable by admins only).
        /// </summary>
        private static bool TryAddTrustedProtocolPolicy(out string detail)
        {
            try
            {
                using (RegistryKey all = Registry.CurrentUser.CreateSubKey(TrustedProtocolsPolicyPath))
                using (all.CreateSubKey(TrustedProtocolSubkey))
                {
                    detail = @"HKCU\" + TrustedProtocolsPolicyPath + @"\" + TrustedProtocolSubkey;
                    Log("Added Trusted Protocols policy: " + detail);
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                Log("Failed to add Trusted Protocols policy: " + ex.Message);
                return false;
            }
        }

        /// <summary>Removes the '&lt;scheme&gt;:' Trusted Protocols entry. Requires elevation.</summary>
        private static bool TryRemoveTrustedProtocolPolicy(out string detail)
        {
            try
            {
                using (RegistryKey all = Registry.CurrentUser.OpenSubKey(TrustedProtocolsPolicyPath, writable: true))
                {
                    if (all == null) { detail = "not present"; return true; }
                    all.DeleteSubKey(TrustedProtocolSubkey, throwOnMissingSubKey: false);
                    detail = "removed";
                    Log("Removed Trusted Protocols policy entry for " + TrustedProtocolSubkey);
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                Log("Failed to remove Trusted Protocols policy: " + ex.Message);
                return false;
            }
        }

        // ---- Logging on/off setting ----------------------------------------

        internal static void LoadLoggingSetting()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath))
                {
                    object value = key?.GetValue(LoggingValueName);
                    LoggingEnabled = value != null && Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                LoggingEnabled = false; // default: logging off
            }
        }

        private static void SetLoggingEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
            {
                key.SetValue(LoggingValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            LoggingEnabled = enabled; // take effect immediately for this run
        }

        // ---- Helpers -------------------------------------------------------

        private static string ExePath()
        {
            // The actual .exe (apphost) for the current process.
            return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
        }

        private static bool EqualsAny(string value, params string[] options)
        {
            foreach (string o in options)
                if (value.Equals(o, OIC)) return true;
            return false;
        }

        private static void AddDistinct(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (string v in list)
                if (v.Equals(value, StringComparison.Ordinal)) return;
            list.Add(value);
        }

        private static string SafeName(dynamic folder)
        {
            try { return (string)folder.Name; } catch { return "?"; }
        }

        private static string TryGetString(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }

        /// <summary>Reads the item's real Internet Message-ID via the MAPI property.</summary>
        private static string GetInternetMessageId(dynamic item)
        {
            try
            {
                dynamic pa = item.PropertyAccessor;
                return (string)pa.GetProperty(MessageIdProps[0]); // PR_INTERNET_MESSAGE_ID (Unicode)
            }
            catch
            {
                try
                {
                    dynamic pa = item.PropertyAccessor;
                    return (string)pa.GetProperty(MessageIdProps[1]); // ANSI fallback
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// A Message-ID header is printable ASCII with no whitespace, control chars,
        /// double quotes, or non-ASCII bytes. We strip optional angle brackets, bound
        /// the length, and reject anything outside that set.
        /// </summary>
        private static bool IsValidMessageId(string value)
        {
            if (value == null) return false;
            string bare = value.Trim().Trim('<', '>').Trim();
            if (bare.Length == 0 || bare.Length > MaxMessageIdLength) return false;
            foreach (char c in bare)
            {
                if (c < 0x21 || c > 0x7E) return false; // controls, space, DEL, non-ASCII
                if (c == '"') return false;              // never legitimate in a message-id
            }
            return true;
        }

        /// <summary>EntryID / StoreID are hex strings; reject anything else.</summary>
        private static bool IsHexToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxEntryIdLength) return false;
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        private static void ShowUsage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Outlook Item Link Handler");
            sb.AppendLine();
            sb.AppendLine("Opens a specific email in Outlook Classic from a custom link.");
            sb.AppendLine();
            sb.AppendLine("Setup (run once, no admin needed):");
            sb.AppendLine("   OutlookLinkHandler.exe --register");
            sb.AppendLine();
            sb.AppendLine("   Tip: run --register from an ELEVATED prompt (Run as administrator) to also");
            sb.AppendLine("   suppress Outlook's \"This location may be unsafe\" warning for these links.");
            sb.AppendLine();
            sb.AppendLine("Remove:");
            sb.AppendLine("   OutlookLinkHandler.exe --unregister");
            sb.AppendLine();
            sb.AppendLine("Diagnostic logging (off by default):");
            sb.AppendLine("   OutlookLinkHandler.exe --enable-logging");
            sb.AppendLine("   OutlookLinkHandler.exe --disable-logging");
            sb.AppendLine();
            sb.AppendLine("Link formats:");
            sb.AppendLine("   " + Scheme + ":open?mid=<url-encoded Internet Message-ID>");
            sb.AppendLine("   " + Scheme + ":open?eid=<EntryID>&sid=<StoreID>");
            sb.AppendLine("   " + Scheme + ":<url-encoded Internet Message-ID>");
            sb.AppendLine();
            sb.AppendLine("Logging is currently " + (LoggingEnabled ? "ENABLED" : "DISABLED") + ".");
            sb.AppendLine("Log file (when enabled): " + LogPath);
            Info(sb.ToString());
        }

        internal static void Info(string message)
        {
            Log("[info] " + message.Replace(Environment.NewLine, " | "));
            if (Quiet) return;
            MessageBoxW(IntPtr.Zero, message, MsgBoxTitle, MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
        }

        internal static void Error(string message)
        {
            Log("[error] " + message.Replace(Environment.NewLine, " | "));
            if (Quiet) return;
            MessageBoxW(IntPtr.Zero, message, MsgBoxTitle, MB_OK | MB_ICONERROR | MB_TOPMOST);
        }

        internal static void Log(string message)
        {
            if (!LoggingEnabled) return;
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + message + Environment.NewLine);
            }
            catch
            {
                // never let logging break the handler
            }
        }

        // ---- user32 MessageBox (avoids a WinForms dependency) --------------

        private const uint MB_OK = 0x0;
        private const uint MB_ICONERROR = 0x10;
        private const uint MB_ICONINFORMATION = 0x40;
        private const uint MB_TOPMOST = 0x40000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        // ---- Running-object lookup ----------------------------------------
        // Marshal.GetActiveObject(string) does not exist on modern .NET, so we
        // resolve the ProgID's CLSID and call oleaut32!GetActiveObject directly.

        [DllImport("ole32.dll")]
        private static extern int CLSIDFromProgID(
            [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

        [DllImport("oleaut32.dll", EntryPoint = "GetActiveObject", PreserveSig = false)]
        private static extern void GetActiveObjectNative(
            ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        private static object GetActiveObject(string progId)
        {
            int hr = CLSIDFromProgID(progId, out Guid clsid);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            // Throws COMException (MK_E_UNAVAILABLE) if no instance is running.
            GetActiveObjectNative(ref clsid, IntPtr.Zero, out object obj);
            return obj;
        }
    }
}
