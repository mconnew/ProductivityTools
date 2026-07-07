using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OutlookLinkHandler
{
    /// <summary>
    /// Out-of-process (LocalServer32) COM implementation of a Windows shell
    /// "DelegateExecute" verb handler for the <c>outlookitem:</c> protocol.
    ///
    /// Why this exists:
    /// On managed/corporate machines an enterprise Microsoft Defender
    /// "Attack Surface Reduction" rule ("Block Office communication application
    /// from creating child processes", GUID 26190899-1602-49E8-8B27-EB1D0A1CE869)
    /// blocks Outlook from launching ANY child process. A classic
    /// <c>shell\open\command = "handler.exe" "%1"</c> registration therefore fails
    /// with "Access is denied" because Outlook itself is the process that would
    /// spawn the handler.
    ///
    /// The fix is to register the protocol with a <c>DelegateExecute</c> CLSID that
    /// points at this COM LocalServer32. When Outlook invokes the protocol the shell
    /// activates the COM object, and COM/DCOM (RPCSS, hosted in svchost.exe) launches
    /// this server process. Outlook is NOT the parent of the new process, so the
    /// Office-child-process ASR rule does not fire. This is the same mechanism modern
    /// browsers use so that clicking an http link inside Outlook opens the browser.
    ///
    /// Interfaces implemented (per the Windows shell "ExecuteCommand" verb contract):
    ///   IExecuteCommand      - required; Execute() invokes the verb
    ///   IObjectWithSelection - required; carries the target (the clicked URL)
    ///   IInitializeCommand   - optional; supplies the verb name
    ///   IObjectWithSite      - optional; supplies the shell site
    /// </summary>
    internal static class ComServer
    {
        // ---- HRESULTs / activation constants -------------------------------
        private const int S_OK = 0;
        private const int S_FALSE = 1;
        private static readonly int E_NOINTERFACE = unchecked((int)0x80004002);
        private static readonly int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

        private const uint CLSCTX_LOCAL_SERVER = 0x4;
        private const uint REGCLS_SINGLEUSE = 0x0;

        private const uint COINIT_APARTMENTTHREADED = 0x2;
        private const uint COINIT_DISABLE_OLE1DDE = 0x4;

        // SIGDN display-name forms used to pull the URL back out of the shell item.
        private const int SIGDN_NORMALDISPLAY = 0;
        private static readonly int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
        private static readonly int SIGDN_URL = unchecked((int)0x80068000);

        private const uint WM_TIMER = 0x0113;
        private const uint WM_QUIT = 0x0012;
        private static readonly UIntPtr WATCHDOG_TIMER_ID = (UIntPtr)1;

        // Native thread id of the STA that pumps the message loop. Because the verb
        // object advertises IAgileObject, COM may dispatch Execute() on an RPC worker
        // thread rather than this STA; a thread timer set from there would never be
        // pumped. We therefore post the quit directly to this captured thread id.
        private static uint _mainThreadId;

        // Absolute upper bound on server lifetime if the shell never calls Execute.
        private const uint WatchdogMs = 120000;
        // Small delay after Execute so the COM reply is delivered before we exit.
        private const uint QuitDelayMs = 250;

        /// <summary>
        /// Entry point when the process is launched by COM (command line contains
        /// "-Embedding"). Registers the class factory and pumps messages until the
        /// verb has been executed (or the watchdog fires), then exits.
        /// </summary>
        internal static int RunServer()
        {
            Program.LoadLoggingSetting();
            Program.Log("COM server starting. " + DescribeProcess());

            _mainThreadId = GetCurrentThreadId();

            int hrInit = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
            // S_OK or S_FALSE (already initialized on this thread) are both fine.
            if (hrInit < 0)
            {
                Program.Log("CoInitializeEx failed: 0x" + hrInit.ToString("X8", CultureInfo.InvariantCulture));
                return 1;
            }

            uint cookie = 0;
            bool registered = false;
            try
            {
                var factory = new ClassFactory();
                Guid clsid = new Guid(Program.HandlerClsid);
                int hr = CoRegisterClassObject(ref clsid, factory,
                    CLSCTX_LOCAL_SERVER, REGCLS_SINGLEUSE, out cookie);
                if (hr < 0)
                {
                    Program.Log("CoRegisterClassObject failed: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                    return 1;
                }
                registered = true;
                Program.Log("Class object registered (cookie=" + cookie + "). Entering message loop.");

                // Safety net: never linger if the shell abandons the activation.
                SetTimer(IntPtr.Zero, WATCHDOG_TIMER_ID, WatchdogMs, IntPtr.Zero);

                MessageLoop();
            }
            finally
            {
                if (registered && cookie != 0)
                {
                    try { CoRevokeClassObject(cookie); } catch { /* may be auto-revoked (single-use) */ }
                }
            }

            Program.Log("COM server exiting.");
            return 0;
        }

        private static void MessageLoop()
        {
            int ret;
            while ((ret = GetMessage(out MSG msg, IntPtr.Zero, 0, 0)) != 0)
            {
                if (ret == -1) break; // GetMessage error
                if (msg.message == WM_TIMER)
                {
                    // Either the post-Execute quit timer or the watchdog fired.
                    PostQuitMessage(0);
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        /// <summary>Called by the verb once work is complete to shut the server down promptly.</summary>
        internal static void ScheduleQuit()
        {
            uint tid = _mainThreadId;
            if (tid == 0) return;

            // Delay briefly so COM can marshal the Execute() reply back to the shell
            // before we tear the apartment down, then wake the STA loop. WM_QUIT makes
            // GetMessage return 0, ending MessageLoop cleanly. Posting to the thread id
            // works regardless of which thread COM dispatched Execute() on.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    System.Threading.Thread.Sleep((int)QuitDelayMs);
                    PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                catch { /* best effort; the watchdog is the safety net */ }
            });
        }

        // ================================================================
        //  Verb handler
        // ================================================================
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ExecuteCommandVerb
            : IExecuteCommand, IObjectWithSelection, IInitializeCommand, IObjectWithSite,
              IExecuteCommandApplicationHostEnvironment, IForegroundTransfer,
              IAgileObject
        {
            private IShellItemArray _selection;
            private string _parameters;
            private string _verb;
            private object _site;

            // ---- IExecuteCommand ----
            public int SetKeyState(uint grfKeyState) => S_OK;

            public int SetParameters(string pszParameters)
            {
                _parameters = pszParameters;
                Program.Log("IExecuteCommand.SetParameters: " + (pszParameters ?? "(null)"));
                return S_OK;
            }

            public int SetPosition(POINT pt) => S_OK;
            public int SetShowWindow(int nShow) => S_OK;
            public int SetNoShowUI(int fNoShowUI) => S_OK;
            public int SetDirectory(string pszDirectory) => S_OK;

            public int Execute()
            {
                try
                {
                    string url = ResolveUrl();
                    if (string.IsNullOrEmpty(url))
                    {
                        Program.Log("Execute: could not determine a URL from the shell invocation.");
                        Program.Error("The link could not be read (no URL was supplied by Windows).");
                    }
                    else
                    {
                        Program.Log("Execute: resolved URL = " + url);
                        Program.HandleProtocolUrl(url);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("Execute FATAL: " + ex);
                    Program.Error("Failed to open the email in Outlook.\n\n" + ex.Message);
                }
                finally
                {
                    ScheduleQuit();
                }
                return S_OK;
            }

            /// <summary>
            /// The clicked URL can arrive either as the verb parameters or, more
            /// commonly, as the "selection" shell item. Try both and log what we see.
            /// </summary>
            private string ResolveUrl()
            {
                if (LooksLikeOurUrl(_parameters))
                    return _parameters.Trim();

                if (_selection != null)
                {
                    string fromSel = UrlFromSelection(_selection);
                    if (!string.IsNullOrEmpty(fromSel))
                        return fromSel;
                }

                // Last resort: parameters even if they don't obviously match (logged).
                return string.IsNullOrWhiteSpace(_parameters) ? null : _parameters.Trim();
            }

            private static string UrlFromSelection(IShellItemArray sia)
            {
                try
                {
                    if (sia.GetCount(out uint count) < 0 || count == 0) return null;
                    if (sia.GetItemAt(0, out IShellItem item) < 0 || item == null) return null;

                    string best = null;
                    foreach (int sigdn in new[] { SIGDN_URL, SIGDN_DESKTOPABSOLUTEPARSING, SIGDN_NORMALDISPLAY })
                    {
                        string name = DisplayName(item, sigdn);
                        Program.Log("Selection display-name (sigdn=0x" +
                            sigdn.ToString("X", CultureInfo.InvariantCulture) + "): " + (name ?? "(null)"));
                        if (LooksLikeOurUrl(name))
                            return name.Trim();
                        if (best == null && !string.IsNullOrWhiteSpace(name))
                            best = name.Trim();
                    }
                    return best;
                }
                catch (Exception ex)
                {
                    Program.Log("UrlFromSelection error: " + ex.Message);
                    return null;
                }
            }

            private static string DisplayName(IShellItem item, int sigdn)
            {
                IntPtr p = IntPtr.Zero;
                try
                {
                    if (item.GetDisplayName(sigdn, out p) != S_OK || p == IntPtr.Zero)
                        return null;
                    return Marshal.PtrToStringUni(p);
                }
                catch
                {
                    return null;
                }
                finally
                {
                    if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
                }
            }

            private static bool LooksLikeOurUrl(string s)
            {
                return !string.IsNullOrWhiteSpace(s) &&
                       s.Trim().StartsWith(Program.Scheme + ":", StringComparison.OrdinalIgnoreCase);
            }

            // ---- IObjectWithSelection ----
            public int SetSelection(IShellItemArray psia)
            {
                _selection = psia;
                Program.Log("IObjectWithSelection.SetSelection received (" + (psia != null) + ").");
                return S_OK;
            }

            public int GetSelection(ref Guid riid, out IntPtr ppv)
            {
                ppv = IntPtr.Zero;
                if (_selection == null) return E_NOINTERFACE;
                IntPtr punk = Marshal.GetIUnknownForObject(_selection);
                try { return Marshal.QueryInterface(punk, in riid, out ppv); }
                finally { Marshal.Release(punk); }
            }

            // ---- IInitializeCommand ----
            public int Initialize(string pszCommandName, IntPtr ppb)
            {
                _verb = pszCommandName;
                Program.Log("IInitializeCommand.Initialize verb='" + (pszCommandName ?? "(null)") + "'.");
                return S_OK;
            }

            // ---- IObjectWithSite ----
            public int SetSite(object punkSite)
            {
                _site = punkSite;
                return S_OK;
            }

            public int GetSite(ref Guid riid, out IntPtr ppv)
            {
                ppv = IntPtr.Zero;
                if (_site == null) return E_NOINTERFACE;
                IntPtr punk = Marshal.GetIUnknownForObject(_site);
                try { return Marshal.QueryInterface(punk, in riid, out ppv); }
                finally { Marshal.Release(punk); }
            }

            // ---- IExecuteCommandApplicationHostEnvironment ----
            // The shell queries this on out-of-process URL DelegateExecute handlers to
            // decide whether to activate in the classic desktop or the immersive (Store)
            // environment. Browsers implement it; without it the shell aborts a URL verb
            // invocation with E_NOINTERFACE before ever calling Execute. Always desktop.
            public int GetValue(out int pahe)
            {
                pahe = AHE_DESKTOP;
                Program.Log("IExecuteCommandApplicationHostEnvironment.GetValue -> AHE_DESKTOP");
                return S_OK;
            }

            // ---- IForegroundTransfer ----
            public int AllowForegroundTransfer(IntPtr lpvReserved)
            {
                Program.Log("IForegroundTransfer.AllowForegroundTransfer");
                return S_OK;
            }
        }

        // ================================================================
        //  Class factory
        // ================================================================
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ClassFactory : IClassFactory
        {
            public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
            {
                ppvObject = IntPtr.Zero;
                if (pUnkOuter != IntPtr.Zero)
                    return CLASS_E_NOAGGREGATION;

                var verb = new ExecuteCommandVerb();
                IntPtr punk = Marshal.GetIUnknownForObject(verb);
                try
                {
                    int hr = Marshal.QueryInterface(punk, in riid, out ppvObject);
                    return hr;
                }
                finally
                {
                    Marshal.Release(punk);
                }
            }

            public int LockServer(bool fLock) => S_OK;
        }

        // ================================================================
        //  Diagnostics: who launched us?
        // ================================================================
        private static string DescribeProcess()
        {
            string self;
            try { self = "pid " + Process.GetCurrentProcess().Id; }
            catch { self = "pid ?"; }

            string parent = "?";
            try
            {
                int ppid = GetParentProcessId();
                if (ppid > 0)
                {
                    string pname;
                    try { pname = Process.GetProcessById(ppid).ProcessName; }
                    catch { pname = "(exited)"; }
                    parent = pname + " (pid " + ppid + ")";
                }
            }
            catch (Exception ex)
            {
                parent = "unavailable (" + ex.Message + ")";
            }

            // When launched via DelegateExecute the parent should be svchost (RPCSS),
            // NOT OUTLOOK -- that is what lets us bypass the Office-child ASR rule.
            return self + ", launched by " + parent;
        }

        private static int GetParentProcessId()
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(Process.GetCurrentProcess().Handle,
                0 /* ProcessBasicInformation */, ref pbi, Marshal.SizeOf(pbi), out _);
            if (status != 0) return -1;
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }

        // ================================================================
        //  COM interop declarations
        // ================================================================
        [ComImport, Guid("7f9185b0-cb92-43c5-80a9-92277a4f7b54")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExecuteCommand
        {
            [PreserveSig] int SetKeyState(uint grfKeyState);
            [PreserveSig] int SetParameters([MarshalAs(UnmanagedType.LPWStr)] string pszParameters);
            [PreserveSig] int SetPosition(POINT pt);
            [PreserveSig] int SetShowWindow(int nShow);
            [PreserveSig] int SetNoShowUI(int fNoShowUI);
            [PreserveSig] int SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDirectory);
            [PreserveSig] int Execute();
        }

        [ComImport, Guid("1c9cd5bb-98e9-4491-a60f-31aacc72b83c")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectWithSelection
        {
            [PreserveSig] int SetSelection([MarshalAs(UnmanagedType.Interface)] IShellItemArray psia);
            [PreserveSig] int GetSelection(ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("85075acf-231f-40ea-9610-d26b7b58f638")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeCommand
        {
            [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszCommandName, IntPtr ppb);
        }

        [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectWithSite
        {
            [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object punkSite);
            [PreserveSig] int GetSite(ref Guid riid, out IntPtr ppv);
        }

        // Marker interface. Implementing it advertises the object as "agile" so COM
        // will not attempt to standard-marshal it across apartments (which fails for a
        // managed CCW and aborts the shell's verb invocation with E_NOINTERFACE).
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }

        // AHE_TYPE values for IExecuteCommandApplicationHostEnvironment.GetValue.
        private const int AHE_DESKTOP = 0;
        private const int AHE_IMMERSIVE = 1;

        // Implemented by out-of-process URL DelegateExecute handlers (e.g. browsers)
        // so the shell can pick the desktop vs. immersive activation environment.
        [ComImport, Guid("18b21aa9-e184-4ff0-9f5e-f882d03771b3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExecuteCommandApplicationHostEnvironment
        {
            [PreserveSig] int GetValue(out int pahe);
        }

        // Also implemented by browsers' DelegateExecute handlers; lets the launched
        // process pull itself to the foreground.
        [ComImport, Guid("00000145-0000-0000-c000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IForegroundTransfer
        {
            [PreserveSig] int AllowForegroundTransfer(IntPtr lpvReserved);
        }

        [ComImport, Guid("00000001-0000-0000-c000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IClassFactory
        {
            [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int GetCount(out uint pdwNumItems);
            [PreserveSig] int GetItemAt(uint dwIndex, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        // ---- ole32 ----
        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(
            ref Guid rclsid,
            [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
            uint dwClsContext, uint flags, out uint lpdwRegister);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint dwRegister);

        // ---- user32 message pump ----
        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // ---- ntdll (parent pid, diagnostics only) ----
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength, out int returnLength);
    }
}
