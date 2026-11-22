// One-shot setup of the Breakaway (FRC 3937) robot prefab, following docs.mosimulator.com (Initial Robot Setup,
// Drivetrain Setup). Run from Tools > Modding > Setup Breakaway (3937), or the UnityLive bridge:
// MoSim.EditorTools.BreakawaySetup.Run
// 1. FBX instance -> unpack completely -> drop the hidden REVIEW parts and the game piece
// 2. stand it upright (Y up, intake toward +Z), drivetrain centre on the root, wheels on the drivetrain's wheel contact
// 3. extract the subsystems (Drive / Shooter / Intake), delete the CAD container, Auto Organize
// 4. Guess Generic Collider (Box Only: rollers and round parts get none) on parts larger than 5 cm, Auto Organize again
// 3b. pivot objects: "Turret" (bearing-ring centre) holds the shooter, "Intake Arm" (MotionX hub axis) holds the arm
// 4b. Guess Interaction Roller (Merge Inline) per roller set -> one RollerGroup each, linked to the Breakaway script
// 4c. GenericJoints (turret Y + GenericTurret aimed from an SWM reference, intake arm X), PIDs, flywheel interpolation
// 5. DriveController values and swerve module positions (SDS MK5n-style modules, Kraken X60)
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.Drivetrain;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MoSim.EditorTools
{
    internal static class BreakawaySetup
    {
        const string Dir = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Breakaway/";
        const string PrefabPath = Dir + "Breakaway.prefab";
        const string FbxPath = Dir + "2026.2 Robot Design - simplified.fbx";

        // hidden / duplicate / replaced parts from the simplification report (REVIEW collections) + the game piece
        static readonly string[] Review =
        {
            "0.25\" ID Bearing (0.5\" OD, 0.188\" WD, Shielded).005",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).001",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).003",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).004",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).005",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).006",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).008",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).009",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).010",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).012",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).013",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).015",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).017",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).022",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).025",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).028",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).029",
            "0.5\" ID Bearing (0.875\" OD, 0.281\" WD, Shielded).030",
            "0.5\" ID Bearing (1.125\" OD, 0.313\" WD, Shielded)",
            "0.5\" ID Bearing (1.125\" OD, 0.313\" WD, Shielded).002",
            "1.625\" Flex Wheel (40A, 1/2\" Wide, 1/2\" Hex Stretch Bore).006",
            "1.625\" Flex Wheel (40A, 1/2\" Wide, 1/2\" Hex Stretch Bore).009",
            "3088A429_Carbon Steel Shim.001",
            "3088A429_Carbon Steel Shim.002",
            "3088A429_Carbon Steel Shim.003",
            "3088A429_Carbon Steel Shim.004",
            "3088A429_Carbon Steel Shim.005",
            "3088A429_Carbon Steel Shim.006",
            "3088A429_Carbon Steel Shim.007",
            "3088A429_Carbon Steel Shim.008",
            "3088A429_Carbon Steel Shim.009",
            "3088A429_Carbon Steel Shim.010",
            "3088A429_Carbon Steel Shim.013",
            "3088A429_Carbon Steel Shim.014",
            "3088A429_Carbon Steel Shim.016",
            "3088A429_Carbon Steel Shim.017",
            "3088A429_Carbon Steel Shim.018",
            "3088A429_Carbon Steel Shim.020",
            "3088A429_Carbon Steel Shim.021",
            "3088A429_Carbon Steel Shim.023",
            "3088A429_Carbon Steel Shim.024",
            "3088A429_Carbon Steel Shim.025",
            "3088A429_Carbon Steel Shim.027",
            "3088A429_Carbon Steel Shim.028",
            "3088A429_Carbon Steel Shim.029",
            "3088A429_Carbon Steel Shim.030",
            "3088A429_Carbon Steel Shim.031",
            "IN-29 Spacer",
            "Kraken X60 Brushless Motor.009",
            "Part 1",
            "Part 81.002",
            "am-3949 2.25N OverMolded 500Hex Compliant Wheel V6",
            "am-3949 2.25N OverMolded 500Hex Compliant Wheel V6.001"
        };

        static readonly (string cad, string name)[] Subsystems =
        {
            ("Drivetrain Roller Bed Assembly Copy 1 <1>", "Drive"),
            ("Turrett Shooter V3 <1>", "Shooter"),
            ("V3 Comp Intake Assembly <1>", "Intake"),
        };

        // swerve drive motors (three module corners) -> drivetrain centre
        static readonly string[] DriveMotors =
            { "Kraken X60 Brushless Motor.005", "Kraken X60 Brushless Motor.006", "Kraken X60 Brushless Motor.010" };

        const float HalfTrackX = 0.265f, HalfBaseZ = 0.25f;   // module centres (CAD): 530 x 500 mm

        // InteractionRoller.RollerMotor order (for the log only)
        enum RollerMotorName { FullSize, Medium, Small, Tiny, Shooter, Unstable }

        // roller sets: Breakaway field, subsystem, part-name pattern, motor (RollerMotor index), running rpm, roller name
        static readonly (string field, string sub, string names, int motor, float rpm, string label)[] RollerSets =
        {
            // IN-42 Big Tube.001 is the arm's pivot tube (sprocket-driven), not a roller
            ("intakeRollers", "Intake Arm", @"^(IN-42 Big Tube|Part 5|Part 6)$",                          0, 2500f, "Intake Roller"),
            ("hopperRollers", "Drive",      @"^Part 67(\.\d{3})?$",                                        1, 1200f, "Hopper Roller"),
            ("feederRollers", "Drive",      @"^(Part 73|Part 80|Part 77\.002|DT-104)$|Flex Wheel",         1, 2500f, "Feeder Roller"),
            ("kickerRollers", "Turret",     @"Flex Wheel",                                                 1, 3000f, "Kicker Roller"),
            ("shooterWheels", "Turret",     @"Solid Urethane Wheel|Brass Flywheel|^am-3949",               4, 3500f, "Shooter Wheel"),
        };

        // moving mechanisms (CAD names): the turret turns on its 267 mm bearing ring (vertical axis through the ring
        // centre); the intake arm ("Intake V3 Assembly <1>") pivots on the axis through its two MotionX hubs
        const string TurretRing = "a64fbb17d93e9dac33c44939c0b4de06_ap214";
        const string IntakeArmCad = "Intake V3 Assembly <1>";
        static readonly string[] IntakeHubs =
            { "1/2\" Hex MotionX Hub (1.125\" OD, #10-32 Clearance)", "1/2\" Hex MotionX Hub (1.125\" OD, #10-32 Clearance).001" };

        // flywheel rpm by distance to the target (m); start values, tune in play mode
        static readonly (float d, float rpm)[] FlywheelMap = { (2.2f, 1600f), (3.2f, 1950f), (3.9f, 2350f), (4.1f, 2550f), (5.5f, 3500f), (8f, 6000f) };

        [MenuItem("Tools/Modding/Setup Breakaway (3937)")]
        static void MenuEntry() => Debug.Log(Run());

        public static string Run()
        {
            var log = new StringBuilder();
            // the FBX may have been replaced on disk: import it now so the instance below is the current file
            AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var fbxCheck = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            int meshCount = fbxCheck ? fbxCheck.GetComponentsInChildren<MeshFilter>(true).Length : 0;
            if (meshCount < 100) return $"ERROR the FBX has only {meshCount} mesh(es): re-export it with its meshes; nothing changed";
            log.AppendLine($"FBX: {meshCount} meshes");
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            if (root.transform.Find("Drive") != null) return "Breakaway already has a Drive subsystem: nothing changed";
            foreach (var (cad, _) in Subsystems)
                if (root.transform.Find(cad) != null)
                    return $"ERROR '{cad}' is already in Breakaway (inherited from the BaseRobot template?): restore BaseRobot first; nothing changed";

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (fbx == null) return "ERROR FBX not found: " + FbxPath;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx, stage.scene);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.transform.SetParent(root.transform, false);

            // 1. hidden parts
            var drop = new HashSet<string>(Review);
            int removed = 0;
            foreach (var t in inst.GetComponentsInChildren<Transform>(true).Where(t => t != inst.transform && drop.Contains(t.name)).ToList())
                if (t) { Object.DestroyImmediate(t.gameObject); removed++; }
            log.AppendLine($"removed {removed} hidden/duplicate parts");

            // 2. scale, orientation, position
            Bounds b = WorldBounds(inst);
            float big = Mathf.Max(b.size.x, b.size.y, b.size.z);
            float s = Mathf.Pow(10f, Mathf.Round(Mathf.Log10(1.084f / big)));
            if (!Mathf.Approximately(s, 1f)) { inst.transform.localScale *= s; log.AppendLine($"unit scale x{s}"); }
            Transform intake = Find(inst.transform, Subsystems[2].cad), turret = Find(inst.transform, Subsystems[1].cad);
            if (intake == null || turret == null) { Object.DestroyImmediate(inst); return "ERROR intake/turret assemblies not found in the FBX\n" + log; }
            Quaternion bestQ = Quaternion.identity; float best = float.MaxValue;
            foreach (Quaternion q in AxisRotations())
            {
                inst.transform.localRotation = q;
                Bounds all = WorldBounds(inst), bi = WorldBounds(intake.gameObject), bt = WorldBounds(turret.gameObject);
                float score = Mathf.Abs(all.size.y - 0.61f) * 10f + (bi.center.z > all.center.z ? 0f : 5f) + (bt.center.y > bi.center.y ? 0f : 5f);
                if (score < best) { best = score; bestQ = q; }
            }
            inst.transform.localRotation = bestQ;
            b = WorldBounds(inst);
            log.AppendLine($"orientation {bestQ.eulerAngles} size {b.size.x:F3} x {b.size.y:F3} x {b.size.z:F3} (score {best:F2})");

            var motors = inst.GetComponentsInChildren<Renderer>(true).Where(r => DriveMotors.Contains(r.name)).Select(r => r.bounds).ToList();
            Vector3 centre = b.center;
            if (motors.Count >= 3)
            {
                float x0 = motors.Min(m => m.center.x), x1 = motors.Max(m => m.center.x), z0 = motors.Min(m => m.center.z), z1 = motors.Max(m => m.center.z);
                centre = new Vector3((x0 + x1) / 2f, 0f, (z0 + z1) / 2f);
            }
            else log.AppendLine("WARNING swerve drive motors not found: centred on the bounding box");

            var dc = root.GetComponent<DriveController>();
            if (dc == null) { Object.DestroyImmediate(inst); return "ERROR no DriveController on the Breakaway root (BaseRobot template?)\n" + log; }
            var so = new SerializedObject(dc);
            var dtParent = so.FindProperty("drivetrainParent").objectReferenceValue as GameObject;
            float floor = 0f;
            var wheels = dtParent ? dtParent.GetComponentsInChildren<WheelCollider>(true) : new WheelCollider[0];
            if (wheels.Length > 0) floor = wheels.Min(w => w.transform.TransformPoint(w.center).y - w.radius);
            inst.transform.position += new Vector3(-centre.x, floor - b.min.y, -centre.z);
            log.AppendLine($"drivetrain centre moved to the root; wheel contact y {floor:F4}");

            // 3. subsystems
            var subs = new List<GameObject>();
            foreach (var (cad, name) in Subsystems)
            {
                Transform t = Find(inst.transform, cad);
                if (t == null) { log.AppendLine("WARNING missing " + cad); continue; }
                t.SetParent(root.transform, true); t.name = name; subs.Add(t.gameObject);
            }
            var rest = inst.GetComponentsInChildren<MeshRenderer>(true);
            if (rest.Length > 0)
            {
                var st = new GameObject("Stationary"); st.transform.SetParent(root.transform, false);
                foreach (var r in rest) r.transform.SetParent(st.transform, true);
                subs.Add(st); log.AppendLine($"Stationary: {rest.Length} loose parts");
            }
            Object.DestroyImmediate(inst);

            // 3b. moving mechanisms get pivot objects (identity rotation: docs' offset-parent rule), made subsystems
            GameObject turretGo = null, armGo = null;
            GameObject shooter = subs.FirstOrDefault(g => g.name == "Shooter");
            Renderer ring = shooter ? shooter.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == TurretRing) : null;
            if (shooter && ring)
            {
                turretGo = Pivot(root, "Turret", ring.bounds.center);
                shooter.transform.SetParent(turretGo.transform, true);
                subs[subs.IndexOf(shooter)] = turretGo;
                log.AppendLine($"Turret pivot at {turretGo.transform.localPosition} (bearing ring centre)");
            }
            else log.AppendLine("WARNING turret ring not found: no turret joint");
            GameObject intakeSub = subs.FirstOrDefault(g => g.name == "Intake");
            Transform arm = intakeSub ? Find(intakeSub.transform, IntakeArmCad) : null;
            var hubs = intakeSub ? intakeSub.GetComponentsInChildren<Renderer>(true).Where(r => IntakeHubs.Contains(r.name)).ToList() : new List<Renderer>();
            if (arm && hubs.Count == 2)
            {
                armGo = Pivot(root, "Intake Arm", (hubs[0].bounds.center + hubs[1].bounds.center) / 2f);
                arm.SetParent(armGo.transform, true);
                subs.Add(armGo);
                Vector3 axis = (hubs[1].bounds.center - hubs[0].bounds.center).normalized;
                log.AppendLine($"Intake Arm pivot at {armGo.transform.localPosition}, hub axis {axis} (expects ~X)");
            }
            else log.AppendLine("WARNING intake arm or its hubs not found: no intake joint");

            log.AppendLine(RunMenu("GameObject/Auto Organize", subs.Cast<Object>().ToArray()));

            // 4. colliders (docs: Guess Generic Colliders; Box Only keeps rollers free of colliders)
            foreach (var sub in subs)
            {
                var targets = sub.GetComponentsInChildren<MeshFilter>(true)
                    .Where(m => m.sharedMesh != null && m.GetComponent<Renderer>() && MaxExtent(m.GetComponent<Renderer>().bounds) > 0.05f)
                    .Select(m => (Object)m.gameObject).ToArray();
                log.AppendLine($"{sub.name}: " + RunMenu("GameObject/Guess Generic Collider (Box Only)", targets));
            }
            log.AppendLine(RunMenu("GameObject/Auto Organize", subs.Cast<Object>().ToArray()));
            int colliders = subs.Sum(g => g.GetComponentsInChildren<Collider>(true).Length);
            log.AppendLine($"colliders in the subsystems: {colliders}");

            // 4b. rollers (docs: "For rollers that move together, select the group ... Guess Interaction Roller").
            // Each set is its own run, so each gets its own RollerGroup; the robot script drives the groups.
            var robotType = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Breakaway"))
                .FirstOrDefault(t => t != null && typeof(MonoBehaviour).IsAssignableFrom(t));
            Component robot = robotType == null ? null : (root.GetComponent(robotType) ?? root.AddComponent(robotType));
            var robotSo = robot ? new SerializedObject(robot) : null;
            if (robot == null) log.AppendLine("WARNING Breakaway script type not found: roller groups not linked");
            foreach (var set in RollerSets)
            {
                GameObject sub = subs.FirstOrDefault(g => g.name == set.sub);
                if (sub == null) { log.AppendLine($"WARNING no {set.sub} subsystem for {set.field}"); continue; }
                var targets = sub.GetComponentsInChildren<MeshFilter>(true)
                    .Where(m => m.sharedMesh != null && System.Text.RegularExpressions.Regex.IsMatch(m.gameObject.name, set.names))
                    .Select(m => (Object)m.gameObject).ToArray();
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
                if (robotSo != null && newGroups.Count > 0) robotSo.FindProperty(set.field).objectReferenceValue = newGroups[0];
                log.AppendLine($"{set.field}: {res}; {newRollers.Count} roller(s), {newGroups.Count} group(s), {(RollerMotorName)set.motor} {set.rpm} rpm");
            }
            log.AppendLine(RunMenu("GameObject/Auto Organize", subs.Cast<Object>().ToArray()));   // docs: re-run at the end

            // 4c. joints (docs: Generic Joints, Turret): GenericJoint adds the Rigidbody + ConfigurableJoint; connected
            // body = the robot; every axis locked except the moving one. Turret: Y axis, automatic centre of mass off,
            // mass >= 5, GenericTurret. Intake arm: X axis.
            var robotRb = root.GetComponent<Rigidbody>();
            if (turretGo)
            {
                AddJoint(turretGo, robotRb, ConfigurableJointMotion.Locked, ConfigurableJointMotion.Free, 5f, true);
                var gt = turretGo.GetComponent<GenericTurret>() ?? turretGo.AddComponent<GenericTurret>();
                // shoot-while-moving: the turret aims from swmRef, which the base class leads by velocity x time of flight
                var anchor = new GameObject("SWM Anchor"); anchor.transform.SetParent(root.transform, false);
                anchor.transform.position = turretGo.transform.position;
                var swmRef = new GameObject("SWM Ref"); swmRef.transform.SetParent(anchor.transform, false);
                var gts = new SerializedObject(gt);
                gts.FindProperty("referencePoint").objectReferenceValue = swmRef.transform;
                gts.FindProperty("trackContinuously").boolValue = true;
                gts.ApplyModifiedPropertiesWithoutUndo();
                if (robotSo != null)
                {
                    robotSo.FindProperty("turret").objectReferenceValue = gt;
                    robotSo.FindProperty("swmRef").objectReferenceValue = swmRef.transform;
                    robotSo.FindProperty("swmSettings.timeOfFlight").floatValue = 0.8f;
                    robotSo.FindProperty("swmSettings.velocityDampeningValue").floatValue = 0.15f;
                    robotSo.FindProperty("swmSettings.shootingDriveMultiplier").floatValue = 0.5f;
                    SetPid(robotSo, "turretPid", 0.5f, 0f, 0.1f, 4f);
                }
                log.AppendLine("Turret: GenericJoint (Y) + GenericTurret, reference point = SWM Ref");
            }
            if (armGo)
            {
                AddJoint(armGo, robotRb, ConfigurableJointMotion.Free, ConfigurableJointMotion.Locked, 2f, false);
                if (robotSo != null)
                {
                    robotSo.FindProperty("intakeJoint").objectReferenceValue = armGo.GetComponent<GenericJoint>();
                    SetPid(robotSo, "intakePid", 0.5f, 0f, 0.1f, 4f);
                }
                log.AppendLine("Intake Arm: GenericJoint (X), CAD pose = deployed (0), stowed -100 deg");
            }
            if (robotSo != null)
            {
                var map = robotSo.FindProperty("flywheelInterpolation.interpolationMap");
                map.arraySize = FlywheelMap.Length;
                for (int i = 0; i < FlywheelMap.Length; i++)
                {
                    map.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = FlywheelMap[i].d;
                    map.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = FlywheelMap[i].rpm;
                }
                log.AppendLine($"flywheel interpolation: {FlywheelMap.Length} points");
            }
            robotSo?.ApplyModifiedPropertiesWithoutUndo();

            // 5. drive controller (docs: Drivetrain Setup)
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
            so.FindProperty("gearRatio").floatValue = 6.03f;                // SDS MK5n R2 (estimate)
            so.FindProperty("wheelDiameterInches").floatValue = 4f;
            so.FindProperty("mechanicalLossPercent").floatValue = 2.5f;
            so.FindProperty("frictionCoefficient").floatValue = 1.3f;
            so.FindProperty("biteFactor").floatValue = 0.25f;
            so.FindProperty("contactPatchWidthInches").floatValue = 1.5f;
            so.FindProperty("contactTolerance").floatValue = 0.01f;
            so.FindProperty("lateralGripResponse").floatValue = 0.1f;
            so.ApplyModifiedPropertiesWithoutUndo();
            var mv = typeof(DriveController).GetField("maxVelocity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            mv.SetValue(dc, (MoSimLib.SecureFloat)4.6f);                    // reachable m/s (free speed ~5.3)
            PrefabUtility.RecordPrefabInstancePropertyModifications(dc);
            EditorUtility.SetDirty(dc);
            log.AppendLine("DriveController set (Swerve, Kraken X60 FOC, 6.03:1, 4 in wheels, 135 lb, 4.6 m/s)");

            if (dtParent)
            {
                int moved = 0;
                foreach (string group in new[] { "Wheels", "Raycast" })
                {
                    Transform g = dtParent.transform.Find(group);
                    if (g == null) continue;
                    foreach (Transform w in g)
                    {
                        if (!(w.name.Length == 2 && "FB".Contains(w.name[0]) && "LR".Contains(w.name[1]))) continue;
                        float sx = w.name[1] == 'L' ? -1f : 1f, sz = w.name[0] == 'F' ? 1f : -1f;
                        w.localPosition = new Vector3(sx * HalfTrackX, w.localPosition.y, sz * HalfBaseZ);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(w);
                        moved++;
                    }
                }
                log.AppendLine($"swerve modules placed at +-{HalfTrackX} x +-{HalfBaseZ} m ({moved} transforms)");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool ok);
            if (ok) stage.ClearDirtiness();   // already saved: leaving the stage must not open a "save changes?" dialog
            StageUtility.GoToMainStage();
            log.AppendLine(ok ? "SAVED " + PrefabPath : "ERROR save failed");
            return log.ToString();
        }

        // InteractionRoller spins each model with Transform.Rotate about the model's OWN pivot. CAD meshes keep their
        // pivot at the assembly origin, so an off-axis pivot makes the part orbit instead of spin. Fix: give each such
        // model a pivot parent on the roller's axis (roller frame, so it turns about local X) and spin that instead.
        // Leaves belts (no models) and already-centred models alone. Run: MoSim.EditorTools.BreakawaySetup.FixRollerPivots
        [MenuItem("Tools/Modding/Breakaway: Fix Roller Pivots")]
        static void FixPivotsMenu() => Debug.Log(FixRollerPivots());

        public static string FixRollerPivots()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();
            int fixedCount = 0, ok = 0;
            foreach (var roller in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                var so = new SerializedObject(roller);
                if (so.FindProperty("useBeltMode").boolValue) continue;
                var models = so.FindProperty("models");
                Vector3 p = roller.transform.position, d = roller.transform.right;
                for (int i = 0; i < models.arraySize; i++)
                {
                    var entry = models.GetArrayElementAtIndex(i);
                    var model = entry.FindPropertyRelative("model").objectReferenceValue as GameObject;
                    if (model == null) continue;
                    Vector3 offAxis = Vector3.ProjectOnPlane(model.transform.position - p, d);
                    if (offAxis.magnitude < 0.002f) { ok++; continue; }
                    var r = model.GetComponent<Renderer>();
                    Vector3 c = r ? r.bounds.center : model.transform.position;
                    Vector3 onAxis = p + Vector3.Project(c - p, d);
                    var pivot = new GameObject(model.name + " Pivot");
                    pivot.transform.SetParent(model.transform.parent, false);
                    pivot.transform.SetPositionAndRotation(onAxis, roller.transform.rotation);
                    pivot.layer = model.layer;
                    model.transform.SetParent(pivot.transform, true);
                    entry.FindPropertyRelative("model").objectReferenceValue = pivot;
                    entry.FindPropertyRelative("jointAxis").enumValueIndex = 0;   // roller turns about its local X
                    entry.FindPropertyRelative("reversed").boolValue = false;
                    fixedCount++;
                    log.AppendLine($"  {roller.name}: {model.name} was {offAxis.magnitude * 1000f:F0} mm off the axis");
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"roller models re-pivoted onto their axis: {fixedCount}, already on axis: {ok}\n{log}" +
                   (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // ---- hood + hopper extension (added after the first setup; edits the existing prefab, keeps hand-made changes)
        // Hood: the SH-11/SH-12 adjustable plates and everything they carry pivot on the SH-16 shaft (the gear train
        // from the 18t gear drives into SH-16, so it must be the pivot). Hopper extension: the clear walls Part 15 /
        // Part 15.001 / Part 16 (+ Part 77 brackets) telescope inside the DT-39 walls; the CAD has them extended.
        static readonly string[] HoodParts =
        {
            "SH-11 Adjustable Shooter plate Left", "SH-12 Adjustable Shooter plate Right", "SH-16", "SH-17", "SH-18", "SH-19",
            "SH-21", "SH-21.001", "SH-22", "SH-23", "SH-26", "SH-26.001", "SH-26.002", "SH-28", "SH-29", "SH-30", "SH-31.008",
            "SH-35", "10t Steel Spur Gear (10 DP, 1/2\" Hex Bore)", "10t Steel Spur Gear (10 DP, 1/2\" Hex Bore).001",
            "32t Aluminum Spur Gear (20 DP, 1/2\" Hex Bore)", "32t Aluminum Spur Gear (20 DP, 1/2\" Hex Bore).001",
            "32t Aluminum Spur Gear (20 DP, 1/2\" Hex Bore).002", "24t Aluminum Pulley (HTD 5mm, 9mm Wide, 1/2\" Hex Bore)",
            "24t Aluminum Pulley (HTD 5mm, 9mm Wide, 1/2\" Hex Bore).001", "24t Aluminum Pulley (HTD 5mm, 9mm Wide, 1/2\" Hex Bore).003",
            "45T 5M 9mm Wide Belt", "1.625\" Flex Wheel (40A, 1/2\" Wide, 1/2\" Hex Stretch Bore)",
            "1.625\" Flex Wheel (40A, 1/2\" Wide, 1/2\" Hex Stretch Bore).001", "1.625\" Flex Wheel (40A, 1/2\" Wide, 1/2\" Hex Stretch Bore).002",
            "1.625\" Flex Wheel (40A, 1/2\" Wide, 1/2\" Hex Stretch Bore).003",
            "am-3949 2.25N OverMolded 500Hex Compliant Wheel V6.002", "am-3949 2.25N OverMolded 500Hex Compliant Wheel V6.003",
        };
        const string HoodPivotShaft = "SH-16";
        static readonly string[] HopperParts = { "Part 15", "Part 15.001", "Part 16", "Part 77", "Part 77.001" };
        const string FrameFrontRail = "DT-02 Front Drive rail";
        static readonly (float d, float deg)[] HoodMap = { (2.5f, 0f), (3.9f, 12f), (4.5f, 17f), (6f, 24f), (8f, 30f) };

        public static string AddHoodAndHopper()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();
            if (root.transform.Find("Turret/Hood") || root.transform.Find("Hopper Extension"))
                return "Hood / Hopper Extension already exist: nothing changed";
            Transform turretT = root.transform.Find("Turret"), driveT = root.transform.Find("Drive"), intakeT = root.transform.Find("Intake");
            if (!turretT || !driveT || !intakeT) return "ERROR Turret / Drive / Intake subsystem missing";
            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Breakaway");
            var robotSo = robot ? new SerializedObject(robot) : null;
            if (robotSo == null || robotSo.FindProperty("hoodJoint") == null) return "ERROR Breakaway script missing or not recompiled";

            // ---- hood
            var shaft = turretT.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == HoodPivotShaft);
            if (shaft == null) return "ERROR hood pivot shaft SH-16 not found";
            Vector3 sz = shaft.bounds.size;
            Vector3 axis = sz.x >= sz.y && sz.x >= sz.z ? Vector3.right : sz.y >= sz.z ? Vector3.up : Vector3.forward;
            var hood = new GameObject("Hood");
            hood.transform.SetParent(turretT, false);
            hood.transform.SetPositionAndRotation(shaft.bounds.center, Quaternion.FromToRotation(Vector3.right, axis));
            var moved = MoveParts(turretT, hood.transform, HoodParts, log);
            log.AppendLine($"Hood: pivot on SH-16 at {hood.transform.localPosition}, axis {axis}, {moved.Count} parts moved");

            // ---- hopper extension: start retracted so the front wall sits at the frame's front rail
            var hop = new GameObject("Hopper Extension");
            hop.transform.SetParent(root.transform, false);
            var front = intakeT.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "Part 16");
            var rail = driveT.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == FrameFrontRail);
            if (front == null || rail == null) return "ERROR Part 16 or the front drive rail not found\n" + log;
            hop.transform.SetPositionAndRotation(front.bounds.center, Quaternion.identity);
            var hopMoved = MoveParts(intakeT, hop.transform, HopperParts, log);
            float retract = front.bounds.max.z - rail.bounds.max.z;   // robot forward = +Z
            hop.transform.position -= new Vector3(0f, 0f, retract);
            log.AppendLine($"Hopper Extension: {hopMoved.Count} parts, retracted {retract * 1000f:F0} mm into the frame");

            // ---- joints (docs: Generic Joints): hood on the turret body, hopper linear on the robot body
            AddJoint(hood, turretT.GetComponent<Rigidbody>(), ConfigurableJointMotion.Free, ConfigurableJointMotion.Locked, 1f, false);
            var hj = hop.AddComponent<GenericJoint>();
            var hrb = hop.GetComponent<Rigidbody>(); hrb.mass = 1f;
            var cj = hop.GetComponent<ConfigurableJoint>();
            cj.connectedBody = root.GetComponent<Rigidbody>();
            cj.anchor = Vector3.zero; cj.axis = Vector3.right; cj.secondaryAxis = Vector3.up;
            cj.xMotion = cj.yMotion = ConfigurableJointMotion.Locked; cj.zMotion = ConfigurableJointMotion.Free;
            cj.angularXMotion = cj.angularYMotion = cj.angularZMotion = ConfigurableJointMotion.Locked;

            robotSo.FindProperty("hoodJoint").objectReferenceValue = hood.GetComponent<GenericJoint>();
            robotSo.FindProperty("hopperJoint").objectReferenceValue = hj;
            robotSo.FindProperty("hopperExtendedDistance").floatValue = retract / 0.0254f;
            SetPid(robotSo, "hoodPid", 0.5f, 0f, 0.1f, 4f);        // docs' angular starting values
            SetPid(robotSo, "hopperPid", 15f, 0f, 0.1f, 3f);       // docs' linear starting values
            var map = robotSo.FindProperty("hoodInterpolation.interpolationMap");
            map.arraySize = HoodMap.Length;
            for (int i = 0; i < HoodMap.Length; i++)
            {
                map.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = HoodMap[i].d;
                map.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = HoodMap[i].deg;
            }
            robotSo.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine($"linked hoodJoint, hopperJoint (extends {retract / 0.0254f:F1} in), PIDs, hood map ({HoodMap.Length} points)");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return log + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Moves the named meshes (or their roller pivot parents), the colliders fitted to them, and any roller whose
        // models all moved, from under `from` to under `to` (world positions kept).
        static List<Transform> MoveParts(Transform from, Transform to, string[] names, StringBuilder log)
        {
            var set = new HashSet<string>(names);
            var meshes = from.GetComponentsInChildren<MeshRenderer>(true).Where(r => set.Contains(r.name) && !r.transform.IsChildOf(to)).ToList();
            var bounds = meshes.Select(r => r.bounds).ToList();
            var moved = new List<Transform>();
            foreach (var r in meshes)
            {
                Transform t = r.transform;
                if (t.parent && t.parent.name == t.name + " Pivot") t = t.parent;   // roller model pivot from FixRollerPivots
                t.SetParent(to, true); moved.Add(t);
            }
            int cols = 0;
            foreach (var c in from.GetComponentsInChildren<Collider>(true).Where(c => !c.transform.IsChildOf(to)).ToList())
            {
                Bounds cb = c.bounds;
                if (bounds.Any(b => (b.center - cb.center).magnitude < 0.01f && (b.size - cb.size).magnitude < 0.03f))
                { c.transform.SetParent(to, true); cols++; }
            }
            int rollers = 0;
            foreach (var roller in from.GetComponentsInChildren<InteractionRoller>(true).Where(r => !r.transform.IsChildOf(to)).ToList())
            {
                var models = new SerializedObject(roller).FindProperty("models");
                var objs = Enumerable.Range(0, models.arraySize)
                    .Select(i => models.GetArrayElementAtIndex(i).FindPropertyRelative("model").objectReferenceValue as GameObject).ToList();
                if (objs.Count > 0 && objs.All(o => o && o.transform.IsChildOf(to))) { roller.transform.SetParent(to, true); rollers++; }
            }
            int missing = names.Length - meshes.Count;
            log.AppendLine($"  -> {to.name}: {meshes.Count} meshes, {cols} colliders, {rollers} rollers" + (missing > 0 ? $" ({missing} names not found)" : ""));
            return moved;
        }

        // GenericJoint reads linear position as transform.localPosition and expects 0 at rest (docs: offset parent
        // rule); angular joints expect local rotation 0 except about their axis. Wraps a joint object in an offset
        // parent carrying its pose so its own local position / rotation are zero. Fixes Hopper Extension and Hood.
        public static string FixJointOffsets()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var log = new StringBuilder();
            foreach (string path in new[] { "Hopper Extension", "Turret/Hood", "Intake Arm", "Turret" })
            {
                Transform j = root.transform.Find(path);
                if (j == null) { log.AppendLine($"{path}: not found"); continue; }
                bool needsPos = path == "Hopper Extension" && j.localPosition.sqrMagnitude > 1e-8f;
                bool needsRot = Quaternion.Angle(j.localRotation, Quaternion.identity) > 0.01f;
                if (!needsPos && !needsRot) { log.AppendLine($"{path}: already zeroed"); continue; }
                var offset = new GameObject(j.name + " Offset");
                offset.transform.SetParent(j.parent, false);
                offset.transform.localPosition = j.localPosition;
                offset.transform.localRotation = j.localRotation;
                offset.transform.SetSiblingIndex(j.GetSiblingIndex());
                j.SetParent(offset.transform, true);
                j.localPosition = Vector3.zero; j.localRotation = Quaternion.identity;
                log.AppendLine($"{path}: wrapped in '{offset.name}' (was pos {offset.transform.localPosition}, rot {offset.transform.localEulerAngles})");
            }
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return log + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Owner 2026-11-20: the hood pivots about the shooter-wheel shaft (SH-20, the 4" urethane wheels' shaft), not SH-16.
        // Moves the "Hood Offset" pivot onto SH-20's axis (axis direction from the shaft's long side); hood parts keep
        // their world poses, the Hood joint object stays at local zero under the offset.
        public static string MoveHoodPivot()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            Transform turret = root.transform.Find("Turret");
            Transform offset = turret ? turret.Find("Hood Offset") : null;
            Transform hood = offset ? offset.Find("Hood") : null;
            if (hood == null) return "ERROR Turret/Hood Offset/Hood not found (run FixJointOffsets first)";
            var shaft = turret.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "SH-20" && !r.transform.IsChildOf(hood));
            if (shaft == null) return "ERROR shooter-wheel shaft SH-20 not found on the turret";
            Vector3 sz = shaft.bounds.size;
            Vector3 axis = sz.x >= sz.y && sz.x >= sz.z ? Vector3.right : sz.y >= sz.z ? Vector3.up : Vector3.forward;

            var kids = new List<(Transform t, Vector3 p, Quaternion r)>();
            foreach (Transform c in hood) kids.Add((c, c.position, c.rotation));
            Vector3 was = offset.position;
            offset.SetPositionAndRotation(shaft.bounds.center, Quaternion.FromToRotation(Vector3.right, axis));
            hood.localPosition = Vector3.zero; hood.localRotation = Quaternion.identity;
            foreach (var k in kids) k.t.SetPositionAndRotation(k.p, k.r);
            var cj = hood.GetComponent<ConfigurableJoint>();
            if (cj) { cj.anchor = Vector3.zero; cj.axis = Vector3.right; cj.secondaryAxis = Vector3.up; }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"Hood pivot moved from {turret.InverseTransformPoint(was)} to SH-20 at {offset.localPosition} " +
                   $"(axis {axis}, moved {(shaft.bounds.center - was).magnitude * 1000f:F0} mm); {kids.Count} hood parts kept in place\n" +
                   (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Direction fixes from ReportRollerDirections (owner 2026-11-20: "still cant shoot and intake sucks"):
        //  - intake rollers spun front-face-down / top-forward (pushing fuel out): over-the-bumper needs spin about -X
        //  - the feeder tunnel's horizontal pairs pushed fuel forward (+Z) back into the hopper; the belts are behind
        //  - turret too slow to aim (log: 10-50 deg error): PID Max 4 -> 10 and tolerance 5 -> 15 deg (1706's values)
        public static string FixDirectionsAndTurret()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var sb = new StringBuilder();
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                var so = new SerializedObject(r);
                if (so.FindProperty("useBeltMode").boolValue)
                {
                    sb.AppendLine($"belt {r.name}: local up {Fmt(root.InverseTransformDirection(r.transform.up))}, " +
                                  $"forward {Fmt(root.InverseTransformDirection(r.transform.forward))}, rpm {so.FindProperty("rpm").floatValue:F0} (left as is)");
                    continue;
                }
                var rpmP = so.FindProperty("rpm");
                Vector3 axis = root.InverseTransformDirection(r.transform.right);
                Vector3 w = axis * Mathf.Sign(rpmP.floatValue == 0 ? 1 : rpmP.floatValue);
                bool onArm = r.transform.IsChildOf(root.Find("Intake Arm") ?? root);
                bool feederTunnel = r.name == "Feeder Roller" && Mathf.Abs(axis.x) > 0.9f
                                    && r.transform.parent && r.transform.parent.name.StartsWith("Drive");
                bool flip = (onArm && w.x > 0.5f) || (feederTunnel && Vector3.Cross(w, r.transform.position.y < root.TransformPoint(0, 0.25f, 0).y ? Vector3.up : Vector3.down).z > 0.5f);
                if (!flip) continue;
                rpmP.floatValue = -rpmP.floatValue;
                so.ApplyModifiedPropertiesWithoutUndo();
                sb.AppendLine($"flipped {r.transform.parent?.name}/{r.name} at {Fmt(root.InverseTransformPoint(r.transform.position))} -> rpm {rpmP.floatValue:F0}");
            }
            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Breakaway");
            if (robot)
            {
                var rs = new SerializedObject(robot);
                rs.FindProperty("turretPid.Max").floatValue = 10f;
                rs.FindProperty("turretTolerance").floatValue = 15f;
                rs.ApplyModifiedPropertiesWithoutUndo();
                sb.AppendLine("turret PID Max 10, aim tolerance 15 deg");
            }
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return sb + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        static string Fmt(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        // The kicker belts copy the CAD's two 120T x 15 mm HTD5 belts: flat conveyors along the robot's width (X) above
        // and below the fuel, carrying it -X toward the turret. A belt grips with its local +/-Y faces and moves them
        // along local Z (+Y face -> +Z at positive rpm). The hand-placed belts had their faces pointing +/-Z (into the
        // tunnel exit), so fuel hit their edge. Orients both: bottom belt grips with its +Y face, top belt with its -Y
        // face, both moving fuel -X; surface speed matched to the feeder rollers; length = CAD 280 mm.
        public static string FixKickerBelts()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var belts = root.GetComponentsInChildren<InteractionRoller>(true)
                .Where(r => new SerializedObject(r).FindProperty("useBeltMode").boolValue).OrderBy(r => r.transform.position.y).ToList();
            if (belts.Count != 2) return $"ERROR expected 2 belts, found {belts.Count}";
            var feeder = root.GetComponentsInChildren<InteractionRoller>(true).FirstOrDefault(r => r.name == "Feeder Roller" && r.transform.parent.name == "Drive (2)");
            float feedSpeed = 4f;   // m/s fallback
            if (feeder)
            {
                var fs = new SerializedObject(feeder);
                feedSpeed = Mathf.Abs(fs.FindProperty("rpm").floatValue) * 2f * Mathf.PI / 60f * fs.FindProperty("radius").floatValue * 0.0254f;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                var b = belts[i];
                bool bottom = i == 0;
                // bottom: +Y face (top) runs +local Z = -X;  top: -Y face (bottom) runs -local Z = -X
                Vector3 fwd = root.TransformDirection(bottom ? Vector3.left : Vector3.right);
                b.transform.rotation = Quaternion.LookRotation(fwd, root.up);
                var so = new SerializedObject(b);
                float r = so.FindProperty("radius").floatValue * 0.0254f;
                float rpm = feedSpeed / Mathf.Max(r, 0.001f) * 60f / (2f * Mathf.PI);
                so.FindProperty("rpm").floatValue = Mathf.Round(rpm);
                so.FindProperty("beltLength").floatValue = 11f;
                so.ApplyModifiedPropertiesWithoutUndo();
                sb.AppendLine($"{b.name} ({(bottom ? "bottom" : "top")}, y {root.InverseTransformPoint(b.transform.position).y:F2}): " +
                              $"faces up/down, runs fuel toward -X (turret), rpm {Mathf.Round(rpm)} (surface {feedSpeed:F1} m/s), length 11 in");
            }
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return sb + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // The two hand-made kicker belts face the feeder tunnel (+Z) but drove fuel opposite ways along X; the turret
        // entry is at -X (Belt already moves fuel -X), so Belt (2) is reversed to match.
        public static string FlipBelt2()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            var belt = stage.prefabContentsRoot.GetComponentsInChildren<InteractionRoller>(true).FirstOrDefault(r => r.name == "Belt (2)");
            if (belt == null) return "ERROR Belt (2) not found";
            var so = new SerializedObject(belt);
            var rpm = so.FindProperty("rpm");
            rpm.floatValue = -rpm.floatValue;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"Belt (2) rpm -> {rpm.floatValue:F0}\n" + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Owner 2026-11-20: "replace the belts with a bunch of little rollers". The two kicker belts become two rows of
        // six 1.5" rollers (2" long, axis along Z) at the CAD belts' running surfaces (bottom run top y 0.173, top run
        // bottom y 0.315 -> 14 mm squeeze on the 150 mm fuel), x 0.013..0.253 (CAD pulley centres), z -0.13. Both rows
        // move fuel -X toward the turret at the feeder's surface speed. They reuse the belt group (Breakaway.kickerBelts),
        // copy a tunnel feeder roller's settings, and have no spinning models (the CAD belt meshes stay as the visual).
        public static string ReplaceBeltsWithRollers()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var all = root.GetComponentsInChildren<InteractionRoller>(true);
            var belts = all.Where(r => new SerializedObject(r).FindProperty("useBeltMode").boolValue).ToList();
            if (belts.Count == 0) return "ERROR no belts found (already replaced?)";
            var group = belts[0].GetComponentInParent<RollerGroup>();
            if (group == null) return "ERROR the belts' roller group was not found";
            var template = all.FirstOrDefault(r => r.name == "Feeder Roller" && r.transform.parent.name == "Drive (2)"
                                                   && Mathf.Abs(root.InverseTransformDirection(r.transform.right).x) > 0.9f);
            if (template == null) return "ERROR no tunnel feeder roller to copy settings from";
            var ts = new SerializedObject(template);
            float feedSpeed = Mathf.Abs(ts.FindProperty("rpm").floatValue) * 2f * Mathf.PI / 60f * ts.FindProperty("radius").floatValue * 0.0254f;

            const float radiusIn = 0.75f, lengthIn = 2f, r = radiusIn * 0.0254f;
            float rpm = Mathf.Round(feedSpeed / r * 60f / (2f * Mathf.PI));
            const int perRow = 6;
            const float x0 = 0.013f, x1 = 0.253f, z = -0.13f;
            float[] rowY = { 0.173f - r, 0.315f + r };   // bottom row under the fuel, top row above it
            var created = new List<InteractionRoller>();
            for (int row = 0; row < 2; row++)
            {
                for (int i = 0; i < perRow; i++)
                {
                    var go = Object.Instantiate(template.gameObject, group.transform);
                    go.name = $"Kicker Roller {(row == 0 ? "Low" : "High")} {i + 1}";
                    foreach (Transform c in go.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(c.gameObject);
                    float x = x0 + (x1 - x0) * i / (perRow - 1);
                    go.transform.position = root.TransformPoint(new Vector3(x, rowY[row], z));
                    // axis +Z: bottom row positive rpm moves its top surface -X; top row negative rpm moves its bottom -X
                    go.transform.rotation = Quaternion.LookRotation(root.TransformDirection(Vector3.left), root.TransformDirection(Vector3.up))
                                            * Quaternion.identity;
                    go.transform.rotation = Quaternion.FromToRotation(go.transform.right, root.forward) * go.transform.rotation;
                    var roller = go.GetComponent<InteractionRoller>();
                    var so = new SerializedObject(roller);
                    so.FindProperty("radius").floatValue = radiusIn;
                    so.FindProperty("length").floatValue = lengthIn;
                    so.FindProperty("rpm").floatValue = row == 0 ? rpm : -rpm;
                    so.FindProperty("models").arraySize = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    created.Add(roller);
                }
            }
            // the group now drives the rollers instead of the belts
            var gs = new SerializedObject(group);
            var members = gs.FindProperty("rollers");
            members.arraySize = created.Count;
            for (int i = 0; i < created.Count; i++)
            {
                var e = members.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("IntRoller").objectReferenceValue = created[i];
                e.FindPropertyRelative("GenRoller").objectReferenceValue = null;
                e.FindPropertyRelative("PvffController").objectReferenceValue = null;
                e.FindPropertyRelative("Flywheel").objectReferenceValue = null;
                e.FindPropertyRelative("Multiplier").floatValue = 1f;
                e.FindPropertyRelative("flipped").boolValue = false;
            }
            gs.ApplyModifiedPropertiesWithoutUndo();
            group.gameObject.name = "Kicker Roller Group";
            foreach (var b in belts) Object.DestroyImmediate(b.gameObject);

            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"replaced {belts.Count} belts with {created.Count} rollers ({perRow} per row, {radiusIn * 2f}\" x {lengthIn}\", " +
                   $"{rpm} rpm = {feedSpeed:F1} m/s surface, low row y {rowY[0]:F3} / high row y {rowY[1]:F3}, x {x0}..{x1}, z {z}) " +
                   $"in '{group.name}' (still Breakaway.kickerBelts)\n" + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Owner 2026-11-20: "add rollers that follow the curve of Part 81.003". The chute floor (slice at z -0.13, the
        // fuel's centre plane) sweeps from the kicker exit (x -0.04, y 0.18) up and left to the turret ring (y 0.36), concave
        // toward the fuel (curvature centre ~(-0.04, 0.36)). The fuel-side surface = the slice points nearest that centre
        // per angle. Rollers (1.5" x 5", axis Z) are placed every ~45 mm along it, sticking 5 mm out of the surface,
        // spinning so their fuel-side surface runs along the curve toward the turret at the kicker rollers' speed; they
        // join the kicker roller group (Breakaway.kickerBelts), so the feed gate runs them.
        public static string AddChuteRollers()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            if (root.GetComponentsInChildren<InteractionRoller>(true).Any(r => r.name.StartsWith("Chute Roller")))
                return "chute rollers already exist: nothing changed";
            var mf = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == "Part 81.003" && m.sharedMesh);
            if (mf == null) return "ERROR Part 81.003 not found";
            const float zCut = -0.13f;
            var v = mf.sharedMesh.vertices.Select(p => root.InverseTransformPoint(mf.transform.TransformPoint(p))).ToArray();
            var tri = mf.sharedMesh.triangles;
            var pts = new List<Vector2>();
            for (int i = 0; i < tri.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    Vector3 a = v[tri[i + e]], b = v[tri[i + (e + 1) % 3]];
                    if ((a.z - zCut) * (b.z - zCut) > 0f || Mathf.Approximately(a.z, b.z)) continue;
                    Vector3 p = Vector3.Lerp(a, b, (zCut - a.z) / (b.z - a.z));
                    pts.Add(new Vector2(p.x, p.y));
                }
            // fuel-side envelope: per 4-degree bin around the curvature centre, the nearest point
            var C = new Vector2(-0.04f, 0.36f);
            var env = new List<Vector2>();
            for (float a = 268f; a >= 182f; a -= 4f)
            {
                var bin = pts.Where(p => { float ang = Mathf.Atan2(p.y - C.y, p.x - C.x) * Mathf.Rad2Deg; if (ang < 0) ang += 360f; return Mathf.Abs(ang - a) <= 2f; }).ToList();
                if (bin.Count == 0) continue;
                env.Add(bin.OrderBy(p => (p - C).sqrMagnitude).First());
            }
            if (env.Count < 4) return $"ERROR could not trace the chute curve ({env.Count} points from {pts.Count})";
            // smooth (3-point) and resample every ~45 mm along the curve
            var sm = env.Select((p, i) => (env[Mathf.Max(0, i - 1)] + p + env[Mathf.Min(env.Count - 1, i + 1)]) / 3f).ToList();
            var group = root.GetComponentsInChildren<RollerGroup>(true).FirstOrDefault(g => g.name == "Kicker Roller Group");
            var template = root.GetComponentsInChildren<InteractionRoller>(true).FirstOrDefault(r => r.name == "Kicker Roller Low 3");
            if (group == null || template == null) return "ERROR Kicker Roller Group / Kicker Roller Low 3 not found (replace the belts first)";
            var tsx = new SerializedObject(template);
            float rpm = Mathf.Abs(tsx.FindProperty("rpm").floatValue);
            const float radiusIn = 0.75f, lengthIn = 5f, r = radiusIn * 0.0254f, step = 0.045f, protrude = 0.005f;
            var created = new List<InteractionRoller>();
            var sb = new StringBuilder();
            float carry = 0.02f;   // first roller 20 mm in from the entry
            for (int i = 0; i < sm.Count - 1; i++)
            {
                Vector2 a = sm[i], b = sm[i + 1];
                float seg = (b - a).magnitude;
                while (carry <= seg)
                {
                    Vector2 s = Vector2.Lerp(a, b, carry / seg);
                    Vector2 t = (b - a).normalized;                       // travel direction: entry -> turret
                    Vector2 n = (C - s).normalized;                       // toward the fuel (curvature centre side)
                    Vector2 c2 = s - n * (r - protrude);                  // roller centre: top sticks 5 mm out
                    var go = Object.Instantiate(template.gameObject, group.transform);
                    go.name = $"Chute Roller {created.Count + 1}";
                    foreach (Transform ch in go.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(ch.gameObject);
                    go.transform.position = root.TransformPoint(new Vector3(c2.x, c2.y, zCut));
                    go.transform.rotation = Quaternion.LookRotation(root.TransformDirection(Vector3.left), root.up);   // right = +Z
                    // surface at offset n moves along (+Z * sign) x n; pick the sign that moves it along t
                    Vector3 vel = Vector3.Cross(Vector3.forward, new Vector3(n.x, n.y, 0f));
                    float sign = Vector2.Dot(new Vector2(vel.x, vel.y), t) >= 0f ? 1f : -1f;
                    var so = new SerializedObject(go.GetComponent<InteractionRoller>());
                    so.FindProperty("radius").floatValue = radiusIn;
                    so.FindProperty("length").floatValue = lengthIn;
                    so.FindProperty("rpm").floatValue = sign * rpm;
                    so.FindProperty("models").arraySize = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    created.Add(go.GetComponent<InteractionRoller>());
                    sb.AppendLine($"  {go.name} at ({c2.x:F3},{c2.y:F3}) rpm {sign * rpm:F0}");
                    carry += step;
                }
                carry -= seg;
            }
            var gs = new SerializedObject(group);
            var members = gs.FindProperty("rollers");
            int start = members.arraySize;
            members.arraySize = start + created.Count;
            for (int i = 0; i < created.Count; i++)
            {
                var e = members.GetArrayElementAtIndex(start + i);
                e.FindPropertyRelative("IntRoller").objectReferenceValue = created[i];
                e.FindPropertyRelative("GenRoller").objectReferenceValue = null;
                e.FindPropertyRelative("PvffController").objectReferenceValue = null;
                e.FindPropertyRelative("Flywheel").objectReferenceValue = null;
                e.FindPropertyRelative("Multiplier").floatValue = 1f;
                e.FindPropertyRelative("flipped").boolValue = false;
            }
            gs.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"traced the chute curve from {pts.Count} slice points ({env.Count} envelope points, " +
                   $"({sm[0].x:F3},{sm[0].y:F3}) -> ({sm[sm.Count - 1].x:F3},{sm[sm.Count - 1].y:F3})); added {created.Count} chute rollers " +
                   $"to '{group.name}':\n{sb}" + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Cross-sections of a mesh (robot frame) in the plane z = zCut: every triangle edge crossing the plane gives a
        // point (x, y). Returned as "x,y" lines for analysing the chute's curve.
        public static string SliceChute() => Slice("Part 81.003", -0.13f);

        static string Slice(string meshName, float zCut)
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var mf = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == meshName && m.sharedMesh);
            if (mf == null) return "ERROR mesh not found: " + meshName;
            var mesh = mf.sharedMesh;
            var v = mesh.vertices.Select(p => root.InverseTransformPoint(mf.transform.TransformPoint(p))).ToArray();
            var t = mesh.triangles;
            var pts = new List<Vector2>();
            for (int i = 0; i < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    Vector3 a = v[t[i + e]], b = v[t[i + (e + 1) % 3]];
                    if ((a.z - zCut) * (b.z - zCut) > 0f || Mathf.Approximately(a.z, b.z)) continue;
                    float k = (zCut - a.z) / (b.z - a.z);
                    Vector3 p = Vector3.Lerp(a, b, k);
                    pts.Add(new Vector2(p.x, p.y));
                }
            var uniq = pts.Select(p => new Vector2(Mathf.Round(p.x * 1000f) / 1000f, Mathf.Round(p.y * 1000f) / 1000f)).Distinct().ToList();
            var bmin = v.Aggregate(Vector3.Min); var bmax = v.Aggregate(Vector3.Max);
            return $"{meshName}: {v.Length} verts, bounds {Fmt(bmin)}..{Fmt(bmax)}; slice z={zCut}: {uniq.Count} points\n" +
                   string.Join("\n", uniq.OrderBy(p => p.x).ThenBy(p => p.y).Select(p => $"{p.x:F3},{p.y:F3}"));
        }

        // The hand-made chute colliders (Part 81.003/GeneratedClusterColliders) put the chute floor 17-35 mm above a
        // ball's bottom where it leaves the last kicker roller (lip walls to y 0.19, floor to 0.21; ball bottom 0.173).
        // Ramps the turret end of both kicker rows up (pair 1 +35 mm, pair 2 +17 mm; squeeze unchanged) so fuel leaves
        // at chute-floor height, and removes the setup's solid box on the old belt's backing tube (Part 64.001,
        // 'Box (57)' at y 0.325..0.355) that would block the raised path.
        public static string RampKickerExit()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var sb = new StringBuilder();
            var lift = new Dictionary<int, float> { { 2, 0.017f }, { 1, 0.035f } };   // roller 1 is at the turret end (x 0.013)
            int moved = 0;
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                var m = System.Text.RegularExpressions.Regex.Match(r.name, @"^Kicker Roller (Low|High) (\d)$");
                if (!m.Success || !lift.TryGetValue(int.Parse(m.Groups[2].Value), out float dy)) continue;
                Vector3 p = root.InverseTransformPoint(r.transform.position);
                if (p.x > 0.08f) { sb.AppendLine($"skipped {r.name}: x {p.x:F3} is not at the turret end"); continue; }
                r.transform.position = root.TransformPoint(p + new Vector3(0f, dy, 0f));
                sb.AppendLine($"{r.name}: x {p.x:F3} y {p.y:F3} -> {p.y + dy:F3}");
                moved++;
            }
            var box = root.GetComponentsInChildren<BoxCollider>(true).Where(b => b.name == "Box (57)")
                .FirstOrDefault(b => (root.InverseTransformPoint(b.bounds.center) - new Vector3(0.13f, 0.34f, -0.13f)).magnitude < 0.02f);
            if (box) { Object.DestroyImmediate(box.gameObject); sb.AppendLine("removed Box (57) (old belt backing tube)"); }
            if (moved == 0) return "ERROR no kicker rollers moved\n" + sb;
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return sb + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Removes 'Box (61)', the setup's solid box on the Part 72 plate across the kicker-roller exit (robot frame centre
        // (-0.02, 0.16, -0.13), 0.01 x 0.10 x 0.21 m): fuel stopped against it at the last kicker roller.
        public static string RemoveKickerExitBox()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var expect = new Vector3(-0.02f, 0.16f, -0.13f);
            var box = root.GetComponentsInChildren<BoxCollider>(true)
                .Where(b => b.name == "Box (61)")
                .OrderBy(b => (root.InverseTransformPoint(b.bounds.center) - expect).magnitude).FirstOrDefault();
            if (box == null) return "ERROR Box (61) not found";
            Vector3 p = root.InverseTransformPoint(box.bounds.center);
            if ((p - expect).magnitude > 0.02f) return $"ERROR Box (61) is at {Fmt(p)}, not at the kicker exit: nothing changed";
            string where = $"{box.transform.parent?.parent?.name}/{box.transform.parent?.name}";
            Object.DestroyImmediate(box.gameObject);
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"removed Box (61) under {where} at {Fmt(p)}\n" + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Removes the setup's solid boxes that block the turret entry: the box fitted to the hollow chute Part 81.003
        // (it has hand-made GeneratedClusterColliders) and the one on Part 76.001 overlapping fuel leaving the belts.
        // Only boxes directly under Drive/collider whose fitted mesh matches are removed.
        public static string RemoveFuelPathBlockers()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            Transform cols = root.Find("Drive/collider") ?? root.Find("Drive/Model/collider");
            if (cols == null) return "ERROR Drive collider folder not found";
            var meshes = root.GetComponentsInChildren<MeshRenderer>(true);
            // + Part 72: the 6 mm plate across the kicker-roller exit; its solid box stands 32 mm above a ball's bottom
            //   (the real foam ball squashes past it, the sim's rigid one stops at the last kicker roller)
            var targets = new[] { "Part 81.003", "Part 76.001", "Part 72" };
            var targetMeshes = meshes.Where(m => targets.Contains(m.name)).ToList();
            var sb = new StringBuilder();
            foreach (var box in cols.GetComponentsInChildren<BoxCollider>(true).ToList())
            {
                if (box.transform.parent != cols) continue;
                // the box fitted to a target mesh: same bounds centre (within 2 cm); the fitted box can be larger than
                // the mesh's axis-aligned bounds (fitted at an angle), so size is not compared
                var src = targetMeshes.FirstOrDefault(m => (m.bounds.center - box.bounds.center).magnitude < 0.02f);
                if (src == null) continue;
                sb.AppendLine($"removed '{box.name}' (solid box on {src.name}, size {Fmt(box.bounds.size)})");
                Object.DestroyImmediate(box.gameObject);
            }
            if (sb.Length == 0)
            {
                var near = cols.GetComponentsInChildren<BoxCollider>(true).Where(b => b.transform.parent == cols)
                    .Select(b => (b, d: targetMeshes.Count == 0 ? -1f : targetMeshes.Min(m => (m.bounds.center - b.bounds.center).magnitude)))
                    .OrderBy(x => x.d).Take(3).Select(x => $"{x.b.name} {x.d * 1000f:F0} mm");
                return $"nothing removed; target meshes found {targetMeshes.Count}; nearest boxes: {string.Join(", ", near)}";
            }
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return sb + (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        // Lists colliders whose bounds overlap the fuel path (robot frame: hopper floor -> feeder tunnel -> belts ->
        // turret entry), with the mesh each was fitted to (same bounds centre), to find solid boxes blocking fuel.
        public static string ReportFuelPathColliders()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var path = new Bounds(new Vector3(0.02f, 0.26f, -0.07f), new Vector3(0.56f, 0.15f, 0.36f));   // x -0.26..0.30, y 0.185..0.335, z -0.25..0.11
            var meshes = root.GetComponentsInChildren<MeshRenderer>(true);
            var sb = new StringBuilder();
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger || c is WheelCollider) continue;
                Bounds b = c.bounds;
                if (!b.Intersects(path)) continue;
                var src = meshes.OrderBy(m => (m.bounds.center - b.center).sqrMagnitude).FirstOrDefault();
                Vector3 p = root.InverseTransformPoint(b.center);
                sb.AppendLine($"{c.GetType().Name} '{c.name}' under {c.transform.parent?.parent?.name}/{c.transform.parent?.name} " +
                              $"centre {Fmt(p)} size {Fmt(b.size)} <- {src?.name}");
            }
            StageUtility.GoToMainStage();
            return sb.Length == 0 ? "no colliders in the fuel path" : sb.ToString();
        }

        // Reports, per roller (robot frame: +Z forward/intake side, +Y up, +X right), the spin axis * rpm sign and the
        // surface velocity on the roller's top and front faces. InteractionRoller spins about transform.right * rpm
        // (right-hand rule), so the surface at offset r moves along (right * sign) x r.
        public static string ReportRollerDirections()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            Transform root = stage.prefabContentsRoot.transform;
            var sb = new StringBuilder();
            foreach (var r in root.GetComponentsInChildren<InteractionRoller>(true))
            {
                var so = new SerializedObject(r);
                float rpm = so.FindProperty("rpm").floatValue;
                bool belt = so.FindProperty("useBeltMode").boolValue;
                Vector3 w = root.InverseTransformDirection(r.transform.right) * Mathf.Sign(rpm == 0 ? 1 : rpm);
                Vector3 top = Vector3.Cross(w, Vector3.up), front = Vector3.Cross(w, Vector3.forward);
                Vector3 p = root.InverseTransformPoint(r.transform.position);
                string grp = r.GetComponentInParent<RollerGroup>() ? "" : "";
                sb.AppendLine($"{r.transform.parent?.name}/{r.name} pos({p.x:F2},{p.y:F2},{p.z:F2}) rpm {rpm:F0}{(belt ? " BELT" : "")} " +
                              $"axis*sign({w.x:F2},{w.y:F2},{w.z:F2}) topSurface({top.x:F1},{top.y:F1},{top.z:F1}) frontSurface({front.x:F1},{front.y:F1},{front.z:F1}) " +
                              $"r={so.FindProperty("radius").floatValue:F2}in L={so.FindProperty("length").floatValue:F1}in");
            }
            StageUtility.GoToMainStage();
            return sb.ToString();
        }

        // Links the RollerGroup made only of belts (the kicker belts added by hand) to Breakaway.kickerBelts.
        public static string LinkKickerBelts()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var beltGroups = root.GetComponentsInChildren<RollerGroup>(true).Where(g =>
            {
                // the group's own member list (not its children: a roller of another group can sit under it)
                var members = new SerializedObject(g).FindProperty("rollers");
                var rollers = Enumerable.Range(0, members.arraySize)
                    .Select(i => members.GetArrayElementAtIndex(i).FindPropertyRelative("IntRoller").objectReferenceValue as InteractionRoller)
                    .ToList();
                return rollers.Count > 0 && rollers.All(r => r && new SerializedObject(r).FindProperty("useBeltMode").boolValue);
            }).ToList();
            if (beltGroups.Count != 1)
            {
                var sb = new StringBuilder($"ERROR expected one belt-only roller group, found {beltGroups.Count}; nothing changed\n");
                foreach (var g in root.GetComponentsInChildren<RollerGroup>(true))
                {
                    var members = new SerializedObject(g).FindProperty("rollers");
                    sb.Append($"  group {g.name} ({g.transform.parent?.name}): children ");
                    sb.Append(string.Join(", ", g.GetComponentsInChildren<InteractionRoller>(true)
                        .Select(r => r.name + (new SerializedObject(r).FindProperty("useBeltMode").boolValue ? "[belt]" : ""))));
                    sb.AppendLine($"; serialized members {members?.arraySize}");
                }
                return sb.ToString();
            }
            var robot = root.GetComponents<MonoBehaviour>().FirstOrDefault(m => m && m.GetType().Name == "Breakaway");
            if (robot == null) return "ERROR no Breakaway script on the prefab root";
            var so = new SerializedObject(robot);
            var prop = so.FindProperty("kickerBelts");
            if (prop == null) return "ERROR Breakaway has no kickerBelts field (not recompiled yet?)";
            prop.objectReferenceValue = beltGroups[0];
            so.ApplyModifiedPropertiesWithoutUndo();
            beltGroups[0].gameObject.name = "Kicker Belt Group";
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return $"kickerBelts -> {beltGroups[0].name} ({beltGroups[0].GetComponentsInChildren<InteractionRoller>(true).Length} belts)\n" +
                   (saved ? "SAVED " + PrefabPath : "ERROR save failed");
        }

        static string RunMenu(string item, Object[] selection)
        {
            if (selection.Length == 0) return item + ": nothing selected";
            // ToolRegistry drops repeat runs of a tool until the next editor tick (Unity dispatches GameObject/
            // commands once per selected object); each call here is a separate pass, so clear that guard first
            var registry = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Editor.ToolRegistry")).FirstOrDefault(t => t != null);
            var running = registry?.GetField("Running", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
            running?.GetType().GetMethod("Clear")?.Invoke(running, null);
            Selection.objects = selection;
            bool ran = EditorApplication.ExecuteMenuItem(item);
            Selection.objects = new Object[0];
            return item + (ran ? $": ran on {selection.Length} object(s)" : ": UNAVAILABLE");
        }

        static float MaxExtent(Bounds b) => Mathf.Max(b.size.x, b.size.y, b.size.z);

        static GameObject Pivot(GameObject root, string name, Vector3 worldPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = worldPos;
            go.transform.localRotation = Quaternion.identity;
            return go;
        }

        static void AddJoint(GameObject go, Rigidbody connected, ConfigurableJointMotion angX, ConfigurableJointMotion angY,
                             float mass, bool fixedCentreOfMass)
        {
            if (!go.GetComponent<GenericJoint>()) go.AddComponent<GenericJoint>();   // RequireComponent adds Rigidbody + ConfigurableJoint
            var rb = go.GetComponent<Rigidbody>();
            rb.mass = mass;
            if (fixedCentreOfMass) rb.centerOfMass = Vector3.zero;                    // setting it turns the automatic one off
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

        static Bounds WorldBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            Bounds b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var f = Find(c, name); if (f) return f; }
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

        public static string AddHopperNet()
        {
            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;

            Transform hopperExt = Find(root.transform, "Hopper Extension");
            if (hopperExt == null) return "ERROR Hopper Extension not found";

            var netMat = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/Prefabs/Games/Rebuilt/Robots/CitrusCircuits/Net 2.mat");
            if (netMat == null) return "ERROR Net 2.mat not found";

            // Remove any previous net / corners / anchors if re-running.
            var toRemove = new List<GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Net" || t.name.StartsWith("NetCorner") || t.name.StartsWith("NetAnchor_"))
                    toRemove.Add(t.gameObject);
            }
            foreach (var go in toRemove)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);

            Transform MakeAnchor(string name, Transform parent, Vector3 worldPos)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.transform.position = worldPos;
                go.transform.rotation = Quaternion.identity;
                return go.transform;
            }

            // Back edge (gridX = 0, gridY = 0..7) wraps around the turret cutout (turret is x in [-0.31, 0.03], z in [-0.29, 0.03])
            // and extends to the back-right corner of the hopper (z = -0.34, top of Part 2.014 / DT-09) so fuel cannot escape there.
            Transform c00 = MakeAnchor("NetCorner00", root.transform, new Vector3(-0.34f, 0.50f, 0.035f));
            Transform a01 = MakeAnchor("NetAnchor_0_1", root.transform, new Vector3(-0.21f, 0.50f, 0.035f));
            Transform a02 = MakeAnchor("NetAnchor_0_2", root.transform, new Vector3(-0.08f, 0.50f, 0.035f));
            Transform a03 = MakeAnchor("NetAnchor_0_3", root.transform, new Vector3(0.04f, 0.50f, 0.035f));
            Transform a04 = MakeAnchor("NetAnchor_0_4", root.transform, new Vector3(0.04f, 0.485f, -0.15f));
            Transform a05 = MakeAnchor("NetAnchor_0_5", root.transform, new Vector3(0.04f, 0.47f, -0.34f));
            Transform a06 = MakeAnchor("NetAnchor_0_6", root.transform, new Vector3(0.19f, 0.47f, -0.34f));
            Transform c01 = MakeAnchor("NetCorner01", root.transform, new Vector3(0.34f, 0.47f, -0.34f));

            // Front edge (gridX = 7) on Hopper Extension at the top of Part 16 so the net stretches as it extends.
            Transform c10 = MakeAnchor("NetCorner10", hopperExt, new Vector3(-0.34f, 0.505f, 0.32f));
            Transform a72 = MakeAnchor("NetAnchor_7_2", hopperExt, new Vector3(-0.11f, 0.505f, 0.32f));
            Transform a75 = MakeAnchor("NetAnchor_7_5", hopperExt, new Vector3(0.11f, 0.505f, 0.32f));
            Transform c11 = MakeAnchor("NetCorner11", hopperExt, new Vector3(0.34f, 0.505f, 0.32f));

            var netGo = new GameObject("Net");
            netGo.transform.SetParent(root.transform, false);
            netGo.AddComponent<MeshFilter>();
            var mr = netGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = netMat;

            var solver = netGo.AddComponent<NetSolver>();
            solver.corner00 = c00;
            solver.corner10 = c10;
            solver.corner01 = c01;
            solver.corner11 = c11;
            solver.customAnchors = new List<NetSolver.NodeAnchor>
            {
                new NetSolver.NodeAnchor { targetTransform = c00, gridX = 0, gridY = 0 },
                new NetSolver.NodeAnchor { targetTransform = a01, gridX = 0, gridY = 1 },
                new NetSolver.NodeAnchor { targetTransform = a02, gridX = 0, gridY = 2 },
                new NetSolver.NodeAnchor { targetTransform = a03, gridX = 0, gridY = 3 },
                new NetSolver.NodeAnchor { targetTransform = a04, gridX = 0, gridY = 4 },
                new NetSolver.NodeAnchor { targetTransform = a05, gridX = 0, gridY = 5 },
                new NetSolver.NodeAnchor { targetTransform = a06, gridX = 0, gridY = 6 },
                new NetSolver.NodeAnchor { targetTransform = c01, gridX = 0, gridY = 7 },
                new NetSolver.NodeAnchor { targetTransform = c10, gridX = 7, gridY = 0 },
                new NetSolver.NodeAnchor { targetTransform = a72, gridX = 7, gridY = 2 },
                new NetSolver.NodeAnchor { targetTransform = a75, gridX = 7, gridY = 5 },
                new NetSolver.NodeAnchor { targetTransform = c11, gridX = 7, gridY = 7 },
            };
            solver.resolutionX = 8;
            solver.resolutionY = 8;
            solver.useAuthoredRestShape = true;
            solver.netWidth = 0.68f;
            solver.netHeight = 0.66f;
            solver.restLengthScale = 1f;
            solver.subSteps = 3;
            // More stretch than 16 (BombSquad uses stretchCompliance=0.0015, shearCompliance=0.0001).
            solver.stretchCompliance = 0.02f;
            solver.shearCompliance = 0.0013f;
            solver.bendCompliance = 0.006f;
            solver.damping = 0.008f;
            solver.visualSmoothingIntensity = 1f;
            solver.gravity = new Vector3(0f, -9.81f, 0f);
            solver.particleMass = 0.01f;
            solver.collisionRadius = 0.045f;
            int fuelLayer = LayerMask.NameToLayer("GamePiece1");
            solver.collisionLayers = fuelLayer >= 0 ? (1 << fuelLayer) : 4096;
            solver.collisionSubstepInterval = 2;
            solver.anchorBorderFraction = 1f;
            netGo.AddComponent<NetParentFollower>();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return saved ? $"SAVED {PrefabPath} with hopper Net (stretchCompliance={solver.stretchCompliance})" : "ERROR save failed";
        }
    }
}
