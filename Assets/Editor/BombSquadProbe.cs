// Editor-only diagnostics for the BombSquad (16) robot, driven through UnityLiveBridge. Watches every play session:
//   MoSim.EditorTools.BombSquadProbe.Report  - collider overlaps with the field (spawn / match start) and intake arm sag
//   MoSim.EditorTools.BombSquadProbe.Clear   - forget what was recorded so far
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MoSim.EditorTools
{
    [InitializeOnLoad]
    internal static class BombSquadProbe
    {
        static BombSquadProbe()
        {
            EditorApplication.update += Sample;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) Save(); };
        }

        // the report is also written to <project>/Logs/BombSquadProbe.txt every 2 s while playing, so it survives Unity closing
        private static float _nextSave;
        private static void Save()
        {
            if (Events.Count == 0) return;
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Logs", "BombSquadProbe.txt"), Report()); }
            catch (System.Exception) { }
        }

        private static MonoBehaviour _robot;
        private static Collider[] _robotCols;
        private static Rigidbody _rb;
        private static float _nextFind, _found, _nextSample, _enabledAt = -1f;
        private static string _lastState = "";
        private static int _fuelLayer;
        private static readonly Collider[] Hits = new Collider[128];
        private static readonly List<string> Events = new();
        private static readonly Dictionary<string, (int count, float maxDepth, float first, string state)> Overlaps = new();
        private static readonly List<string> ArmLog = new();
        private static float _worstSag;
        private static Quaternion _armZero = Quaternion.identity;
        private static int _lastFuel = -1, _fuelLeft;
        private static float _nextShooterLog;
        private static readonly List<string> ShooterLog = new();
        private static string _worstSagAt = "";

        // robot-local box counted as "fuel in the robot"
        private const float HalfX = 0.42f, MinZ = -0.43f, MaxZ = 0.45f, MinY = 0.02f, MaxY = 0.7f;

        public static string Clear()
        {
            Events.Clear(); Overlaps.Clear(); ArmLog.Clear(); ShooterLog.Clear(); _worstSag = 0f; _worstSagAt = "";
            _lastFuel = -1; _fuelLeft = 0;
            return "cleared";
        }

        private static object Field(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static string GameState()
        {
            var t = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MoSimCore.BaseClasses.GameManagement.BaseGameManager"))
                .FirstOrDefault(x => x != null);
            var inst = t?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            return inst?.GetType().GetProperty("RobotState")?.GetValue(inst)?.ToString() ?? "none";
        }

        private static void Sample()
        {
            if (!EditorApplication.isPlaying) { _robot = null; return; }
            if (EditorApplication.isPaused) return;
            if (_robot == null)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 0.25f;
                _robot = Object.FindObjectsOfType<MonoBehaviour>().FirstOrDefault(m => m.GetType().Name == "BombSquad");
                if (_robot == null) return;
                _robotCols = _robot.GetComponentsInChildren<Collider>(true);
                _rb = _robot.GetComponent<Rigidbody>();
                _fuelLayer = LayerMask.NameToLayer("GamePiece1");
                _found = Time.time; _enabledAt = -1f; _lastState = "";
                Clear();
                var j = Field(_robot, "intakeJoint") as Component;
                if (j != null) _armZero = Quaternion.Inverse(_robot.transform.rotation) * j.transform.rotation;
                Events.Add($"t=0.0 robot found at {_robot.transform.position} rot {_robot.transform.eulerAngles}");
            }

            string state = GameState();
            if (state != _lastState)
            {
                Events.Add($"t={Time.time - _found:F1} state {_lastState} -> {state} at {_robot.transform.position}");
                if (state == "Enabled" && _enabledAt < 0) _enabledAt = Time.time;
                _lastState = state;
            }
            if (Time.realtimeSinceStartup >= _nextSave) { _nextSave = Time.realtimeSinceStartup + 2f; Save(); }
            if (Time.time < _nextSample) return;
            _nextSample = Time.time + 0.1f;
            // overlaps: densely for the first 3 s after spawn and after enable, then every 0.5 s
            bool early = Time.time - _found < 3f || (_enabledAt >= 0 && Time.time - _enabledAt < 3f);
            if (early) SampleOverlaps(state);
            SampleArm(state);
        }

        private static void SampleOverlaps(string state)
        {
            foreach (var c in _robotCols)
            {
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                var b = c.bounds;
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents, Hits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var o = Hits[i];
                    if (o == c || o.transform.IsChildOf(_robot.transform) || o.gameObject.layer == _fuelLayer) continue;
                    if (Physics.GetIgnoreCollision(c, o)) continue;
                    if (!Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, o, o.transform.position, o.transform.rotation,
                            out _, out float depth) || depth < 0.01f) continue;
                    string key = $"{Path(c.transform)}  x  {o.transform.root.name}/{o.name}";
                    float t = Time.time - _found;
                    Overlaps[key] = Overlaps.TryGetValue(key, out var v)
                        ? (v.count + 1, Mathf.Max(v.maxDepth, depth), v.first, v.state)
                        : (1, depth, t, state);
                }
            }
        }

        private static void SampleArm(string state)
        {
            var joint = Field(_robot, "intakeJoint") as Component;
            if (joint == null) return;
            // arm angle about the robot's X axis, measured from the prefab pose (same zero as the script)
            Quaternion rel = Quaternion.Inverse(_robot.transform.rotation) * joint.transform.rotation;
            (Quaternion.Inverse(_armZero) * rel).ToAngleAxis(out float ang, out Vector3 ax);
            float angle = Vector3.Dot(ax, Vector3.right) < 0 ? -ang : ang;
            if (angle > 180f) angle -= 360f;
            float target = (float)(Field(_robot, "_intakeTarget") ?? 0f);
            var cfg = joint.GetComponent<ConfigurableJoint>();
            float torque = cfg ? cfg.currentTorque.magnitude : 0f;
            float maxF = cfg ? cfg.angularXDrive.maximumForce : 0f;
            float err = Mathf.DeltaAngle(target, angle);
            int fuel = 0;
            int n = Physics.OverlapSphereNonAlloc(_robot.transform.position + Vector3.up * 0.3f, 1.2f, Hits, 1 << _fuelLayer);
            var seen = new HashSet<Rigidbody>();
            for (int i = 0; i < n; i++)
            {
                var body = Hits[i].attachedRigidbody;
                if (body == null || !seen.Add(body)) continue;
                var l = _robot.transform.InverseTransformPoint(body.position);
                if (Mathf.Abs(l.x) < HalfX && l.z > MinZ && l.z < MaxZ && l.y > MinY && l.y < MaxY) fuel++;
            }
            if (_lastFuel >= 0 && fuel < _lastFuel) _fuelLeft += _lastFuel - fuel;
            _lastFuel = fuel;
            string line = $"t={Time.time - _found:F1} {state} angle={angle:F0} target={target:F0} err={err:F0} torque={torque:F0} maxForce={(maxF > 1e30f ? "inf" : maxF.ToString("F0"))} " +
                          $"deployed={Field(_robot, "_intakeDeployed")} agitating={Field(_robot, "_agitating")} fuelInRobot={fuel} | {Shooter()}";
            if (Time.time >= _nextShooterLog && (bool)(_robot.GetType().GetProperty("ShootHeld", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_robot) ?? false))
            {
                _nextShooterLog = Time.time + 0.5f;
                ShooterLog.Add(line);
                if (ShooterLog.Count > 200) ShooterLog.RemoveAt(0);
            }
            if (Mathf.Abs(err) > Mathf.Abs(_worstSag)) { _worstSag = err; _worstSagAt = line; }
            if (Mathf.Abs(err) > 10f && (ArmLog.Count == 0 || !ArmLog[^1].Contains($"fuelInRobot={fuel}") || Time.frameCount % 20 == 0))
            {
                ArmLog.Add(line);
                if (ArmLog.Count > 400) ArmLog.RemoveAt(0);
            }
        }

        private static object Res(object group, string method)
        {
            if (group == null) return null;
            var r = group.GetType().GetMethod(method)?.Invoke(group, null);
            return r?.GetType().GetProperty("Value")?.GetValue(r);
        }

        private static string Shooter()
        {
            object fw = Field(_robot, "flywheel"), kl = Field(_robot, "kickerLower"), ku = Field(_robot, "kickerUpper"), bed = Field(_robot, "rollerBed");
            float F(object o) => o is float f ? f : 0f;
            var aim = Field(_robot, "_aim");
            object aimErr = aim?.GetType().GetMethod("GetRotationError")?.Invoke(aim, null);
            return $"flywheel cur={F(Res(fw, "GetAverageCurrentRpm")):F0} target={F(Res(fw, "GetAverageTargetRpm")):F0} atSpeed={Res(fw, "AtSpeed")} " +
                   $"kickers={F(Res(kl, "GetAverageCurrentRpm")):F0}/{F(Res(ku, "GetAverageCurrentRpm")):F0} bed={F(Res(bed, "GetAverageCurrentRpm")):F0} " +
                   $"aimErr={aimErr} fuelLeftRobot={_fuelLeft}";
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && p != _robot.transform; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts.Count > 4 ? parts.Take(2).Concat(new[] { "..." }).Concat(parts.Skip(parts.Count - 2)) : parts);
        }

        public static string Report()
        {
            if (_robot == null && Events.Count == 0) return "No BombSquad seen yet (enter play mode and pick the robot)";
            var sb = new StringBuilder();
            sb.AppendLine("EVENTS"); foreach (var e in Events) sb.AppendLine("  " + e);
            sb.AppendLine($"OVERLAPS (robot collider x other, deeper than 1 cm): {Overlaps.Count}");
            foreach (var kv in Overlaps.OrderBy(k => k.Value.first).Take(40))
                sb.AppendLine($"  first t={kv.Value.first:F1} ({kv.Value.state}) samples={kv.Value.count} maxDepth={kv.Value.maxDepth * 100:F1}cm  {kv.Key}");
            sb.AppendLine($"SHOOTER while shoot held (every 0.5 s, last 30 of {ShooterLog.Count})");
            foreach (var l in ShooterLog.Skip(Mathf.Max(0, ShooterLog.Count - 30))) sb.AppendLine("  " + l);
            sb.AppendLine($"ARM worst error {_worstSag:F0} deg: {_worstSagAt}");
            sb.AppendLine($"ARM samples with |error| > 10 deg: {ArmLog.Count} (last 40)");
            foreach (var l in ArmLog.Skip(Mathf.Max(0, ArmLog.Count - 40))) sb.AppendLine("  " + l);
            return sb.ToString();
        }
    }
}
