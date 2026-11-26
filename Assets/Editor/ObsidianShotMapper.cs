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
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace MoSim.EditorTools
{
    /// <summary>
    /// Automatic shot mapping for FRC 5454 Obsidian, built on the same machinery as BreakawayShotMapper.
    /// For each distance in front of the alliance hub and each hood angle, it sweeps flywheel RPM until shots go
    /// from short to long, interpolates the RPM that lands on the hub centre, verifies it with a volley, and keeps
    /// the best hood angle per distance. The result is written to Logs/ObsidianShotMapper_Fit.json and can be
    /// applied to Obsidian.prefab (flywheel map, hood map and SOTM time-of-flight table).
    ///
    /// Tools > Obsidian Shot Mapper > Run Auto Map   (opens the Rebuilt scene and enters Play mode if needed)
    /// Tools > Obsidian Shot Mapper > Apply Fitted Map To Prefab   (after the run, out of Play mode)
    /// Optional settings: Logs/ObsidianShotMapper_Job.json (written with defaults on first run).
    /// </summary>
    [InitializeOnLoad]
    internal static class ObsidianShotMapper
    {
        private const string PrefabPath = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Obsidian/Obsidian.prefab";
        private const string AssetPath = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/Arkansas Modpack/Obsidian/Obsidian.asset";
        private const string FuelPrefabPath = "Assets/Prefabs/Games/Rebuilt/Fuel.prefab";
        private const string RebuiltScenePath = "Assets/Scenes/Rebuilt/Rebuilt.unity";
        private const string JobJsonPath = "Logs/ObsidianShotMapper_Job.json";
        private const string FitJsonPath = "Logs/ObsidianShotMapper_Fit.json";
        private const string LogTxtPath = "Logs/ObsidianShotMapper.txt";
        private const string PendingKey = "ObsidianShotMapper.PendingJob";

        private static MapperRunner _runner;
        private static string _lastStatus = "Idle";

        static ObsidianShotMapper()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                if (!string.IsNullOrEmpty(SessionState.GetString(PendingKey, "")))
                {
                    SessionState.SetString(PendingKey, "");
                    StartRunner();
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

        [MenuItem("Tools/Obsidian Shot Mapper/Run Auto Map")]
        public static void RunAutoMapMenu() => Debug.Log("[ObsidianShotMapper] " + RunAutoMap());

        [MenuItem("Tools/Obsidian Shot Mapper/Run Consistency Study")]
        public static void RunStudyMenu() => Debug.Log("[ObsidianShotMapper] " + RunAutoMap("study"));

        [MenuItem("Tools/Obsidian Shot Mapper/Status")]
        public static void StatusMenu() => Debug.Log("[ObsidianShotMapper] " + GetStatus());

        [MenuItem("Tools/Obsidian Shot Mapper/Stop")]
        public static void StopMenu() => Debug.Log("[ObsidianShotMapper] " + Stop());

        [MenuItem("Tools/Obsidian Shot Mapper/Apply Fitted Map To Prefab")]
        public static void ApplyMenu() => Debug.Log("[ObsidianShotMapper] " + ApplyFit());

        private const string ModeKey = "ObsidianShotMapper.Mode";

        public static string RunAutoMap() => RunAutoMap("map");

        public static string RunAutoMap(string mode)
        {
            LoadOrWriteJob();
            SessionState.SetString(ModeKey, mode);
            if (!EditorApplication.isPlaying)
            {
                if (SceneManager.GetActiveScene().path != RebuiltScenePath)
                {
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return "Cancelled";
                    EditorSceneManager.OpenScene(RebuiltScenePath, OpenSceneMode.Single);
                }
                SessionState.SetString(PendingKey, "1");
                EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
                _lastStatus = "Entering Play Mode to run the auto map...";
                return _lastStatus;
            }
            return StartRunner();
        }

        private static MapJob LoadOrWriteJob()
        {
            Directory.CreateDirectory("Logs");
            if (File.Exists(JobJsonPath))
            {
                var job = JsonUtility.FromJson<MapJob>(File.ReadAllText(JobJsonPath));
                if (job != null) return job;
            }
            var def = new MapJob();
            File.WriteAllText(JobJsonPath, JsonUtility.ToJson(def, true));
            return def;
        }

        private static string StartRunner()
        {
            if (!EditorApplication.isPlaying) return "ERROR: not in Play Mode";
            var job = LoadOrWriteJob();
            if (_runner != null)
            {
                UnityEngine.Object.DestroyImmediate(_runner.gameObject);
                _runner = null;
            }
            var go = new GameObject("ObsidianShotMapperRunner") { hideFlags = HideFlags.DontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<MapperRunner>();
            bool study = SessionState.GetString(ModeKey, "map") == "study";
            _runner.StartJob(job, study);
            _lastStatus = study
                ? $"Started consistency study at {job.studyDistance:F1} m, {job.studyRpm:F0} rpm, hood {job.studyHood:F0}"
                : $"Started auto map: {job.distances.Length} distance(s) x {job.hoodAngles.Length} hood angle(s)";
            return _lastStatus;
        }

        public static string GetStatus() => _runner != null ? _runner.StatusSummary : _lastStatus;

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

        public static string ApplyFit()
        {
            if (EditorApplication.isPlaying) return "ERROR: exit Play Mode before applying prefab edits";
            if (!File.Exists(FitJsonPath)) return "ERROR: " + FitJsonPath + " not found (run the auto map first)";
            var fit = JsonUtility.FromJson<FitResult>(File.ReadAllText(FitJsonPath));
            if (fit == null || fit.points == null || fit.points.Count == 0) return "ERROR: fit has no points";

            var pts = fit.points.Where(p => p.ok).OrderBy(p => p.interpDistance).ToList();
            if (pts.Count < 2) return $"ERROR: only {pts.Count} usable point(s) in the fit, not applying";

            var stage = PrefabStageUtility.OpenPrefab(PrefabPath);
            if (stage == null) return "ERROR: could not open " + PrefabPath;
            GameObject root = stage.prefabContentsRoot;
            var robot = root.GetComponent<global::Obsidian>();
            if (robot == null) return "ERROR: Obsidian component missing";

            var so = new SerializedObject(robot);
            var fw = so.FindProperty("flywheelInterpolation.interpolationMap");
            var hd = so.FindProperty("hoodInterpolation.interpolationMap");
            fw.arraySize = pts.Count;
            hd.arraySize = pts.Count;
            for (int i = 0; i < pts.Count; i++)
            {
                fw.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = pts[i].interpDistance;
                fw.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = pts[i].rpm;
                hd.GetArrayElementAtIndex(i).FindPropertyRelative("distance").floatValue = pts[i].interpDistance;
                hd.GetArrayElementAtIndex(i).FindPropertyRelative("value").floatValue = pts[i].hood;
            }

            var tofPts = pts.Where(p => p.tof > 0.05f).OrderBy(p => p.horizDistance).ToList();
            if (tofPts.Count >= 2)
            {
                var tof = so.FindProperty("tofByDistance");
                tof.arraySize = tofPts.Count;
                for (int i = 0; i < tofPts.Count; i++)
                    tof.GetArrayElementAtIndex(i).vector2Value = new Vector2(tofPts[i].horizDistance, tofPts[i].tof);
            }

            string turretNote = "";
            if (fit.setTurretOffset && so.FindProperty("turret").objectReferenceValue is GenericTurret turret)
            {
                var tSo = new SerializedObject(turret);
                var off = tSo.FindProperty("angleOffset");
                turretNote = $", turret angleOffset {off.floatValue:F1} -> {fit.turretAngleOffset:F1}";
                off.floatValue = fit.turretAngleOffset;
                tSo.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(turret);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(robot);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            StageUtility.GoToMainStage();
            return saved
                ? $"SAVED {PrefabPath}: {pts.Count} map point(s), {(tofPts.Count >= 2 ? tofPts.Count : 0)} time-of-flight point(s){turretNote}"
                : "ERROR: save failed";
        }

        // ---- Settings & results ----
        [Serializable]
        public class MapJob
        {
            public string alliance = "Blue";
            public float timeScale = 2f;
            public float settleSeconds = 0.9f;
            public float ballInterval = 0.35f;
            public float postLastBallWait = 2.0f;
            // robot-local spawn point: inside the kicker loop, so the rollers carry the ball straight up into the shooter
            public Vector3 ballSpawnLocal = new Vector3(-0.127f, 0.256f, -0.076f);
            // feed balls through the hopper (as in a match) instead of straight into the kicker loop
            public bool feedFromHopper = false;
            public Vector3 hopperDropLocal = new Vector3(0.05f, 0.42f, 0.22f);
            public float hopperExtraWait = 4.0f;
            public bool deployIntakeOnce = true;
            // horizontal turret-to-hub distances (m), measured straight out from the hub into the alliance zone
            public float[] distances = { 1.8f, 2.3f, 2.8f, 3.3f, 3.8f, 4.2f };
            public float lateralOffset = 0f;   // m sideways from the hub centre line
            public float robotYawDeg = 0f;
            public float[] hoodAngles = { 0f, 8f, 16f, 24f, 32f };
            public float rpmStart = 1400f;
            public float rpmStep = 300f;
            public float rpmMin = 600f;
            public float rpmMax = 6000f;
            public int sweepBalls = 2;
            public int verifyBalls = 6;
            public int minScoredToAccept = 4;   // a verify volley must score at least this many to become a map point
            public int refineSteps = 3;         // bracket-narrowing volleys before the verify volley
            public int refineBalls = 3;
            public float refineMinGap = 60f;    // rpm: stop refining once the bracket is this narrow

            // One hood angle per distance (same order as distances), flatter for longer shots, so neighbouring map
            // points are the same kind of shot. If the scheduled angle can't score, +/- tolerance is tried.
            public bool useHoodSchedule = true;
            public float[] hoodSchedule = { 16f, 20f, 22f, 24f, 28f, 30f };
            public float hoodScheduleTolerance = 4f;

            // aim calibration before mapping (centres the sideways miss by adjusting the turret's angleOffset)
            public bool calibrateAim = true;
            public float aimCalDistance = 2.3f;
            public float aimCalRpm = 3000f;
            public float aimCalHood = 20f;
            public float aimCalProbeDeg = 4f;

            // test spots: straight out from the hub when that's clear, otherwise swung sideways (up to
            // maxLineAngleDeg) until neither the robot nor the shot path comes near the climb tower
            public bool avoidTower = true;
            public float towerClearance = 0.8f;      // m between the robot centre and the tower
            public float shotPathClearance = 0.35f;  // m between the turret-to-hub line and the tower
            public float maxLineAngleDeg = 70f;

            // consistency study (Tools > Obsidian Shot Mapper > Run Consistency Study)
            public float studyDistance = 2.3f;
            public float studyRpm = 2435f;
            public float studyHood = 20f;
            public int studyBalls = 8;
            public float studyInterval = 0.35f;
        }

        [Serializable]
        public class FitPoint
        {
            public float horizDistance;
            public float interpDistance;
            public float rpm;
            public float hood;
            public int scored;
            public int fired;
            public float avgDr;
            public float avgDLat;
            public float tof;
            public bool ok;
        }

        [Serializable]
        public class FitResult
        {
            public string alliance;
            public bool setTurretOffset;
            public float turretAngleOffset;
            public bool done;
            public string error = "";
            public List<FitPoint> points = new();
        }

        private class ShotStats
        {
            public int fired, launched, crossed, scored;
            public float medianDr = float.NaN, avgDr = float.NaN, avgDLat = float.NaN, avgTof = float.NaN;
            public float avgSpeed = float.NaN, avgPitch = float.NaN, avgLaunchY = float.NaN;
            public float speedStd = float.NaN, pitchStd = float.NaN, drStd = float.NaN;
            public float hoodErrAvg = float.NaN, hoodErrMax = float.NaN, rpmAtLaunchAvg = float.NaN, spinAvg = float.NaN;
            public readonly List<string> perBall = new();
            public float interpDistance, horizDistance;
            public bool AllShort => launched > 0 && (crossed == 0 || medianDr < 0f);
            public bool Long => crossed > 0 && medianDr > 0f;
        }

        private class MapperRunner : MonoBehaviour
        {
            public string StatusSummary { get; private set; } = "Starting";
            private MapJob _job;
            private readonly FitResult _fit = new();
            private readonly StringBuilder _log = new();
            private readonly List<GameObject> _spawnedBalls = new();

            private Keyboard _vkb;
            private InputAction _actShoot, _actIntake, _actManual, _actPass;
            private bool _holdShoot, _holdIntake;
            private InputSettings.BackgroundBehavior _origBg;
            private InputSettings.EditorInputBehaviorInPlayMode _origEditorInput;
            private bool _inputSaved;

            // robot state
            private global::Obsidian _robot;
            private GenericTurret _turret;
            private GenericJoint _hood;
            private RebuiltInterpolation _fwInterp, _hoodInterp;
            private DistanceValue[] _origFwMap, _origHoodMap;
            private FieldInfo _sortedCacheField, _hoodTargetField;
            private Rigidbody[] _rbs;
            private Vector3[] _relPos;
            private Quaternion[] _relRot;
            private float _spawnY;
            private Vector3 _hubPos;
            private HubScoring _hubScoring;
            private GameObject _fuelPrefab;
            private Transform _gpParent;

            private static readonly FieldInfo LastScoredTimeField =
                typeof(RebuiltGamePieceController).GetField("_lastScoredTime", BindingFlags.Instance | BindingFlags.NonPublic);

            private bool _study;
            private readonly List<Bounds> _towerBounds = new();
            private RollerGroup _shooterWheels;
            private Transform _hoodArc;
            private PidConstants _hoodPid;

            public void StartJob(MapJob job, bool study)
            {
                _job = job;
                _study = study;
                StartCoroutine(Run());
            }

            private void OnDestroy()
            {
                Time.timeScale = 1f;
                CleanupBalls();
                RestoreMaps();
                if (_vkb != null && _vkb.added)
                {
                    InputSystem.QueueStateEvent(_vkb, new KeyboardState());
                    InputSystem.Update();
                }
                if (_inputSaved)
                {
                    InputSystem.settings.backgroundBehavior = _origBg;
                    InputSystem.settings.editorInputBehaviorInPlayMode = _origEditorInput;
                }
                foreach (var a in new[] { _actShoot, _actIntake, _actManual, _actPass })
                {
                    if (a == null) continue;
                    a.Disable();
                    a.Dispose();
                }
                if (_vkb != null && _vkb.added) InputSystem.RemoveDevice(_vkb);
            }

            // ---------------- main routine ----------------
            private IEnumerator Run()
            {
                Alliance alliance = _job.alliance.Equals("Red", StringComparison.OrdinalIgnoreCase) ? Alliance.Red : Alliance.Blue;
                _fit.alliance = alliance.ToString();
                yield return EnsureSpawned(alliance);
                if (!string.IsNullOrEmpty(_fit.error)) { Finish(); yield break; }

                _robot = FindFirstObjectByType<global::Obsidian>();
                if (_robot == null) { _fit.error = "Obsidian did not spawn"; Finish(); yield break; }

                KeepMatchEnabled();
                Time.timeScale = Mathf.Clamp(_job.timeScale, 0.1f, 8f);
                foreach (var gp in FindObjectsByType<RebuiltGamePieceController>(FindObjectsSortMode.None))
                    if (gp != null) Destroy(gp.gameObject);

                const BindingFlags np = BindingFlags.Instance | BindingFlags.NonPublic;
                var t = typeof(global::Obsidian);
                _turret = t.GetField("turret", np)?.GetValue(_robot) as GenericTurret;
                _hood = t.GetField("hoodJoint", np)?.GetValue(_robot) as GenericJoint;
                _fwInterp = t.GetField("flywheelInterpolation", np)?.GetValue(_robot) as RebuiltInterpolation;
                _hoodInterp = t.GetField("hoodInterpolation", np)?.GetValue(_robot) as RebuiltInterpolation;
                _hoodTargetField = t.GetField("_hoodTarget", np);
                _shooterWheels = t.GetField("shooterWheels", np)?.GetValue(_robot) as RollerGroup;
                _hoodArc = t.GetField("_hoodArc", np)?.GetValue(_robot) as Transform;
                _hoodPid = t.GetField("hoodPid", np)?.GetValue(_robot) as PidConstants;
                _sortedCacheField = typeof(Interpolation).GetField("_sortedCache", np);
                if (_turret == null || _hood == null || _fwInterp == null || _hoodInterp == null)
                {
                    _fit.error = "Could not read turret/hood/interpolation fields from Obsidian";
                    Finish();
                    yield break;
                }
                _origFwMap = _fwInterp.interpolationMap;
                _origHoodMap = _hoodInterp.interpolationMap;

                string hubNodeName = alliance == Alliance.Blue ? "BlueHub" : "RedHub";
                int autoLayer = LayerMask.NameToLayer("AutoAngleNodes");
                var hubNode = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(g => g.layer == autoLayer && g.name.Equals(hubNodeName, StringComparison.OrdinalIgnoreCase));
                _hubScoring = FindObjectsByType<HubScoring>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(h => h.Alliance == alliance);
                if (hubNode == null || _hubScoring == null)
                {
                    _fit.error = $"Missing hub node ({hubNode != null}) or HubScoring ({_hubScoring != null})";
                    Finish();
                    yield break;
                }
                _hubPos = hubNode.transform.position;
                _fuelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FuelPrefabPath);
                var gpWorld = GameObject.FindGameObjectWithTag("GamePieceWorld");
                _gpParent = gpWorld != null ? gpWorld.transform : null;
                _spawnY = _robot.transform.position.y;

                SetupVirtualInput();
                _log.AppendLine($"Obsidian auto map | alliance {alliance} | hub {Fmt(_hubPos)} | timeScale {Time.timeScale:F1}");

                if (_job.deployIntakeOnce)
                {
                    StatusSummary = "Deploying intake...";
                    SetKeys(false, true);
                    yield return Hold(0.9f);
                    SetKeys(false, false);
                    yield return Hold(0.3f);
                }

                _rbs = _robot.GetComponentsInChildren<Rigidbody>(true);
                _relPos = new Vector3[_rbs.Length];
                _relRot = new Quaternion[_rbs.Length];
                for (int i = 0; i < _rbs.Length; i++)
                {
                    _relPos[i] = _robot.transform.InverseTransformPoint(_rbs[i].transform.position);
                    _relRot[i] = Quaternion.Inverse(_robot.transform.rotation) * _rbs[i].transform.rotation;
                }

                FindTower();

                if (_study)
                {
                    yield return Study();
                    RestoreMaps();
                    SetKeys(false, false);
                    Finish();
                    yield break;
                }

                if (_job.calibrateAim) yield return CalibrateAim();

                var lastRpmForHood = new Dictionary<float, float>();
                float lastAcceptedHood = float.NegativeInfinity;

                for (int di = 0; di < _job.distances.Length; di++)
                {
                    float d = _job.distances[di];
                    if (!TryPickSpot(d, out Vector3 spot, out float lineAngle))
                    {
                        _log.AppendLine($"BEST d={d:F1}m: skipped, no test spot clear of the tower and inside the field");
                        continue;
                    }
                    _log.AppendLine($"d={d:F1}m: testing from turret spot ({spot.x:F2}, {spot.z:F2}), {lineAngle:+0;-0;0} deg off the straight-out line");
                    FitPoint best = null;
                    float bestKey = float.NegativeInfinity;

                    // One smooth hood-vs-distance schedule (flatter as distance grows) so neighbouring map points are
                    // the same kind of shot and blending between them stays valid. The scheduled angle is tried
                    // first; the neighbours only if it can't score.
                    bool scheduled = _job.useHoodSchedule && _job.hoodSchedule != null && di < _job.hoodSchedule.Length;
                    float[] hoodCandidates = scheduled
                        ? new[] { _job.hoodSchedule[di], _job.hoodSchedule[di] + _job.hoodScheduleTolerance, _job.hoodSchedule[di] - _job.hoodScheduleTolerance }
                            .Select(h => Mathf.Max(0f, h)).Distinct()
                            .Where(h => h >= lastAcceptedHood - 0.01f).ToArray()   // never step back: keeps hood rising with distance
                        : _job.hoodAngles;

                    foreach (float hood in hoodCandidates)
                    {
                        if (scheduled && best != null) break;   // scheduled (or nearest) angle already works
                        string tag = $"[d={d:F1}m hood={hood:F0}]";
                        float startRpm = lastRpmForHood.TryGetValue(hood, out float prev) ? prev - _job.rpmStep : _job.rpmStart;
                        startRpm = Mathf.Clamp(startRpm, _job.rpmMin, _job.rpmMax);

                        // sweep: find a short/long bracket
                        float rShort = float.NaN, drShort = float.NaN, rLong = float.NaN, drLong = float.NaN;
                        float rpm = startRpm;
                        ShotStats s = null;
                        for (int guard = 0; guard < 24; guard++)
                        {
                            StatusSummary = $"{tag} sweeping {rpm:F0} rpm";
                            yield return Volley(spot, rpm, hood, _job.sweepBalls, _job.ballInterval, r => s = r);
                            float vReq = RequiredSpeed(s.horizDistance, _hubPos.y - s.avgLaunchY, s.avgPitch);
                            _log.AppendLine($"  {tag} rpm {rpm,5:F0}: launched {s.launched}/{s.fired} crossed {s.crossed} scored {s.scored} medianDr {F(s.medianDr)} | v0 {s.avgSpeed:F2} m/s pitch {s.avgPitch:F1} deg | needs ~{vReq:F2} m/s");
                            SaveProgress();
                            if (s.launched == 0) break;   // feeding/shooter problem at this setting: give up on this hood

                            if (!float.IsNaN(s.avgPitch) && float.IsNaN(vReq))
                            {
                                // at this launch angle the ball would hit the front of the hub below the rim at any speed
                                _log.AppendLine($"  {tag} launch angle {s.avgPitch:F1} deg is too flat to clear the hub rim at {s.horizDistance:F1} m, skipping hood");
                                rShort = rLong = float.NaN;
                                break;
                            }
                            // estimated rpm that gives the required speed, assuming exit speed scales with rpm
                            float ratio = !float.IsNaN(vReq) && s.avgSpeed > 0.5f ? vReq / s.avgSpeed : float.NaN;
                            if (s.Long)
                            {
                                rLong = rpm; drLong = s.medianDr;
                                if (!float.IsNaN(rShort)) break;
                                float next = float.IsNaN(ratio) ? rpm - _job.rpmStep : rpm * Mathf.Clamp(ratio, 0.6f, 0.97f);
                                rpm = Mathf.Min(next, rpm - _job.rpmStep * 0.5f);
                                if (rpm < _job.rpmMin) break;
                            }
                            else
                            {
                                if (!float.IsNaN(ratio) && ratio < 0.7f && s.crossed == 0)
                                {
                                    // already well over the speed it needs and still not reaching the rim: something
                                    // (usually the hub's front face) is in the way at this angle
                                    _log.AppendLine($"  {tag} {s.avgSpeed:F1} m/s is well past the ~{vReq:F1} m/s needed but nothing crosses the rim, skipping hood");
                                    rShort = rLong = float.NaN;
                                    break;
                                }
                                rShort = rpm; drShort = s.crossed > 0 ? s.medianDr : float.NaN;
                                if (!float.IsNaN(rLong)) break;
                                float next = float.IsNaN(ratio) ? rpm + _job.rpmStep : rpm * Mathf.Clamp(ratio, 1.03f, 1.6f);
                                rpm = Mathf.Max(next, rpm + _job.rpmStep * 0.5f);
                                if (rpm > _job.rpmMax) break;
                            }
                        }

                        if (float.IsNaN(rShort) || float.IsNaN(rLong))
                        {
                            _log.AppendLine($"  {tag} no short/long bracket in {_job.rpmMin:F0}-{_job.rpmMax:F0} rpm, skipped");
                            continue;
                        }

                        // Estimate between the bracket ends. A ball that clips the hub's far rim always reads about
                        // +0.78 m however long it was, so a long end at that value is only trusted as "long": halve.
                        float Estimate(float rS, float dS, float rL, float dL)
                        {
                            bool linear = !float.IsNaN(dS) && !float.IsNaN(dL) && dL > dS && dL < 0.7f;
                            float r = linear ? rS + (0f - dS) * (rL - rS) / (dL - dS) : 0.5f * (rS + rL);
                            return Mathf.Clamp(r, Mathf.Min(rS, rL), Mathf.Max(rS, rL));
                        }

                        // Refine: narrow the bracket a few times before the verify volley.
                        for (int k = 0; k < _job.refineSteps && Mathf.Abs(rLong - rShort) > _job.refineMinGap; k++)
                        {
                            float rMid = Estimate(rShort, drShort, rLong, drLong);
                            StatusSummary = $"{tag} refining {rMid:F0} rpm";
                            yield return Volley(spot, rMid, hood, _job.refineBalls, _job.ballInterval, r => s = r);
                            _log.AppendLine($"  {tag} refine {rMid,5:F0}: crossed {s.crossed}/{s.fired} scored {s.scored} medianDr {F(s.medianDr)} | v0 {s.avgSpeed:F2}±{s.speedStd:F2} pitch {s.avgPitch:F1}");
                            if (s.launched == 0) break;
                            if (s.Long) { rLong = rMid; drLong = s.medianDr; }
                            else { rShort = rMid; drShort = s.crossed > 0 ? s.medianDr : float.NaN; }
                        }

                        float rFit = Estimate(rShort, drShort, rLong, drLong);
                        lastRpmForHood[hood] = rFit;

                        StatusSummary = $"{tag} verifying {rFit:F0} rpm";
                        yield return Volley(spot, rFit, hood, _job.verifyBalls, _job.ballInterval, r => s = r);
                        var fp = new FitPoint
                        {
                            horizDistance = s.horizDistance,
                            interpDistance = s.interpDistance,
                            rpm = rFit,
                            hood = hood,
                            scored = s.scored,
                            fired = s.fired,
                            avgDr = s.avgDr,
                            avgDLat = s.avgDLat,
                            tof = s.avgTof,
                            ok = s.scored >= Mathf.Min(_job.minScoredToAccept, _job.verifyBalls)
                        };
                        _log.AppendLine($"  {tag} VERIFY {rFit:F0} rpm: scored {s.scored}/{s.fired} avgDr {F(s.avgDr)} avgDLat {F(s.avgDLat)} tof {F(s.avgTof)} v0 {s.avgSpeed:F2}±{s.speedStd:F2} pitch {s.avgPitch:F1}±{s.pitchStd:F1} (map distance {s.interpDistance:F2})");

                        // best hood: most scored, then closest to the centre; only accepted volleys count
                        float key = s.scored * 100f - (float.IsNaN(s.avgDr) ? 50f : Mathf.Abs(s.avgDr) * 10f);
                        if (fp.ok && key > bestKey) { bestKey = key; best = fp; }
                        SaveProgress();
                    }

                    if (best != null)
                    {
                        _fit.points.Add(best);
                        lastAcceptedHood = best.hood;
                        _log.AppendLine($"BEST d={d:F1}m: {best.rpm:F0} rpm, hood {best.hood:F0}, scored {best.scored}/{best.fired}, map distance {best.interpDistance:F2}, tof {F(best.tof)}");
                    }
                    else
                    {
                        _log.AppendLine($"BEST d={d:F1}m: none found (no hood angle scored {_job.minScoredToAccept}/{_job.verifyBalls})");
                    }
                    SaveProgress();
                }

                RestoreMaps();
                SetKeys(false, false);
                Finish();
            }

            // ---------------- one volley ----------------
            private IEnumerator Volley(Vector3 spot, float rpm, float hood, int balls, float interval, Action<ShotStats> done)
                => Volley(spot, rpm, hood, balls, interval, _job.feedFromHopper, done);

            private IEnumerator Volley(Vector3 spot, float rpm, float hood, int balls, float interval, bool hopper, Action<ShotStats> done)
            {
                KeepMatchEnabled();
                CleanupBalls();
                SetKeys(false, false);
                SetOverride(rpm, hood);

                // place the robot so the turret sits on the chosen spot
                var pos = new Vector3(spot.x, _spawnY, spot.z);
                Teleport(pos, _job.robotYawDeg);
                yield return new WaitForFixedUpdate();
                Vector3 tp = _turret.transform.position;
                Teleport(pos + new Vector3(spot.x - tp.x, 0f, spot.z - tp.z), _job.robotYawDeg);

                SetKeys(true, false);
                yield return Hold(_job.settleSeconds);

                var stats = new ShotStats { fired = balls };
                Vector3 turretPos = _turret.transform.position;
                stats.horizDistance = Vector2.Distance(new Vector2(turretPos.x, turretPos.z), new Vector2(_hubPos.x, _hubPos.z));
                stats.interpDistance = _fwInterp.Distance;
                Vector3 toHub = new Vector3(_hubPos.x - turretPos.x, 0f, _hubPos.z - turretPos.z).normalized;
                Vector3 right = new Vector3(toHub.z, 0f, -toHub.x);

                var tracked = new List<Tracked>();
                for (int bi = 0; bi < balls; bi++)
                {
                    // hopper feed: drop balls in over the spindexer and let the robot's own feed path (spindexer,
                    // kicker entry, kicker loop) deliver them, as in a match; kicker feed: straight into the loop
                    Vector3 local = hopper
                        ? _job.hopperDropLocal + new Vector3(((bi % 3) - 1) * 0.09f, (bi / 3) * 0.17f, 0f)
                        : _job.ballSpawnLocal;
                    Vector3 wp = _robot.transform.TransformPoint(local);
                    var go = Instantiate(_fuelPrefab, wp, Quaternion.identity, _gpParent);
                    _spawnedBalls.Add(go);
                    var rb = go.GetComponent<Rigidbody>();
                    if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                    tracked.Add(new Tracked { go = go, rb = rb, ctrl = go.GetComponent<RebuiltGamePieceController>(), spawnTime = Time.time, prevPos = wp });
                    float next = Time.time + interval;
                    while (Time.time < next)
                    {
                        KeepMatchEnabled();
                        PumpKeys();
                        yield return new WaitForFixedUpdate();
                        Step(tracked, toHub, right);
                    }
                }
                float end = Time.time + _job.postLastBallWait + (hopper ? _job.hopperExtraWait : 0f);
                while (Time.time < end)
                {
                    KeepMatchEnabled();
                    PumpKeys();
                    yield return new WaitForFixedUpdate();
                    Step(tracked, toHub, right);
                }
                SetKeys(false, false);

                stats.launched = tracked.Count(b => b.launched);
                if (stats.launched > 0)
                {
                    var measured = tracked.Where(b => b.launched && b.measured).ToList();
                    if (measured.Count > 0)
                    {
                        stats.avgSpeed = measured.Average(b => b.launchSpeed);
                        stats.avgPitch = measured.Average(b => b.launchPitch);
                        stats.avgLaunchY = measured.Average(b => b.launchY);
                        stats.speedStd = Std(measured.Select(b => b.launchSpeed));
                        stats.pitchStd = Std(measured.Select(b => b.launchPitch));
                        stats.hoodErrAvg = measured.Average(b => b.hoodErr);
                        stats.hoodErrMax = measured.Max(b => Mathf.Abs(b.hoodErr));
                        stats.rpmAtLaunchAvg = measured.Average(b => b.wheelRpm);
                        stats.spinAvg = measured.Average(b => b.spin);
                    }
                }
                foreach (var b in tracked)
                {
                    stats.perBall.Add(!b.launched
                        ? $"not launched (ended at robot-local {Fmt(_robot.transform.InverseTransformPoint(b.rb != null ? b.rb.position : Vector3.zero))})"
                        : $"v0 {b.launchSpeed:F2} pitch {b.launchPitch:F1} hoodErr {b.hoodErr:+0.0;-0.0;0.0} wheel {b.wheelRpm:F0} rpm spin {b.spin:F0} rad/s" +
                          (b.crossed ? $" dr {b.dr:+0.00;-0.00;0.00} dLat {b.dLat:+0.00;-0.00;0.00}" : " (never came down through rim height)") +
                          (b.scored ? " SCORED" : ""));
                }
                stats.scored = tracked.Count(b => b.scored);
                var crossed = tracked.Where(b => b.crossed).ToList();
                stats.crossed = crossed.Count;
                if (crossed.Count > 0)
                {
                    var drs = crossed.Select(b => b.dr).OrderBy(x => x).ToList();
                    stats.medianDr = drs.Count % 2 == 1 ? drs[drs.Count / 2] : 0.5f * (drs[drs.Count / 2 - 1] + drs[drs.Count / 2]);
                    stats.avgDr = drs.Average();
                    stats.avgDLat = crossed.Average(b => b.dLat);
                    stats.avgTof = crossed.Average(b => b.tof);
                    stats.drStd = Std(drs);
                }
                CleanupBalls();
                done(stats);
            }

            private class Tracked
            {
                public GameObject go;
                public Rigidbody rb;
                public RebuiltGamePieceController ctrl;
                public float spawnTime, launchTime;
                public Vector3 prevPos;
                public bool launched, crossed, scored;
                public float dr, dLat, tof;
                public float launchSpeed, launchPitch, launchY;
                public float hoodErr, wheelRpm, spin;
                public bool measured;
            }

            private static float Std(IEnumerable<float> values)
            {
                var list = values.ToList();
                if (list.Count < 2) return 0f;
                float mean = list.Average();
                return Mathf.Sqrt(list.Sum(x => (x - mean) * (x - mean)) / (list.Count - 1));
            }

            private void Step(List<Tracked> tracked, Vector3 fwd, Vector3 right)
            {
                foreach (var b in tracked)
                {
                    if (b.go == null || b.rb == null) continue;
                    Vector3 p = b.rb.position;
                    Vector3 v = b.rb.velocity;
                    if (b.ctrl != null && ((b.ctrl.SlotIndex >= 0 && b.ctrl.scored) ||
                                           (LastScoredTimeField != null && (float)LastScoredTimeField.GetValue(b.ctrl) >= b.spawnTime)))
                        b.scored = true;

                    if (!b.launched)
                    {
                        if (p.y > _robot.transform.position.y + 0.62f && v.y > 0f && v.magnitude > 2f)
                        {
                            b.launched = true;
                            b.launchTime = Time.time;
                        }
                    }
                    if (b.launched && !b.measured)
                    {
                        // measure once the ball is clear of the hood (0.35 m from the shaft), so the reading isn't
                        // taken while the wheel is still accelerating it
                        Vector3 shaft = _hood.transform.position;
                        if ((p - shaft).magnitude > 0.35f)
                        {
                            b.measured = true;
                            b.launchSpeed = v.magnitude;
                            b.launchPitch = Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
                            b.launchY = p.y;
                            b.spin = b.rb.angularVelocity.magnitude;
                            float target = _hoodTargetField != null ? (float)_hoodTargetField.GetValue(_robot) : 0f;
                            b.hoodErr = Mathf.DeltaAngle(target, _hood.GetAngle());
                            b.wheelRpm = FirstShooterRoller() is InteractionRoller ir ? Mathf.Abs(ir.realRpm) : 0f;
                        }
                    }
                    if (b.launched && !b.crossed && b.prevPos.y >= _hubPos.y && p.y < _hubPos.y && v.y < 0f)
                    {
                        float frac = Mathf.Clamp01((b.prevPos.y - _hubPos.y) / Mathf.Max(1e-4f, b.prevPos.y - p.y));
                        Vector3 c = Vector3.Lerp(b.prevPos, p, frac);
                        Vector3 err = new Vector3(c.x - _hubPos.x, 0f, c.z - _hubPos.z);
                        b.crossed = true;
                        b.dr = Vector3.Dot(err, fwd);
                        b.dLat = Vector3.Dot(err, right);
                        b.tof = Mathf.Lerp(Time.time - Time.fixedDeltaTime, Time.time, frac) - b.launchTime;
                    }
                    b.prevPos = p;
                }
            }

            // ---------------- aim calibration ----------------
            private static readonly FieldInfo TurretOffsetField =
                typeof(GenericTurret).GetField("angleOffset", BindingFlags.Instance | BindingFlags.NonPublic);

            /// <summary>
            /// Fires one volley at the current turret angleOffset and one at +probe degrees, measures how the
            /// sideways miss moves per degree, and sets the offset that centres the shots. The rest of the run is
            /// measured with the corrected aim, and Apply writes it to the prefab's turret.
            /// </summary>
            private IEnumerator CalibrateAim()
            {
                if (TurretOffsetField == null) yield break;
                if (!TryPickSpot(_job.aimCalDistance, out Vector3 spot, out _)) { _log.AppendLine("Aim calibration: no clear spot, skipped"); yield break; }
                float orig = (float)TurretOffsetField.GetValue(_turret);
                ShotStats a = null, b = null;

                StatusSummary = "Aim calibration (current offset)";
                yield return Volley(spot, _job.aimCalRpm, _job.aimCalHood, 4, _job.ballInterval, r => a = r);
                TurretOffsetField.SetValue(_turret, orig + _job.aimCalProbeDeg);
                StatusSummary = "Aim calibration (probe offset)";
                yield return Volley(spot, _job.aimCalRpm, _job.aimCalHood, 4, _job.ballInterval, r => b = r);
                TurretOffsetField.SetValue(_turret, orig);

                if (a.crossed == 0 || b.crossed == 0 || float.IsNaN(a.avgDLat) || float.IsNaN(b.avgDLat))
                {
                    _log.AppendLine($"Aim calibration: shots didn't come down through rim height at {_job.aimCalRpm:F0} rpm / hood {_job.aimCalHood:F0} (crossed {a.crossed}, {b.crossed}); aim left at {orig:F1}");
                    yield break;
                }
                float slope = (b.avgDLat - a.avgDLat) / _job.aimCalProbeDeg;   // metres of sideways miss per degree
                if (Mathf.Abs(slope) < 0.005f)
                {
                    _log.AppendLine($"Aim calibration: sideways miss barely moved with the offset (dLat {a.avgDLat:+0.00;-0.00} -> {b.avgDLat:+0.00;-0.00}); aim left at {orig:F1}");
                    yield break;
                }
                float corr = Mathf.Clamp(-a.avgDLat / slope, -10f, 10f);
                float newOffset = orig + corr;
                TurretOffsetField.SetValue(_turret, newOffset);
                _fit.setTurretOffset = true;
                _fit.turretAngleOffset = newOffset;
                _log.AppendLine($"Aim calibration: dLat {a.avgDLat:+0.00;-0.00} m at offset {orig:F1}, {b.avgDLat:+0.00;-0.00} m at {orig + _job.aimCalProbeDeg:F1} " +
                                $"-> {slope:+0.000;-0.000} m/deg, angleOffset set to {newOffset:F1} for this run");
                SaveProgress();
            }

            // ---------------- tower avoidance ----------------
            private void FindTower()
            {
                _towerBounds.Clear();
                foreach (var c in FindObjectsByType<Collider>(FindObjectsSortMode.None))
                {
                    if (c == null || !c.enabled || c.isTrigger || c.transform.IsChildOf(_robot.transform)) continue;
                    if (c.GetComponentInParent<RebuiltGamePieceController>() != null) continue;
                    bool tower = false;
                    for (Transform t = c.transform; t != null; t = t.parent)
                    {
                        if (t.name.IndexOf("Tower", StringComparison.OrdinalIgnoreCase) >= 0) { tower = true; break; }
                    }
                    if (tower) _towerBounds.Add(c.bounds);
                }
                if (_towerBounds.Count == 0)
                {
                    _log.AppendLine("No tower colliders found (nothing named *Tower*): test spots are not tower-checked");
                    return;
                }
                // log each tower as one box (towers on both alliances)
                var merged = new List<Bounds>();
                foreach (var b in _towerBounds)
                {
                    int i = merged.FindIndex(m => { var e = m; e.Expand(0.5f); return e.Intersects(b); });
                    if (i < 0) merged.Add(b);
                    else { var m = merged[i]; m.Encapsulate(b); merged[i] = m; }
                }
                foreach (var m in merged)
                    _log.AppendLine($"Tower colliders: x {m.min.x:F2}..{m.max.x:F2}, z {m.min.z:F2}..{m.max.z:F2}, top {m.max.y:F2} m");
            }

            private static float DistXZ(Bounds b, Vector3 p)
            {
                float dx = Mathf.Max(b.min.x - p.x, 0f, p.x - b.max.x);
                float dz = Mathf.Max(b.min.z - p.z, 0f, p.z - b.max.z);
                return Mathf.Sqrt(dx * dx + dz * dz);
            }

            private static bool SegmentHitsXZ(Bounds b, Vector3 a, Vector3 c, float pad)
            {
                for (int i = 0; i <= 40; i++)
                {
                    if (DistXZ(b, Vector3.Lerp(a, c, i / 40f)) < pad) return true;
                }
                return false;
            }

            /// <summary>
            /// Turret spot <paramref name="d"/> m from the hub: straight out into the alliance zone if that's clear,
            /// otherwise the smallest swing to either side that keeps the robot and the shot path clear of the tower
            /// and the robot inside the field.
            /// </summary>
            private bool TryPickSpot(float d, out Vector3 spot, out float angleUsed)
            {
                float outX = Mathf.Sign(_hubPos.x);
                if (outX == 0f) outX = 1f;
                Bounds field = FieldBounds();
                spot = default;
                angleUsed = 0f;
                for (float a = 0f; a <= _job.maxLineAngleDeg + 0.01f; a += 5f)
                {
                    foreach (float sgn in a == 0f ? new[] { 1f } : new[] { 1f, -1f })
                    {
                        float ang = a * sgn;
                        float rad = ang * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(outX * Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                        Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
                        Vector3 p = new Vector3(_hubPos.x, _spawnY, _hubPos.z) + dir * d + perp * _job.lateralOffset;
                        if (p.x < field.min.x + 0.55f || p.x > field.max.x - 0.55f || p.z < field.min.z + 0.55f || p.z > field.max.z - 0.55f) continue;
                        if (_job.avoidTower && _towerBounds.Any(b => DistXZ(b, p) < _job.towerClearance ||
                                                                     SegmentHitsXZ(b, p, _hubPos, _job.shotPathClearance)))
                            continue;
                        spot = p;
                        angleUsed = ang;
                        return true;
                    }
                }
                return false;
            }

            private Bounds FieldBounds()
            {
                var carpet = GameObject.Find("Carpet");
                if (carpet != null)
                {
                    var cols = carpet.GetComponentsInChildren<Collider>().Where(c => c.enabled).ToArray();
                    if (cols.Length > 0)
                    {
                        Bounds b = cols[0].bounds;
                        foreach (var c in cols) b.Encapsulate(c.bounds);
                        if (b.size.x > 5f && b.size.z > 3f) return b;
                    }
                }
                return new Bounds(Vector3.zero, new Vector3(16.5f, 4f, 8.0f));
            }

            // ---------------- consistency study ----------------
            private InteractionRoller FirstShooterRoller()
            {
                var inst = ShooterInstances();
                return inst != null && inst.Length > 0 ? inst[0]?.GetInteractionRoller() : null;
            }

            private RollerGroup.RollerInstance[] ShooterInstances()
            {
                if (_shooterWheels == null) return null;
                var f = typeof(RollerGroup).GetField("rollers", BindingFlags.Instance | BindingFlags.NonPublic);
                return f?.GetValue(_shooterWheels) as RollerGroup.RollerInstance[];
            }

            private static readonly FieldInfo IrSlip = typeof(InteractionRoller).GetField("motorSlipResponse", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly FieldInfo IrStiff = typeof(InteractionRoller).GetField("compressionStiffness", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly FieldInfo IrFc = typeof(InteractionRoller).GetField("frictionCoupledToNormal", BindingFlags.Instance | BindingFlags.NonPublic);

            private IEnumerator Study()
            {
                if (!TryPickSpot(_job.studyDistance, out Vector3 spot, out float ang))
                {
                    _fit.error = $"No clear test spot at {_job.studyDistance:F1} m";
                    yield break;
                }
                _log.AppendLine($"Consistency study: {_job.studyBalls} balls per variant at {_job.studyRpm:F0} rpm, hood {_job.studyHood:F0}, " +
                                $"{_job.studyDistance:F1} m from turret spot ({spot.x:F2}, {spot.z:F2}), {ang:+0;-0;0} deg off straight-out");
                _log.AppendLine("hoodErr = hood joint angle minus commanded angle at launch (deg); wheel = first shooter wheel's real rpm at launch");

                var rollers = (ShooterInstances() ?? Array.Empty<RollerGroup.RollerInstance>())
                    .Select(i => i?.GetInteractionRoller()).Where(r => r != null).ToArray();
                var origSlip = rollers.Select(r => (float)IrSlip.GetValue(r)).ToArray();
                var origStiff = rollers.Select(r => (float)IrStiff.GetValue(r)).ToArray();
                var origFc = rollers.Select(r => (bool)IrFc.GetValue(r)).ToArray();
                PhysicMaterial arcMat = _hoodArc != null ? _hoodArc.GetComponentInChildren<Collider>()?.sharedMaterial : null;
                float origArcFriction = arcMat != null ? arcMat.dynamicFriction : 0f;
                float origHoodP = _hoodPid != null ? _hoodPid.kP : 0f;
                float origHoodMax = _hoodPid != null ? _hoodPid.Max : 0f;

                // kicker rollers run at their Inspector rpm (never SetRpm'd by Obsidian), so scale that field
                var kickerGroup = typeof(global::Obsidian).GetField("kickerRollers", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_robot) as RollerGroup;
                var rgRollers = typeof(RollerGroup).GetField("rollers", BindingFlags.Instance | BindingFlags.NonPublic);
                var kickerInstances = kickerGroup != null ? rgRollers?.GetValue(kickerGroup) as RollerGroup.RollerInstance[] : null;
                var kickers = (kickerInstances ?? Array.Empty<RollerGroup.RollerInstance>())
                    .Select(i => i?.GetInteractionRoller()).Where(r => r != null).ToArray();
                var irRpm = typeof(InteractionRoller).GetField("rpm", BindingFlags.Instance | BindingFlags.NonPublic);
                var origKickRpm = kickers.Select(r => (float)irRpm.GetValue(r)).ToArray();
                void ScaleKicker(float k)
                {
                    for (int i = 0; i < kickers.Length; i++) irRpm.SetValue(kickers[i], origKickRpm[i] * k);
                }
                _log.AppendLine($"kicker rollers: {kickers.Length} at {(origKickRpm.Length > 0 ? Mathf.Abs(origKickRpm[0]) : 0f):F0} rpm");

                Action resetReach = null;
                void ResetVariant()
                {
                    ScaleKicker(1f);
                    resetReach?.Invoke();
                    for (int i = 0; i < rollers.Length; i++)
                    {
                        IrSlip.SetValue(rollers[i], origSlip[i]);
                        IrStiff.SetValue(rollers[i], origStiff[i]);
                        IrFc.SetValue(rollers[i], origFc[i]);
                    }
                    if (arcMat != null) { arcMat.dynamicFriction = origArcFriction; arcMat.staticFriction = origArcFriction; }
                    if (_hoodPid != null) { _hoodPid.kP = origHoodP; _hoodPid.Max = origHoodMax; }
                }

                // Shooter wheel reach: grow each wheel's grip zone (radius) by the same amount as its squish, so the
                // solid core is unchanged, and raise contact damping so a ball doesn't bounce out of the grip early.
                var irRadius = typeof(InteractionRoller).GetField("radius", BindingFlags.Instance | BindingFlags.NonPublic);
                var irSquish = typeof(InteractionRoller).GetField("maxSquish", BindingFlags.Instance | BindingFlags.NonPublic);
                var irMRadius = typeof(InteractionRoller).GetField("_mRadius", BindingFlags.Instance | BindingFlags.NonPublic);
                var irTrigger = typeof(InteractionRoller).GetField("_triggerCollider", BindingFlags.Instance | BindingFlags.NonPublic);
                var irDamp = typeof(InteractionRoller).GetField("compressionDamping", BindingFlags.Instance | BindingFlags.NonPublic);
                var origRad = rollers.Select(r => (float)irRadius.GetValue(r)).ToArray();
                var origSq = rollers.Select(r => (float)irSquish.GetValue(r)).ToArray();
                var origDamp = rollers.Select(r => (float)irDamp.GetValue(r)).ToArray();
                void SetReach(float extraIn, float damping)
                {
                    for (int i = 0; i < rollers.Length; i++)
                    {
                        float rad = origRad[i] + extraIn;
                        irRadius.SetValue(rollers[i], rad);
                        irSquish.SetValue(rollers[i], origSq[i] + extraIn);
                        irMRadius.SetValue(rollers[i], rad * 0.0254f);
                        if (irTrigger.GetValue(rollers[i]) is CapsuleCollider cap) cap.radius = rad * 0.0254f;
                        irDamp.SetValue(rollers[i], damping < 0f ? origDamp[i] : damping);
                    }
                }
                resetReach = () => SetReach(0f, -1f);
                _log.AppendLine("shooter wheels: " + string.Join(", ", rollers.Select((r, i) => $"radius {origRad[i]:F2} in, squish {origSq[i]:F2} in, damping {origDamp[i]:F0}")));

                var variants = new List<(string name, float interval, bool hopper, Action apply)>
                {
                    ("kicker-loop feed, current wheels", 0.14f, false, () => { }),
                    ("hopper feed, current wheels", 0.1f, true, () => { }),
                    ("kicker-loop feed, wheel reach +0.5 in, damping 150", 0.14f, false, () => SetReach(0.5f, 150f)),
                    ("hopper feed, wheel reach +0.5 in, damping 150", 0.1f, true, () => SetReach(0.5f, 150f)),
                    ("kicker-loop feed, wheel reach +1.0 in, damping 150", 0.14f, false, () => SetReach(1.0f, 150f)),
                    ("hopper feed, wheel reach +1.0 in, damping 150", 0.1f, true, () => SetReach(1.0f, 150f)),
                };

                foreach (var v in variants)
                {
                    ResetVariant();
                    v.apply();
                    StatusSummary = $"Study: {v.name}";
                    ShotStats s = null;
                    yield return Volley(spot, _job.studyRpm, _job.studyHood, _job.studyBalls, v.interval, v.hopper, r => s = r);
                    _log.AppendLine($"\n== {v.name}: scored {s.scored}/{s.fired}, launched {s.launched} | v0 {s.avgSpeed:F2} ± {s.speedStd:F2} m/s | pitch {s.avgPitch:F1} ± {s.pitchStd:F1} deg | " +
                                    $"range spread ± {s.drStd:F2} m (avg {F(s.avgDr)}) | hoodErr avg {s.hoodErrAvg:+0.0;-0.0;0.0} max {s.hoodErrMax:F1} | wheel {s.rpmAtLaunchAvg:F0} rpm | spin {s.spinAvg:F0} rad/s");
                    for (int i = 0; i < s.perBall.Count; i++) _log.AppendLine($"   ball {i}: {s.perBall[i]}");
                    SaveProgress();
                }
                ResetVariant();
            }

            // ---------------- helpers ----------------
            private void SetOverride(float rpm, float hood)
            {
                var fw = new[] { new DistanceValue { distance = 0f, value = rpm } };
                var hd = new[] { new DistanceValue { distance = 0f, value = hood } };
                _fwInterp.interpolationMap = fw;
                _hoodInterp.interpolationMap = hd;
                _sortedCacheField?.SetValue(_fwInterp, fw);
                _sortedCacheField?.SetValue(_hoodInterp, hd);
            }

            private void RestoreMaps()
            {
                if (_fwInterp == null || _origFwMap == null) return;
                _fwInterp.interpolationMap = _origFwMap;
                _hoodInterp.interpolationMap = _origHoodMap;
                var init = typeof(Interpolation).GetMethod("InitializeCache", BindingFlags.Instance | BindingFlags.NonPublic);
                init?.Invoke(_fwInterp, null);
                init?.Invoke(_hoodInterp, null);
            }

            private void Teleport(Vector3 pos, float yawDeg)
            {
                Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
                _robot.transform.SetPositionAndRotation(pos, rot);
                for (int i = 0; i < _rbs.Length; i++)
                {
                    var rb = _rbs[i];
                    if (rb == null) continue;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    if (rb.gameObject != _robot.gameObject)
                    {
                        Vector3 wp = pos + rot * _relPos[i];
                        Quaternion wr = rot * _relRot[i];
                        rb.transform.SetPositionAndRotation(wp, wr);
                        rb.position = wp;
                        rb.rotation = wr;
                    }
                    else
                    {
                        rb.position = pos;
                        rb.rotation = rot;
                        rb.constraints = RigidbodyConstraints.FreezeAll;
                    }
                }
                Physics.SyncTransforms();
            }

            private IEnumerator Hold(float seconds)
            {
                float end = Time.time + seconds;
                while (Time.time < end)
                {
                    KeepMatchEnabled();
                    PumpKeys();
                    yield return new WaitForFixedUpdate();
                }
            }

            private void CleanupBalls()
            {
                foreach (var b in _spawnedBalls) if (b != null) Destroy(b);
                _spawnedBalls.Clear();
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

            private IEnumerator EnsureSpawned(Alliance alliance)
            {
                StatusSummary = "Waiting for managers...";
                float deadline = Time.realtimeSinceStartup + 20f;
                while ((BaseGameManager.Instance == null || FindFirstObjectByType<RobotSpawnController>() == null) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (BaseGameManager.Instance == null) { _fit.error = "BaseGameManager not found"; yield break; }

                var rsc = FindFirstObjectByType<RobotSpawnController>();
                if (MatchManager.Instance != null)
                    MatchManager.Instance.GetOrCreatePlayerSettings(0).alliance = alliance;

                var meta = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(AssetPath);
                if (meta == null) { _fit.error = "Obsidian.asset not found"; yield break; }

                while (!rsc.EditorWaitingForInitialSpawn && !rsc.EditorInitialSpawnComplete && Time.realtimeSinceStartup < deadline)
                    yield return null;

                var existing = FindFirstObjectByType<global::Obsidian>();
                if (existing == null || existing.Alliance != alliance || rsc.EditorRobotOverride != meta)
                {
                    StatusSummary = "Switching robot to Obsidian...";
                    if (!rsc.EditorTrySwitchRobot(meta, 0, out string err))
                    {
                        _fit.error = "EditorTrySwitchRobot failed: " + err;
                        yield break;
                    }
                    yield return null;
                    while (BaseGameManager.Instance.IsResetting && Time.realtimeSinceStartup < deadline + 20f)
                        yield return null;
                }
                while ((FindFirstObjectByType<global::Obsidian>() == null || !rsc.EditorInitialSpawnComplete) && Time.realtimeSinceStartup < deadline + 20f)
                    yield return null;

                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return new WaitForSeconds(0.25f);
            }

            private void SetupVirtualInput()
            {
                if (!_inputSaved)
                {
                    _origBg = InputSystem.settings.backgroundBehavior;
                    _origEditorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
                    _inputSaved = true;
                }
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

                foreach (var dev in InputSystem.devices.ToArray())
                    if (dev is Keyboard kb && kb.name.StartsWith("ObsidianMapperVKB")) InputSystem.RemoveDevice(kb);
                _vkb = InputSystem.AddDevice<Keyboard>("ObsidianMapperVKB");
                string prefix = $"/{_vkb.name}/";
                _actShoot = new InputAction("OM_Shoot", InputActionType.Button, prefix + "f9");
                _actIntake = new InputAction("OM_Intake", InputActionType.Button, prefix + "f10");
                _actManual = new InputAction("OM_Manual", InputActionType.Button, prefix + "f11");
                _actPass = new InputAction("OM_Pass", InputActionType.Button, prefix + "f12");
                _actShoot.Enable();
                _actIntake.Enable();
                _actManual.Enable();
                _actPass.Enable();

                var actionField = typeof(MoSimInput).GetField("action", BindingFlags.Instance | BindingFlags.NonPublic);
                actionField.SetValue(_robot.OuttakeAction, _actShoot);
                actionField.SetValue(_robot.IntakeAction, _actIntake);
                actionField.SetValue(_robot.ManualShoot, _actManual);
                actionField.SetValue(_robot.AutoPass, _actPass);
            }

            private void SetKeys(bool shoot, bool intake)
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

            private void PumpKeys()
            {
                if (_vkb == null) return;
                bool ok = _actShoot != null && _actShoot.IsPressed() == _holdShoot &&
                          _actIntake != null && _actIntake.IsPressed() == _holdIntake;
                if (!ok) SetKeys(_holdShoot, _holdIntake);
            }

            private void SaveProgress()
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(LogTxtPath, _log.ToString());
                File.WriteAllText(FitJsonPath, JsonUtility.ToJson(_fit, true));
            }

            private void Finish()
            {
                _fit.done = true;
                if (!string.IsNullOrEmpty(_fit.error)) _log.AppendLine("ERROR: " + _fit.error);
                SaveProgress();
                Time.timeScale = 1f;
                StatusSummary = string.IsNullOrEmpty(_fit.error)
                    ? $"DONE: {_fit.points.Count} distance(s) mapped. Exit Play mode, then Tools > Obsidian Shot Mapper > Apply Fitted Map To Prefab."
                    : "ERROR: " + _fit.error;
                Debug.Log("[ObsidianShotMapper] " + StatusSummary + "\n" + _log);
            }

            /// <summary>
            /// Launch speed (m/s) that lands a ball launched at <paramref name="pitchDeg"/> on a point
            /// <paramref name="horiz"/> m away and <paramref name="rise"/> m higher, ignoring drag. NaN when the
            /// angle is too flat to get there at any speed.
            /// </summary>
            private static float RequiredSpeed(float horiz, float rise, float pitchDeg)
            {
                if (float.IsNaN(pitchDeg) || float.IsNaN(rise) || horiz <= 0.01f) return float.NaN;
                float th = pitchDeg * Mathf.Deg2Rad;
                float c = Mathf.Cos(th);
                float denom = 2f * c * c * (horiz * Mathf.Tan(th) - rise);
                if (denom <= 1e-4f) return float.NaN;
                return Mathf.Sqrt(Physics.gravity.magnitude * horiz * horiz / denom);
            }

            private static string F(float v) => float.IsNaN(v) ? "-" : v.ToString("+0.00;-0.00;0.00");
        }

        private static string Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }
}
