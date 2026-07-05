using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using pfcls;

namespace AtlasCreoSpike
{
    // Self-diagnosing spike for the Creo VB API (async / COM), Creo 10.
    //
    // Goal: prove, on the user's REAL Creo, the five primitives the future
    // CreoAdapter needs, before we commit to the full adapter:
    //   [1] attach to a running Creo session
    //   [2] read the active model (TOP.asm)
    //   [3] list every model loaded in session  (reliability floor)
    //   [4] walk the assembly tree recursively   (core of WalkAssembly)
    //   [5] export one model to STEP             (neutral artifact)
    //
    // Design: exactly ONE strongly-typed token is used - `new CCpfcAsyncConnection()` -
    // so the project compiles against the pfcls interop. Everything else is
    // late-bound via `dynamic` + reflection over the pfcls assembly, so any
    // Creo-10 name/signature difference surfaces as a LOGGED runtime message
    // (with a dump of the real interface members) instead of blocking the run.
    // The log file is the deliverable - paste it back and we correct the adapter.
    internal static class Program
    {
        private static StreamWriter _log;
        private static readonly Assembly Pfc = typeof(CCpfcAsyncConnection).Assembly;

        [STAThread]
        private static int Main()
        {
            string logPath = Path.Combine(Path.GetTempPath(), "creo_spike.log");
            _log = new StreamWriter(logPath, false) { AutoFlush = true };

            Log("=== Atlas Creo VB-API spike ===");
            Log("pfcls interop : " + Pfc.FullName);
            Log("log file      : " + logPath);

            dynamic session = Connect();
            if (session == null) { Finish(); return 1; }

            dynamic active = ShowActiveModel(session);
            ListModels(session);
            if (active != null) WalkTree(active);
            if (active != null) ExportStep(active);

            Finish();
            return 0;
        }

        // [1] Attach to a running Creo session ---------------------------------
        private static dynamic Connect()
        {
            Log("");
            Log("[1] Connecting to the running Creo session ...");

            dynamic connClass;
            try { connClass = new CCpfcAsyncConnection(); }
            catch (Exception ex) { Log("    cannot create CCpfcAsyncConnection: " + ex.Message); return null; }

            // Connect(user, password, host, timeoutSeconds). Try a few arg shapes.
            var argsets = new[]
            {
                new object[] { null, null, null, 5 },
                new object[] { null, null, null, null },
                new object[] { "", "", ".", 5 },
            };
            foreach (var a in argsets)
            {
                try
                {
                    dynamic conn = connClass.Connect(a[0], a[1], a[2], a[3]);
                    if (conn == null) continue;
                    dynamic session = conn.Session;
                    Log("    connected - session acquired.");
                    return session;
                }
                catch (Exception ex) { Log("    connect attempt failed: " + ex.Message); }
            }
            Log("    ALL connect attempts failed. Is Creo running with the VB API installed?");
            return null;
        }

        // [2] Active model (should be TOP.asm) ---------------------------------
        private static dynamic ShowActiveModel(dynamic session)
        {
            Log("");
            Log("[2] Active model:");
            dynamic m = null;
            try { m = session.CurrentModel; }
            catch
            {
                try { m = session.GetCurrentModel(); }
                catch (Exception ex)
                {
                    Log("    CurrentModel / GetCurrentModel failed: " + ex.Message);
                    DumpPfcInterface("basesession");
                    return null;
                }
            }
            if (m == null) { Log("    (none - open TOP.asm in Creo and re-run)"); return null; }
            Log("    FileName = " + Try(() => m.FileName));
            Log("    FullName = " + Try(() => m.FullName));
            Log("    Type     = " + Try(() => m.Type));
            return m;
        }

