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
using RobotFramework.Components;
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
            try { res = Run(true) + System.Environment.NewLine + Report(); }
            catch (System.Exception e) { res = "ERROR " + e; }
            Debug.Log("OBSIDIAN SETUP" + System.Environment.NewLine + res);
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
                // intake end along +Z: the direction from the robot centre to the intake, in plan, must point at +Z
                Vector3 toIntake = bi.center - all.center; toIntake.y = 0f;
                float facing = toIntake.sqrMagnitude > 1e-8f ? Vector3.Dot(toIntake.normalized, Vector3.forward) : -1f;
                float score = Mathf.Abs(all.size.y - RobotHeight) * 10f + (1f - facing) * 5f + (modY < all.center.y ? 0f : 5f)
                              + Quaternion.Angle(q, Quaternion.identity) * 1e-4f;
                if (score < best) { best = score; bestQ = q; }
            }
            inst.transform.localRotation = bestQ;
            b = WorldBounds(inst);
            log.AppendLine($"orientation {bestQ.eulerAngles} size {b.size.x:F3} x {b.size.y:F3} x {b.size.z:F3} (score {best:F3}); intake centre {WorldBounds(intake.gameObject).center - b.center} from the robot centre");

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

        public static string InspectParts()
        {
            var go = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var sb = new StringBuilder();
                foreach (string subName in new[] { "Intake", "Drive", "Climber", "Turret", "Shooter" })
                {
                    var sub = go.transform.Find(subName);
                    if (sub == null) { sb.AppendLine($"MISSING {subName}"); continue; }
                    sb.AppendLine($"=== {subName} (children={sub.childCount}) ===");
                    foreach (var r in sub.GetComponentsInChildren<Renderer>(true))
                    {
                        var b = r.bounds;
                        sb.AppendLine($"  {r.name} | path={AnimationUtility.CalculateTransformPath(r.transform, sub)} | c=({b.center.x:F3},{b.center.y:F3},{b.center.z:F3}) s=({b.size.x:F3},{b.size.y:F3},{b.size.z:F3})");
                    }
                }
                string outPath = System.IO.Path.GetFullPath("Library/UnityLive/obsidian_parts.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString());
                return "Wrote " + outPath + " (" + sb.Length + " chars)";
            }
            finally { PrefabUtility.UnloadPrefabContents(go); }
        }

        [MenuItem("Tools/Modding/Obsidian: Wire Mechanisms & References")]
        static void WireMechanismsMenu() => Debug.Log(WireMechanisms());

        public static string WireMechanisms()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            if (robot == null) return "ERROR Obsidian script not found on root";
            var robotSo = new SerializedObject(robot);
            var robotRb = root.GetComponent<Rigidbody>();
            if (robotRb == null) return "ERROR no Rigidbody on root";

            Transform driveT = root.transform.Find("Drive");
            Transform intakeT = root.transform.Find("Intake");
            Transform climberT = root.transform.Find("Climber");
            Transform turretSubT = root.transform.Find("Turret");
            Transform shooterT = root.transform.Find("Shooter");
            if (!driveT || !intakeT || !climberT || !turretSubT || !shooterT)
                return "ERROR missing one of Drive/Intake/Climber/Turret/Shooter";

            // 1. Turret pivot at bearing ring 5454-26-P-4203
            GameObject turretPivot = root.transform.Find("Turret Offset/Turret Pivot")?.gameObject;
            if (turretPivot == null)
            {
                var ring = turretSubT.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "5454-26-P-4203");
                Vector3 ringCenter = ring ? ring.bounds.center : new Vector3(-0.140f, 0.352f, -0.076f);
                var turretOffset = new GameObject("Turret Offset");
                turretOffset.transform.SetParent(root.transform, false);
                turretOffset.transform.position = ringCenter;
                turretOffset.transform.localRotation = Quaternion.identity;

                turretPivot = new GameObject("Turret Pivot");
                turretPivot.transform.SetParent(turretOffset.transform, false);

                // Move Shooter and rotating turret parts (y >= 0.350) under Turret Pivot
                shooterT.SetParent(turretPivot.transform, true);
                var rotParts = turretSubT.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r.bounds.center.y >= 0.350f)
                    .Select(r => r.transform).ToList();
                foreach (var t in rotParts) t.SetParent(turretPivot.transform, true);
                log.AppendLine($"Turret Pivot at {ringCenter}: moved Shooter + {rotParts.Count} rotating turret parts");
            }
            AddAngularJoint(turretPivot, robotRb, ConfigurableJointMotion.Locked, ConfigurableJointMotion.Free, 5f, true);
            var gt = turretPivot.GetComponent<GenericTurret>() ?? turretPivot.AddComponent<GenericTurret>();
            Transform swmAnchor = root.transform.Find("SWM Anchor");
            if (swmAnchor == null)
            {
                var anchorGo = new GameObject("SWM Anchor");
                anchorGo.transform.SetParent(root.transform, false);
                anchorGo.transform.position = turretPivot.transform.position;
                swmAnchor = anchorGo.transform;
            }
            Transform swmRef = swmAnchor.Find("SWM Ref");
            if (swmRef == null)
            {
                var refGo = new GameObject("SWM Ref");
                refGo.transform.SetParent(swmAnchor, false);
                swmRef = refGo.transform;
            }
            var gts = new SerializedObject(gt);
            gts.FindProperty("referencePoint").objectReferenceValue = swmRef;
            gts.FindProperty("trackContinuously").boolValue = true;
            gts.ApplyModifiedPropertiesWithoutUndo();

            // 2. Hood pivot on shooter-wheel shaft 5454-26-P-5109 under Turret Pivot
            GameObject hoodGo = turretPivot.transform.Find("Hood Offset/Hood")?.gameObject;
            if (hoodGo == null)
            {
                var shaft = shooterT.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "5454-26-P-5109");
                Vector3 hoodPos = shaft ? shaft.bounds.center : new Vector3(-0.233f, 0.444f, -0.042f);
                Vector3 axis = Vector3.forward;
                if (shaft != null)
                {
                    // Align local X with the shaft's horizontal direction
                    var mf = shaft.GetComponent<MeshFilter>();
                    Vector3 sz = shaft.bounds.size;
                    axis = (sz.x >= sz.z ? Vector3.right : Vector3.forward);
                    if (mf && mf.sharedMesh)
                    {
                        var verts = mf.sharedMesh.vertices.Select(v => shaft.transform.TransformPoint(v)).ToList();
                        var pMin = verts.OrderBy(v => v.z).First();
                        var pMax = verts.OrderByDescending(v => v.z).First();
                        Vector3 dir = pMax - pMin; dir.y = 0f;
                        if (dir.sqrMagnitude > 1e-6f) axis = dir.normalized;
                    }
                }
                var hoodOffset = new GameObject("Hood Offset");
                hoodOffset.transform.SetParent(turretPivot.transform, false);
                hoodOffset.transform.SetPositionAndRotation(hoodPos, Quaternion.FromToRotation(Vector3.right, axis));

                hoodGo = new GameObject("Hood");
                hoodGo.transform.SetParent(hoodOffset.transform, false);

                string[] hoodNames = { "5454-26-P-5102", "5454-26-P-5104", "5454-26-P-5107", "5454-26-P-5110", "5454-26-P-5110.001" };
                int moved = MoveNamedParts(shooterT, hoodGo.transform, hoodNames);
                log.AppendLine($"Hood at {hoodPos} axis {axis}: moved {moved} parts");
            }
            AddAngularJoint(hoodGo, turretPivot.GetComponent<Rigidbody>(), ConfigurableJointMotion.Free, ConfigurableJointMotion.Locked, 1f, false);

            // 3. Intake Arm pivot at bronze bushings (y=0.288, z=0.388)
            GameObject armGo = root.transform.Find("Intake Arm Offset/Intake Arm")?.gameObject;
            if (armGo == null)
            {
                var bushings = intakeT.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r.name.StartsWith("10.25mm ID Flanged Bronze Bushing")).ToList();
                Vector3 armPivot = bushings.Count > 0
                    ? new Vector3(0f, bushings.Average(b => b.bounds.center.y), bushings.Average(b => b.bounds.center.z))
                    : new Vector3(0f, 0.288f, 0.388f);
                var armOffset = new GameObject("Intake Arm Offset");
                armOffset.transform.SetParent(root.transform, false);
                armOffset.transform.position = armPivot;
                armOffset.transform.localRotation = Quaternion.identity;

                armGo = new GameObject("Intake Arm");
                armGo.transform.SetParent(armOffset.transform, false);
                var armKids = new List<Transform>();
                foreach (Transform c in intakeT) armKids.Add(c);
                foreach (var c in armKids) c.SetParent(armGo.transform, true);
                log.AppendLine($"Intake Arm at {armPivot}: moved {armKids.Count} child group(s)");
            }
            AddAngularJoint(armGo, robotRb, ConfigurableJointMotion.Free, ConfigurableJointMotion.Locked, 2f, false);

            // 4. Climber carriage (linear Y joint)
            GameObject climberCarriage = root.transform.Find("Climber Offset/Climber Carriage")?.gameObject;
            if (climberCarriage == null)
            {
                var climberOffset = new GameObject("Climber Offset");
                climberOffset.transform.SetParent(root.transform, false);
                climberOffset.transform.position = new Vector3(0.060f, 0.346f, -0.216f);
                climberOffset.transform.localRotation = Quaternion.identity;

                climberCarriage = new GameObject("Climber Carriage");
                climberCarriage.transform.SetParent(climberOffset.transform, false);

                string[] carriageNames = { "Part 3.002", "Part 4.001", "Part 5", "Part 6", "Part 7" };
                int moved = MoveNamedParts(climberT, climberCarriage.transform, carriageNames);
                log.AppendLine($"Climber Carriage: moved {moved} parts");
            }
            if (!climberCarriage.GetComponent<GenericJoint>()) climberCarriage.AddComponent<GenericJoint>();
            var clRb = climberCarriage.GetComponent<Rigidbody>(); clRb.mass = 2f;
            var clCj = climberCarriage.GetComponent<ConfigurableJoint>();
            clCj.connectedBody = robotRb;
            clCj.anchor = Vector3.zero; clCj.axis = Vector3.right; clCj.secondaryAxis = Vector3.up;
            clCj.xMotion = ConfigurableJointMotion.Locked;
            clCj.yMotion = ConfigurableJointMotion.Free;
            clCj.zMotion = ConfigurableJointMotion.Locked;
            clCj.angularXMotion = clCj.angularYMotion = clCj.angularZMotion = ConfigurableJointMotion.Locked;

            // 5. Rollers -> RollerGroups
            var rollerSets = new (string field, Transform scope, string pattern, int motor, float rpm, string label)[]
            {
                ("intakeRollers", armGo.transform, @"^(Deadaxle Tube_|5454-P-2200$)", 0, 2500f, "Intake Roller"),
                ("spindexerRollers", driveT, @"^(Part 1\.00[45]|Part 2\.00[78]|4"" Colson Performa Wheel.*|2"" ION Compliant Wheel \(30A, 1/2"" Hex Bore\))$", 1, 1500f, "Spindexer Roller"),
                ("kickerRollers", driveT, @"^(2"" ION Compliant Wheel \(30A, 1/2"" Hex Bore\)\.\d{3}|2"" Stealth Wheel \(50A, 1/2"" Hex Bore\).*)$", 1, 3000f, "Kicker Roller"),
                ("shooterWheels", turretPivot.transform, @"^(4"" Stealth Wheel \(40A, 1/2"" Hex Bore\).*|Flywheel \(For 4"" Stealth Wheel\).*)$", 4, 3500f, "Shooter Wheel"),
            };

            foreach (var set in rollerSets)
            {
                if (robotSo.FindProperty(set.field).objectReferenceValue != null)
                {
                    log.AppendLine($"{set.field}: already linked");
                    continue;
                }
                var targets = set.scope.GetComponentsInChildren<MeshFilter>(true)
                    .Where(m => m.sharedMesh != null && System.Text.RegularExpressions.Regex.IsMatch(m.gameObject.name, set.pattern))
                    .Select(m => (Object)m.gameObject).ToArray();
                if (targets.Length == 0) { log.AppendLine($"WARNING {set.field}: 0 targets matched"); continue; }

                var groupsBefore = new HashSet<RollerGroup>(root.GetComponentsInChildren<RollerGroup>(true));
                var rollersBefore = new HashSet<InteractionRoller>(root.GetComponentsInChildren<InteractionRoller>(true));
                string res = RunMenu("GameObject/Guess Interaction Roller (Merge Inline)", targets);
                var newGroups = root.GetComponentsInChildren<RollerGroup>(true).Where(g => !groupsBefore.Contains(g)).ToList();
                var newRollers = root.GetComponentsInChildren<InteractionRoller>(true).Where(r => !rollersBefore.Contains(r)).ToList();
                foreach (var r in newRollers)
                {
                    var rs = new SerializedObject(r);
                    rs.FindProperty("motor").intValue = set.motor;
                    rs.FindProperty("rpm").floatValue = set.rpm;
                    rs.ApplyModifiedPropertiesWithoutUndo();
                    r.gameObject.name = set.label;
                }
                foreach (var g in newGroups) g.gameObject.name = set.label + " Group";
                if (newGroups.Count > 0) robotSo.FindProperty(set.field).objectReferenceValue = newGroups[0];
                log.AppendLine($"{set.field}: {res}; {newRollers.Count} roller(s), {newGroups.Count} group(s)");
            }

            // Re-pivot roller models onto their roller axis so they spin in place
            int fixedPivots = 0;
            foreach (var roller in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                var rso = new SerializedObject(roller);
                if (rso.FindProperty("useBeltMode").boolValue) continue;
                var models = rso.FindProperty("models");
                Vector3 p = roller.transform.position, d = roller.transform.right;
                for (int i = 0; i < models.arraySize; i++)
                {
                    var entry = models.GetArrayElementAtIndex(i);
                    var model = entry.FindPropertyRelative("model").objectReferenceValue as GameObject;
                    if (model == null) continue;
                    Vector3 offAxis = Vector3.ProjectOnPlane(model.transform.position - p, d);
                    if (offAxis.magnitude < 0.002f) continue;
                    var r = model.GetComponent<Renderer>();
                    Vector3 c = r ? r.bounds.center : model.transform.position;
                    Vector3 onAxis = p + Vector3.Project(c - p, d);
                    var pivot = new GameObject(model.name + " Pivot");
                    pivot.transform.SetParent(model.transform.parent, false);
                    pivot.transform.SetPositionAndRotation(onAxis, roller.transform.rotation);
                    pivot.layer = model.layer;
                    model.transform.SetParent(pivot.transform, true);
                    entry.FindPropertyRelative("model").objectReferenceValue = pivot;
                    entry.FindPropertyRelative("jointAxis").enumValueIndex = 0;
                    entry.FindPropertyRelative("reversed").boolValue = false;
                    fixedPivots++;
                }
                rso.ApplyModifiedPropertiesWithoutUndo();
            }
            log.AppendLine($"roller model pivots fixed: {fixedPivots}");

            // 6. Link joints, PIDs, SWM, and interpolation maps on Obsidian
            robotSo.FindProperty("turret").objectReferenceValue = gt;
            robotSo.FindProperty("hoodJoint").objectReferenceValue = hoodGo.GetComponent<GenericJoint>();
            robotSo.FindProperty("intakeJoint").objectReferenceValue = armGo.GetComponent<GenericJoint>();
            robotSo.FindProperty("climberJoint").objectReferenceValue = climberCarriage.GetComponent<GenericJoint>();
            robotSo.FindProperty("swmRef").objectReferenceValue = swmRef;
            robotSo.FindProperty("swmSettings.timeOfFlight").floatValue = 0.8f;
            robotSo.FindProperty("swmSettings.velocityDampeningValue").floatValue = 0.15f;
            robotSo.FindProperty("swmSettings.shootingDriveMultiplier").floatValue = 0.35f;

            SetPid(robotSo, "turretPid", 0.5f, 0f, 0.1f, 10f);
            SetPid(robotSo, "hoodPid", 0.5f, 0f, 0.1f, 4f);
            SetPid(robotSo, "intakePid", 0.5f, 0f, 0.1f, 4f);
            SetPid(robotSo, "climberPid", 15f, 0f, 0.1f, 3f);

            var fwPts = new (float d, float rpm)[] { (2.2f, 1600f), (3.2f, 1950f), (3.9f, 2350f), (4.1f, 2550f), (5.5f, 3500f), (8f, 6000f) };
            var fwMap = robotSo.FindProperty("flywheelInterpolation.interpolationMap");
            fwMap.arraySize = fwPts.Length;
            for (int i = 0; i < fwPts.Length; i++)
            {
                fwMap.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = fwPts[i].d;
                fwMap.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = fwPts[i].rpm;
            }

            var hdPts = new (float d, float deg)[] { (2.5f, 0f), (3.9f, 12f), (4.5f, 17f), (6f, 24f), (8f, 30f) };
            var hdMap = robotSo.FindProperty("hoodInterpolation.interpolationMap");
            hdMap.arraySize = hdPts.Length;
            for (int i = 0; i < hdPts.Length; i++)
            {
                hdMap.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = hdPts[i].d;
                hdMap.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = hdPts[i].deg;
            }

            robotSo.ApplyModifiedPropertiesWithoutUndo();

            var orgTargets = new Object[] { driveT.gameObject, armGo, climberT.gameObject, climberCarriage, turretPivot };
            log.AppendLine(RunMenu("GameObject/Auto Organize", orgTargets));

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        static int MoveNamedParts(Transform from, Transform to, string[] names)
        {
            var set = new HashSet<string>(names);
            var meshes = from.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => set.Contains(r.name) && !r.transform.IsChildOf(to)).ToList();
            var bounds = meshes.Select(r => r.bounds).ToList();
            foreach (var r in meshes) r.transform.SetParent(to, true);
            foreach (var c in from.GetComponentsInChildren<Collider>(true).Where(c => !c.transform.IsChildOf(to)).ToList())
            {
                Bounds cb = c.bounds;
                if (bounds.Any(b => (b.center - cb.center).magnitude < 0.01f && (b.size - cb.size).magnitude < 0.03f))
                    c.transform.SetParent(to, true);
            }
            return meshes.Count;
        }

        static void AddAngularJoint(GameObject go, Rigidbody connected, ConfigurableJointMotion angX, ConfigurableJointMotion angY,
                                    float mass, bool fixedCentreOfMass)
        {
            if (!go.GetComponent<GenericJoint>()) go.AddComponent<GenericJoint>();
            var rb = go.GetComponent<Rigidbody>();
            rb.mass = mass;
            if (fixedCentreOfMass) rb.centerOfMass = Vector3.zero;
            var cj = go.GetComponent<ConfigurableJoint>();
            cj.connectedBody = connected;
            cj.anchor = Vector3.zero;
            cj.axis = Vector3.right;
            cj.secondaryAxis = Vector3.up;
            cj.xMotion = cj.yMotion = cj.zMotion = ConfigurableJointMotion.Locked;
            cj.angularXMotion = angX;
            cj.angularYMotion = angY;
            cj.angularZMotion = ConfigurableJointMotion.Locked;
        }

        static void SetPid(SerializedObject so, string field, float p, float i, float d, float max)
        {
            so.FindProperty(field + ".kP").floatValue = p;
            so.FindProperty(field + ".kI").floatValue = i;
            so.FindProperty(field + ".kD").floatValue = d;
            so.FindProperty(field + ".Max").floatValue = max;
            so.FindProperty(field + ".Isaturation").floatValue = 0f;
        }

        public static string FixObsidianLayout()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            if (robot == null) return "ERROR Obsidian script not found on root";
            var robotSo = new SerializedObject(robot);

            // 1. Move stationary intake side plates & bushings out of Intake Arm back into Intake
            Transform intakeT = root.transform.Find("Intake");
            Transform armT = root.transform.Find("Intake Arm Offset/Intake Arm");
            if (intakeT && armT)
            {
                string[] stationaryIntake =
                {
                    "5454-26-P-2106", "5454-26-P-2111",
                    "10.25mm ID Flanged Bronze Bushing (0.625\" OD, 0.333\" WD)",
                    "10.25mm ID Flanged Bronze Bushing (0.625\" OD, 0.333\" WD).001",
                    "10.25mm ID Flanged Bronze Bushing (0.625\" OD, 0.333\" WD).002",
                    "10.25mm ID Flanged Bronze Bushing (0.625\" OD, 0.333\" WD).003",
                    "5454-26-P-2123", "5454-26-P-2123.001",
                    "5454-26-P-2127", "5454-26-P-2128",
                    "5454-26-P-2134", "Part 8.004",
                    "MAXPlanetary Output Slice.001",
                    "1/2\" Rounded Hex (1.5\"L 13.75mm OD)",
                    "10t Aluminum Spur Gear (10 DP, 1/2\" Hex Bore)",
                    "10t Aluminum Spur Gear (10 DP, 1/2\" Hex Bore).001",
                };
                int moved = MoveNamedParts(armT, intakeT, stationaryIntake);
                var cj = armT.GetComponent<ConfigurableJoint>();
                if (cj)
                {
                    cj.xMotion = ConfigurableJointMotion.Locked;
                    cj.yMotion = ConfigurableJointMotion.Locked;
                    cj.zMotion = ConfigurableJointMotion.Free;
                    cj.angularXMotion = cj.angularYMotion = cj.angularZMotion = ConfigurableJointMotion.Locked;
                }
                SetPid(robotSo, "intakePid", 15f, 0f, 0.1f, 3f);
                log.AppendLine($"Intake Arm -> linear Z: moved {moved} stationary parts back to Intake");

                // Flip intake rollers to positive RPM (forward)
                int flipped = 0;
                foreach (var r in armT.GetComponentsInChildren<InteractionRoller>(true))
                {
                    var rso = new SerializedObject(r);
                    var rpmP = rso.FindProperty("rpm");
                    if (rpmP.floatValue < 0f)
                    {
                        rpmP.floatValue = -rpmP.floatValue;
                        rso.ApplyModifiedPropertiesWithoutUndo();
                        flipped++;
                    }
                }
                log.AppendLine($"Intake rollers flipped to positive RPM: {flipped}");
            }

            // 2. Move stationary turret base plate 5454-26-P-4101 & 5454-26-P-4121 out of Turret Pivot back to Turret
            Transform turretSubT = root.transform.Find("Turret");
            Transform turretPivot = root.transform.Find("Turret Offset/Turret Pivot");
            if (turretSubT && turretPivot)
            {
                string[] stationaryTurret = { "5454-26-P-4101", "5454-26-P-4121" };
                int moved = MoveNamedParts(turretPivot, turretSubT, stationaryTurret);
                log.AppendLine($"Turret Pivot: moved {moved} stationary base plate(s) back to Turret");
            }

            robotSo.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string DiagnoseObsidian()
        {
            var go = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var sb = new StringBuilder();
                var arm = go.transform.Find("Intake Arm Offset/Intake Arm");
                if (arm)
                {
                    sb.AppendLine("=== Intake Arm Renderers ===");
                    foreach (var r in arm.GetComponentsInChildren<Renderer>(true))
                        sb.AppendLine($"  {r.name} c={r.bounds.center:F3}");
                }
                sb.AppendLine("=== InteractionRollers ===");
                foreach (var r in go.GetComponentsInChildren<InteractionRoller>(true))
                {
                    var rso = new SerializedObject(r);
                    float rpm = rso.FindProperty("rpm").floatValue;
                    bool belt = rso.FindProperty("useBeltMode").boolValue;
                    Vector3 axis = go.transform.InverseTransformDirection(r.transform.right);
                    Vector3 fwd = go.transform.InverseTransformDirection(r.transform.forward);
                    Vector3 pos = go.transform.InverseTransformPoint(r.transform.position);
                    string grp = r.transform.parent ? r.transform.parent.name : "?";
                    sb.AppendLine($"  [{grp}] {r.name} | pos={pos:F3} | rpm={rpm:F0} | belt={belt} | axis(right)={axis:F2} | fwd={fwd:F2}");
                }
                sb.AppendLine("=== Chute/Turret Colliders ===");
                foreach (var c in go.GetComponentsInChildren<Collider>(true))
                {
                    Vector3 pos = go.transform.InverseTransformPoint(c.bounds.center);
                    if (pos.x > -0.35f && pos.x < 0.05f && pos.z > -0.25f && pos.z < 0.10f && pos.y > 0.05f)
                        sb.AppendLine($"  {c.name} ({c.GetType().Name}) | c={pos:F3} | s={c.bounds.size:F3} | parent={c.transform.parent?.name}");
                }
                string outPath = System.IO.Path.GetFullPath("Library/UnityLive/obsidian_diag.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString());
                return "Wrote " + outPath;
            }
            finally { PrefabUtility.UnloadPrefabContents(go); }
        }

        public static string FixObsidianLayout2()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            // 1. Intake Arm: move any remaining stationary rack/pinion parts (c.z < 0.35 in local space) back to Intake
            Transform intakeT = root.transform.Find("Intake");
            Transform armT = root.transform.Find("Intake Arm Offset/Intake Arm");
            if (intakeT && armT)
            {
                var toMove = new List<Transform>();
                foreach (var r in armT.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.name.StartsWith("5454-26-P-2101") || r.name == "5454-26-P-2107") continue; // sliding side plates
                    if (root.transform.InverseTransformPoint(r.bounds.center).z < 0.35f)
                        toMove.Add(r.transform);
                }
                var bounds = toMove.Select(t => t.GetComponent<Renderer>().bounds).ToList();
                foreach (var t in toMove) t.SetParent(intakeT, true);
                foreach (var c in armT.GetComponentsInChildren<Collider>(true).ToList())
                {
                    Bounds cb = c.bounds;
                    if (bounds.Any(b => (b.center - cb.center).magnitude < 0.01f && (b.size - cb.size).magnitude < 0.03f))
                        c.transform.SetParent(intakeT, true);
                }
                log.AppendLine($"Intake Arm: moved {toMove.Count} stationary rear/rack parts (z < 0.35) back to Intake");
            }

            // 2. Climber Carriage: swap brackets back to Climber, move inner 15" stage + top hook into Climber Carriage
            Transform climberT = root.transform.Find("Climber");
            Transform carriageT = root.transform.Find("Climber Offset/Climber Carriage");
            if (climberT && carriageT)
            {
                string[] staticBrackets = { "Part 3.002", "Part 4.001", "Part 5", "Part 6", "Part 7" };
                int back = MoveNamedParts(carriageT, climberT, staticBrackets);

                string[] innerStage =
                {
                    "Tube 2\"x1\"x15\"", "Tube 2\"x1\"x15\".001",
                    "Tube 2\"x1\"x2.25\"", "Tube 2\"x1\"x2.25\".001",
                    "Part 13.001", "Part 9", "Part 10", "Part 11", "Part 12",
                    "Tube Plug (2\" x 1\" x 1/16\" Tube).001", "Tube Plug (2\" x 1\" x 1/16\" Tube).002",
                    "Part 6.001", "Part 8.001"
                };
                int fwd = MoveNamedParts(climberT, carriageT, innerStage);
                log.AppendLine($"Climber Carriage: moved {back} static brackets out, moved {fwd} inner-stage/hook parts in");
            }

            // 3. Fix Kicker Roller directions so all push UP (+Y) toward the shooter (chute center x = -0.140)
            int kickerFixed = 0, intakeFixed = 0, removedOld = 0;
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                if (r.name == "Roller" || r.name == "Roller (2)")
                {
                    Object.DestroyImmediate(r.gameObject);
                    removedOld++;
                    continue;
                }
                var rso = new SerializedObject(r);
                var rpmP = rso.FindProperty("rpm");
                Vector3 axis = root.transform.InverseTransformDirection(r.transform.right);
                Vector3 pos = root.transform.InverseTransformPoint(r.transform.position);

                if (r.name == "Kicker Roller")
                {
                    Vector3 toCenter = new Vector3(-0.140f - pos.x, 0f, 0f);
                    Vector3 vel = Vector3.Cross(axis * Mathf.Sign(rpmP.floatValue == 0 ? 1 : rpmP.floatValue), toCenter);
                    if (vel.y < 0f)
                    {
                        rpmP.floatValue = -rpmP.floatValue;
                        rso.ApplyModifiedPropertiesWithoutUndo();
                        kickerFixed++;
                    }
                }
                else if (r.name == "Intake Roller" && !rso.FindProperty("useBeltMode").boolValue)
                {
                    if (axis.x * Mathf.Sign(rpmP.floatValue == 0 ? 1 : rpmP.floatValue) < 0f)
                    {
                        rpmP.floatValue = -rpmP.floatValue;
                        rso.ApplyModifiedPropertiesWithoutUndo();
                        intakeFixed++;
                    }
                }
            }
            log.AppendLine($"Rollers: kickerFixed={kickerFixed}, intakeFixed={intakeFixed}, removedOld={removedOld}");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixShootingAndRamp()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            // 1. Remove solid BoxColliders sealing the turret ring hole (y=0.362..0.365) & duplicate full-robot bumper box (y=0.087)
            int removedCols = 0;
            foreach (var bc in root.GetComponentsInChildren<BoxCollider>(true).ToList())
            {
                if (bc.gameObject == root) continue; // keep root BumperCollider
                Vector3 pos = root.transform.InverseTransformPoint(bc.bounds.center);
                Vector3 sz = bc.bounds.size;
                bool isTurretPlate = Mathf.Abs(pos.x + 0.140f) < 0.02f && Mathf.Abs(pos.z + 0.082f) < 0.02f &&
                                     pos.y > 0.35f && pos.y < 0.38f && sz.x > 0.3f && sz.z > 0.3f;
                bool isFullBumperBox = sz.x > 0.8f && sz.z > 0.8f && pos.y > 0.05f && pos.y < 0.12f;
                if (isTurretPlate || isFullBumperBox)
                {
                    Object.DestroyImmediate(bc);
                    removedCols++;
                }
            }
            log.AppendLine($"Removed {removedCols} blocking BoxCollider(s)");

            // 2. Set GenericTurret angleOffset = 70 (was -110, which aimed the shooter 180 deg off)
            var gt = root.GetComponentInChildren<GenericTurret>(true);
            if (gt != null)
            {
                var gts = new SerializedObject(gt);
                gts.FindProperty("angleOffset").floatValue = 70f;
                gts.ApplyModifiedPropertiesWithoutUndo();
                log.AppendLine("GenericTurret angleOffset set to 70 deg");
            }

            // 3. Create Intake Ramp Pivot under Intake Arm and link to Obsidian
            Transform armT = root.transform.Find("Intake Arm Offset/Intake Arm");
            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            var robotSo = robot ? new SerializedObject(robot) : null;
            if (armT != null && robotSo != null)
            {
                Transform rampPivot = armT.Find("Intake Ramp Pivot");
                if (rampPivot == null)
                {
                    var go = new GameObject("Intake Ramp Pivot");
                    go.transform.SetParent(armT, false);
                    go.transform.position = root.transform.TransformPoint(new Vector3(0.001f, 0.117f, 0.450f));
                    go.transform.localRotation = Quaternion.identity;
                    rampPivot = go.transform;

                    string[] rampParts = { "5454-26-P-2119", "5454-26-P-2126", "5454-26-P-2132", "5454-26-P-2133" };
                    int moved = MoveNamedParts(armT, rampPivot, rampParts);
                    log.AppendLine($"Intake Ramp Pivot: moved {moved} ramp parts");
                }
                robotSo.FindProperty("intakeRampPivot").objectReferenceValue = rampPivot;
                robotSo.FindProperty("climberExtendedDistance").floatValue = 11f;
                robotSo.FindProperty("turretTolerance").floatValue = 5f;
                robotSo.ApplyModifiedPropertiesWithoutUndo();
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixTurretRampAndWall()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            // 1. Move stationary bracket 5454-26-P-4120 out of Turret Pivot back to Turret
            Transform turretSubT = root.transform.Find("Turret");
            Transform turretPivot = root.transform.Find("Turret Offset/Turret Pivot");
            if (turretSubT && turretPivot)
            {
                int moved = MoveNamedParts(turretPivot, turretSubT, new[] { "5454-26-P-4120" });
                log.AppendLine($"Turret Pivot: moved {moved} stationary bracket (5454-26-P-4120) back to Turret");
            }

            // 2. Move Intake Ramp Pivot to high-end hinge (0, 0.288, 0.388) keeping existing children in place,
            //    and add the two ramp side arms (5454-26-P-2106, 5454-26-P-2111) from Intake.
            Transform intakeT = root.transform.Find("Intake");
            Transform armT = root.transform.Find("Intake Arm Offset/Intake Arm");
            Transform rampPivot = armT ? armT.Find("Intake Ramp Pivot") : null;
            if (intakeT && armT && rampPivot)
            {
                var kids = new List<(Transform t, Vector3 p, Quaternion r)>();
                foreach (Transform c in rampPivot) kids.Add((c, c.position, c.rotation));
                rampPivot.position = root.transform.TransformPoint(new Vector3(0f, 0.288f, 0.388f));
                rampPivot.localRotation = Quaternion.identity;
                foreach (var k in kids) k.t.SetPositionAndRotation(k.p, k.r);

                int moved = MoveNamedParts(intakeT, rampPivot, new[] { "5454-26-P-2106", "5454-26-P-2111" });
                log.AppendLine($"Intake Ramp Pivot moved to high end (0, 0.288, 0.388); added {moved} ramp arms");
            }

            // 3. Add complex collider to curved hopper wall Part 1 in Drive
            Transform driveT = root.transform.Find("Drive");
            if (driveT)
            {
                var part1 = driveT.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Part 1");
                if (part1 != null)
                {
                    log.AppendLine("Part 1: " + RunMenu("GameObject/Guess Complex Collider", new Object[] { part1.gameObject }));
                    log.AppendLine(RunMenu("GameObject/Auto Organize", new Object[] { driveT.gameObject }));
                }
                else log.AppendLine("WARNING Part 1 not found in Drive");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixWallConeHookAndShooter()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            // 1. Flip shooterWheels RPM to negative (-3500) so front face throws fuel UP into the hood
            int shooterFlipped = 0;
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                if (r.name == "Shooter Wheel")
                {
                    var rso = new SerializedObject(r);
                    var rpmP = rso.FindProperty("rpm");
                    if (rpmP.floatValue > 0f)
                    {
                        rpmP.floatValue = -rpmP.floatValue;
                        rso.ApplyModifiedPropertiesWithoutUndo();
                        shooterFlipped++;
                    }
                }
            }
            log.AppendLine($"Shooter wheels flipped to negative RPM: {shooterFlipped}");

            // 2. Replace Part 1 complex colliders with 32 box colliders
            Transform driveT = root.transform.Find("Drive");
            if (driveT)
            {
                foreach (var t in driveT.GetComponentsInChildren<Transform>(true).Where(x => x.name.StartsWith("collider-complex-Part 1")).ToList())
                    Object.DestroyImmediate(t.gameObject);

                var part1 = driveT.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == "Part 1" && m.sharedMesh);
                if (part1 != null)
                {
                    var boxes = global::Editor.CompoundColliderFit.Fit(part1.sharedMesh, part1.transform.localToWorldMatrix, 32, null);
                    global::Editor.GuessComplexCollider.CreateColliders(part1, boxes, "ObsidianSetup");
                    log.AppendLine($"Part 1: created {boxes.Length} box colliders");
                }

                // 3. Add collider to spindexer cone (Part 28) and parent it to the center spindexer wheel so it spins with it
                var cone = driveT.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == "Part 28" && m.sharedMesh);
                if (cone != null)
                {
                    if (!cone.GetComponent<MeshCollider>())
                    {
                        var mc = cone.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = cone.sharedMesh;
                        mc.convex = true;
                    }
                    // Find the center spindexer roller (pos ~ (-0.013, 0.145, 0.038)) and attach cone to its spinning model/pivot
                    var centerRoller = driveT.GetComponentsInChildren<InteractionRoller>(true)
                        .Where(r => r.name == "Spindexer Roller")
                        .OrderBy(r => (root.transform.InverseTransformPoint(r.transform.position) - new Vector3(-0.013f, 0.145f, 0.038f)).sqrMagnitude)
                        .FirstOrDefault();
                    if (centerRoller != null)
                    {
                        var rso = new SerializedObject(centerRoller);
                        var models = rso.FindProperty("models");
                        if (models.arraySize > 0)
                        {
                            var firstModel = models.GetArrayElementAtIndex(0).FindPropertyRelative("model").objectReferenceValue as GameObject;
                            if (firstModel != null)
                            {
                                cone.transform.SetParent(firstModel.transform, true);
                                log.AppendLine($"Cone (Part 28): added convex MeshCollider and parented to {firstModel.name}");
                            }
                        }
                    }
                }
            }

            // 4. Add complex colliders (8 boxes) to climber hook (Part 13.001) on Climber Carriage
            Transform carriageT = root.transform.Find("Climber Offset/Climber Carriage");
            if (carriageT)
            {
                var hook = carriageT.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == "Part 13.001" && m.sharedMesh);
                if (hook != null)
                {
                    foreach (var t in carriageT.GetComponentsInChildren<Transform>(true).Where(x => x.name.StartsWith("collider-complex-Part 13.001")).ToList())
                        Object.DestroyImmediate(t.gameObject);
                    var boxes = global::Editor.CompoundColliderFit.Fit(hook.sharedMesh, hook.transform.localToWorldMatrix, 8, null);
                    global::Editor.GuessComplexCollider.CreateColliders(hook, boxes, "ObsidianSetup");
                    log.AppendLine($"Climber hook (Part 13.001): created {boxes.Length} box colliders");
                }
            }

            // 5. Set intakeRampFoldAngle = -35 on Obsidian
            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            if (robot != null)
            {
                var robotSo = new SerializedObject(robot);
                robotSo.FindProperty("intakeRampFoldAngle").floatValue = -35f;
                robotSo.ApplyModifiedPropertiesWithoutUndo();
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixKickerChuteAndClimb()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            // 1. Replace solid BoxColliders on the kicker chute cutout plates (5454-26-P-6101..6104) with compound box colliders
            Transform driveT = root.transform.Find("Drive");
            if (driveT)
            {
                string[] chutePlates = { "5454-26-P-6101", "5454-26-P-6102", "5454-26-P-6103", "5454-26-P-6104" };
                int removed = 0, fitted = 0;
                foreach (string plateName in chutePlates)
                {
                    var mf = driveT.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == plateName && m.sharedMesh);
                    if (mf == null) continue;
                    Bounds pb = mf.GetComponent<Renderer>().bounds;
                    foreach (var bc in driveT.GetComponentsInChildren<BoxCollider>(true).ToList())
                    {
                        if (bc.transform.parent && bc.transform.parent.name.StartsWith("collider-complex-")) continue;
                        Bounds cb = bc.bounds;
                        if ((cb.center - pb.center).magnitude < 0.02f && (cb.size - pb.size).magnitude < 0.05f)
                        {
                            Object.DestroyImmediate(bc);
                            removed++;
                        }
                    }
                    foreach (var t in driveT.GetComponentsInChildren<Transform>(true).Where(x => x.name.StartsWith("collider-complex-" + plateName)).ToList())
                        Object.DestroyImmediate(t.gameObject);

                    var boxes = global::Editor.CompoundColliderFit.Fit(mf.sharedMesh, mf.transform.localToWorldMatrix, 8, null);
                    global::Editor.GuessComplexCollider.CreateColliders(mf, boxes, "ObsidianSetup");
                    fitted++;
                }
                log.AppendLine($"Kicker chute plates: removed {removed} solid BoxCollider(s), fitted {fitted} compound collider sets");
            }

            // 2. Lower climberExtendedDistance slightly (11 -> 9.5 in)
            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            if (robot != null)
            {
                var robotSo = new SerializedObject(robot);
                robotSo.FindProperty("climberExtendedDistance").floatValue = 9.5f;
                robotSo.ApplyModifiedPropertiesWithoutUndo();
                log.AppendLine("climberExtendedDistance set to 9.5 in");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixShooterDirAndClimbStrength()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            int flipped = 0;
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                if (r.name == "Shooter Wheel")
                {
                    var rso = new SerializedObject(r);
                    var rpmP = rso.FindProperty("rpm");
                    if (rpmP.floatValue < 0f)
                    {
                        rpmP.floatValue = -rpmP.floatValue;
                        rso.ApplyModifiedPropertiesWithoutUndo();
                        flipped++;
                    }
                }
            }
            log.AppendLine($"Shooter wheels flipped to positive RPM: {flipped}");

            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            if (robot != null)
            {
                var robotSo = new SerializedObject(robot);
                SetPid(robotSo, "climberPid", 60f, 0f, 1f, 50f);
                robotSo.ApplyModifiedPropertiesWithoutUndo();
                log.AppendLine("climberPid set to P=60, I=0, D=1, Max=50");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixHoodAndClimbMass()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Obsidian");
            if (robot != null)
            {
                var robotSo = new SerializedObject(robot);
                robotSo.FindProperty("hoodStowAngle").floatValue = 0f;
                robotSo.FindProperty("hoodMaxAngle").floatValue = 35f;
                SetPid(robotSo, "climberPid", 15f, 0.05f, 0.5f, 5f);
                robotSo.FindProperty("climberPid.Isaturation").floatValue = 5f;
                robotSo.ApplyModifiedPropertiesWithoutUndo();
                log.AppendLine("hoodStowAngle=0, hoodMaxAngle=35, climberPid=(15, 0.05, 0.5, Max=5, Isat=5)");
            }

            Transform carriageT = root.transform.Find("Climber Offset/Climber Carriage");
            if (carriageT)
            {
                var clRb = carriageT.GetComponent<Rigidbody>();
                if (clRb != null)
                {
                    clRb.mass = 40f;
                    log.AppendLine("Climber Carriage Rigidbody mass set to 40 kg");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        public static string FixShooterFlywheel()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();

            int flipped = 0;
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                if (r.name == "Shooter Wheel")
                {
                    var rso = new SerializedObject(r);
                    var rpmP = rso.FindProperty("rpm");
                    if (rpmP.floatValue > 0f)
                    {
                        rpmP.floatValue = -rpmP.floatValue;
                        rso.ApplyModifiedPropertiesWithoutUndo();
                        flipped++;
                    }
                }
            }
            log.AppendLine($"Shooter wheels flipped to negative RPM (-3500): {flipped}");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            log.AppendLine(saved ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }
    }
}
