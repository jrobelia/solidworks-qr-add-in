using SolidWorksQrAddIn.Core;

namespace SolidWorksQrAddIn.LiveProbe
{
    public static partial class Program
    {
        /// <summary>
        /// Calls the add-in's public probe methods through the RCW's dispatch
        /// interface (InvokeMember by name) — the same route SolidWorks uses
        /// for command callbacks. Used when the RCW won't QI our managed
        /// IAddInProbe interface.
        /// </summary>
        private sealed class LateBoundProbe : IAddInProbe
        {
            private readonly object _addin;

            public LateBoundProbe(object addin) { _addin = addin; }

            private string Invoke(string method, params object[] args)
            {
                var result = _addin.GetType().InvokeMember(
                    method,
                    System.Reflection.BindingFlags.InvokeMethod |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance,
                    null, _addin, args);
                return result as string ?? "(no result)";
            }

            public string RunQrSketchProbe() => Invoke("RunQrSketchProbe");
            public string RunFailingQrSketchProbe() => Invoke("RunFailingQrSketchProbe");
            public string RunCleanupFailQrSketchProbe() => Invoke("RunCleanupFailQrSketchProbe");
            public string AddinStateProbe() => Invoke("AddinStateProbe");
            public string OpenQrWindowProbe() => Invoke("OpenQrWindowProbe");
            public string CloseQrWindowProbe() => Invoke("CloseQrWindowProbe");
            public string WindowStateProbe() => Invoke("WindowStateProbe");
            public string SetQrField(string name, string value) => Invoke("SetQrField", name, value);
            public string CreateQrFromWindowProbe() => Invoke("CreateQrFromWindowProbe");
            public string UndoRedoQrProbe() => Invoke("UndoRedoQrProbe");
            public string PreviewSignatureProbe() => Invoke("PreviewSignatureProbe");
            public string PatternCensusProbe(string sketchName) => Invoke("PatternCensusProbe", sketchName);
            public string PatternDimsProbe(string sketchName) => Invoke("PatternDimsProbe", sketchName);
            public string SetPatternDimProbe(string dimensionName, double valueM)
                => Invoke("SetPatternDimProbe", dimensionName, valueM);
            public string SwPropertiesProbe() => Invoke("SwPropertiesProbe");
            public string SwPropertyResolveProbe(string name) => Invoke("SwPropertyResolveProbe", name);
        }
    }
}
