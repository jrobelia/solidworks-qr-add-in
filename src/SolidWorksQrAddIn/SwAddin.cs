using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Icons;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorksQrAddIn.Host;
using SolidWorksQrAddIn.UI;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace SolidWorksQrAddIn
{
    /// <summary>
    /// The COM-visible SolidWorks add-in object. Own COM identity (CLSID,
    /// ProgId, command group) for the QR add-in — nothing is shared with the
    /// InvenTree add-in.
    ///
    /// ConnectToSW wires the command surface; DisconnectFromSW removes the
    /// command group (permanently, so reloads never duplicate commands) and
    /// releases every field-held COM reference. The IAddInProbe test-hook
    /// surface lives in SwAddin.Probes.cs.
    /// </summary>
    [ComVisible(true)]
    [Guid("DA932451-66B9-4092-9534-292AD913DEAC")] // == AddinIdentity.Clsid
    [ProgId(AddinIdentity.ProgId)]
    [SwAddin(Title = AddinIdentity.Title, Description = AddinIdentity.Description, LoadAtStartup = true)]
    public partial class SwAddin : ISwAddin, IAddInProbe
    {
        private ISldWorks? _sw;
        private ICommandManager? _cmdMgr;
        private int _cookie;
        private bool _connected;
        private QrSketchSession? _session;
        private AddInWpfThread? _wpf;

        static SwAddin()
        {
            // The add-in is loaded into SLDWORKS.exe whose probing base is the
            // SolidWorks install dir — resolve our dependencies (Core, later
            // QRCoder/Clipper2) from the deployed add-in folder.
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var addinDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var dll = Path.Combine(addinDir, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
            };
        }

        public SwAddin()
        {
            Diag.Log("ctor — object created by SolidWorks");
        }

        // ---------------- ISwAddin ----------------

        public bool ConnectToSW(object thisSW, int cookie)
        {
            try
            {
                _sw = (ISldWorks)thisSW;
                _cookie = cookie;
                _sw.SetAddinCallbackInfo2(0, this, cookie);
                Diag.Log("SetAddinCallbackInfo2 ok");

                _cmdMgr = (ICommandManager)_sw.GetCommandManager(cookie);
                // Drop any stale runtime copy of our group from a previous load so
                // reconnecting can never produce a duplicate command.
                _cmdMgr.RemoveCommandGroup2(AddinIdentity.CommandGroupUserId, true);

                int errors = 0;
                var group = (ICommandGroup)_cmdMgr.CreateCommandGroup2(
                    AddinIdentity.CommandGroupUserId,
                    "QR Sketch",
                    "QR sketch commands",
                    "Create QR sketches on a selected plane or planar face",
                    -1, false, ref errors);
                Diag.Log($"CreateCommandGroup2 errors={errors} group={(group == null ? "null" : "ok")}");
                if (group == null)
                    return Fail("CreateCommandGroup2 returned null");

                group.AddCommandItem2(
                    "Create QR Sketch",
                    -1,
                    "Insert a native QR-code sketch on the selected plane or planar face",
                    "Creates one QR sketch on the selected reference plane or planar face, left open for editing",
                    0,
                    "CreateQrSketch",
                    "EnableQrSketch",
                    0,
                    (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem);

                // Toolbar/menu icons: IconList is a string[] of PNG paths —
                // one strip file per resolution (20..128), one icon wide for
                // our single command (ImageIndex 0). MainIconList dresses the
                // group's own toolbar button the same way. Extraction is
                // idempotent into <deployed add-in folder>\icons and must
                // never fail ConnectToSW — a read-only folder leaves the
                // command reachable by name, just unadorned.
                try
                {
                    string addinDir = Path.GetDirectoryName(
                        Assembly.GetExecutingAssembly().Location) ?? ".";
                    var icons = QrToolbarIcons.EnsureExtracted(
                        Path.Combine(addinDir, "icons"));
                    group.IconList = icons;
                    group.MainIconList = icons;
                    Diag.Log($"command group icons set ({icons.Length} sizes)");
                }
                catch (Exception ex)
                {
                    Diag.Log($"icon setup skipped: {ex.GetType().Name}: {ex.Message}");
                }

                group.HasMenu = true;
                group.HasToolbar = true;
                group.Activate();

                // The interaction state behind the one modeless window lives
                // here for the connection's lifetime; the view factory builds
                // a fresh WPF window only when the command opens one.
                _session = new QrSketchSession(CreateQrWindow);

                _connected = true;
                Diag.Log("connected");
                return true;
            }
            catch (Exception ex)
            {
                return Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool Fail(string why)
        {
            Diag.Log($"ConnectToSW FAILED: {why}");
            _connected = false;
            return false;
        }

        public bool DisconnectFromSW()
        {
            // _connected first: a command callback or a window Create handler
            // in flight sees "not connected" and reports instead of touching a
            // released _sw — nothing can create geometry after unload.
            _connected = false;
            // Release the modeless window first — it must not outlive the
            // add-in — then stop the STA pump that owned it.
            try { _session?.Release(); } catch { }
            _session = null;
            try { _wpf?.Shutdown(); } catch { }
            _wpf = null;
            try
            {
                if (_cmdMgr != null)
                {
                    // RuntimeOnly=false: full removal — a later load creates the
                    // group fresh instead of duplicating it.
                    _cmdMgr.RemoveCommandGroup2(AddinIdentity.CommandGroupUserId, false);
                    Marshal.ReleaseComObject(_cmdMgr);
                }
            }
            catch { /* unloading must never throw back into SolidWorks */ }
            _cmdMgr = null;

            try
            {
                if (_sw != null)
                    Marshal.ReleaseComObject(_sw);
            }
            catch { }
            _sw = null;
            return true;
        }

        // ---------------- command surface ----------------

        /// <summary>The one not-connected sentence every entry point shares.</summary>
        private const string NotConnectedMessage =
            "The QR add-in is not connected to SolidWorks.";

        /// <summary>
        /// SolidWorks invokes this by name when the command runs: open the one
        /// modeless QR window, or focus it if it is already open.
        /// </summary>
        public void CreateQrSketch()
        {
            try
            {
                if (_session == null || _sw == null || !_connected)
                {
                    Report(NotConnectedMessage);
                    return;
                }
                _session.OpenOrFocusWindow();
            }
            catch (Exception ex)
            {
                Report($"The QR window could not be opened: {ex.Message}");
            }
        }

        /// <summary>Enable callback: always enabled; context errors are reported on invoke.</summary>
        public int EnableQrSketch() => 1;

        private void Report(string message) =>
            _sw?.SendMsgToUser2(message,
                (int)swMessageBoxIcon_e.swMbWarning,
                (int)swMessageBoxBtn_e.swMbOk);

        /// <summary>
        /// View factory for the session: a fresh WPF window owned by the
        /// SolidWorks main window, created on the add-in's dedicated STA pump
        /// (callers — menu callback or COM probe — may be on any thread; the
        /// window marshals itself for every later call). The window pulls its
        /// state from the session and asks for a fresh host adapter per
        /// Create — no cached support.
        /// </summary>
        private IQrSketchWindow CreateQrWindow()
        {
            var owner = OwnerHandle();
            _wpf ??= new AddInWpfThread();
            var window = _wpf.Invoke(() => new QrSketchWindow(_session!, () =>
            {
                if (_sw == null || !_connected)
                    throw new SketchHostException(NotConnectedMessage);
                return new SolidWorksSketchHost(_sw);
            }, owner));
            Diag.Log("QR window created");
            return window;
        }

        /// <summary>
        /// The SolidWorks main-window handle for WPF ownership. IFrame is the
        /// documented route; the process main-window handle is the fallback.
        /// Zero means "unowned" — degraded, never fatal.
        /// </summary>
        private IntPtr OwnerHandle()
        {
            try
            {
                var frame = _sw?.Frame() as IFrame;
                long hwnd = frame?.GetHWndx64() ?? 0;
                if (hwnd != 0)
                    return new IntPtr(hwnd);
            }
            catch (Exception ex) { Diag.Log($"IFrame.GetHWndx64 failed: {ex.Message}"); }
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                    return hwnd;
            }
            catch { }
            return IntPtr.Zero;
        }

        // ---------------- COM registration ----------------

        /// <summary>
        /// RegAsm /codebase calls this: writes the SolidWorks discovery +
        /// startup entries. Machine-wide discovery (HKLM AddIns) needs admin;
        /// when absent we fall back to the per-user HKCU mirror that this
        /// baseline installation also honours.
        /// </summary>
        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                Apply(RegistrationPlan.MachineWide());
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Apply(RegistrationPlan.PerUser());
            }
        }

        /// <summary>
        /// RegAsm /unregister calls this: removes only this add-in's discovery /
        /// startup keys under both hives. Never touches other add-ins' entries.
        /// </summary>
        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            foreach (var path in RegistrationPlan.UnregisterPaths())
            {
                try { Registry.LocalMachine.DeleteSubKeyTree(path, false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
                try { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            }
        }

        private static void Apply(IEnumerable<RegistryWrite> entries)
        {
            foreach (var e in entries)
            {
                RegistryKey hive = e.Hive == RegistryHiveKind.LocalMachine
                    ? Registry.LocalMachine
                    : Registry.CurrentUser;
                using (RegistryKey? key = hive.CreateSubKey(e.KeyPath, true))
                {
                    if (key == null)
                        throw new UnauthorizedAccessException($"Could not open {e.KeyPath} for writing.");
                    if (e.Value is int dword)
                        key.SetValue("", dword, RegistryValueKind.DWord); // (default) value
                    else
                        key.SetValue(e.ValueName ?? "", e.Value, RegistryValueKind.String);
                }
            }
        }
    }
}