        // [3] Reliability floor: every loaded model ----------------------------
        private static void ListModels(dynamic session)
        {
            Log("");
            Log("[3] Models loaded in session:");
            try
            {
                dynamic models = session.ListModels();
                int n = (int)models.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic m = models.Item(i);
                    Log("    - " + Try(() => m.FileName) + "   (type " + Try(() => m.Type) + ")");
                }
                if (n == 0) Log("    (empty)");
            }
            catch (Exception ex) { Log("    ListModels failed: " + ex.Message); DumpPfcInterface("basesession"); }
        }

        // [4] Recursive component walk - core of WalkAssembly ------------------
        private static void WalkTree(dynamic active)
        {
            Log("");
            Log("[4] Assembly tree (recursive component walk):");
            Log("    " + Try(() => (string)active.FileName));
            try { WalkInto(active, active, new List<int>(), 1); }
            catch (Exception ex) { Log("    walk failed: " + ex.Message); DumpPfcInterface("solid", "componentfeat"); }
        }

        private static void WalkInto(dynamic root, dynamic asm, List<int> path, int depth)
        {
            // ListFeaturesByType(visibleOnly, featType) - argument order varies by
            // version, so try both before giving up.
            dynamic feats = null;
            var attempts = new Func<dynamic>[]
            {
                () => asm.ListFeaturesByType(true, ComponentFeatType()),
                () => asm.ListFeaturesByType(ComponentFeatType(), true),
            };
            foreach (var attempt in attempts)
            {
                try { feats = attempt(); if (feats != null) break; }
                catch { /* try next shape */ }
            }
            if (feats == null)
            {
                Log(Indent(depth) + "(ListFeaturesByType found no components / failed)");
                if (depth == 1) DumpPfcInterface("solid");
                return;
            }

            int n = (int)feats.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic feat = feats.Item(i);
                int id;
                try { id = (int)feat.Id; } catch { continue; }

                var childPath = new List<int>(path) { id };
                dynamic leaf;
                try
                {
                    dynamic ids = MakeIntSeq(childPath);
                    dynamic cpClass = NewPfc("componentpath");
                    dynamic compPath = cpClass.Create(root, ids);
                    leaf = compPath.Leaf;
                }
                catch (Exception ex)
                {
                    Log(Indent(depth) + "- <comp id " + id + "> (leaf resolve failed: " + ex.Message + ")");
                    continue;
                }

                string name = Try(() => (string)leaf.FileName);
                Log(Indent(depth) + "- " + name);

                if (name != null && name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
                    WalkInto(root, leaf, childPath, depth + 1);
            }
        }

        // [5] STEP export of the active model ----------------------------------
        private static void ExportStep(dynamic model)
        {
            Log("");
            string outFile = Path.Combine(Path.GetTempPath(), "creo_spike_export.stp");
            Log("[5] STEP export -> " + outFile);
            try
            {
                object asmSingle = EnumVal("assemblyconfiguration", "single", 0);
                dynamic instr;
                try
                {
                    dynamic flags = NewPfcAny(new[] { "geometryflags" }).Create();
                    TrySet(() => flags.AsSolids = true, () => flags.SetAsSolids(true));
                    dynamic siClass = NewPfcAny(new[] { "step3dexport" }, new[] { "step", "export" }, new[] { "stepexport" });
                    instr = siClass.Create(asmSingle, flags);
                }
                catch (Exception ex)
                {
                    Log("    building STEP instructions with flags failed (" + ex.Message + "); trying config-only overload");
                    dynamic siClass = NewPfcAny(new[] { "step3dexport" }, new[] { "step", "export" }, new[] { "stepexport" });
                    instr = siClass.Create(asmSingle);
                }

                model.Export(outFile, instr);
                Log("    Export() returned. File on disk: " + File.Exists(outFile));
            }
            catch (Exception ex)
            {
                Log("    STEP export failed: " + ex.Message);
                DumpPfcInterface("model");
                DumpPfcInterface("step3dexport");
            }
        }

        // ---- pfcls reflection helpers ---------------------------------------

