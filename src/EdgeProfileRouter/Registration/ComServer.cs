using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using EdgeProfileRouter.Diagnostics;

namespace EdgeProfileRouter.Registration;

/// <summary>
/// Out-of-process COM server for the browser ProgId's <c>DelegateExecute</c> handler.
/// COM/RPCSS starts this process instead of making the application that opened the link
/// create <c>EdgeProfileRouter.exe</c> directly.
/// </summary>
internal static class ComServer
{
    private const int S_OK = 0;
    private static readonly int E_NOINTERFACE = unchecked((int)0x80004002);
    private static readonly int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_SINGLEUSE = 0x0;
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint COINIT_DISABLE_OLE1DDE = 0x4;

    private const int SIGDN_NORMALDISPLAY = 0;
    private static readonly int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
    private static readonly int SIGDN_URL = unchecked((int)0x80068000);

    private const uint WM_TIMER = 0x0113;
    private const uint WM_QUIT = 0x0012;
    private static readonly UIntPtr WatchdogTimerId = (UIntPtr)1;

    private const uint WatchdogMs = 120000;
    private const int QuitDelayMs = 250;

    private static uint _mainThreadId;

    /// <summary>
    /// Registers the single-use class factory and pumps the STA until the shell invokes the verb.
    /// </summary>
    internal static int RunServer()
    {
        Log.Write("COM server starting. " + DescribeProcess());
        _mainThreadId = GetCurrentThreadId();

        int initResult = CoInitializeEx(
            IntPtr.Zero,
            COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
        if (initResult < 0)
        {
            Log.Write("CoInitializeEx failed: 0x"
                + initResult.ToString("X8", CultureInfo.InvariantCulture));
            return 1;
        }

        uint cookie = 0;
        bool registered = false;
        try
        {
            var factory = new ClassFactory();
            Guid clsid = new(BrowserRegistration.HandlerClsid);
            int result = CoRegisterClassObject(
                ref clsid,
                factory,
                CLSCTX_LOCAL_SERVER,
                REGCLS_SINGLEUSE,
                out cookie);
            if (result < 0)
            {
                Log.Write("CoRegisterClassObject failed: 0x"
                    + result.ToString("X8", CultureInfo.InvariantCulture));
                return 1;
            }

            registered = true;
            Log.Write("COM class object registered. Entering message loop.");
            SetTimer(IntPtr.Zero, WatchdogTimerId, WatchdogMs, IntPtr.Zero);
            MessageLoop();
        }
        finally
        {
            if (registered && cookie != 0)
            {
                try
                {
                    CoRevokeClassObject(cookie);
                }
                catch
                {
                    // REGCLS_SINGLEUSE may already have revoked the class object.
                }
            }

            CoUninitialize();
        }

        Log.Write("COM server exiting.");
        return 0;
    }

    private static void MessageLoop()
    {
        int result;
        while ((result = GetMessage(out MSG message, IntPtr.Zero, 0, 0)) != 0)
        {
            if (result == -1)
                break;

            if (message.message == WM_TIMER)
            {
                PostQuitMessage(0);
                continue;
            }

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private static void ScheduleQuit()
    {
        uint threadId = _mainThreadId;
        if (threadId == 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(QuitDelayMs);
                PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // The watchdog remains as a safety net.
            }
        });
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ExecuteCommandVerb :
        IExecuteCommand,
        IObjectWithSelection,
        IInitializeCommand,
        IObjectWithSite,
        IExecuteCommandApplicationHostEnvironment,
        IForegroundTransfer,
        IAgileObject
    {
        private IShellItemArray? _selection;
        private string? _parameters;
        private object? _site;

        public int SetKeyState(uint grfKeyState) => S_OK;

        public int SetParameters(string? parameters)
        {
            _parameters = parameters;
            Log.Write("IExecuteCommand.SetParameters: " + (parameters ?? "(null)"));
            return S_OK;
        }

        public int SetPosition(POINT point) => S_OK;
        public int SetShowWindow(int showWindow) => S_OK;
        public int SetNoShowUI(int noShowUi) => S_OK;
        public int SetDirectory(string? directory) => S_OK;

        public int Execute()
        {
            try
            {
                string? input = ResolveInput();
                if (string.IsNullOrWhiteSpace(input))
                {
                    Log.Write("COM Execute could not determine a URL or file from the shell invocation.");
                }
                else
                {
                    Log.Write("COM Execute resolved input: " + input);
                    Program.RouteShellInput(input);
                }
            }
            catch (Exception ex)
            {
                Log.Write("COM Execute failed: " + ex);
            }
            finally
            {
                ScheduleQuit();
            }

            return S_OK;
        }

        private string? ResolveInput()
        {
            string? parameters = NormalizeCandidate(_parameters);
            if (LooksLikeWebUrl(parameters))
                return parameters;

            if (_selection is not null)
            {
                string? selection = InputFromSelection(_selection);
                if (!string.IsNullOrWhiteSpace(selection))
                    return selection;
            }

            return parameters;
        }

        private static string? InputFromSelection(IShellItemArray selection)
        {
            try
            {
                if (selection.GetCount(out uint count) < 0 || count == 0)
                    return null;
                if (selection.GetItemAt(0, out IShellItem? item) < 0 || item is null)
                    return null;

                string? fallback = null;
                foreach (int displayNameType in new[]
                {
                    SIGDN_URL,
                    SIGDN_DESKTOPABSOLUTEPARSING,
                    SIGDN_NORMALDISPLAY,
                })
                {
                    string? candidate = NormalizeCandidate(DisplayName(item, displayNameType));
                    Log.Write("COM selection display name (SIGDN=0x"
                        + displayNameType.ToString("X", CultureInfo.InvariantCulture)
                        + "): " + (candidate ?? "(null)"));

                    if (LooksLikeWebUrl(candidate))
                        return candidate;
                    fallback ??= candidate;
                }

                return fallback;
            }
            catch (Exception ex)
            {
                Log.Write("Reading COM shell selection failed: " + ex.Message);
                return null;
            }
        }

        private static string? DisplayName(IShellItem item, int displayNameType)
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                if (item.GetDisplayName(displayNameType, out value) != S_OK
                    || value == IntPtr.Zero)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(value);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (value != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(value);
            }
        }

        private static string? NormalizeCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string result = value.Trim();
            if (result.Length >= 2 && result[0] == '"' && result[^1] == '"')
                result = result[1..^1].Trim();
            return result.Length == 0 ? null : result;
        }

        private static bool LooksLikeWebUrl(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return false;

            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        public int SetSelection(IShellItemArray? selection)
        {
            _selection = selection;
            Log.Write("IObjectWithSelection.SetSelection received: " + (selection is not null));
            return S_OK;
        }

        public int GetSelection(ref Guid interfaceId, out IntPtr value)
        {
            value = IntPtr.Zero;
            if (_selection is null)
                return E_NOINTERFACE;

            IntPtr unknown = Marshal.GetIUnknownForObject(_selection);
            try
            {
                return Marshal.QueryInterface(unknown, in interfaceId, out value);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int Initialize(string? commandName, IntPtr propertyBag)
        {
            Log.Write("IInitializeCommand.Initialize verb: " + (commandName ?? "(null)"));
            return S_OK;
        }

        public int SetSite(object? site)
        {
            _site = site;
            return S_OK;
        }

        public int GetSite(ref Guid interfaceId, out IntPtr value)
        {
            value = IntPtr.Zero;
            if (_site is null)
                return E_NOINTERFACE;

            IntPtr unknown = Marshal.GetIUnknownForObject(_site);
            try
            {
                return Marshal.QueryInterface(unknown, in interfaceId, out value);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int GetValue(out int applicationHostEnvironment)
        {
            applicationHostEnvironment = AHE_DESKTOP;
            return S_OK;
        }

        public int AllowForegroundTransfer(IntPtr reserved) => S_OK;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ClassFactory : IClassFactory
    {
        public int CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr value)
        {
            value = IntPtr.Zero;
            if (outerUnknown != IntPtr.Zero)
                return CLASS_E_NOAGGREGATION;

            var verb = new ExecuteCommandVerb();
            IntPtr unknown = Marshal.GetIUnknownForObject(verb);
            try
            {
                return Marshal.QueryInterface(unknown, in interfaceId, out value);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool lockServer) => S_OK;
    }

    private static string DescribeProcess()
    {
        string self = "pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            int parentId = GetParentProcessId();
            if (parentId <= 0)
                return self + ", parent unavailable";

            string parentName;
            try
            {
                parentName = Process.GetProcessById(parentId).ProcessName;
            }
            catch
            {
                parentName = "(exited)";
            }

            return self + ", launched by " + parentName + " (pid "
                + parentId.ToString(CultureInfo.InvariantCulture) + ")";
        }
        catch (Exception ex)
        {
            return self + ", parent unavailable (" + ex.Message + ")";
        }
    }

    private static int GetParentProcessId()
    {
        var info = new PROCESS_BASIC_INFORMATION();
        int status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0,
            ref info,
            Marshal.SizeOf(info),
            out _);
        return status == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : -1;
    }

    private const int AHE_DESKTOP = 0;

    [ComImport, Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExecuteCommand
    {
        [PreserveSig] int SetKeyState(uint keyState);
        [PreserveSig] int SetParameters([MarshalAs(UnmanagedType.LPWStr)] string? parameters);
        [PreserveSig] int SetPosition(POINT point);
        [PreserveSig] int SetShowWindow(int showWindow);
        [PreserveSig] int SetNoShowUI(int noShowUi);
        [PreserveSig] int SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string? directory);
        [PreserveSig] int Execute();
    }

    [ComImport, Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSelection
    {
        [PreserveSig]
        int SetSelection([MarshalAs(UnmanagedType.Interface)] IShellItemArray? selection);

        [PreserveSig]
        int GetSelection(ref Guid interfaceId, out IntPtr value);
    }

    [ComImport, Guid("85075ACF-231F-40EA-9610-D26B7B58F638")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeCommand
    {
        [PreserveSig]
        int Initialize([MarshalAs(UnmanagedType.LPWStr)] string? commandName, IntPtr propertyBag);
    }

    [ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        [PreserveSig]
        int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);

        [PreserveSig]
        int GetSite(ref Guid interfaceId, out IntPtr value);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport, Guid("18B21AA9-E184-4FF0-9F5E-F882D03771B3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExecuteCommandApplicationHostEnvironment
    {
        [PreserveSig] int GetValue(out int applicationHostEnvironment);
    }

    [ComImport, Guid("00000145-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IForegroundTransfer
    {
        [PreserveSig] int AllowForegroundTransfer(IntPtr reserved);
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr value);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(int displayNameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem item, uint hint, out int order);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int GetAttributes(int attributeFlags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);
        [PreserveSig] int EnumItems(out IntPtr enumerator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pointX;
        public int pointY;
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

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint initialization);

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        ref Guid classId,
        [MarshalAs(UnmanagedType.IUnknown)] object classFactory,
        uint classContext,
        uint flags,
        out uint registrationCookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint registrationCookie);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr window, uint minimumMessage, uint maximumMessage);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(
        IntPtr window,
        UIntPtr timerId,
        uint interval,
        IntPtr timerCallback);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);
}
