using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorks.Interop.sldworks;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// The live-session plumbing every mode shares (moved out of Program.cs
    /// under ticket #63): the default deploy dir, attach-or-launch via
    /// ProgID/CLSID + <see cref="Activator.CreateInstance(Type)"/> (the
    /// verified route — PowerShell late binding does NOT work here), the
    /// startup wait, probe acquisition through <c>GetAddInObject</c> with
    /// the late-bound fallback, the captured AddInsStartup flag (SolidWorks
    /// rewrites it to 0 on every runtime unload — restored at teardown so a
    /// probe run never unticks the user's Start-Up checkbox), and the
    /// spawned-instance teardown (kill by PID only — never ExitApp, never
    /// taskkill /IM, which would hit the user's interactive session).
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// The registered per-user deploy dir — the default when a mode's
        /// arg shape carries no deployDir (and the registered-copy path a
        /// stale auto-load could have answered from).
        /// </summary>
        private static readonly string DefaultDeployDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SolidWorksQrAddIn");

        /// <summary>
        /// true only while the attached instance is a fresh, invisible
        /// COM-launched one — the only kind it is safe to kill. An
        /// instance that is already visible (or already holds documents) is
        /// somebody's interactive session: the probe refuses to mutate it.
        /// </summary>
        private static bool _spawnedFresh;

        /// <summary>
        /// HKCU AddInsStartup\{clsid} value captured at launch. SolidWorks
        /// rewrites the flag to 0 on every runtime unload, so a probe run
        /// would silently untick the user's Start-Up checkbox — restored at
        /// teardown.
        /// </summary>
        private static int? _startupFlagAtLaunch;
        private static bool _startupFlagCaptured;

        private static readonly string StartupFlagKey =
            @"HKEY_CURRENT_USER\" + AddinIdentity.AddinsStartupKeyPath + @"\" + AddinIdentity.Clsid;

        private static void CaptureStartupFlag()
        {
            if (_startupFlagCaptured) return;   // first attach wins — a later one
            _startupFlagCaptured = true;        // could read our own unload's 0
            try
            {
                if (Registry.GetValue(StartupFlagKey, "", null) is int v)
                    _startupFlagAtLaunch = v;
            }
            catch { }
        }

        private static void RestoreStartupFlag()
        {
            if (_startupFlagAtLaunch == null) return;
            try
            {
                Registry.SetValue(StartupFlagKey, "", _startupFlagAtLaunch.Value,
                    RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>
        /// Tears down the spawned instance: restores the captured Start-Up
        /// flag, closes documents (so no recovery prompt greets the user's
        /// next launch), then kills the process by PID. Never ExitApp — a
        /// graceful shutdown flushes this instance's stale per-user settings
        /// snapshot over whatever the user's interactive session saved
        /// meanwhile. Never taskkill /IM either — that would kill the
        /// user's session as well.
        /// </summary>
        private static void TeardownSpawned(ISldWorks? sw)
        {
            RestoreStartupFlag();
            if (!_spawnedFresh || sw == null) return;
            try { sw.CloseAllDocuments(true); } catch { }
            try { Process.GetProcessById(sw.GetProcessID()).Kill(); } catch { }
        }

        private static ISldWorks? AttachOrLaunch(string progId)
        {
            // Accepts a ProgID ("SldWorks.Application.31") or a "{clsid}" —
            // some installed versions register a versioned ProgID whose
            // CLSID lookup fails; the raw CLSID still activates.
            Type? t = null;
            if (progId.StartsWith("{") && Guid.TryParse(progId, out var clsid))
                t = Type.GetTypeFromCLSID(clsid);
            else
                t = Type.GetTypeFromProgID(progId);
            if (t == null) { Console.WriteLine($"FAIL  ProgID/CLSID {progId} not registered"); return null; }
            var created = Activator.CreateInstance(t) as ISldWorks;
            if (created != null) CaptureStartupFlag();
            return created;
        }

        private static void WaitForStartup(ISldWorks sw)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                try { if (sw.StartupProcessCompleted) return; } catch { }
                Thread.Sleep(1000);
            }
        }

        private static IAddInProbe? TryGetProbe(ISldWorks sw)
        {
            var keys = new[]
            {
                AddinIdentity.Clsid,
                AddinIdentity.Clsid.Trim('{', '}'),
                AddinIdentity.Clsid.ToLowerInvariant(),
                AddinIdentity.ProgId,
            };
            // Control: the registered InvenTree add-in's object — distinguishes
            // "our add-in isn't loaded" from "GetAddInObject usage is wrong".
            foreach (var key in keys.Concat(new[] { "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}" }))
            {
                try
                {
                    object? raw = sw.GetAddInObject(key);
                    if (raw == null) { Console.WriteLine($"# GetAddInObject({key}) -> null"); continue; }
                    Console.WriteLine($"# GetAddInObject({key}) -> type={raw.GetType().FullName}");
                    if (raw is IAddInProbe asProbe)
                    {
                        Console.WriteLine("#   cast to IAddInProbe OK");
                        return asProbe;
                    }
                    bool isOurs = raw is SolidWorks.Interop.swpublished.ISwAddin;
                    Console.WriteLine($"#   not IAddInProbe; IsSwAddin={isOurs} — trying IDispatch invocation");
                    if (isOurs)
                        return new LateBoundProbe(raw);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# GetAddInObject({key}) -> {ex.GetType().Name}: {ex.Message}");
                }
            }
            return null;
        }
    }
}
