using System;
using System.Collections.Generic;
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
        private ITaskpaneView? _taskPaneView;
        private QrTaskPaneControl? _pane;
        private PaneKeyboardFilter? _keyboardFilter;
        private int _cookie;
        private bool _connected;
        private QrSketchSession? _session;

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

                // Menu icons: IconList is a string[] of PNG paths — one strip
                // file per resolution (20..128), one icon wide for our single
                // command (ImageIndex 0). MainIconList dresses the group's own
                // button the same way. The task-pane tab reuses one of these
                // PNGs as its icon. Extraction must never fail ConnectToSW.
                // Icon extraction target is per-user first: an Inno-installed
                // DLL lives in Program Files (read-only), so beside-the-DLL
                // extraction fails there. QrIconLocations orders candidates;
                // the first that writes wins. Best-effort — a failure leaves
                // the command reachable by name and the tab unadorned.
                string paneIcon = string.Empty;
                string[]? icons = null;
                string? iconDir = null;
                string addinDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? ".";
                foreach (var dir in QrIconLocations.ExtractionDirectories(addinDir))
                {
                    try
                    {
                        icons = QrToolbarIcons.EnsureExtracted(dir);
                        iconDir = dir;
                        Diag.Log($"icons extracted to {dir}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Diag.Log($"icon extraction to {dir} failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                if (icons != null)
                {
                    try
                    {
                        group.IconList = icons;
                        group.MainIconList = icons;
                        // The task-pane tab renders ~16-18px and silently
                        // ignores the 32px strip — dedicated small render.
                        paneIcon = QrToolbarIcons.EnsurePaneIcon(iconDir!);
                        Diag.Log($"command group icons set ({icons.Length} sizes); pane icon {paneIcon}");
                    }
                    catch (Exception ex)
                    {
                        Diag.Log($"icon setup skipped: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Menu-only command surface: no custom toolbar — the QR UI is a
                // docked task-pane tab (user direction). The swToolbarItem flag
                // stays so an engineer can still pin the command themselves via
                // Tools → Customize.
                group.HasMenu = true;
                group.Activate();

                // The interaction state behind the pane lives here for the
                // connection's lifetime; the view factory returns the pane
                // adapter — the task-pane tab is the single surface.
                _session = new QrSketchSession(CreateQrWindow);

                _pane = new QrTaskPaneControl(
                    _session, CreateSketchHost,
                    () => { try { _taskPaneView?.ShowView(); } catch { } },
                    () => { try { _taskPaneView?.HideView(); } catch { } });

                _taskPaneView = (ITaskpaneView)_sw.CreateTaskpaneView2(
                    paneIcon, "QR Sketch");
                if (_taskPaneView == null)
                    return Fail("CreateTaskpaneView2 returned null");
                _taskPaneView.DisplayWindowFromHandlex64(_pane.Handle.ToInt64());
                Diag.Log("task pane created");

                // SolidWorks' pump eats WM_CHAR before the hosted WPF
                // HwndSource sees it ("can delete but not type"). A thread
                // message filter re-raising through ComponentDispatcher feeds
                // it back to WPF. Installed once per connect, forward-only,
                // best-effort: no filter must never break the pane.
                try
                {
                    _keyboardFilter = new PaneKeyboardFilter();
                    System.Windows.Forms.Application.AddMessageFilter(_keyboardFilter);
                    Diag.Log($"pane keyboard filter installed on thread {System.Threading.Thread.CurrentThread.ManagedThreadId}");
                }
                catch (Exception ex)
                {
                    _keyboardFilter = null;
                    Diag.Log($"pane keyboard filter skipped: {ex.GetType().Name}: {ex.Message}");
                }

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
            // Uninstall the keyboard message filter FIRST — it must never
            // outlive the hosted WPF surface it forwards messages to.
            try
            {
                if (_keyboardFilter != null)
                    System.Windows.Forms.Application.RemoveMessageFilter(_keyboardFilter);
                Diag.Log("pane keyboard filter removed");
            }
            catch { /* unloading must never throw back into SolidWorks */ }
            _keyboardFilter = null;
            // Release the pane surface first — it must not outlive the
            // add-in — then remove the task-pane view itself. Registered
            // undo handlers release their document references too, so an
            // unloaded add-in can never be called back into dead state.
            try { QrSketchUndoRegistry.ReleaseAll(); } catch { }
            try { _session?.Release(); } catch { }
            _session = null;
            try { _taskPaneView?.DeleteView(); } catch { }
            if (_taskPaneView != null)
            {
                try { Marshal.ReleaseComObject(_taskPaneView); } catch { }
                _taskPaneView = null;
            }
            try { _pane?.Dispose(); } catch { }
            _pane = null;
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
        /// SolidWorks invokes this by name when the command runs: show the QR
        /// task-pane tab, or bring it forward if it is already shown.
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
                Report($"The QR pane could not be shown: {ex.Message}");
            }
        }

        /// <summary>Enable callback: always enabled; context errors are reported on invoke.</summary>
        public int EnableQrSketch() => 1;

        private void Report(string message) =>
            _sw?.SendMsgToUser2(message,
                (int)swMessageBoxIcon_e.swMbWarning,
                (int)swMessageBoxBtn_e.swMbOk);

        /// <summary>
        /// View factory for the session: the task-pane adapter. The pane is
        /// created once in ConnectToSW on SolidWorks' STA UI thread and lives
        /// for the connection — the factory returns that same control so a
        /// "reopened" pane keeps its hosted view, and the adapter marshals
        /// every call onto the UI thread for arbitrary callers (menu callback,
        /// COM probe).
        /// </summary>
        private IQrSketchWindow CreateQrWindow()
        {
            if (_pane == null)
                throw new InvalidOperationException(NotConnectedMessage);
            return _pane;
        }

        /// <summary>
        /// Fresh host adapter per Create — never cached support. Called from
        /// the pane's Create button (already on the UI thread) and from probes.
        /// </summary>
        private ISketchHost CreateSketchHost()
        {
            if (_sw == null || !_connected)
                throw new SketchHostException(NotConnectedMessage);
            return new SolidWorksSketchHost(_sw);
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