        // Instantiate the first pfcls coclass whose type name contains ALL fragments.
        private static dynamic NewPfc(params string[] fragments)
        {
            foreach (Type t in Pfc.GetTypes())
            {
                if (t.IsInterface || t.IsAbstract || !t.IsClass) continue;
                if (!NameContainsAll(t.Name, fragments)) continue;
                try { return Activator.CreateInstance(t); } catch { }
            }
            throw new Exception("no pfcls coclass matches [" + string.Join(", ", fragments) + "]");
        }

        // Try several fragment-sets in order (handles renamed classes across versions).
        private static dynamic NewPfcAny(params string[][] fragmentSets)
        {
            foreach (var set in fragmentSets)
            {
                try { return NewPfc(set); } catch { }
            }
            throw new Exception("no pfcls coclass matched any of the candidate name sets");
        }

        private static object ComponentFeatType() => EnumVal("featuretype", "component", 1);

        private static object EnumVal(string enumFrag, string valueFrag, int fallback)
        {
            foreach (Type t in Pfc.GetTypes())
            {
                if (!t.IsEnum) continue;
                if (!t.Name.ToLowerInvariant().Contains(enumFrag.ToLowerInvariant())) continue;
                foreach (string vn in Enum.GetNames(t))
                    if (vn.ToLowerInvariant().Contains(valueFrag.ToLowerInvariant()))
                        return Enum.Parse(t, vn);
            }
            return fallback;
        }

        private static dynamic MakeIntSeq(IEnumerable<int> vals)
        {
            dynamic seq = NewPfc("intseq");
            // Some pfc sequence coclasses need a .Create() before use; probe by Count.
            try { var _ = seq.Count; }
            catch { try { seq = seq.Create(); } catch { } }
            foreach (int v in vals)
            {
                try { seq.Append(v); }
                catch { try { seq.Insert(seq.Count, v); } catch { seq.Set(seq.Count, v); } }
            }
            return seq;
        }

        // ---- diagnostics + misc ---------------------------------------------

        // Print the real member list of every pfcls INTERFACE matching the
        // fragments - this is how we learn the exact Creo-10 API when a guess misses.
        private static void DumpPfcInterface(params string[] fragments)
        {
            Log("    --- pfcls interfaces matching [" + string.Join(", ", fragments) + "] ---");
            bool any = false;
            foreach (Type t in Pfc.GetTypes())
            {
                if (!t.IsInterface) continue;
                if (!NameContainsAny(t.Name, fragments)) continue;
                any = true;
                Log("    " + t.Name + ":");
                foreach (MemberInfo mi in t.GetMembers())
                    Log("        " + mi.MemberType + " " + mi.Name);
            }
            if (!any) Log("    (no matching interfaces found)");
        }

        private static bool NameContainsAll(string name, string[] fragments)
        {
            string n = name.ToLowerInvariant();
            foreach (string f in fragments) if (!n.Contains(f.ToLowerInvariant())) return false;
            return true;
        }

        private static bool NameContainsAny(string name, string[] fragments)
        {
            string n = name.ToLowerInvariant();
            foreach (string f in fragments) if (n.Contains(f.ToLowerInvariant())) return true;
            return false;
        }

        private static void TrySet(Action a, Action b)
        {
            try { a(); } catch { try { b(); } catch { } }
        }

        private static string Try(Func<object> f)
        {
            try { object v = f(); return v == null ? "<null>" : v.ToString(); }
            catch (Exception ex) { return "<err: " + ex.Message + ">"; }
        }

        private static string Indent(int depth) => new string(' ', depth * 4);

        private static void Log(string m)
        {
            Console.WriteLine(m);
            _log?.WriteLine(m);
        }

        private static void Finish()
        {
            Log("");
            Log("=== spike done - paste this log (or " + Path.Combine(Path.GetTempPath(), "creo_spike.log") + ") back to continue ===");
            _log?.Flush();
            _log?.Dispose();
            Console.WriteLine();
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
        }
    }
}
