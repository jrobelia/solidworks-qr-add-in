using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Registration;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Guards the new COM identity: the add-in must never reuse the InvenTree
    /// add-in's GUID, and every identity surface must be internally consistent.
    /// </summary>
    public class ComIdentityTests
    {
        private const string InvenTreeClsid = "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}";

        [Fact]
        public void Addin_clsid_is_a_valid_guid()
        {
            Assert.True(System.Guid.TryParse(AddinIdentity.Clsid, out _));
        }

        [Fact]
        public void Addin_clsid_is_not_the_inventree_addins_guid()
        {
            Assert.NotEqual(InvenTreeClsid, AddinIdentity.Clsid);
        }

        [Fact]
        public void Probe_interface_iid_is_a_valid_distinct_guid_matching_the_declared_constant()
        {
            var attr = (System.Runtime.InteropServices.GuidAttribute?)
                System.Attribute.GetCustomAttribute(
                    typeof(IAddInProbe), typeof(System.Runtime.InteropServices.GuidAttribute));
            Assert.NotNull(attr);
            Assert.True(System.Guid.TryParse(attr!.Value, out _));
            // The attribute needs a literal; it must stay in sync with the constant.
            Assert.Equal(AddinIdentity.ProbeIid.Trim('{', '}'), attr.Value);
            Assert.NotEqual(
                AddinIdentity.Clsid.Trim('{', '}').ToLowerInvariant(),
                attr.Value.ToLowerInvariant());
            Assert.NotEqual(
                InvenTreeClsid.Trim('{', '}').ToLowerInvariant(),
                attr.Value.ToLowerInvariant());
        }

        [Fact]
        public void Probe_interface_is_com_visible()
        {
            var attr = (System.Runtime.InteropServices.ComVisibleAttribute?)
                System.Attribute.GetCustomAttribute(
                    typeof(IAddInProbe), typeof(System.Runtime.InteropServices.ComVisibleAttribute));
            Assert.NotNull(attr);
            Assert.True(attr!.Value);
        }
    }
}
