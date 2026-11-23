// One-shot setup of the Obsidian (FRC 5454) robot prefab, following docs.mosimulator.com (Initial Robot Setup,
// Drivetrain Setup), the same steps as BreakawaySetup. Run from Tools > Modding > Setup Obsidian (5454), or the UnityLive
// bridge: MoSim.EditorTools.ObsidianSetup.Run
// 1. FBX instance -> unpack completely (the FBX already leaves out the simplification's hidden REVIEW parts)
// 2. stand it upright (Y up, intake toward +Z), drivetrain centre on the root, swerve treads on the wheel contact
// 3. subsystems by CAD assembly (Intake / Turret / Shooter / Climber, the rest Drive), delete the CAD container,
//    Auto Organize
// 4. Guess Generic Collider (Box Only) on parts larger than 5 cm, Auto Organize again
// 5. DriveController values and swerve module positions measured from the four SDS MK4i modules (Kraken X60 drive)
// Mechanisms (rollers, joints, auto-aim) are not set up here.
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RobotFramework.Controllers.Drivetrain;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MoSim.EditorTools
{
    internal static class ObsidianSetup
    {
        const string Dir = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Obsidian/";
        const string PrefabPath = Dir + "Obsidian.prefab";
        const string FbxPath = Dir + "2026 - 5454 main robot - simplified.fbx";
        const float RobotHeight = 0.543f;   // simplified robot, upright (m)

        // CAD assemblies (name prefixes) -> subsystem names; everything else goes to Drive
        static readonly (string cad, string name)[] Subsystems =
        {
            ("5454-A-2000", "Intake"),
            ("5454-26-A-4000", "Turret"),
            ("5454-26-A-5000", "Shooter"),
            ("Elevator Climb Assembly", "Climber"),
        };
        const string ModulePrefix = "SDS MK4I swerve";

        [MenuItem("Tools/Modding/Setup Obsidian (5454)")]
        static void MenuEntry() => Debug.Log(Run());

        // headless: Unity.exe -batchmode -projectPath <project> -executeMethod MoSim.EditorTools.ObsidianSetup.RunBatch -logFile <log>
        // (no -quit: this exits itself, 0 on success, 1 on any ERROR)
        public static void RunBatch()
        {
            string res;
            try { res = Run(true) + "
" + Report(); }
            catch (System.Exception e) { res = "ERROR " + e; }
            Debug.Log("OBSIDIAN SETUP
" + res);
            EditorApplication.Exit(res.Contains("ERROR") ? 1 : 0);
        }

        public static string Run() => Run(false);

        static string Run(bool batch)
        {
            var log = new StringBuilder();
            AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (fbx == null) return "ERROR FBX not found: " + FbxPath;
            int meshCount = fbx.GetComponentsInChildren<MeshFilter>(true).Length;
            if (meshCount < 100) return $"ERROR the FBX has only {meshCount} mesh(es): re-export it with its meshes; nothing changed";
            log.AppendLine($"FBX: {meshCount} meshes");

            // the prefab stage needs the editor UI; headless runs edit the prefab contents directly
            var stage = batch ? null : PrefabStageUtility.OpenPrefab(PrefabPath);
            if (!batch && stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = batch ? PrefabUtility.LoadPrefabContents(PrefabPath) : stage.prefabContentsRoot;
            try
            {
            if (root.transform.Find("Drive") != null) return "Obsidian already has a Drive subsystem: nothing changed";
            var dc = root.GetComponent<DriveController>();
            if (dc == null) return "ERROR no DriveController on the Obsidian root (BaseRobot template?): nothing changed";

            var inst = batch ? (GameObject)PrefabUtility.InstantiatePrefab(fbx) : (GameObject)PrefabUtility.InstantiatePrefab(fbx, stage.scene);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.transform.SetParent(root.transform, false);

            // 2. scale, orientation, position
            Bounds b = WorldBounds(inst);
            float big = Mathf.Max(b.size.x, b.size.y, b.size.z);
            float s = Mathf.Pow(10f, Mathf.Round(Mathf.Log10(1.1f / big)));
            if (!Mathf.Approximately(s, 1f)) { inst.transform.localScale *= s; log.AppendLine($"unit scale x{s}"); }
            Transform intake = FindPrefix(inst.transform, Subsystems[0].cad);
            var modules = inst.GetComponentsInChildren<MeshFilter>(true).Where(m => m.name.StartsWith(ModulePrefix) && m.sharedMesh).ToList();
            if (intake == null || modules.Count != 4)
            { Object.DestroyImmediate(inst); return $"ERROR intake assembly {(intake ? "found" : "missing")}, {modules.Count} swerve modules (need 4); nothing changed\n" + log; }
            Quaternion bestQ = Quaternion.identity; float best = float.MaxValue;
            foreach (Quaternion q in AxisRotations())
            {
                inst.transform.localRotation = q;
                Bounds all = WorldBounds(inst), bi = WorldBounds(intake.gameObject);
                float modY = modules.Average(m => m.GetComponent<Renderer>().bounds.center.y);
                float score = Mathf.Abs(all.size.y - RobotHeight) * 10f + (bi.center.z > all.center.z ? 0f : 5f) + (modY < all.center.y ? 0f : 5f)
                              + Quaternion.Angle(q, Quaternion.identity) * 1e-4f;
                if (score < best) { best = score; bestQ = q; }
            }
            inst.transform.localRotation = bestQ;
            b = WorldBounds(inst);
            log.AppendLine($"orientation {bestQ.eulerAngles} size {b.size.x:F3} x {b.size.y:F3} x {b.size.z:F3} (score {best:F3})");

            // wheel contact per module: centroid of the module mesh's lowest 4 mm of vertices (the tread under the steer axis)
            var contacts = modules.Select(TreadContact).ToList();
            Vector3 centre = new Vector3(contacts.Average(c => c.x), 0f, contacts.Average(c => c.z));
            float treadY = contacts.Average(c => c.y);
            var so = new SerializedObject(dc);
            var dtParent = so.FindProperty("drivetrainParent").objectReferenceValue as GameObject;
            float floor = 0f;
            var wheelColliders = dtParent ? dtParent.GetComponentsInChildren<WheelCollider>(true) : new WheelCollider[0];
            if (wheelColliders.Length > 0) floor = wheelColliders.Min(w => w.transform.TransformPoint(w.center).y - w.radius);
            Vector3 shift = new Vector3(-centre.x, floor - treadY, -centre.z);
            inst.transform.position += shift;
            contacts = contacts.Select(c => c + shift).ToList();
            log.AppendLine($"drivetrain centre moved to the root; tread contact y {floor:F4} (template wheel colliders: {wheelColliders.Length})");
            foreach (var c in contacts) log.AppendLine($"  module contact ({c.x:F4}, {c.y:F4}, {c.z:F4})");

            // 3. subsystems
            var subs = new List<GameObject>();
            foreach (var (cad, name) in Subsystems)
            {
                Transform t = FindPrefix(inst.transform, cad);
                if (t == null) { log.AppendLine("WARNING missing " + cad); continue; }
                t.SetParent(root.transform, true); t.name = name; subs.Add(t.gameObject);
            }
            var rest = inst.GetComponentsInChildren<MeshRenderer>(true);
            var drive = new GameObject("Drive"); drive.transform.SetParent(root.transform, false);
            foreach (var r in rest) r.transform.SetParent(drive.transform, true);
            subs.Insert(0, drive);
            log.AppendLine($"subsystems: {string.Join(", ", subs.Select(g => $"{g.name} ({g.GetComponentsInChildren<MeshRenderer>(true).Length})"))}");
            Object.DestroyImmediate(inst);
            log.AppendLine(RunMenu("GameObject/Auto Organize", subs.Cast<Object>().ToArray()));

            // 4. colliders (docs: Guess Generic Colliders; Box Only keeps round parts free of colliders)
            foreach (var sub in subs)
            {
                var targets = sub.GetComponentsInChildren<MeshFilter>(true)
                    .Where(m => m.sharedMesh != null && m.GetComponent<Renderer>() && MaxExtent(m.GetComponent<Renderer>().bounds) > 0.05f)
                    .Select(m => (Object)m.gameObject).ToArray();
                log.AppendLine($"{sub.name}: " + RunMenu("GameObject/Guess Generic Collider (Box Only)", targets));
            }
            log.AppendLine(RunMenu("GameObject/Auto Organize", subs.Cast<Object>().ToArray()));
            log.AppendLine($"colliders in the subsystems: {subs.Sum(g => g.GetComponentsInChildren<Collider>(true).Length)}");

            // robot script on the root (its HandleSwm waits for swmRef, so an empty one is safe)
            var robotType = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Obsidian"))
                .FirstOrDefault(t => t != null && typeof(MonoBehaviour).IsAssignableFrom(t));
            if (robotType != null && root.GetComponent(robotType) == null) { root.AddComponent(robotType); log.AppendLine("Obsidian script added to the root"); }

            // 5. drive controller (docs: Drivetrain Setup). SDS MK4i, Kraken X60 drive (CAD); L2 ratio and weight are estimates
            so.Update();
            so.FindProperty("drivetrainType").intValue = 0;                 // Swerve
            so.FindProperty("maxOmega").floatValue = 0f;                    // kinematic limit
            so.FindProperty("motor.motorType").intValue = 1;                // KrakenX60_FOC
            so.FindProperty("motor.config.K_p").floatValue = 0.5f;
            so.FindProperty("motor.config.K_v").floatValue = 0.13f;
            so.FindProperty("motor.config.StatorLimit").floatValue = 100f;
            so.FindProperty("motor.config.SupplyLimit").floatValue = 40f;
            so.FindProperty("realWorldWeightLbs").floatValue = 135f;        // match weight incl. battery + bumpers (estimate)
            so.FindProperty("motorsPerPowertrain").intValue = 1;
            so.FindProperty("gearRatio").floatValue = 6.75f;                // SDS MK4i L2 (estimate: the CAD does not name the ratio)
            so.FindProperty("wheelDiameterInches").floatValue = 4f;
            so.FindProperty("mechanicalLossPercent").floatValue = 2.5f;
            so.FindProperty("frictionCoefficient").floatValue = 1.3f;
            so.FindProperty("biteFactor").floatValue = 0.25f;
            so.FindProperty("contactPatchWidthInches").floatValue = 1.5f;
            so.FindProperty("contactTolerance").floatValue = 0.01f;
            so.FindProperty("lateralGripResponse").floatValue = 0.1f;
            so.ApplyModifiedPropertiesWithoutUndo();
            var mv = typeof(DriveController).GetField("maxVelocity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            mv.SetValue(dc, (MoSimLib.SecureFloat)4.0f);                    // reachable m/s (free speed ~4.6 at 6.75:1)
            PrefabUtility.RecordPrefabInstancePropertyModifications(dc);
            EditorUtility.SetDirty(dc);
            log.AppendLine("DriveController set (Swerve, Kraken X60 FOC, MK4i L2 6.75:1, 4 in wheels, 135 lb, 4.0 m/s)");

            if (dtParent)
            {
                int moved = 0;
                foreach (string group in new[] { "Wheels", "Raycast" })
                {
                    Transform g = dtParent.transform.Find(group);
                    if (g == null) { log.AppendLine($"WARNING drivetrain has no '{group}'"); continue; }
                    foreach (Transform w in g)
                    {
                        if (!(w.name.Length == 2 && "FB".Contains(w.name[0]) && "LR".Contains(w.name[1]))) { log.AppendLine($"  skipped {group}/{w.name}"); continue; }
                        float sx = w.name[1] == 'L' ? -1f : 1f, sz = w.name[0] == 'F' ? 1f : -1f;
                        Vector3 c = contacts.OrderBy(p => (Mathf.Sign(p.x) == sx ? 0 : 1) + (Mathf.Sign(p.z) == sz ? 0 : 1)).First();
                        Vector3 local = w.parent.InverseTransformPoint(c);
                        w.localPosition = new Vector3(local.x, w.localPosition.y, local.z);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(w);
                        moved++;
                    }
                }
                log.AppendLine($"swerve modules placed on the measured tread contacts ({moved} transforms)");
            }
            else log.AppendLine("WARNING DriveController has no drivetrainParent: modules not placed");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool ok);
            if (!batch)
            {
                if (ok) stage.ClearDirtiness();
                StageUtility.GoToMainStage();
            }
            log.AppendLine(ok ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
            }
            finally { if (batch) PrefabUtility.UnloadPrefabContents(root); }
        }

        // report only: robot bounds, subsystem bounds and the drivetrain wheel positions in the saved prefab
        public static string Report()
        {
            var go = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var sb = new StringBuilder();
                Bounds all = WorldBounds(go);
                sb.AppendLine($"robot bounds centre {all.center} size {all.size}");
                foreach (Transform t in go.transform)
                    if (t.GetComponentsInChildren<Renderer>(true).Length > 0)
                        sb.AppendLine($"  {t.name}: centre {WorldBounds(t.gameObject).center} renderers {t.GetComponentsInChildren<Renderer>(true).Length} colliders {t.GetComponentsInChildren<Collider>(true).Length}");
                var dc = go.GetComponent<DriveController>();
                var dt = dc ? new SerializedObject(dc).FindProperty("drivetrainParent").objectReferenceValue as GameObject : null;
                if (dt)
                    foreach (string group in new[] { "Wheels", "Raycast" })
                    {
                        var g = dt.transform.Find(group);
                        if (g) foreach (Transform w in g) sb.AppendLine($"  {group}/{w.name} world {w.position}");
                    }
                return sb.ToString();
            }
            finally { PrefabUtility.UnloadPrefabContents(go); }
        }

        static Vector3 TreadContact(MeshFilter m)
        {
            var tr = m.transform; var v = m.sharedMesh.vertices.Select(p => tr.TransformPoint(p)).ToList();
            float y0 = v.Min(p => p.y);
            var low = v.Where(p => p.y < y0 + 0.004f).ToList();
            return new Vector3(low.Average(p => p.x), y0, low.Average(p => p.z));
        }

        static string RunMenu(string item, Object[] selection)
        {
            if (selection.Length == 0) return item + ": nothing selected";
            var registry = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Editor.ToolRegistry")).FirstOrDefault(t => t != null);
            var running = registry?.GetField("Running", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
            running?.GetType().GetMethod("Clear")?.Invoke(running, null);
            Selection.objects = selection;
            bool ran = EditorApplication.ExecuteMenuItem(item);
            Selection.objects = new Object[0];
            return item + (ran ? $": ran on {selection.Length} object(s)" : ": UNAVAILABLE");
        }

        static float MaxExtent(Bounds b) => Mathf.Max(b.size.x, b.size.y, b.size.z);

        static Bounds WorldBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            Bounds b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static Transform FindPrefix(Transform t, string prefix)
        {
            if (t.name.StartsWith(prefix)) return t;
            foreach (Transform c in t) { var f = FindPrefix(c, prefix); if (f) return f; }
            return null;
        }

        static IEnumerable<Quaternion> AxisRotations()
        {
            var seen = new List<Quaternion>();
            for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) for (int z = 0; z < 4; z++)
            {
                Quaternion q = Quaternion.Euler(x * 90, y * 90, z * 90);
                if (seen.Any(p => Quaternion.Angle(p, q) < 1f)) continue;
                seen.Add(q); yield return q;
            }
        }
    }
}
