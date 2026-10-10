using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Registration
{
    /// <summary>Which registry hive a planned write/delete targets.</summary>
    public enum RegistryHiveKind
    {
        CurrentUser,
        LocalMachine,
    }

    /// <summary>
    /// One planned registry write. <see cref="ValueName"/> == null means the
    /// key's (default) value. <see cref="Value"/> is a string (REG_SZ) or an
    /// int (REG_DWORD).
    /// </summary>
    public sealed class RegistryWrite
    {
        public RegistryWrite(RegistryHiveKind hive, string keyPath, string? valueName, object value)
        {
            Hive = hive;
            KeyPath = keyPath;
            ValueName = valueName;
            Value = value;
        }

        public RegistryHiveKind Hive { get; }
        public string KeyPath { get; }
        public string? ValueName { get; }
        public object Value { get; }
    }

    /// <summary>
    /// The add-in's COM/discovery identity. This GUID was freshly generated for
    /// this project — it must never collide with or reuse the InvenTree
    /// add-in's GUID ({A1B2C3D4-E5F6-7890-ABCD-EF1234567890}).
    /// </summary>
    public static class AddinIdentity
    {
        /// <summary>CLSID of the SwAddin class — also the SolidWorks AddIns/AddInsStartup key name.</summary>
        public const string Clsid = "{DA932451-66B9-4092-9534-292AD913DEAC}";

        /// <summary>COM-visible test-hook interface IID (IAddInProbe).</summary>
        public const string ProbeIid = "{4813B19B-E14E-4201-B961-4C7AC791DDD8}";

        public const string Title = "SolidWorks QR Add-In";
        public const string Description = "Creates native QR-code sketches from text or URLs inside SolidWorks.";
        public const string ProgId = "SolidWorksQrAddIn.SwAddin";

        /// <summary>userID for the add-in's command group (distinctive constant, not reused anywhere).</summary>
        public const int CommandGroupUserId = 0x51AA;

        public const string AddinsKeyPath = @"SOFTWARE\SolidWorks\AddIns";
        public const string AddinsStartupKeyPath = @"SOFTWARE\SolidWorks\AddInsStartup";
    }

    /// <summary>
    /// Pure description of the registry writes SolidWorks needs for add-in
    /// discovery/startup. Applying the plan is done by SwAddin's
    /// [ComRegisterFunction] (admin/machine-wide path) or the dev PowerShell
    /// script (per-user path) — keeping the plan itself headlessly testable.
    /// </summary>
    public static class RegistrationPlan
    {
        /// <summary>
        /// The official layout for a machine-wide (RegAsm /codebase as admin)
        /// install: HKLM AddIns discovery entry + per-user startup flag.
        /// </summary>
        public static IReadOnlyList<RegistryWrite> MachineWide() => new[]
        {
            new RegistryWrite(RegistryHiveKind.LocalMachine,
                AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid, null, 1),
            new RegistryWrite(RegistryHiveKind.LocalMachine,
                AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid, "Title", AddinIdentity.Title),
            new RegistryWrite(RegistryHiveKind.LocalMachine,
                AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid, "Description", AddinIdentity.Description),
            new RegistryWrite(RegistryHiveKind.CurrentUser,
                AddinIdentity.AddinsStartupKeyPath + @"\" + AddinIdentity.Clsid, null, 1),
        };

        /// <summary>
        /// Per-user (no admin) development install: discovery + startup entries
        /// both under HKCU. The COM CLSID/InprocServer32 block goes under
        /// HKCU\SOFTWARE\Classes — written by the dev script, not this plan.
        /// </summary>
        public static IReadOnlyList<RegistryWrite> PerUser() => new[]
        {
            new RegistryWrite(RegistryHiveKind.CurrentUser,
                AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid, null, 1),
            new RegistryWrite(RegistryHiveKind.CurrentUser,
                AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid, "Title", AddinIdentity.Title),
            new RegistryWrite(RegistryHiveKind.CurrentUser,
                AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid, "Description", AddinIdentity.Description),
            new RegistryWrite(RegistryHiveKind.CurrentUser,
                AddinIdentity.AddinsStartupKeyPath + @"\" + AddinIdentity.Clsid, null, 1),
        };

        /// <summary>
        /// Key paths (hive-relative, under both hives) that unregister deletes.
        /// Always scoped to this add-in's CLSID — never a blanket hive delete.
        /// </summary>
        public static IReadOnlyList<string> UnregisterPaths() => new[]
        {
            AddinIdentity.AddinsKeyPath + @"\" + AddinIdentity.Clsid,
            AddinIdentity.AddinsStartupKeyPath + @"\" + AddinIdentity.Clsid,
        };
    }
}
