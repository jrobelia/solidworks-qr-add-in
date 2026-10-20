using System.Linq;
using SolidWorksQrAddIn.Core.Registration;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// The registration plan is the pure description of every registry write
    /// the add-in's [ComRegisterFunction] (or the dev script) must perform —
    /// the seam where "our own discovery/startup identity" is testable
    /// headlessly.
    /// </summary>
    public class RegistrationPlanTests
    {
        private const string InvenTreeClsid = "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}";

        [Fact]
        public void Machine_wide_plan_discovers_the_addin_under_HKLM()
        {
            var entries = RegistrationPlan.MachineWide();

            var key = entries.Where(e => e.KeyPath ==
                @"SOFTWARE\SolidWorks\AddIns\" + AddinIdentity.Clsid).ToList();
            Assert.Equal(RegistryHiveKind.LocalMachine, key.First().Hive);
            Assert.Contains(key, e => e.ValueName == null && Equals(e.Value, 1));
            Assert.Contains(key, e => e.ValueName == "Title" && Equals(e.Value, AddinIdentity.Title));
            Assert.Contains(key, e => e.ValueName == "Description" && Equals(e.Value, AddinIdentity.Description));
        }

        [Fact]
        public void Machine_wide_plan_enables_startup_under_HKCU_only_when_absent()
        {
            var entries = RegistrationPlan.MachineWide();

            Assert.Contains(entries, e =>
                e.Hive == RegistryHiveKind.CurrentUser &&
                e.KeyPath == @"SOFTWARE\SolidWorks\AddInsStartup\" + AddinIdentity.Clsid &&
                e.ValueName == null && Equals(e.Value, 1) && e.PreserveIfPresent);
        }

        [Fact]
        public void Per_user_plan_discovers_and_enables_the_addin_under_HKCU_only()
        {
            var entries = RegistrationPlan.PerUser();

            Assert.All(entries, e => Assert.Equal(RegistryHiveKind.CurrentUser, e.Hive));
            var addins = entries.Where(e => e.KeyPath ==
                @"SOFTWARE\SolidWorks\AddIns\" + AddinIdentity.Clsid).ToList();
            Assert.Contains(addins, e => e.ValueName == null && Equals(e.Value, 1));
            Assert.Contains(addins, e => e.ValueName == "Title");
            Assert.Contains(addins, e => e.ValueName == "Description");
            Assert.Contains(entries, e =>
                e.KeyPath == @"SOFTWARE\SolidWorks\AddInsStartup\" + AddinIdentity.Clsid &&
                Equals(e.Value, 1) && e.PreserveIfPresent);
        }

        [Fact]
        public void Every_planned_key_is_scoped_to_this_addins_clsid()
        {
            foreach (var e in RegistrationPlan.MachineWide().Concat(RegistrationPlan.PerUser()))
                Assert.Contains(AddinIdentity.Clsid, e.KeyPath);
        }

        [Fact]
        public void No_planned_entry_reuses_the_inventree_addins_identity()
        {
            foreach (var e in RegistrationPlan.MachineWide()
                         .Concat(RegistrationPlan.PerUser())
                         .Concat(RegistrationPlan.UnregisterPaths().Select(p =>
                             new RegistryWrite(RegistryHiveKind.CurrentUser, p, null, ""))))
            {
                Assert.DoesNotContain(InvenTreeClsid, e.KeyPath, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Unregister_covers_exactly_the_registered_addin_and_startup_keys()
        {
            var paths = RegistrationPlan.UnregisterPaths().ToList();

            Assert.Contains(paths, p => p ==
                @"SOFTWARE\SolidWorks\AddIns\" + AddinIdentity.Clsid);
            Assert.Contains(paths, p => p ==
                @"SOFTWARE\SolidWorks\AddInsStartup\" + AddinIdentity.Clsid);
            // No blanket deletes: every path must be scoped to our CLSID.
            Assert.All(paths, p => Assert.Contains(AddinIdentity.Clsid, p));
        }
    }
}
