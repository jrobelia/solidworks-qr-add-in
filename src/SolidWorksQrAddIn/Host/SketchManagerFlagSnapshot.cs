using SolidWorks.Interop.sldworks;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// The ISketchManager flag values an insertion session overrides,
    /// captured as one bundle before the session touches them so every exit
    /// path — commit, abort, abandon — can restore them exactly.
    /// </summary>
    internal sealed class SketchManagerFlagSnapshot
    {
        private readonly bool _addToDB;
        private readonly bool _displayWhenAdded;
        private readonly bool _autoInference;
        private readonly bool _inferenceMode;

        private SketchManagerFlagSnapshot(
            bool addToDB, bool displayWhenAdded, bool autoInference, bool inferenceMode)
        {
            _addToDB = addToDB;
            _displayWhenAdded = displayWhenAdded;
            _autoInference = autoInference;
            _inferenceMode = inferenceMode;
        }

        /// <summary>Read the current flag values off the sketch manager.</summary>
        public static SketchManagerFlagSnapshot Capture(ISketchManager mgr) =>
            new SketchManagerFlagSnapshot(
                mgr.AddToDB, mgr.DisplayWhenAdded, mgr.AutoInference, mgr.InferenceMode);

        /// <summary>
        /// Write the captured values back. Best-effort per flag — a host
        /// that refuses one write still gets the others restored.
        /// </summary>
        public void Restore(ISketchManager mgr)
        {
            try { mgr.AddToDB = _addToDB; } catch { }
            try { mgr.DisplayWhenAdded = _displayWhenAdded; } catch { }
            try { mgr.AutoInference = _autoInference; } catch { }
            try { mgr.InferenceMode = _inferenceMode; } catch { }
        }
    }
}
