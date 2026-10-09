using System;

namespace SolidWorksQrAddIn
{
    /// <summary>
    /// Recreation of the SwAddin attribute used by the official SolidWorks C#
    /// add-in examples — it is sample code, not an SDK type. Carries the values
    /// the COM registration functions write into the SolidWorks AddIns key.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SwAddinAttribute : Attribute
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool LoadAtStartup { get; set; } = true;
    }
}
