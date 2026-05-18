using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Read/write the Atlas-specific SolidWorks custom properties so files
    /// become self-identifying. The "PART_NUMBER" property on a SLDPRT/SLDASM
    /// is the canonical identifier; filename parsing is the fallback.
    ///
    /// Properties are configuration-independent (we use the empty config name
    /// "" which means "all configs"). This matches how engineers typically
    /// set part numbers in template files.
    /// </summary>
    public static class CustomProperties
    {
        public const string PartNumberKey = "PART_NUMBER";
        public const string AtlasRevisionKey = "ATLAS_REVISION";

        public static string Read(IModelDoc2 doc, string key)
        {
            if (doc == null) return null;
            var mgr = doc.Extension.CustomPropertyManager[""];
            if (mgr == null) return null;
            // Get5 returns the resolved value (templates can interpolate);
            // overload args: name, useCached, retVal, retEvaluated, wasResolved.
            string val = "", evaluated = "";
            // swCustomInfoGetType_e signature varies across SW versions; the
            // simpler Get4 takes name, useCached, out val, out evaluated.
            mgr.Get4(key, false, out val, out evaluated);
            return string.IsNullOrEmpty(evaluated) ? val : evaluated;
        }

        public static bool Write(IModelDoc2 doc, string key, string value)
        {
            if (doc == null) return false;
            var mgr = doc.Extension.CustomPropertyManager[""];
            if (mgr == null) return false;
            // Add3 overwrite-mode 2 = swCustomPropertyDeleteAndAdd (always replace).
            int rc = mgr.Add3(key, (int)swCustomInfoType_e.swCustomInfoText, value, 2);
            return rc == (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged;
        }
    }
}
