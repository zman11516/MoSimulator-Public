using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Games.Rebuilt.Components;
using Games.Rebuilt.FieldScripts;
using Games.Rebuilt.GamePieceSystem;
using Games.Rebuilt.Robots;
using Games.Rebuilt.Scoring;
using GameSystems.Management;
using GameSystems.Settings;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.BaseClasses.GameManagement.TimerManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace MoSim.EditorTools
{
    [InitializeOnLoad]
    internal static class BreakawayShotMapper
    {
        private const string BreakawayPrefabPath = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Breakaway/Breakaway.prefab";
        private const string BreakawayAssetPath = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Breakaway/Breakaway.asset";
        private const string FuelPrefabPath = "Assets/Prefabs/Games/Rebuilt/Fuel.prefab";
        private const string RebuiltScenePath = "Assets/Scenes/Rebuilt/Rebuilt.unity";
        private const string JobJsonPath = "Logs/BreakawayShotMapper_Job.json";
        private const string ResultsJsonPath = "Logs/BreakawayShotMapper_Results.json";
        private const string LogTxtPath = "Logs/BreakawayShotMapper.txt";
        private const string TraceTxtPath = "Logs/BreakawayShotMapper_Trace.txt";
        private const string PendingKey = "BreakawayShotMapper.PendingJob";

        private static ShotMapperRunner _runner;
        private static string _lastStatus = "Idle";

        static BreakawayShotMapper()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                string pending = SessionState.GetString(PendingKey, "");
                if (!string.IsNullOrEmpty(pending))
                {
                    SessionState.SetString(PendingKey, "");
                    StartRunnerFromJobJson();
                }
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Time.timeScale = 1f;
                if (_runner != null)
                {
                    UnityEngine.Object.DestroyImmediate(_runner.gameObject);
                    _runner = null;
                }
            }
        }

        // ---- Synchronous inspection (Edit Mode or Play Mode) ----
        public static string Inspect()
        {
            var sb = new StringBuilder();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BreakawayPrefabPath);
            if (prefab == null) return "ERROR: Breakaway.prefab not found";

            var breakaway = prefab.GetComponent<Breakaway>();
            var boxCol = prefab.GetComponent<BoxCollider>();
            sb.AppendLine("=== BREAKAWAY PREFAB ===");
            if (boxCol != null)
            {
                Vector3 c = boxCol.center;
                Vector3 sz = boxCol.size;
                // Note: RebuiltRobotBase computes maxWidth from world bounds.extents.x & y at Awake/Start
                float maxW_0 = Mathf.Sqrt((sz.x * 0.5f) * (sz.x * 0.5f) + (sz.y * 0.5f) * (sz.y * 0.5f));
                sb.AppendLine($"BoxCollider center={Fmt(c)} size={Fmt(sz)} maxWidth(rot0)={maxW_0:F4}");
            }

            if (breakaway != null)
            {
                var so = new SerializedObject(breakaway);
                sb.AppendLine($"angleOffset={GetTurretAngleOffset(breakaway)}");
                InspectTurretAndHood(prefab, breakaway, sb);
                InspectInterp("flywheelInterpolation", so.FindProperty("flywheelInterpolation"), sb);
                InspectInterp("hoodInterpolation", so.FindProperty("hoodInterpolation"), sb);
                sb.AppendLine($"hoodMaxAngle={so.FindProperty("hoodMaxAngle").floatValue} hoodStowAngle={so.FindProperty("hoodStowAngle").floatValue} turretTolerance={so.FindProperty("turretTolerance").floatValue} trenchClearDistance={so.FindProperty("trenchClearDistance").floatValue}");
                sb.AppendLine($"swmSettings: tof={so.FindProperty("swmSettings.timeOfFlight").floatValue} damp={so.FindProperty("swmSettings.velocityDampeningValue").floatValue} driveMp={so.FindProperty("swmSettings.shootingDriveMultiplier").floatValue}");
                InspectPid("turretPid", so.FindProperty("turretPid"), sb);
                InspectPid("hoodPid", so.FindProperty("hoodPid"), sb);

                InspectRollerGroup(prefab.transform, so.FindProperty("shooterWheels").objectReferenceValue as RollerGroup, "shooterWheels", sb);
                InspectRollerGroup(prefab.transform, so.FindProperty("kickerRollers").objectReferenceValue as RollerGroup, "kickerRollers", sb);
                InspectRollerGroup(prefab.transform, so.FindProperty("kickerBelts").objectReferenceValue as RollerGroup, "kickerBelts", sb);
                InspectRollerGroup(prefab.transform, so.FindProperty("feederRollers").objectReferenceValue as RollerGroup, "feederRollers", sb);
                InspectRollerGroup(prefab.transform, so.FindProperty("hopperRollers").objectReferenceValue as RollerGroup, "hopperRollers", sb);
            }

            sb.AppendLine("\n=== SCENE: " + SceneManager.GetActiveScene().path + " ===");
            int autoLayer = LayerMask.NameToLayer("AutoAngleNodes");
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go.layer == autoLayer)
                {
                    sb.AppendLine($"AutoAngleNode: '{go.name}' pos={Fmt(go.transform.position)}");
                }
            }

            foreach (var hs in UnityEngine.Object.FindObjectsByType<HubScoring>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var hso = new SerializedObject(hs);
                var occ = hso.FindProperty("occupyColliders");
                sb.AppendLine($"HubScoring ({hs.Alliance}) at {Fmt(hs.transform.position)}, occupyColliders={occ.arraySize}:");
                for (int i = 0; i < occ.arraySize; i++)
                {
                    var col = occ.GetArrayElementAtIndex(i).objectReferenceValue as Collider;
                    if (col == null) continue;
                    sb.AppendLine($"  [{i}] {col.GetType().Name} '{col.name}' pos={Fmt(col.transform.position)} boundsCenter={Fmt(col.bounds.center)} boundsSize={Fmt(col.bounds.size)} min={Fmt(col.bounds.min)} max={Fmt(col.bounds.max)}");
                }
            }

            // Field structure bounds
            foreach (string name in new[] { "Carpet", "BlueHub", "RedHub", "BlueBump", "RedBump", "FieldPerimeter" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                Bounds b = ComputeColliderBounds(go);
                sb.AppendLine($"Field '{name}': worldPos={Fmt(go.transform.position)} colliderBounds center={Fmt(b.center)} min={Fmt(b.min)} max={Fmt(b.max)}");
            }

            // Check specific trench/bump colliders inside Field
            var field = GameObject.Find("RebuiltField");
            if (field != null)
            {
                foreach (Transform t in field.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.Contains("Trench") || t.name.Contains("Bump") || t.name.Contains("Hub") || t.name.Contains("Depot") || t.name.Contains("Outpost"))
                    {
                        if (t.parent != null && (t.parent.name == "BlueElements" || t.parent.name == "RedElements" || t.parent.name == "RebuiltField"))
                        {
                            Bounds b = ComputeColliderBounds(t.gameObject);
                            sb.AppendLine($"  Element '{t.parent.name}/{t.name}': boundsMin={Fmt(b.min)} boundsMax={Fmt(b.max)}");
                        }
                    }
                }
            }

            // Spawn points
            var rsc = UnityEngine.Object.FindFirstObjectByType<RobotSpawnController>();
            if (rsc != null)
            {
                var rscSo = new SerializedObject(rsc);
                var blueSp = rscSo.FindProperty("blueSpawnPoints");
                for (int i = 0; i < blueSp.arraySize; i++)
                {
                    var tr = blueSp.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                    if (tr != null) sb.AppendLine($"BlueSpawn[{i}]: pos={Fmt(tr.position)} rot={tr.eulerAngles}");
                }
                var redSp = rscSo.FindProperty("redSpawnPoints");
                for (int i = 0; i < redSp.arraySize; i++)
                {
                    var tr = redSp.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                    if (tr != null) sb.AppendLine($"RedSpawn[{i}]: pos={Fmt(tr.position)} rot={tr.eulerAngles}");
                }
            }

            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogTxtPath, sb.ToString());
            return sb.ToString();
        }

        private static string GetTurretAngleOffset(Breakaway breakaway)
        {
            var tProp = new SerializedObject(breakaway).FindProperty("turret").objectReferenceValue as GenericTurret;
            if (tProp == null) return "null";
            var gts = new SerializedObject(tProp);
            return $"{gts.FindProperty("angleOffset").floatValue} (noWrap={gts.FindProperty("wrappingSettings.useNoWrap").boolValue} [{gts.FindProperty("wrappingSettings.noWrapMin").floatValue}, {gts.FindProperty("wrappingSettings.noWrapMax").floatValue}], steerFF={gts.FindProperty("steerFeedFowardConstant").floatValue})";
        }

        private static void InspectTurretAndHood(GameObject prefab, Breakaway breakaway, StringBuilder sb)
        {
            var so = new SerializedObject(breakaway);
            var turret = so.FindProperty("turret").objectReferenceValue as GenericTurret;
            var hood = so.FindProperty("hoodJoint").objectReferenceValue as GenericJoint;
            if (turret != null)
            {
                Vector3 localPos = prefab.transform.InverseTransformPoint(turret.transform.position);
                sb.AppendLine($"Turret localPos={Fmt(localPos)} localRot={turret.transform.localEulerAngles}");
            }
            if (hood != null)
            {
                Vector3 localPos = prefab.transform.InverseTransformPoint(hood.transform.position);
                sb.AppendLine($"Hood localPos={Fmt(localPos)} parent='{hood.transform.parent?.name}' parentLocalPos={Fmt(hood.transform.parent ? hood.transform.parent.localPosition : Vector3.zero)} parentLocalRot={(hood.transform.parent ? hood.transform.parent.localEulerAngles : Vector3.zero)}");
            }
        }

        private static void InspectInterp(string label, SerializedProperty prop, StringBuilder sb)
        {
            var map = prop.FindPropertyRelative("interpolationMap");
            var pts = new List<string>();
            for (int i = 0; i < map.arraySize; i++)
            {
                var el = map.GetArrayElementAtIndex(i);
                pts.Add($"({el.FindPropertyRelative("distance").floatValue:F2}, {el.FindPropertyRelative("value").floatValue:F1})");
            }
            sb.AppendLine($"{label}: [{string.Join(", ", pts)}]");
        }

        private static void InspectPid(string label, SerializedProperty prop, StringBuilder sb)
        {
            sb.AppendLine($"{label}: P={prop.FindPropertyRelative("kP").floatValue} I={prop.FindPropertyRelative("kI").floatValue} D={prop.FindPropertyRelative("kD").floatValue} Max={prop.FindPropertyRelative("Max").floatValue}");
        }

        private static void InspectRollerGroup(Transform root, RollerGroup grp, string label, StringBuilder sb)
        {
            if (grp == null) { sb.AppendLine($"{label}: NULL"); return; }
            var so = new SerializedObject(grp);
            var rollers = so.FindProperty("rollers");
            sb.AppendLine($"{label} ('{grp.name}', count={rollers.arraySize}):");
            for (int i = 0; i < rollers.arraySize; i++)
            {
                var el = rollers.GetArrayElementAtIndex(i);
                var ir = el.FindPropertyRelative("IntRoller").objectReferenceValue as InteractionRoller;
                float mult = el.FindPropertyRelative("Multiplier").floatValue;
                bool flip = el.FindPropertyRelative("flipped").boolValue;
                if (ir == null) { sb.AppendLine($"  [{i}] IntRoller=NULL"); continue; }
                var irSo = new SerializedObject(ir);
                Vector3 lp = root.InverseTransformPoint(ir.transform.position);
                Vector3 rDir = root.InverseTransformDirection(ir.transform.right);
                sb.AppendLine($"  [{i}] '{ir.name}' pos={Fmt(lp)} right={Fmt(rDir)} r={irSo.FindProperty("radius").floatValue:F3}in L={irSo.FindProperty("length").floatValue:F2}in rpm={irSo.FindProperty("rpm").floatValue:F0} motor={irSo.FindProperty("motor").intValue} ignoreMax={irSo.FindProperty("ignoreDefaultMaxRpm").boolValue} flip={flip} mult={mult:F2} belt={irSo.FindProperty("useBeltMode").boolValue} stiff={irSo.FindProperty("compressionStiffness").floatValue} damp={irSo.FindProperty("compressionDamping").floatValue} scrub={irSo.FindProperty("scrubForces").floatValue} slip={irSo.FindProperty("motorSlipResponse").floatValue} fc={irSo.FindProperty("frictionCoupledToNormal").boolValue} mu={irSo.FindProperty("frictionCoefficient").floatValue}");
            }
        }

        private static Bounds ComputeColliderBounds(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !c.isTrigger).ToArray();
            if (cols.Length == 0) cols = go.GetComponentsInChildren<Collider>(true);
            if (cols.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            Bounds b = cols[0].bounds;
            for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);
            return b;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";

        // ---- Async Job Runner entry points ----
        public static string RunJsonJob()
        {
            if (!File.Exists(JobJsonPath)) return "ERROR: " + JobJsonPath + " not found";
            string json = File.ReadAllText(JobJsonPath);
            if (!EditorApplication.isPlaying)
            {
                if (SceneManager.GetActiveScene().path != RebuiltScenePath)
                {
                    EditorSceneManager.OpenScene(RebuiltScenePath, OpenSceneMode.Single);
                }
                SessionState.SetString(PendingKey, "1");
                EditorApplication.delayCall += () => { EditorApplication.ExecuteMenuItem("Edit/Play"); };
                _lastStatus = "Entering Play Mode to run JSON job...";
                return _lastStatus;
            }
            return StartRunnerFromJobJson();
        }

        private static string StartRunnerFromJobJson()
        {
            if (!EditorApplication.isPlaying) return "ERROR: not in Play Mode";
            if (!File.Exists(JobJsonPath)) return "ERROR: " + JobJsonPath + " not found";
            string json = File.ReadAllText(JobJsonPath);
            var job = JsonUtility.FromJson<ShotJob>(json);
            if (job == null) return "ERROR: failed to parse " + JobJsonPath;

            if (_runner != null)
            {
                UnityEngine.Object.DestroyImmediate(_runner.gameObject);
                _runner = null;
            }

            var go = new GameObject("BreakawayShotMapperRunner");
            go.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<ShotMapperRunner>();
            _runner.StartJob(job);
            _lastStatus = $"Started job '{job.name}' with {job.tests.Count} test(s)";
            return _lastStatus;
        }

        public static string GetStatus()
        {
            if (_runner != null) return _runner.StatusSummary;
            return _lastStatus;
        }

        public static string Stop()
        {
            Time.timeScale = 1f;
            if (_runner != null)
            {
                UnityEngine.Object.DestroyImmediate(_runner.gameObject);
                _runner = null;
            }
            _lastStatus = "Stopped";
            return _lastStatus;
        }

        // ---- Apply final tuned values directly to Breakaway.prefab (NEVER BaseRobot.prefab) ----
        public static string ApplyMapFromJson()
        {
            const string applyPath = "Logs/BreakawayShotMapper_Apply.json";
            if (!File.Exists(applyPath)) return "ERROR: " + applyPath + " not found";
            var cfg = JsonUtility.FromJson<ApplyConfig>(File.ReadAllText(applyPath));
            if (cfg == null) return "ERROR: failed to parse " + applyPath;

            bool wasPlaying = EditorApplication.isPlaying;
            if (wasPlaying) return "ERROR: exit Play Mode before applying prefab edits";

            var stage = PrefabStageUtility.OpenPrefab(BreakawayPrefabPath);
            if (stage == null) return "ERROR: could not open " + BreakawayPrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var breakaway = root.GetComponent<Breakaway>();
            if (breakaway == null) return "ERROR: Breakaway component missing";

            var so = new SerializedObject(breakaway);
            if (cfg.flywheelMap != null && cfg.flywheelMap.Length > 0)
            {
                var prop = so.FindProperty("flywheelInterpolation.interpolationMap");
                prop.arraySize = cfg.flywheelMap.Length;
                for (int i = 0; i < cfg.flywheelMap.Length; i++)
                {
                    prop.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = cfg.flywheelMap[i].distance;
                    prop.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = cfg.flywheelMap[i].value;
                }
            }
            if (cfg.hoodMap != null && cfg.hoodMap.Length > 0)
            {
                var prop = so.FindProperty("hoodInterpolation.interpolationMap");
                prop.arraySize = cfg.hoodMap.Length;
                for (int i = 0; i < cfg.hoodMap.Length; i++)
                {
                    prop.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = cfg.hoodMap[i].distance;
                    prop.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = cfg.hoodMap[i].value;
                }
            }
            if (cfg.setHoodLimits)
            {
                so.FindProperty("hoodMaxAngle").floatValue = cfg.hoodMaxAngle;
                so.FindProperty("hoodStowAngle").floatValue = cfg.hoodStowAngle;
            }
            if (cfg.setHoodPid)
            {
                so.FindProperty("hoodPid.kP").floatValue = cfg.hoodP;
                so.FindProperty("hoodPid.kI").floatValue = cfg.hoodI;
                so.FindProperty("hoodPid.kD").floatValue = cfg.hoodD;
                so.FindProperty("hoodPid.Max").floatValue = cfg.hoodMax;
            }
            if (cfg.setTurretAngleOffset)
            {
                var turret = so.FindProperty("turret").objectReferenceValue as GenericTurret;
                if (turret != null)
                {
                    var tSo = new SerializedObject(turret);
                    tSo.FindProperty("angleOffset").floatValue = cfg.turretAngleOffset;
                    tSo.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(turret);
                }
            }
            if (cfg.setSwmTof)
            {
                so.FindProperty("swmSettings.timeOfFlight").floatValue = cfg.swmTimeOfFlight;
            }
            if (cfg.setBackstop)
            {
                so.FindProperty("backstopCenter").vector3Value = cfg.backstopCenter;
                if (cfg.backstopSize.sqrMagnitude > 0f)
                {
                    so.FindProperty("backstopSize").vector3Value = cfg.backstopSize;
                }
            }
            if (cfg.upgradeFullSizeMotorToShooter || cfg.setShooterRollerOverrides)
            {
                var sw = so.FindProperty("shooterWheels").objectReferenceValue as RollerGroup;
                if (sw != null)
                {
                    var swSo = new SerializedObject(sw);
                    var rollers = swSo.FindProperty("rollers");
                    for (int i = 0; i < rollers.arraySize; i++)
                    {
                        var el = rollers.GetArrayElementAtIndex(i);
                        var ir = el.FindPropertyRelative("IntRoller").objectReferenceValue as InteractionRoller;
                        if (ir == null) continue;
                        var irSo = new SerializedObject(ir);
                        bool modifiedIr = false;

                        if (cfg.upgradeFullSizeMotorToShooter && irSo.FindProperty("motor").intValue == 0)
                        {
                            irSo.FindProperty("motor").intValue = 4;
                            modifiedIr = true;
                        }

                        if (cfg.setShooterRollerOverrides)
                        {
                            if (i == 0 && cfg.sw0Disable)
                            {
                                var enProp = irSo.FindProperty("m_Enabled");
                                if (enProp != null) enProp.boolValue = false;
                                ir.enabled = false;
                                modifiedIr = true;
                            }
                            else if (i == 1) // SW1: primary 4" wheel
                            {
                                if (cfg.sw1SetMotor) { irSo.FindProperty("motor").intValue = cfg.sw1Motor; modifiedIr = true; }
                                if (cfg.sw1SetFc) { irSo.FindProperty("frictionCoupledToNormal").boolValue = cfg.sw1Fc; modifiedIr = true; }
                                if (cfg.sw1SetStiffness) { irSo.FindProperty("compressionStiffness").floatValue = cfg.sw1Stiffness; modifiedIr = true; }
                                if (cfg.sw1SetScrub) { irSo.FindProperty("scrubForces").floatValue = cfg.sw1Scrub; modifiedIr = true; }
                                if (cfg.sw1SetSlip) { irSo.FindProperty("motorSlipResponse").floatValue = cfg.sw1Slip; modifiedIr = true; }
                            }
                            else if (i == 2) // SW3: hood wheel
                            {
                                if (cfg.sw3SetFc) { irSo.FindProperty("frictionCoupledToNormal").boolValue = cfg.sw3Fc; modifiedIr = true; }
                                if (cfg.sw3SetStiffness) { irSo.FindProperty("compressionStiffness").floatValue = cfg.sw3Stiffness; modifiedIr = true; }
                                if (cfg.sw3SetScrub) { irSo.FindProperty("scrubForces").floatValue = cfg.sw3Scrub; modifiedIr = true; }
                                if (cfg.sw3SetSlip) { irSo.FindProperty("motorSlipResponse").floatValue = cfg.sw3Slip; modifiedIr = true; }
                                if (cfg.sw3SetMaxSquish) { irSo.FindProperty("maxSquish").floatValue = cfg.sw3MaxSquish; modifiedIr = true; }
                                if (cfg.sw3SetMult)
                                {
                                    el.FindPropertyRelative("Multiplier").floatValue = cfg.sw3Mult;
                                }
                            }
                        }

                        if (modifiedIr)
                        {
                            irSo.ApplyModifiedPropertiesWithoutUndo();
                            PrefabUtility.RecordPrefabInstancePropertyModifications(ir);
                        }
                    }
                    swSo.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(sw);
                }

                if (cfg.setShooterRollerOverrides && (cfg.sw2SetRpm || cfg.sw2SetScrub || cfg.sw2SetFc || cfg.sw2SetStiffness || cfg.sw2SetMaxSquish || cfg.sw2SetSlip))
                {
                    var kr = so.FindProperty("kickerRollers").objectReferenceValue as RollerGroup;
                    if (kr != null)
                    {
                        var krSo = new SerializedObject(kr);
                        var kRollers = krSo.FindProperty("rollers");
                        if (kRollers.arraySize > 0)
                        {
                            var sw2Ir = kRollers.GetArrayElementAtIndex(0).FindPropertyRelative("IntRoller").objectReferenceValue as InteractionRoller;
                            if (sw2Ir != null)
                            {
                                var sw2So = new SerializedObject(sw2Ir);
                                if (cfg.sw2SetRpm) sw2So.FindProperty("rpm").floatValue = cfg.sw2Rpm;
                                if (cfg.sw2SetScrub) sw2So.FindProperty("scrubForces").floatValue = cfg.sw2Scrub;
                                if (cfg.sw2SetFc) sw2So.FindProperty("frictionCoupledToNormal").boolValue = cfg.sw2Fc;
                                if (cfg.sw2SetStiffness) sw2So.FindProperty("compressionStiffness").floatValue = cfg.sw2Stiffness;
                                if (cfg.sw2SetMaxSquish) sw2So.FindProperty("maxSquish").floatValue = cfg.sw2MaxSquish;
                                if (cfg.sw2SetSlip) sw2So.FindProperty("motorSlipResponse").floatValue = cfg.sw2Slip;
                                sw2So.ApplyModifiedPropertiesWithoutUndo();
                                PrefabUtility.RecordPrefabInstancePropertyModifications(sw2Ir);
                            }
                        }
                    }
                }
            }

            if (cfg.setChute56)
            {
                var kb = so.FindProperty("kickerBelts").objectReferenceValue as RollerGroup;
                if (kb != null)
                {
                    var kbSo = new SerializedObject(kb);
                    var kbRollers = kbSo.FindProperty("rollers");
                    for (int i = 0; i < kbRollers.arraySize; i++)
                    {
                        var ir = kbRollers.GetArrayElementAtIndex(i).FindPropertyRelative("IntRoller").objectReferenceValue as InteractionRoller;
                        if (ir == null) continue;
                        if (ir.name == "Chute Roller 5" || ir.name == "Chute Roller 6")
                        {
                            var irSo = new SerializedObject(ir);
                            bool is5 = ir.name == "Chute Roller 5";
                            float r = is5 ? cfg.chute5Radius : cfg.chute6Radius;
                            float ms = is5 ? cfg.chute5MaxSquish : cfg.chute6MaxSquish;
                            if (r > 0f) irSo.FindProperty("radius").floatValue = r;
                            if (ms > 0f) irSo.FindProperty("maxSquish").floatValue = ms;
                            if (cfg.chute56Stiffness > 0f) irSo.FindProperty("compressionStiffness").floatValue = cfg.chute56Stiffness;
                            if (cfg.chute56SetScrub) irSo.FindProperty("scrubForces").floatValue = cfg.chute56Scrub;
                            if (cfg.chute56SetRpm) irSo.FindProperty("rpm").floatValue = cfg.chute56Rpm;
                            irSo.ApplyModifiedPropertiesWithoutUndo();
                            PrefabUtility.RecordPrefabInstancePropertyModifications(ir);
                        }
                    }
                }
            }

            if (cfg.setAllRollerIgnoreLayers)
            {
                foreach (var ir in root.GetComponentsInChildren<InteractionRoller>(true))
                {
                    var irSo = new SerializedObject(ir);
                    irSo.FindProperty("ignoreLayers.m_Bits").intValue = cfg.allRollerIgnoreLayersMask;
                    irSo.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(ir);
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(breakaway);
            PrefabUtility.SaveAsPrefabAsset(root, BreakawayPrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            StageUtility.GoToMainStage();
            return saved ? "SAVED " + BreakawayPrefabPath : "ERROR: save failed";
        }

        // ---- Serializable job & result DTOs ----
        [Serializable]
        public class MapEntry
        {
            public float distance;
            public float value;
        }

        [Serializable]
        public class ApplyConfig
        {
            public MapEntry[] flywheelMap;
            public MapEntry[] hoodMap;
            public bool setHoodLimits;
            public float hoodMaxAngle;
            public float hoodStowAngle;
            public bool setHoodPid;
            public float hoodP, hoodI, hoodD, hoodMax;
            public bool setTurretAngleOffset;
            public float turretAngleOffset;
            public bool setSwmTof;
            public float swmTimeOfFlight;
            public bool setBackstop;
            public Vector3 backstopCenter;
            public Vector3 backstopSize;
            public bool upgradeFullSizeMotorToShooter;

            public bool setShooterRollerOverrides;
            public bool sw0Disable;
            public bool sw1SetMotor; public int sw1Motor;
            public bool sw1SetFc; public bool sw1Fc;
            public bool sw1SetStiffness; public float sw1Stiffness;
            public bool sw1SetScrub; public float sw1Scrub;
            public bool sw1SetSlip; public float sw1Slip;

            public bool sw3SetFc; public bool sw3Fc;
            public bool sw3SetStiffness; public float sw3Stiffness;
            public bool sw3SetScrub; public float sw3Scrub;
            public bool sw3SetSlip; public float sw3Slip;
            public bool sw3SetMaxSquish; public float sw3MaxSquish;
            public bool sw3SetMult; public float sw3Mult;

            public bool sw2SetRpm; public float sw2Rpm;
            public bool sw2SetScrub; public float sw2Scrub;
            public bool sw2SetFc; public bool sw2Fc;
            public bool sw2SetStiffness; public float sw2Stiffness;
            public bool sw2SetMaxSquish; public float sw2MaxSquish;
            public bool sw2SetSlip; public float sw2Slip;

            public bool setChute56;
            public float chute5Radius;
            public float chute5MaxSquish;
            public float chute6Radius;
            public float chute6MaxSquish;
            public float chute56Stiffness;
            public bool chute56SetScrub; public float chute56Scrub;
            public bool chute56SetRpm; public float chute56Rpm;

            public bool setAllRollerIgnoreLayers;
            public int allRollerIgnoreLayersMask;
        }

        [Serializable]
        public class ShotTestCase
        {
            public string label;
            public float x;
            public float z;
            public float yawDeg;
            public bool useOverride;
            public float rpm;
            public float hoodAngle;
            public float turretAngleOffsetOverride = float.NaN;
            public int balls = 4;
            public float ballInterval = 0.45f;
            public bool stowBeforeShot = false;
            public bool upgradeMotor = false;
            public bool traceBall = false;
            public float sw2RpmOverride = -1f;
            public float sw2StiffnessOverride = -1f;
            public float sw2SlipOverride = -1f;
            public float sw3MultOverride = float.NaN;
            public int fcOverride = -1;
            public float stiffnessOverride = -1f;
            public float scrubOverride = -1f;
            public float slipOverride = -1f;
            public bool disableSw0 = false;
            public float hoodPOverride = -1f;
            public bool frictionlessHoodPlates = false;
            public float ballSpawnLocalX = float.NaN;
            public float ballSpawnLocalY = float.NaN;
            public float ballSpawnLocalZ = float.NaN;
            public float backstopXOverride = float.NaN;
            public float backstopYOverride = float.NaN;
            public float backstopSizeYOverride = -1f;
            public float backstopFrictionOverride = -1f;
            public bool frictionlessBox55 = false;
            public float sw2Sw3ScrubOverride = -1f;
            public bool chute56RadiusBoost = false;
            public float chute56StiffnessOverride = -1f;
            public float chute56ScrubOverride = -1f;
            public float chute56RpmOverride = -1f;
            public float sw3MaxSquishOverride = -1f;
            public float sw2MaxSquishOverride = -1f;
            public bool turretProjectionOverride = false;
            public bool freezeRobot = true;
            public bool traceSettle = false;
            public bool fixRollerIgnoreLayers = false;
            public bool sideFunnelOverride = false;
            public bool disableCentringGuides = false;
            public float moveVx = 0f;   // world m/s: drive the robot at this velocity for the whole test (shoot on the move)
            public float moveVz = 0f;
            public bool legacySotm = false;   // use the original fixed-time-of-flight SOTM instead of Breakaway's improved one   // turns off Breakaway's own runtime centring guides
            public float sideFunnelZ = 0.1035f;
            public float sideFunnelTilt = 8.5f;
            public float sideFunnelY = 0.048f;
            public float sideFunnelHeight = 0.105f;
            public bool upperVerticalGuides = false;
        }

        [Serializable]
        public class ShotJob
        {
            public string name = "job";
            public string alliance = "Blue";
            public float timeScale = 1f;
            public float settleSeconds = 0.6f;
            public float postLastBallWait = 2.2f;
            public float ballSpawnLocalX = 0.0f;
            public float ballSpawnLocalY = 0.30f;
            public float ballSpawnLocalZ = 0.05f;
            public bool deployIntakeOnce = true;
            public List<ShotTestCase> tests = new();
        }

        [Serializable]
        public class BallOutcome
        {
            public int index;
            public bool launched;
            public bool jammed;
            public bool crossedRim;
            public bool scored;
            public float dist3D;
            public float launchSpeed;
            public float launchPitchDeg;
            public float launchHoodDeg;
            public float launchTurretErr;
            public float maxY;
            public float dr;
            public float dLat;
            public float tof;
            public float crossVy;
        }

        [Serializable]
        public class TestOutcome
        {
            public string label;
            public float x;
            public float z;
            public float yawDeg;
            public float dist3D;
            public float horizDist;
            public bool inAllianceZone;
            public bool nearTrench;
            public float cmdRpm;
            public float cmdHood;
            public float actualHood;
            public float turretErr;
            public int ballsFired;
            public int ballsLaunched;
            public int ballsScored;
            public float avgDr;
            public float avgDLat;
            public float avgMaxY;
            public float avgTof;
            public float avgLaunchSpeed;
            public List<BallOutcome> balls = new();
        }

        [Serializable]
        public class ShotJobResults
        {
            public string jobName;
            public bool done;
            public int completedTests;
            public int totalTests;
            public string error = "";
            public List<TestOutcome> outcomes = new();
        }

        private class ShotMapperRunner : MonoBehaviour
        {
            public string StatusSummary { get; private set; } = "Starting";
            private ShotJob _job;
            private readonly ShotJobResults _results = new();
            private Keyboard _vkb;
            private InputSettings.BackgroundBehavior _origBgBehavior;
            private InputSettings.EditorInputBehaviorInPlayMode _origEditorInput;
            private bool _bgSaved;
            private bool _holdShoot;
            private bool _holdIntake;
            private InputAction _actShoot;
            private InputAction _actIntake;
            private InputAction _actManual;
            private InputAction _actPass;
            private readonly List<GameObject> _spawnedBalls = new();

            public void StartJob(ShotJob job)
            {
                _job = job;
                _results.jobName = job.name;
                _results.totalTests = job.tests.Count;
                StartCoroutine(RunRoutine());
            }

            private void OnDestroy()
            {
                Time.timeScale = 1f;
                CleanupBalls();
                ReleaseVirtualInput();
                if (_bgSaved)
                {
                    InputSystem.settings.backgroundBehavior = _origBgBehavior;
                    InputSystem.settings.editorInputBehaviorInPlayMode = _origEditorInput;
                }
                if (_actShoot != null) { _actShoot.Disable(); _actShoot.Dispose(); }
                if (_actIntake != null) { _actIntake.Disable(); _actIntake.Dispose(); }
                if (_actManual != null) { _actManual.Disable(); _actManual.Dispose(); }
                if (_actPass != null) { _actPass.Disable(); _actPass.Dispose(); }
                if (_vkb != null && _vkb.added) InputSystem.RemoveDevice(_vkb);
            }

            private void CleanupBalls()
            {
                foreach (var b in _spawnedBalls)
                {
                    if (b != null) Destroy(b);
                }
                _spawnedBalls.Clear();
            }

            private void SetupVirtualInput(Breakaway robot)
            {
                if (!_bgSaved)
                {
                    _origBgBehavior = InputSystem.settings.backgroundBehavior;
                    _origEditorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
                    _bgSaved = true;
                }
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                // without this, keyboard events go to the editor whenever the Game view lacks focus and the robot never shoots
                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

                foreach (var dev in InputSystem.devices.ToArray())
                {
                    if (dev is Keyboard kb && kb.name.StartsWith("ShotMapperVKB"))
                        InputSystem.RemoveDevice(kb);
                }
                _vkb = InputSystem.AddDevice<Keyboard>("ShotMapperVKB");
                string prefix = $"/{_vkb.name}/";
                _actShoot = new InputAction("SM_Shoot", InputActionType.Button, prefix + "f9");
                _actIntake = new InputAction("SM_Intake", InputActionType.Button, prefix + "f10");
                _actManual = new InputAction("SM_Manual", InputActionType.Button, prefix + "f11");
                _actPass = new InputAction("SM_Pass", InputActionType.Button, prefix + "f12");
                _actShoot.Enable();
                _actIntake.Enable();
                _actManual.Enable();
                _actPass.Enable();

                var actionField = typeof(MoSimInput).GetField("action", BindingFlags.Instance | BindingFlags.NonPublic);
                actionField.SetValue(robot.OuttakeAction, _actShoot);
                actionField.SetValue(robot.IntakeAction, _actIntake);
                actionField.SetValue(robot.ManualShoot, _actManual);
                actionField.SetValue(robot.AutoPass, _actPass);
                _holdShoot = false;
                _holdIntake = false;
            }

            private void SetVirtualKeys(bool shoot, bool intake)
            {
                _holdShoot = shoot;
                _holdIntake = intake;
                if (_vkb == null) return;
                var keys = new List<Key>();
                if (shoot) keys.Add(Key.F9);
                if (intake) keys.Add(Key.F10);
                InputSystem.QueueStateEvent(_vkb, new KeyboardState(keys.ToArray()));
                InputSystem.Update();
            }

            private void PumpVirtualKeys()
            {
                if (_vkb == null) return;
                bool sOk = _actShoot != null && _actShoot.IsPressed() == _holdShoot;
                bool iOk = _actIntake != null && _actIntake.IsPressed() == _holdIntake;
                if (!sOk || !iOk)
                {
                    SetVirtualKeys(_holdShoot, _holdIntake);
                }
            }

            private void ReleaseVirtualInput()
            {
                _holdShoot = false;
                _holdIntake = false;
                if (_vkb != null && _vkb.added)
                {
                    InputSystem.QueueStateEvent(_vkb, new KeyboardState());
                    InputSystem.Update();
                }
            }

            private static void KeepMatchEnabled()
            {
                if (BaseGameManager.Instance == null) return;
                var tm = BaseGameManager.Instance.GetComponent<BaseTimerManager>();
                if (tm != null)
                {
                    tm.PauseTimer();
                    typeof(BaseTimerManager).GetProperty("Timer")?.SetValue(tm, 155f);
                    typeof(BaseTimerManager).GetProperty("CurrentGameState")?.SetValue(tm, GameState.Auto);
                    typeof(BaseTimerManager).GetProperty("CurrentRobotState")?.SetValue(tm, RobotState.Enabled);
                }
                RebuiltShifts.ActiveHub = ActiveHub.Both;
            }

            private IEnumerator EnsureBreakawaySpawned(Alliance alliance)
            {
                StatusSummary = "Waiting for managers...";
                float deadline = Time.realtimeSinceStartup + 20f;
                while ((BaseGameManager.Instance == null || FindFirstObjectByType<RobotSpawnController>() == null) && Time.realtimeSinceStartup < deadline)
                    yield return null;

                if (BaseGameManager.Instance == null)
                {
                    _results.error = "BaseGameManager not found";
                    yield break;
                }

                var rsc = FindFirstObjectByType<RobotSpawnController>();
                if (MatchManager.Instance != null)
                {
                    var ps = MatchManager.Instance.GetOrCreatePlayerSettings(0);
                    ps.alliance = alliance;
                }

                var meta = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(BreakawayAssetPath);
                if (meta == null)
                {
                    _results.error = "Breakaway.asset not found";
                    yield break;
                }

                // Wait until spawn controller is ready for initial spawn or already spawned
                while (!rsc.EditorWaitingForInitialSpawn && !rsc.EditorInitialSpawnComplete && Time.realtimeSinceStartup < deadline)
                    yield return null;

                var existing = FindFirstObjectByType<Breakaway>();
                bool needSwitch = existing == null || existing.Alliance != alliance || rsc.EditorRobotOverride != meta;
                if (needSwitch)
                {
                    StatusSummary = "Switching robot to Breakaway...";
                    if (!rsc.EditorTrySwitchRobot(meta, 0, out string err))
                    {
                        _results.error = "EditorTrySwitchRobot failed: " + err;
                        yield break;
                    }
                    yield return null;
                    while (BaseGameManager.Instance.IsResetting && Time.realtimeSinceStartup < deadline + 20f)
                        yield return null;
                }

                while ((FindFirstObjectByType<Breakaway>() == null || !rsc.EditorInitialSpawnComplete) && Time.realtimeSinceStartup < deadline + 20f)
                    yield return null;

                // Let Start() and LateInit() run
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForSeconds(0.25f);
            }

            private void TeleportAndFreezeRobot(Breakaway robot, Rigidbody[] rbs, Vector3[] relPos, Quaternion[] relRot, Vector3 pos, float yawDeg, bool freeze = true)
            {
                Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
                robot.transform.SetPositionAndRotation(pos, rot);
                for (int i = 0; i < rbs.Length; i++)
                {
                    if (rbs[i] == null) continue;
                    rbs[i].velocity = Vector3.zero;
                    rbs[i].angularVelocity = Vector3.zero;
                    if (rbs[i].gameObject != robot.gameObject)
                    {
                        Vector3 wp = pos + rot * relPos[i];
                        Quaternion wr = rot * relRot[i];
                        rbs[i].transform.SetPositionAndRotation(wp, wr);
                        rbs[i].position = wp;
                        rbs[i].rotation = wr;
                    }
                    else
                    {
                        rbs[i].position = pos;
                        rbs[i].rotation = rot;
                        rbs[i].constraints = freeze ? RigidbodyConstraints.FreezeAll : RigidbodyConstraints.None;
                    }
                }
                Physics.SyncTransforms();
            }

            private IEnumerator RunRoutine()
            {
                Alliance alliance = _job.alliance.Equals("Red", StringComparison.OrdinalIgnoreCase) ? Alliance.Red : Alliance.Blue;
                yield return EnsureBreakawaySpawned(alliance);
                if (!string.IsNullOrEmpty(_results.error))
                {
                    FinishJob();
                    yield break;
                }

                var robot = FindFirstObjectByType<Breakaway>();
                if (robot == null)
                {
                    _results.error = "Breakaway did not spawn";
                    FinishJob();
                    yield break;
                }

                KeepMatchEnabled();
                Time.timeScale = Mathf.Clamp(_job.timeScale, 0.1f, 8f);

                // Clear pre-existing field fuel so robot never collides with loose balls at test spots
                foreach (var gp in FindObjectsByType<RebuiltGamePieceController>(FindObjectsSortMode.None))
                {
                    if (gp != null) Destroy(gp.gameObject);
                }

                float spawnY = robot.transform.position.y;
                SetupVirtualInput(robot);

                // Cache reflection fields on Breakaway
                var fTurret = typeof(Breakaway).GetField("turret", BindingFlags.Instance | BindingFlags.NonPublic);
                var fHood = typeof(Breakaway).GetField("hoodJoint", BindingFlags.Instance | BindingFlags.NonPublic);
                var fFwInterp = typeof(Breakaway).GetField("flywheelInterpolation", BindingFlags.Instance | BindingFlags.NonPublic);
                var fHoodInterp = typeof(Breakaway).GetField("hoodInterpolation", BindingFlags.Instance | BindingFlags.NonPublic);
                var fShooterWheels = typeof(Breakaway).GetField("shooterWheels", BindingFlags.Instance | BindingFlags.NonPublic);
                var fKickerRollers = typeof(Breakaway).GetField("kickerRollers", BindingFlags.Instance | BindingFlags.NonPublic);
                var fKickerBelts = typeof(Breakaway).GetField("kickerBelts", BindingFlags.Instance | BindingFlags.NonPublic);
                var fHoodTarget = typeof(Breakaway).GetField("_hoodTarget", BindingFlags.Instance | BindingFlags.NonPublic);
                var fHoodPid = typeof(Breakaway).GetField("hoodPid", BindingFlags.Instance | BindingFlags.NonPublic);
                var fSwmRef = typeof(RebuiltTurretRobot).GetField("swmRef", BindingFlags.Instance | BindingFlags.NonPublic);

                var turret = fTurret.GetValue(robot) as GenericTurret;
                var hoodJoint = fHood.GetValue(robot) as GenericJoint;
                var fwInterp = fFwInterp.GetValue(robot) as RebuiltInterpolation;
                var hoodInterp = fHoodInterp.GetValue(robot) as RebuiltInterpolation;
                var shooterWheels = fShooterWheels.GetValue(robot) as RollerGroup;
                var kickerRollers = fKickerRollers.GetValue(robot) as RollerGroup;
                var kickerBelts = fKickerBelts?.GetValue(robot) as RollerGroup;
                var swmRef = fSwmRef?.GetValue(robot) as Transform;
                var hoodPid = fHoodPid?.GetValue(robot) as PidConstants;
                float origHoodP = hoodPid != null ? hoodPid.kP : 0.5f;
                float origHoodMax = hoodPid != null ? hoodPid.Max : 4f;

                var origFwMap = fwInterp.interpolationMap;
                var origHoodMap = hoodInterp.interpolationMap;
                var sortedCacheField = typeof(Interpolation).GetField("_sortedCache", BindingFlags.Instance | BindingFlags.NonPublic);
                var turretOffsetField = typeof(GenericTurret).GetField("angleOffset", BindingFlags.Instance | BindingFlags.NonPublic);
                float origAngleOffset = (float)turretOffsetField.GetValue(turret);

                var rgRollersField = typeof(RollerGroup).GetField("rollers", BindingFlags.Instance | BindingFlags.NonPublic);
                var irMotorField = typeof(InteractionRoller).GetField("motor", BindingFlags.Instance | BindingFlags.NonPublic);
                var irRpmField = typeof(InteractionRoller).GetField("rpm", BindingFlags.Instance | BindingFlags.NonPublic);
                var irFcField = typeof(InteractionRoller).GetField("frictionCoupledToNormal", BindingFlags.Instance | BindingFlags.NonPublic);
                var irStiffField = typeof(InteractionRoller).GetField("compressionStiffness", BindingFlags.Instance | BindingFlags.NonPublic);
                var irScrubField = typeof(InteractionRoller).GetField("scrubForces", BindingFlags.Instance | BindingFlags.NonPublic);
                var irSlipField = typeof(InteractionRoller).GetField("motorSlipResponse", BindingFlags.Instance | BindingFlags.NonPublic);
                var irRadiusField = typeof(InteractionRoller).GetField("radius", BindingFlags.Instance | BindingFlags.NonPublic);
                var irMaxSquishField = typeof(InteractionRoller).GetField("maxSquish", BindingFlags.Instance | BindingFlags.NonPublic);
                var irMRadiusField = typeof(InteractionRoller).GetField("_mRadius", BindingFlags.Instance | BindingFlags.NonPublic);
                var irTriggerColField = typeof(InteractionRoller).GetField("_triggerCollider", BindingFlags.Instance | BindingFlags.NonPublic);

                var shooterInstances = shooterWheels != null ? rgRollersField?.GetValue(shooterWheels) as RollerGroup.RollerInstance[] : null;
                int swCount = shooterInstances != null ? shooterInstances.Length : 0;
                var origShooterMotors = new InteractionRoller.RollerMotor[swCount];
                var origShooterMults = new float[swCount];
                var origShooterFc = new bool[swCount];
                var origShooterStiff = new float[swCount];
                var origShooterScrub = new float[swCount];
                var origShooterSlip = new float[swCount];
                var origShooterEnabled = new bool[swCount];
                for (int i = 0; i < swCount; i++)
                {
                    var inst = shooterInstances[i];
                    var ir = inst?.GetInteractionRoller();
                    origShooterMults[i] = inst != null ? inst.Multiplier : 1f;
                    origShooterMotors[i] = ir != null ? (InteractionRoller.RollerMotor)irMotorField.GetValue(ir) : InteractionRoller.RollerMotor.FullSize;
                    origShooterFc[i] = ir != null && (bool)irFcField.GetValue(ir);
                    origShooterStiff[i] = ir != null ? (float)irStiffField.GetValue(ir) : 500f;
                    origShooterScrub[i] = ir != null ? (float)irScrubField.GetValue(ir) : 0f;
                    origShooterSlip[i] = ir != null ? (float)irSlipField.GetValue(ir) : 1f;
                    origShooterEnabled[i] = ir != null && ir.enabled;
                }

                var kickerInstances = kickerRollers != null ? rgRollersField?.GetValue(kickerRollers) as RollerGroup.RollerInstance[] : null;
                InteractionRoller sw2Ir = kickerInstances != null && kickerInstances.Length > 0 ? kickerInstances[0]?.GetInteractionRoller() : null;
                float origSw2Rpm = sw2Ir != null ? (float)irRpmField.GetValue(sw2Ir) : 1500f;
                float origSw2Scrub = sw2Ir != null ? (float)irScrubField.GetValue(sw2Ir) : 60f;
                float origSw2Stiff = sw2Ir != null ? (float)irStiffField.GetValue(sw2Ir) : 500f;
                float origSw2Slip = sw2Ir != null ? (float)irSlipField.GetValue(sw2Ir) : 0.5f;

                var kickerBeltInstances = kickerBelts != null ? rgRollersField?.GetValue(kickerBelts) as RollerGroup.RollerInstance[] : null;
                InteractionRoller chute5Ir = null, chute6Ir = null;
                if (kickerBeltInstances != null)
                {
                    foreach (var inst in kickerBeltInstances)
                    {
                        var ir = inst?.GetInteractionRoller();
                        if (ir == null) continue;
                        if (ir.name == "Chute Roller 5") chute5Ir = ir;
                        else if (ir.name == "Chute Roller 6") chute6Ir = ir;
                    }
                }
                float origChute5Rad = chute5Ir != null ? (float)irRadiusField.GetValue(chute5Ir) : 0.75f;
                float origChute5Squish = chute5Ir != null ? (float)irMaxSquishField.GetValue(chute5Ir) : 0.5f;
                float origChute5Stiff = chute5Ir != null ? (float)irStiffField.GetValue(chute5Ir) : 500f;
                float origChute5Scrub = chute5Ir != null ? (float)irScrubField.GetValue(chute5Ir) : 0f;
                float origChute5Rpm = chute5Ir != null ? (float)irRpmField.GetValue(chute5Ir) : 4000f;
                float origChute6Rad = chute6Ir != null ? (float)irRadiusField.GetValue(chute6Ir) : 0.75f;
                float origChute6Squish = chute6Ir != null ? (float)irMaxSquishField.GetValue(chute6Ir) : 0.5f;
                float origChute6Stiff = chute6Ir != null ? (float)irStiffField.GetValue(chute6Ir) : 500f;
                float origChute6Scrub = chute6Ir != null ? (float)irScrubField.GetValue(chute6Ir) : 0f;
                float origChute6Rpm = chute6Ir != null ? (float)irRpmField.GetValue(chute6Ir) : 4000f;

                InteractionRoller sw0Ir = shooterInstances != null && shooterInstances.Length > 0 ? shooterInstances[0]?.GetInteractionRoller() : null;
                CapsuleCollider sw0CoreCol = sw0Ir != null ? sw0Ir.transform.Find("Wheel_InternalCore")?.GetComponent<CapsuleCollider>() : null;
                bool origSw0CoreEnabled = sw0CoreCol != null && sw0CoreCol.enabled;

                InteractionRoller sw3Ir = shooterInstances != null && shooterInstances.Length > 2 ? shooterInstances[2]?.GetInteractionRoller() : null;
                float origSw3Rad = sw3Ir != null ? (float)irRadiusField.GetValue(sw3Ir) : 1.125f;
                float origSw3Squish = sw3Ir != null ? (float)irMaxSquishField.GetValue(sw3Ir) : 0.5f;
                CapsuleCollider sw3CoreCol = sw3Ir != null ? sw3Ir.transform.Find("Wheel_InternalCore")?.GetComponent<CapsuleCollider>() : null;
                bool origSw3CoreEnabled = sw3CoreCol != null && sw3CoreCol.enabled;
                float origSw3CoreRadius = sw3CoreCol != null ? sw3CoreCol.radius : 0.015875f;

                float origSw2Rad = sw2Ir != null ? (float)irRadiusField.GetValue(sw2Ir) : 0.8096479f;
                float origSw2Squish = sw2Ir != null ? (float)irMaxSquishField.GetValue(sw2Ir) : 0.5f;
                CapsuleCollider sw2CoreCol = sw2Ir != null ? sw2Ir.transform.Find("Wheel_InternalCore")?.GetComponent<CapsuleCollider>() : null;
                bool origSw2CoreEnabled = sw2CoreCol != null && sw2CoreCol.enabled;
                float origSw2CoreRadius = sw2CoreCol != null ? sw2CoreCol.radius : 0.007865f;

                void ApplyCoreSquish(InteractionRoller ir, float radIn, float squishIn, CapsuleCollider coreCol)
                {
                    if (ir == null) return;
                    irMaxSquishField.SetValue(ir, squishIn);
                    if (coreCol != null)
                    {
                        float coreR = (radIn - squishIn) * 0.0254f;
                        if (coreR <= 0.002f)
                        {
                            coreCol.enabled = false;
                        }
                        else
                        {
                            coreCol.enabled = true;
                            coreCol.radius = coreR;
                        }
                    }
                }

                ConfigurableJoint turretJoint = turret != null ? turret.GetComponent<ConfigurableJoint>() : null;
                JointProjectionMode origTurretProjMode = turretJoint != null ? turretJoint.projectionMode : JointProjectionMode.None;
                float origTurretProjDist = turretJoint != null ? turretJoint.projectionDistance : 0.1f;
                ConfigurableJoint hoodCfgJoint = hoodJoint != null ? hoodJoint.GetComponent<ConfigurableJoint>() : null;
                Rigidbody driveRb = robot.GetComponent<Rigidbody>();
                Rigidbody turretRb = turret != null ? turret.GetComponent<Rigidbody>() : null;

                var allRollers = robot.GetComponentsInChildren<InteractionRoller>(true);
                var irIgnoreLayersField = typeof(InteractionRoller).GetField("ignoreLayers", BindingFlags.Instance | BindingFlags.NonPublic);
                var irLayerMaskField = typeof(InteractionRoller).GetField("_layerMask", BindingFlags.Instance | BindingFlags.NonPublic);
                var origRollerIgnoreLayers = new LayerMask[allRollers.Length];
                var origRollerLayerMasks = new int[allRollers.Length];
                for (int i = 0; i < allRollers.Length; i++)
                {
                    origRollerIgnoreLayers[i] = irIgnoreLayersField != null ? (LayerMask)irIgnoreLayersField.GetValue(allRollers[i]) : default;
                    origRollerLayerMasks[i] = irLayerMaskField != null ? (int)irLayerMaskField.GetValue(allRollers[i]) : ~0;
                }

                void ApplyRollerReach(InteractionRoller ir, float radIn, float squishIn, float stiff)
                {
                    if (ir == null) return;
                    irRadiusField.SetValue(ir, radIn);
                    if (irMaxSquishField != null) irMaxSquishField.SetValue(ir, squishIn);
                    float mRad = radIn * 0.0254f;
                    irMRadiusField.SetValue(ir, mRad);
                    if (irTriggerColField.GetValue(ir) is CapsuleCollider cap) cap.radius = mRad;
                    irStiffField.SetValue(ir, stiff);
                }

                Collider[] hoodCols = hoodJoint != null ? hoodJoint.GetComponentsInChildren<Collider>(true) : Array.Empty<Collider>();
                PhysicMaterial[] origHoodMats = new PhysicMaterial[hoodCols.Length];
                for (int i = 0; i < hoodCols.Length; i++) origHoodMats[i] = hoodCols[i].sharedMaterial;
                var hoodZeroMat = new PhysicMaterial("HoodZeroFriction")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    bounciness = 0f,
                    frictionCombine = PhysicMaterialCombine.Minimum,
                    bounceCombine = PhysicMaterialCombine.Minimum
                };
                Transform fuelBackstop = turret != null ? turret.transform.Find("Fuel Backstop") : null;
                Vector3 origBackstopPos = fuelBackstop != null ? fuelBackstop.localPosition : new Vector3(0.087f, 0.045f, 0f);
                BoxCollider backstopBox = fuelBackstop != null ? fuelBackstop.GetComponent<BoxCollider>() : null;
                Vector3 origBackstopSize = backstopBox != null ? backstopBox.size : new Vector3(0.01f, 0.09f, 0.16f);
                PhysicMaterial origBackstopMat = backstopBox != null ? backstopBox.sharedMaterial : null;
                var backstopGripMat = new PhysicMaterial("BackstopGrip")
                {
                    dynamicFriction = 1f,
                    staticFriction = 1f,
                    bounciness = 0f,
                    frictionCombine = PhysicMaterialCombine.Maximum,
                    bounceCombine = PhysicMaterialCombine.Minimum
                };
                Transform box55Tf = robot.transform.Find("Drive/collider/Box (55)");
                Collider box55Col = box55Tf != null ? box55Tf.GetComponent<Collider>() : robot.GetComponentsInChildren<Collider>(true).FirstOrDefault(c => c.name == "Box (55)");
                PhysicMaterial origBox55Mat = box55Col != null ? box55Col.sharedMaterial : null;

                GameObject CreateTestSideGuide(string gName, Vector3 lpos, Vector3 leuler)
                {
                    if (turret == null) return null;
                    var go = new GameObject(gName);
                    go.layer = fuelBackstop != null ? fuelBackstop.gameObject.layer : turret.gameObject.layer;
                    go.transform.SetParent(turret.transform, false);
                    go.transform.localPosition = lpos;
                    go.transform.localRotation = Quaternion.Euler(leuler);
                    var bc = go.AddComponent<BoxCollider>();
                    bc.size = new Vector3(0.14f, 0.105f, 0.01f);
                    bc.sharedMaterial = hoodZeroMat;
                    foreach (var rc in robot.GetComponentsInChildren<Collider>(true))
                    {
                        if (rc != null && rc != bc) Physics.IgnoreCollision(bc, rc, true);
                    }
                    go.SetActive(false);
                    return go;
                }
                GameObject leftGuide = CreateTestSideGuide("TestSideGuideLeft", new Vector3(0.015f, 0.048f, 0.1035f), new Vector3(-8.5f, 0f, 0f));
                GameObject rightGuide = CreateTestSideGuide("TestSideGuideRight", new Vector3(0.015f, 0.048f, -0.1035f), new Vector3(8.5f, 0f, 0f));
                GameObject upperLeftGuide = CreateTestSideGuide("TestUpperGuideLeft", new Vector3(0.015f, 0.110f, 0.093f), Vector3.zero);
                if (upperLeftGuide != null) upperLeftGuide.GetComponent<BoxCollider>().size = new Vector3(0.15f, 0.120f, 0.01f);
                GameObject upperRightGuide = CreateTestSideGuide("TestUpperGuideRight", new Vector3(0.015f, 0.110f, -0.093f), Vector3.zero);
                if (upperRightGuide != null) upperRightGuide.GetComponent<BoxCollider>().size = new Vector3(0.15f, 0.120f, 0.01f);

                // Find hub target node and HubScoring
                string hubNodeName = alliance == Alliance.Blue ? "BlueHub" : "RedHub";
                int autoLayer = LayerMask.NameToLayer("AutoAngleNodes");
                GameObject hubNode = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(g => g.layer == autoLayer && g.name.Equals(hubNodeName, StringComparison.OrdinalIgnoreCase));
                Vector3 hubPos = hubNode != null ? hubNode.transform.position : Vector3.zero;
                HubScoring hubScoring = FindObjectsByType<HubScoring>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(h => h.Alliance == alliance);

                if (hubNode == null || hubScoring == null)
                {
                    _results.error = $"Missing hubNode({hubNode != null}) or hubScoring({hubScoring != null})";
                    FinishJob();
                    yield break;
                }

                var fuelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FuelPrefabPath);
                var gpWorldGo = GameObject.FindGameObjectWithTag("GamePieceWorld");
                Transform gpParent = gpWorldGo != null ? gpWorldGo.transform : null;

                var sbLog = new StringBuilder();
                var traceSb = new StringBuilder();
                sbLog.AppendLine($"Job: {_job.name} | Alliance: {alliance} | HubNode: {Fmt(hubPos)} | spawnY={spawnY:F4}");
                traceSb.AppendLine($"Trace for Job: {_job.name} | Alliance: {alliance} | HubNode: {Fmt(hubPos)} | fixedDeltaTime={Time.fixedDeltaTime:F6}");

                string FormatJointDiag(string phase)
                {
                    if (turret == null || turretJoint == null) return $"[DIAG {phase}] turret/joint null";
                    return $"[DIAG {phase}] robotPos={Fmt(robot.transform.position)} robotEuler={Fmt(robot.transform.eulerAngles)} turretLocal={Fmt(turret.transform.localPosition)} anchor={Fmt(turretJoint.anchor)} connAnchor={Fmt(turretJoint.connectedAnchor)} autoCfg={turretJoint.autoConfigureConnectedAnchor} projMode={turretJoint.projectionMode} curForce={Fmt(turretJoint.currentForce)}";
                }

                traceSb.AppendLine(FormatJointDiag("after_spawn"));

                // Deploy intake once (matches normal match start) so hopper extends and locks
                if (_job.deployIntakeOnce)
                {
                    StatusSummary = "Deploying intake...";
                    SetVirtualKeys(false, true);
                    float deployEnd = Time.time + 0.9f;
                    while (Time.time < deployEnd)
                    {
                        KeepMatchEnabled();
                        PumpVirtualKeys();
                        yield return new WaitForFixedUpdate();
                    }
                    SetVirtualKeys(false, false);
                    float postDeployEnd = Time.time + 0.3f;
                    while (Time.time < postDeployEnd)
                    {
                        KeepMatchEnabled();
                        PumpVirtualKeys();
                        yield return new WaitForFixedUpdate();
                    }
                }

                traceSb.AppendLine(FormatJointDiag("after_deploy"));

                // Cache initial child Rigidbody poses relative to robot root AFTER intake deploy / hopper lock
                var cachedRbs = robot.GetComponentsInChildren<Rigidbody>(true);
                var initRelPos = new Vector3[cachedRbs.Length];
                var initRelRot = new Quaternion[cachedRbs.Length];
                for (int i = 0; i < cachedRbs.Length; i++)
                {
                    initRelPos[i] = robot.transform.InverseTransformPoint(cachedRbs[i].transform.position);
                    initRelRot[i] = Quaternion.Inverse(robot.transform.rotation) * cachedRbs[i].transform.rotation;
                }

                for (int ti = 0; ti < _job.tests.Count; ti++)
                {
                    var tc = _job.tests[ti];
                    StatusSummary = $"Running [{ti + 1}/{_job.tests.Count}] {tc.label}...";
                    KeepMatchEnabled();
                    CleanupBalls();

                    Vector3 targetRobotPos = new Vector3(tc.x, spawnY, tc.z);
                    if (turretJoint != null)
                    {
                        if (tc.turretProjectionOverride)
                        {
                            turretJoint.projectionMode = JointProjectionMode.PositionAndRotation;
                            turretJoint.projectionDistance = 0.001f;
                        }
                        else
                        {
                            turretJoint.projectionMode = origTurretProjMode;
                            turretJoint.projectionDistance = origTurretProjDist;
                        }
                    }
                    bool moving = Mathf.Abs(tc.moveVx) + Mathf.Abs(tc.moveVz) > 1e-3f;
                    typeof(Breakaway).GetField("improvedSotm", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(robot, !tc.legacySotm);
                    typeof(Breakaway).GetField("_sotmVelocity", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(robot, Vector3.zero);
                    TeleportAndFreezeRobot(robot, cachedRbs, initRelPos, initRelRot, targetRobotPos, tc.yawDeg, tc.freezeRobot && !moving);
                    if (_mover != null) { Destroy(_mover); _mover = null; }
                    if (moving)
                    {
                        var rootRb = robot.GetComponent<Rigidbody>();
                        rootRb.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
                        _mover = robot.gameObject.AddComponent<RobotMover>();
                        _mover.rb = rootRb;
                        _mover.velocity = new Vector3(tc.moveVx, 0f, tc.moveVz);
                    }
                    typeof(RebuiltTurretRobot).GetField("_smoothedVelocity", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(robot, Vector3.zero);
                    if (tc.traceBall || tc.traceSettle)
                    {
                        traceSb.AppendLine($"\n=== TEST [{ti + 1}/{_job.tests.Count}] {tc.label} freeze={tc.freezeRobot} ===");
                        traceSb.AppendLine(FormatJointDiag($"after_teleport_{tc.label}"));
                    }

                    if (!float.IsNaN(tc.turretAngleOffsetOverride))
                        turretOffsetField.SetValue(turret, tc.turretAngleOffsetOverride);
                    else
                        turretOffsetField.SetValue(turret, origAngleOffset);

                    if (hoodPid != null)
                    {
                        hoodPid.kP = tc.hoodPOverride > 0f ? tc.hoodPOverride : origHoodP;
                        hoodPid.Max = tc.hoodPOverride > 0f ? 10f : origHoodMax;
                    }

                    for (int i = 0; i < allRollers.Length; i++)
                    {
                        if (allRollers[i] == null) continue;
                        if (tc.fixRollerIgnoreLayers)
                        {
                            irIgnoreLayersField?.SetValue(allRollers[i], (LayerMask)73);
                            irLayerMaskField?.SetValue(allRollers[i], ~73);
                        }
                        else
                        {
                            irIgnoreLayersField?.SetValue(allRollers[i], origRollerIgnoreLayers[i]);
                            irLayerMaskField?.SetValue(allRollers[i], origRollerLayerMasks[i]);
                        }
                    }

                    // Restore original shooter parameters before applying per-test overrides
                    if (shooterInstances != null)
                    {
                        for (int i = 0; i < shooterInstances.Length; i++)
                        {
                            var inst = shooterInstances[i];
                            if (inst == null) continue;
                            inst.Multiplier = origShooterMults[i];
                            var ir = inst.GetInteractionRoller();
                            if (ir == null) continue;
                            ir.ChangeMotor(origShooterMotors[i]);
                            irFcField.SetValue(ir, origShooterFc[i]);
                            irStiffField.SetValue(ir, origShooterStiff[i]);
                            irScrubField.SetValue(ir, origShooterScrub[i]);
                            irSlipField.SetValue(ir, origShooterSlip[i]);
                            ir.enabled = origShooterEnabled[i];
                        }
                    }
                    if (sw3Ir != null)
                    {
                        irMaxSquishField.SetValue(sw3Ir, origSw3Squish);
                        if (sw3CoreCol != null)
                        {
                            sw3CoreCol.enabled = origSw3CoreEnabled;
                            sw3CoreCol.radius = origSw3CoreRadius;
                        }
                    }
                    if (sw2Ir != null)
                    {
                        sw2Ir.SetRpm(origSw2Rpm);
                        irScrubField.SetValue(sw2Ir, origSw2Scrub);
                        irStiffField.SetValue(sw2Ir, origSw2Stiff);
                        irSlipField.SetValue(sw2Ir, origSw2Slip);
                        irMaxSquishField.SetValue(sw2Ir, origSw2Squish);
                        if (sw2CoreCol != null)
                        {
                            sw2CoreCol.enabled = origSw2CoreEnabled;
                            sw2CoreCol.radius = origSw2CoreRadius;
                        }
                    }
                    ApplyRollerReach(chute5Ir, origChute5Rad, origChute5Squish, origChute5Stiff);
                    ApplyRollerReach(chute6Ir, origChute6Rad, origChute6Squish, origChute6Stiff);
                    if (chute5Ir != null)
                    {
                        irScrubField.SetValue(chute5Ir, origChute5Scrub);
                        chute5Ir.SetRpm(origChute5Rpm);
                    }
                    if (chute6Ir != null)
                    {
                        irScrubField.SetValue(chute6Ir, origChute6Scrub);
                        chute6Ir.SetRpm(origChute6Rpm);
                    }
                    if (tc.chute56RadiusBoost)
                    {
                        float s = tc.chute56StiffnessOverride > 0f ? tc.chute56StiffnessOverride : origChute5Stiff;
                        ApplyRollerReach(chute5Ir, 1.50f, 1.25f, s);
                        ApplyRollerReach(chute6Ir, 1.85f, 1.60f, s);
                    }
                    else if (tc.chute56StiffnessOverride > 0f)
                    {
                        if (chute5Ir != null) irStiffField.SetValue(chute5Ir, tc.chute56StiffnessOverride);
                        if (chute6Ir != null) irStiffField.SetValue(chute6Ir, tc.chute56StiffnessOverride);
                    }
                    if (tc.chute56ScrubOverride >= 0f)
                    {
                        if (chute5Ir != null) irScrubField.SetValue(chute5Ir, tc.chute56ScrubOverride);
                        if (chute6Ir != null) irScrubField.SetValue(chute6Ir, tc.chute56ScrubOverride);
                    }
                    if (tc.chute56RpmOverride > 0f)
                    {
                        chute5Ir?.SetRpm(tc.chute56RpmOverride);
                        chute6Ir?.SetRpm(tc.chute56RpmOverride);
                    }
                    for (int i = 0; i < hoodCols.Length; i++)
                    {
                        if (hoodCols[i] != null) hoodCols[i].sharedMaterial = tc.frictionlessHoodPlates ? hoodZeroMat : origHoodMats[i];
                    }
                    if (box55Col != null) box55Col.sharedMaterial = tc.frictionlessBox55 ? hoodZeroMat : origBox55Mat;
                    if (fuelBackstop != null)
                    {
                        Vector3 bsPos = origBackstopPos;
                        if (!float.IsNaN(tc.backstopXOverride)) bsPos.x = tc.backstopXOverride;
                        if (!float.IsNaN(tc.backstopYOverride)) bsPos.y = tc.backstopYOverride;
                        fuelBackstop.localPosition = bsPos;
                    }
                    if (backstopBox != null)
                    {
                        Vector3 bsSize = origBackstopSize;
                        if (tc.backstopSizeYOverride > 0f) bsSize.y = tc.backstopSizeYOverride;
                        backstopBox.size = bsSize;
                        if (tc.backstopFrictionOverride >= 0f)
                        {
                            backstopGripMat.dynamicFriction = tc.backstopFrictionOverride;
                            backstopGripMat.staticFriction = tc.backstopFrictionOverride;
                            backstopGripMat.frictionCombine = tc.backstopFrictionOverride > 0f ? PhysicMaterialCombine.Maximum : PhysicMaterialCombine.Minimum;
                            backstopBox.sharedMaterial = backstopGripMat;
                        }
                        else
                        {
                            backstopBox.sharedMaterial = origBackstopMat;
                        }
                    }

                    if (tc.upgradeMotor && shooterInstances != null)
                    {
                        foreach (var inst in shooterInstances)
                        {
                            var ir = inst?.GetInteractionRoller();
                            if (ir != null) ir.ChangeMotor(InteractionRoller.RollerMotor.Shooter);
                        }
                    }

                    if (leftGuide != null)
                    {
                        leftGuide.SetActive(tc.sideFunnelOverride);
                        leftGuide.transform.localPosition = new Vector3(0.015f, tc.sideFunnelY, tc.sideFunnelZ);
                        leftGuide.transform.localRotation = Quaternion.Euler(-tc.sideFunnelTilt, 0f, 0f);
                        var lbc = leftGuide.GetComponent<BoxCollider>();
                        if (lbc != null) lbc.size = new Vector3(0.15f, tc.sideFunnelHeight, 0.01f);
                    }
                    if (rightGuide != null)
                    {
                        rightGuide.SetActive(tc.sideFunnelOverride);
                        rightGuide.transform.localPosition = new Vector3(0.015f, tc.sideFunnelY, -tc.sideFunnelZ);
                        rightGuide.transform.localRotation = Quaternion.Euler(tc.sideFunnelTilt, 0f, 0f);
                        var rbc = rightGuide.GetComponent<BoxCollider>();
                        if (rbc != null) rbc.size = new Vector3(0.15f, tc.sideFunnelHeight, 0.01f);
                    }
                    upperLeftGuide?.SetActive(tc.upperVerticalGuides);
                    foreach (Transform ch in turret.transform)
                        if (ch.name.StartsWith("Centring Guide")) ch.gameObject.SetActive(!tc.disableCentringGuides);
                    upperRightGuide?.SetActive(tc.upperVerticalGuides);
                    if (sw0CoreCol != null) sw0CoreCol.enabled = origSw0CoreEnabled;
                    if (shooterInstances != null)
                    {
                        if (tc.disableSw0 && shooterInstances.Length > 0)
                        {
                            if (sw0Ir != null) sw0Ir.enabled = false;
                            if (sw0CoreCol != null) sw0CoreCol.enabled = false;
                        }
                        for (int i = 1; i < shooterInstances.Length; i++)
                        {
                            var ir = shooterInstances[i]?.GetInteractionRoller();
                            if (ir == null) continue;
                            if (tc.fcOverride >= 0) irFcField.SetValue(ir, tc.fcOverride != 0);
                            if (tc.stiffnessOverride > 0f) irStiffField.SetValue(ir, tc.stiffnessOverride);
                            bool isSw3 = ir.name.Contains("Shooter Wheel 3") || i == 2;
                            if (isSw3 && tc.sw2Sw3ScrubOverride >= 0f) irScrubField.SetValue(ir, tc.sw2Sw3ScrubOverride);
                            else if (tc.scrubOverride >= 0f) irScrubField.SetValue(ir, tc.scrubOverride);
                            if (tc.slipOverride > 0f) irSlipField.SetValue(ir, tc.slipOverride);
                        }
                    }

                    if (tc.sw3MaxSquishOverride >= 0f && sw3Ir != null)
                    {
                        ApplyCoreSquish(sw3Ir, origSw3Rad, tc.sw3MaxSquishOverride, sw3CoreCol);
                    }

                    if (!float.IsNaN(tc.sw3MultOverride) && shooterInstances != null)
                    {
                        for (int i = 0; i < shooterInstances.Length; i++)
                        {
                            var inst = shooterInstances[i];
                            var ir = inst?.GetInteractionRoller();
                            if (ir != null && (ir.name.Contains("Shooter Wheel 3") || i == 2))
                            {
                                inst.Multiplier = tc.sw3MultOverride;
                                break;
                            }
                        }
                    }

                    if (tc.sw2RpmOverride >= 0f && sw2Ir != null)
                    {
                        sw2Ir.SetRpm(tc.sw2RpmOverride);
                    }
                    if (tc.sw2StiffnessOverride > 0f && sw2Ir != null)
                    {
                        irStiffField.SetValue(sw2Ir, tc.sw2StiffnessOverride);
                    }
                    if (tc.sw2SlipOverride > 0f && sw2Ir != null)
                    {
                        irSlipField.SetValue(sw2Ir, tc.sw2SlipOverride);
                    }
                    if (tc.sw2MaxSquishOverride >= 0f && sw2Ir != null)
                    {
                        ApplyCoreSquish(sw2Ir, origSw2Rad, tc.sw2MaxSquishOverride, sw2CoreCol);
                    }
                    float sw2ScrubTarget = tc.sw2Sw3ScrubOverride >= 0f ? tc.sw2Sw3ScrubOverride : tc.scrubOverride;
                    if (sw2ScrubTarget >= 0f && sw2Ir != null)
                    {
                        irScrubField.SetValue(sw2Ir, sw2ScrubTarget);
                    }

                    if (tc.useOverride)
                    {
                        var fwOv = new[] { new DistanceValue { distance = 0f, value = tc.rpm } };
                        var hdOv = new[] { new DistanceValue { distance = 0f, value = tc.hoodAngle } };
                        fwInterp.interpolationMap = fwOv;
                        hoodInterp.interpolationMap = hdOv;
                        sortedCacheField.SetValue(fwInterp, fwOv);
                        sortedCacheField.SetValue(hoodInterp, hdOv);
                    }
                    else
                    {
                        fwInterp.interpolationMap = origFwMap;
                        hoodInterp.interpolationMap = origHoodMap;
                        sortedCacheField.SetValue(fwInterp, null);
                        sortedCacheField.SetValue(hoodInterp, null);
                        typeof(Interpolation).GetMethod("InitializeCache", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(fwInterp, null);
                        typeof(Interpolation).GetMethod("InitializeCache", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(hoodInterp, null);
                    }

                    if (tc.stowBeforeShot)
                    {
                        SetVirtualKeys(false, false);
                        float stowEnd = Time.time + 0.6f;
                        while (Time.time < stowEnd)
                        {
                            KeepMatchEnabled();
                            yield return new WaitForFixedUpdate();
                        }
                    }

                    RobotPartCollisionWatcher turretWatcher = null;
                    RobotPartCollisionWatcher hoodWatcher = null;
                    if (tc.traceSettle)
                    {
                        if (turret != null)
                        {
                            turretWatcher = turret.gameObject.AddComponent<RobotPartCollisionWatcher>();
                            turretWatcher.Initialize("Turret", robot.transform, traceSb);
                        }
                        if (hoodJoint != null)
                        {
                            hoodWatcher = hoodJoint.gameObject.AddComponent<RobotPartCollisionWatcher>();
                            hoodWatcher.Initialize("Hood", robot.transform, traceSb);
                        }
                    }

                    // Hold shoot
                    SetVirtualKeys(true, false);

                    // Wait for turret + hood to settle
                    float settleStart = Time.time;
                    float settleEnd = settleStart + _job.settleSeconds;
                    int settleStep = 0;
                    Vector3 lastLoggedTurretLocal = turret != null ? turret.transform.localPosition : Vector3.zero;
                    string FormatSettleStep(int k)
                    {
                        Vector3 tLoc = turret != null ? turret.transform.localPosition : Vector3.zero;
                        Vector3 tRbP = turretRb != null ? turretRb.position : Vector3.zero;
                        Vector3 dRbP = driveRb != null ? driveRb.position : Vector3.zero;
                        Vector3 dPos = robot.transform.position;
                        float tYawL = turret != null ? turret.transform.localEulerAngles.y : 0f;
                        Vector3 hLoc = hoodJoint != null ? hoodJoint.transform.localPosition : Vector3.zero;
                        Vector3 tF = turretJoint != null ? turretJoint.currentForce : Vector3.zero;
                        Vector3 hF = hoodCfgJoint != null ? hoodCfgJoint.currentForce : Vector3.zero;
                        return $"  [SETTLE step={k} t={Time.time - settleStart:F3}] turretLocal={Fmt(tLoc)} turretRbPos={Fmt(tRbP)} driveRbPos={Fmt(dRbP)} drivePos={Fmt(dPos)} turretYawL={tYawL:F1} hoodLocal={Fmt(hLoc)} tForce={Fmt(tF)} hForce={Fmt(hF)}";
                    }
                    while (Time.time < settleEnd)
                    {
                        KeepMatchEnabled();
                        PumpVirtualKeys();
                        yield return new WaitForFixedUpdate();
                        if (tc.traceSettle && turret != null)
                        {
                            Vector3 curLocal = turret.transform.localPosition;
                            bool moved = Vector3.Distance(curLocal, lastLoggedTurretLocal) > 0.001f;
                            if (settleStep < 80 && (settleStep <= 15 || moved))
                            {
                                traceSb.AppendLine(FormatSettleStep(settleStep));
                                lastLoggedTurretLocal = curLocal;
                            }
                            settleStep++;
                        }
                    }
                    if (tc.traceSettle)
                    {
                        traceSb.AppendLine(FormatSettleStep(settleStep) + " [final]");
                    }

                    Vector3 turretPos = turret.transform.position;
                    float dist3D = Vector3.Distance(turretPos, hubPos);
                    float horizDist = Vector2.Distance(new Vector2(turretPos.x, turretPos.z), new Vector2(hubPos.x, hubPos.z));

                    if (tc.traceBall)
                    {
                        traceSb.AppendLine(FormatJointDiag($"after_settle_{tc.label}"));
                        traceSb.AppendLine($"=== TEST [{ti + 1}/{_job.tests.Count}] {tc.label} pos=({tc.x:F2},{tc.z:F2}) yaw={tc.yawDeg:F0} outtake={(bool)robot.OuttakeAction} rpm={fwInterp.Output:F0} hoodCmd={((float)fHoodTarget.GetValue(robot)):F1} hoodAct={Mathf.DeltaAngle(0f, hoodJoint.GetAngle()):F2} tErr={(float)turret.GetError():F3} turretWorldEuler={Fmt(turret.transform.eulerAngles)} swmRefDelta={Fmt((swmRef != null ? swmRef.position : turretPos) - turretPos)} robotPos={Fmt(robot.transform.position)} turretLocal={Fmt(turret.transform.localPosition)} swmParentLocal={Fmt(swmRef != null && swmRef.parent != null ? swmRef.parent.localPosition : Vector3.zero)} upgrade={tc.upgradeMotor} sw2Rpm={(sw2Ir != null ? sw2Ir.TargetRpm : -1f):F0} sw3Mult={(shooterInstances != null && shooterInstances.Length > 2 ? shooterInstances[2].Multiplier : -1f):F2} ===");
                    }

                    var outcome = new TestOutcome
                    {
                        label = string.IsNullOrEmpty(tc.label) ? $"T{ti}" : tc.label,
                        x = tc.x,
                        z = tc.z,
                        yawDeg = tc.yawDeg,
                        dist3D = dist3D,
                        horizDist = horizDist,
                        inAllianceZone = robot.InAllianceZone(),
                        nearTrench = robot.UnderTrench(turret.transform, 0.25f),
                        cmdRpm = fwInterp.Output,
                        cmdHood = (float)fHoodTarget.GetValue(robot),
                        actualHood = Mathf.DeltaAngle(0f, hoodJoint.GetAngle()),
                        turretErr = turret.GetError(),
                        ballsFired = tc.balls
                    };

                    // Track active balls for this test
                    var tracked = new List<TrackedBall>();
                    int prevScore = GetHubScore(hubScoring);

                    for (int bi = 0; bi < tc.balls; bi++)
                    {
                        KeepMatchEnabled();
                        float sx = float.IsNaN(tc.ballSpawnLocalX) ? _job.ballSpawnLocalX : tc.ballSpawnLocalX;
                        float sy = float.IsNaN(tc.ballSpawnLocalY) ? _job.ballSpawnLocalY : tc.ballSpawnLocalY;
                        float sz = float.IsNaN(tc.ballSpawnLocalZ) ? _job.ballSpawnLocalZ : tc.ballSpawnLocalZ;
                        Vector3 localSpawn = new Vector3(sx, sy, sz);
                        Vector3 worldSpawn = robot.transform.TransformPoint(localSpawn);
                        var ballGo = Instantiate(fuelPrefab, worldSpawn, Quaternion.identity, gpParent);
                        _spawnedBalls.Add(ballGo);
                        var ballRb = ballGo.GetComponent<Rigidbody>();
                        var gpCtrl = ballGo.GetComponent<RebuiltGamePieceController>();
                        if (ballRb != null)
                        {
                            var rootBody = robot.GetComponent<Rigidbody>();
                            ballRb.velocity = rootBody != null ? rootBody.velocity : Vector3.zero; // a held ball moves with the robot
                            ballRb.angularVelocity = Vector3.zero;
                        }

                        if (tc.traceBall && bi <= 1)
                        {
                            var cl = ballGo.AddComponent<BallContactLogger>();
                            cl.Initialize(bi, robot.transform, turret.transform, ballRb, hoodJoint, traceSb, Time.time);
                        }

                        var tb = new TrackedBall
                        {
                            go = ballGo,
                            rb = ballRb,
                            ctrl = gpCtrl,
                            spawnTime = Time.time,
                            prevPos = worldSpawn,
                            maxY = worldSpawn.y,
                            res = new BallOutcome { index = bi, dist3D = dist3D }
                        };
                        tracked.Add(tb);

                        float nextTime = Time.time + tc.ballInterval;
                        while (Time.time < nextTime)
                        {
                            KeepMatchEnabled();
                            PumpVirtualKeys();
                            yield return new WaitForFixedUpdate();
                            StepTracking(tracked, robot, turret, hoodJoint, hubPos, swmRef, shooterInstances, sw2Ir, tc.traceBall ? traceSb : null);
                        }
                    }

                    // Wait for all balls to finish flight & score
                    float endWait = Time.time + _job.postLastBallWait;
                    while (Time.time < endWait)
                    {
                        KeepMatchEnabled();
                        PumpVirtualKeys();
                        yield return new WaitForFixedUpdate();
                        StepTracking(tracked, robot, turret, hoodJoint, hubPos, swmRef, shooterInstances, sw2Ir, tc.traceBall ? traceSb : null);
                    }

                    SetVirtualKeys(false, false);
                    if (turretWatcher != null) Destroy(turretWatcher);
                    if (_mover != null)
                    {
                        sbLog.AppendLine($"  moving test: commanded v=({tc.moveVx:F2},{tc.moveVz:F2}) actual v={Fmt(robot.GetComponent<Rigidbody>().velocity)} endPos={Fmt(robot.transform.position)}");
                        Destroy(_mover); _mover = null;
                        robot.GetComponent<Rigidbody>().velocity = Vector3.zero;
                    }
                    if (hoodWatcher != null) Destroy(hoodWatcher);

                    foreach (var tb in tracked)
                    {
                        if (!tb.res.launched)
                        {
                            tb.res.jammed = true;
                            Vector3 ep = tb.go != null ? robot.transform.InverseTransformPoint(tb.rb.position) : Vector3.zero;
                            sbLog.AppendLine($"  [UNLAUNCHED ball {tb.res.index}] endLocal={Fmt(ep)} speed={(tb.rb != null ? tb.rb.velocity.magnitude : 0f):F2}");
                        }
                        if (tb.res.launched) outcome.ballsLaunched++;
                        if (tb.res.scored) outcome.ballsScored++;
                        outcome.balls.Add(tb.res);
                    }

                    var crossed = outcome.balls.Where(b => b.crossedRim).ToList();
                    var launched = outcome.balls.Where(b => b.launched).ToList();
                    outcome.avgDr = crossed.Count > 0 ? crossed.Average(b => b.dr) : 0f;
                    outcome.avgDLat = crossed.Count > 0 ? crossed.Average(b => b.dLat) : 0f;
                    outcome.avgMaxY = launched.Count > 0 ? launched.Average(b => b.maxY) : 0f;
                    outcome.avgTof = crossed.Count > 0 ? crossed.Average(b => b.tof) : 0f;
                    outcome.avgLaunchSpeed = launched.Count > 0 ? launched.Average(b => b.launchSpeed) : 0f;
                    float avgPitch = launched.Count > 0 ? launched.Average(b => b.launchPitchDeg) : 0f;
                    float avgYawErr = launched.Count > 0 ? launched.Average(b => b.launchTurretErr) : 0f;

                    int newScore = GetHubScore(hubScoring);
                    int scoreDelta = newScore - prevScore;

                    _results.outcomes.Add(outcome);
                    _results.completedTests = ti + 1;

                    float firstSpawn = tracked.Count > 0 ? tracked.Min(b => b.spawnTime) : 0f;
                    string launchTimes = string.Join(",", tracked.Where(b => b.res.launched).Select(b => (b.launchTime - firstSpawn).ToString("F2")));
                    sbLog.AppendLine($"  launchT=[{launchTimes}]");
                    string line = $"[{ti + 1}/{_job.tests.Count}] {outcome.label} pos=({tc.x:F2},{tc.z:F2}) yaw={tc.yawDeg:F0} d3D={dist3D:F2}m (horiz={horizDist:F2}m) inZone={outcome.inAllianceZone} rpm={outcome.cmdRpm:F0} hood={outcome.actualHood:F1}/{outcome.cmdHood:F1} tErr={outcome.turretErr:F2} | scored={outcome.ballsScored}/{tc.balls} (hubDelta={scoreDelta}, launched={outcome.ballsLaunched}) | dr={outcome.avgDr:+0.00;-0.00;0.00} dLat={outcome.avgDLat:+0.00;-0.00;0.00} yawErr={avgYawErr:+0.0;-0.0;0.0}deg pitch={avgPitch:F1}deg maxY={outcome.avgMaxY:F2} v0={outcome.avgLaunchSpeed:F2} tof={outcome.avgTof:F2}s";
                    sbLog.AppendLine(line);
                    StatusSummary = line;
                    SaveProgress(sbLog.ToString(), traceSb.ToString());
                }

                // Restore any runtime roller/PID/map overrides before exiting job
                fwInterp.interpolationMap = origFwMap;
                hoodInterp.interpolationMap = origHoodMap;
                sortedCacheField.SetValue(fwInterp, null);
                sortedCacheField.SetValue(hoodInterp, null);
                typeof(Interpolation).GetMethod("InitializeCache", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(fwInterp, null);
                typeof(Interpolation).GetMethod("InitializeCache", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(hoodInterp, null);
                if (hoodPid != null)
                {
                    hoodPid.kP = origHoodP;
                    hoodPid.Max = origHoodMax;
                }
                if (shooterInstances != null)
                {
                    for (int i = 0; i < shooterInstances.Length; i++)
                    {
                        var inst = shooterInstances[i];
                        if (inst == null) continue;
                        inst.Multiplier = origShooterMults[i];
                        var ir = inst.GetInteractionRoller();
                        if (ir == null) continue;
                        ir.ChangeMotor(origShooterMotors[i]);
                        irFcField.SetValue(ir, origShooterFc[i]);
                        irStiffField.SetValue(ir, origShooterStiff[i]);
                        irScrubField.SetValue(ir, origShooterScrub[i]);
                        irSlipField.SetValue(ir, origShooterSlip[i]);
                        ir.enabled = origShooterEnabled[i];
                    }
                }
                if (sw0CoreCol != null) sw0CoreCol.enabled = origSw0CoreEnabled;
                if (leftGuide != null) Destroy(leftGuide);
                if (rightGuide != null) Destroy(rightGuide);
                if (sw3Ir != null)
                {
                    irMaxSquishField.SetValue(sw3Ir, origSw3Squish);
                    if (sw3CoreCol != null)
                    {
                        sw3CoreCol.enabled = origSw3CoreEnabled;
                        sw3CoreCol.radius = origSw3CoreRadius;
                    }
                }
                if (sw2Ir != null)
                {
                    sw2Ir.SetRpm(origSw2Rpm);
                    irScrubField.SetValue(sw2Ir, origSw2Scrub);
                    irMaxSquishField.SetValue(sw2Ir, origSw2Squish);
                    if (sw2CoreCol != null)
                    {
                        sw2CoreCol.enabled = origSw2CoreEnabled;
                        sw2CoreCol.radius = origSw2CoreRadius;
                    }
                }
                ApplyRollerReach(chute5Ir, origChute5Rad, origChute5Squish, origChute5Stiff);
                ApplyRollerReach(chute6Ir, origChute6Rad, origChute6Squish, origChute6Stiff);
                if (chute5Ir != null) irScrubField.SetValue(chute5Ir, origChute5Scrub);
                if (chute6Ir != null) irScrubField.SetValue(chute6Ir, origChute6Scrub);
                if (turretJoint != null)
                {
                    turretJoint.projectionMode = origTurretProjMode;
                    turretJoint.projectionDistance = origTurretProjDist;
                }
                for (int i = 0; i < hoodCols.Length; i++)
                {
                    if (hoodCols[i] != null) hoodCols[i].sharedMaterial = origHoodMats[i];
                }
                if (box55Col != null) box55Col.sharedMaterial = origBox55Mat;
                if (hoodZeroMat != null) Destroy(hoodZeroMat);
                if (fuelBackstop != null) fuelBackstop.localPosition = origBackstopPos;
                if (backstopBox != null) backstopBox.size = origBackstopSize;
                turretOffsetField.SetValue(turret, origAngleOffset);
                for (int i = 0; i < allRollers.Length; i++)
                {
                    if (allRollers[i] == null) continue;
                    irIgnoreLayersField?.SetValue(allRollers[i], origRollerIgnoreLayers[i]);
                    irLayerMaskField?.SetValue(allRollers[i], origRollerLayerMasks[i]);
                }

                Time.timeScale = 1f;
                FinishJob(sbLog.ToString(), traceSb.ToString());
            }

            private RobotMover _mover;

            private class RobotMover : MonoBehaviour
            {
                public Rigidbody rb;
                public Vector3 velocity;
                private void FixedUpdate()
                {
                    if (rb != null) rb.velocity = velocity;
                }
            }

            private class RobotPartCollisionWatcher : MonoBehaviour
            {
                private string _partName;
                private Transform _robotRoot;
                private StringBuilder _traceSb;
                private float _startTime;
                private int _stayCount;

                public void Initialize(string partName, Transform robotRoot, StringBuilder traceSb)
                {
                    _partName = partName;
                    _robotRoot = robotRoot;
                    _traceSb = traceSb;
                    _startTime = Time.time;
                }

                private string GetFullPath(Transform t)
                {
                    var parts = new List<string>();
                    Transform cur = t;
                    int steps = 0;
                    while (cur != null && steps < 10)
                    {
                        parts.Add(cur.name);
                        if (cur == _robotRoot) break;
                        cur = cur.parent;
                        steps++;
                    }
                    parts.Reverse();
                    return string.Join("/", parts);
                }

                private void LogCol(string kind, Collision c)
                {
                    if (_traceSb == null || c == null || c.collider == null) return;
                    Vector3 pt = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
                    _traceSb.AppendLine($"  [PART_{kind} {_partName} t={Time.time - _startTime:F3}] other='{GetFullPath(c.collider.transform)}' impulse={Fmt(c.impulse)} pt={Fmt(pt)}");
                }

                private void OnCollisionEnter(Collision c) => LogCol("ENTER", c);

                private void OnCollisionStay(Collision c)
                {
                    if (_stayCount < 5)
                    {
                        _stayCount++;
                        LogCol($"STAY#{_stayCount}", c);
                    }
                }
            }

            private class BallContactLogger : MonoBehaviour
            {
                private int _ballIndex;
                private Transform _robotTransform;
                private Transform _turretTransform;
                private Rigidbody _rb;
                private GenericJoint _hoodJoint;
                private StringBuilder _traceSb;
                private float _startTime;
                private Vector3 _prevVel;

                public void Initialize(int ballIndex, Transform robotTransform, Transform turretTransform, Rigidbody rb, GenericJoint hoodJoint, StringBuilder traceSb, float startTime)
                {
                    _ballIndex = ballIndex;
                    _robotTransform = robotTransform;
                    _turretTransform = turretTransform;
                    _rb = rb;
                    _hoodJoint = hoodJoint;
                    _traceSb = traceSb;
                    _startTime = startTime;
                    _prevVel = rb != null ? rb.velocity : Vector3.zero;
                }

                private void FixedUpdate()
                {
                    if (_rb != null) _prevVel = _rb.velocity;
                }

                private void OnCollisionEnter(Collision c)
                {
                    if (_traceSb == null || _rb == null || _turretTransform == null) return;
                    if (transform.position.y >= 1.25f) return;
                    Vector3 ptWorld = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
                    Vector3 ptLocal = _turretTransform.InverseTransformPoint(ptWorld);
                    Vector3 localVel = _turretTransform.InverseTransformDirection(_rb.velocity);
                    float hood = _hoodJoint != null ? Mathf.DeltaAngle(0f, _hoodJoint.GetAngle()) : 0f;
                    string path = GetColliderPath(c.collider.transform);
                    _traceSb.AppendLine($"  [t={Time.time - _startTime:F3} b={_ballIndex}] COLLIDE '{path}' ptLocal={Fmt(ptLocal)} vPrev={_prevVel.magnitude:F2} vNow={_rb.velocity.magnitude:F2} localVel={Fmt(localVel)} hood={hood:F1}");
                }

                private string GetColliderPath(Transform t)
                {
                    var parts = new List<string>();
                    Transform cur = t;
                    int steps = 0;
                    while (cur != null && steps < 5)
                    {
                        parts.Add(cur.name);
                        if (cur == _turretTransform || cur == _robotTransform) break;
                        cur = cur.parent;
                        steps++;
                    }
                    parts.Reverse();
                    return string.Join("/", parts);
                }
            }

            private class TrackedBall
            {
                public GameObject go;
                public Rigidbody rb;
                public RebuiltGamePieceController ctrl;
                public float spawnTime;
                public float launchTime;
                public Vector3 prevPos;
                public float maxY;
                public BallOutcome res;
            }

            private static readonly FieldInfo LastScoredTimeField =
                typeof(RebuiltGamePieceController).GetField("_lastScoredTime", BindingFlags.Instance | BindingFlags.NonPublic);

            private void StepTracking(
                List<TrackedBall> tracked,
                Breakaway robot,
                GenericTurret turret,
                GenericJoint hoodJoint,
                Vector3 hubPos,
                Transform swmRef,
                RollerGroup.RollerInstance[] shooterInstances,
                InteractionRoller sw2Ir,
                StringBuilder traceSb)
            {
                Vector3 turretPos = turret.transform.position;
                Vector3 toHub = new Vector3(hubPos.x - turretPos.x, 0f, hubPos.z - turretPos.z);
                float h = toHub.magnitude;
                Vector3 fwd = h > 1e-4f ? toHub / h : Vector3.forward;
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

                foreach (var tb in tracked)
                {
                    if (tb.go == null || tb.rb == null) continue;
                    Vector3 curPos = tb.rb.position;
                    Vector3 vel = tb.rb.velocity;

                    if (tb.ctrl != null && ((tb.ctrl.SlotIndex >= 0 && tb.ctrl.scored) || (LastScoredTimeField != null && (float)LastScoredTimeField.GetValue(tb.ctrl) >= tb.spawnTime)))
                    {
                        tb.res.scored = true;
                    }

                    if (traceSb != null && tb.res.index == 0 && (Time.time - tb.spawnTime) < 1.4f && curPos.y < 1.35f)
                    {
                        Vector3 lpos = turret.transform.InverseTransformPoint(curPos);
                        Vector3 lvel = turret.transform.InverseTransformDirection(vel);
                        Vector3 wL = turret.transform.InverseTransformDirection(tb.rb.angularVelocity);
                        float hoodAct = Mathf.DeltaAngle(0f, hoodJoint.GetAngle());
                        float tErr = turret.GetError();
                        string swRpms = shooterInstances != null
                            ? string.Join(",", shooterInstances.Select(s => s?.GetInteractionRoller() != null ? $"{s.GetInteractionRoller().realRpm:F0}" : "?"))
                            : "";
                        float sw2Real = sw2Ir != null ? sw2Ir.realRpm : 0f;
                        traceSb.AppendLine($"  [t={Time.time - tb.spawnTime:F3} b=0] posL={Fmt(lpos)} velL={Fmt(lvel)} wL={Fmt(wL)} spd={vel.magnitude:F2} yW={curPos.y:F3} hood={hoodAct:F2} tErr={tErr:F2} sw=[{swRpms}] sw2={sw2Real:F0}");
                    }

                    if (!tb.res.launched)
                    {
                        float spd = vel.magnitude;
                        if (curPos.y > robot.transform.position.y + 0.67f && spd > 2.0f)
                        {
                            tb.res.launched = true;
                            tb.launchTime = Time.time;
                            tb.res.launchSpeed = spd;
                            float horizSpd = Mathf.Sqrt(vel.x * vel.x + vel.z * vel.z);
                            tb.res.launchPitchDeg = Mathf.Atan2(vel.y, horizSpd) * Mathf.Rad2Deg;
                            tb.res.launchHoodDeg = Mathf.DeltaAngle(0f, hoodJoint.GetAngle());
                            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
                            tb.res.launchTurretErr = horizVel.sqrMagnitude > 0.01f ? Vector3.SignedAngle(fwd, horizVel, Vector3.up) : 0f;

                            if (traceSb != null)
                            {
                                Vector3 localLaunchVel = turret.transform.InverseTransformDirection(vel);
                                float localYawErrDeg = Mathf.Atan2(localLaunchVel.z, -localLaunchVel.x) * Mathf.Rad2Deg;
                                Vector3 turretWorldEuler = turret.transform.eulerAngles;
                                Vector3 swmRefPos = swmRef != null ? swmRef.position : turretPos;
                                traceSb.AppendLine($"  [LAUNCH b={tb.res.index} t={Time.time - tb.spawnTime:F3}] spd={spd:F2} pitch={tb.res.launchPitchDeg:F1} worldYawErr={tb.res.launchTurretErr:+0.00;-0.00;0.00} localYawErr={localYawErrDeg:+0.00;-0.00;0.00} localLaunchVel={Fmt(localLaunchVel)} turretEuler={Fmt(turretWorldEuler)} turretPos={Fmt(turretPos)} swmRef={Fmt(swmRefPos)}");
                            }
                        }
                    }
                    else
                    {
                        if (curPos.y > tb.maxY) { tb.maxY = curPos.y; tb.res.maxY = curPos.y; }
                        if (!tb.res.crossedRim && tb.prevPos.y >= hubPos.y && curPos.y < hubPos.y && vel.y < 0f)
                        {
                            float frac = Mathf.Clamp01((tb.prevPos.y - hubPos.y) / Mathf.Max(1e-4f, tb.prevPos.y - curPos.y));
                            Vector3 cross = Vector3.Lerp(tb.prevPos, curPos, frac);
                            Vector3 err = new Vector3(cross.x - hubPos.x, 0f, cross.z - hubPos.z);
                            tb.res.crossedRim = true;
                            tb.res.dr = Vector3.Dot(err, fwd);
                            tb.res.dLat = Vector3.Dot(err, right);
                            tb.res.tof = Mathf.Lerp(Time.time - Time.fixedDeltaTime, Time.time, frac) - tb.launchTime;
                            tb.res.crossVy = vel.y;
                        }
                    }

                    tb.prevPos = curPos;
                }
            }

            private static int GetHubScore(HubScoring hs)
            {
                var fAuto = typeof(HubScoring).GetField("autoScore", BindingFlags.Instance | BindingFlags.NonPublic);
                var fTotal = typeof(HubScoring).GetField("totalScore", BindingFlags.Instance | BindingFlags.NonPublic);
                float a = (SecureFloat)fAuto.GetValue(hs);
                float t = (SecureFloat)fTotal.GetValue(hs);
                return Mathf.RoundToInt(a + t);
            }

            private void SaveProgress(string logText, string traceText = null)
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(LogTxtPath, logText);
                File.WriteAllText(ResultsJsonPath, JsonUtility.ToJson(_results, true));
                if (!string.IsNullOrEmpty(traceText))
                    File.WriteAllText(TraceTxtPath, traceText);
            }

            private void FinishJob(string logText = "", string traceText = null)
            {
                _results.done = true;
                SaveProgress(string.IsNullOrEmpty(logText) ? $"Error: {_results.error}" : logText, traceText);
                StatusSummary = string.IsNullOrEmpty(_results.error)
                    ? $"DONE ({_results.completedTests}/{_results.totalTests})"
                    : $"ERROR: {_results.error}";
            }
        }
    }
}
