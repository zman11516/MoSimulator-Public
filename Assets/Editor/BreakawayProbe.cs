// Editor-only diagnostics for the Breakaway (3937) robot, driven through UnityLiveBridge:
//   MoSim.EditorTools.BreakawayProbe.Start   - begin watching fuel that leaves the robot (play mode)
//   MoSim.EditorTools.BreakawayProbe.Report  - what left, where, and what the robot was doing
//   MoSim.EditorTools.BreakawayProbe.Stop
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MoSim.EditorTools
{
    [InitializeOnLoad]
    internal static class BreakawayProbe
    {
        // watches automatically in every play session; Report works any time during it
        static BreakawayProbe()
        {
            EditorApplication.update += Sample;
            _watching = true;
        }

        private static float _nextFind;

        private class Track
        {
            public bool inside;
            public float insideSince;
            public Vector3 lastLocal, lastLocalVel;
            public string lastTouch;
            public float nextLaunchLog;
        }

        private static readonly List<(string touch, Vector3 local, Vector3 vel, string state)> Launches = new();

        public static string Launched()
        {
            var sb = new StringBuilder($"fast fuel (>2 m/s relative to robot): {Launches.Count}\n");
            foreach (var g in Launches.GroupBy(l => l.touch).OrderByDescending(g => g.Count()).Take(8))
                sb.AppendLine($"  {g.Key}: {g.Count()}  avg vel ({g.Average(l => l.vel.x):F1},{g.Average(l => l.vel.y):F1},{g.Average(l => l.vel.z):F1})");
            foreach (var l in Launches.Skip(Mathf.Max(0, Launches.Count - 12)))
                sb.AppendLine($"  at ({l.local.x:F2},{l.local.y:F2},{l.local.z:F2}) vel ({l.vel.x:F1},{l.vel.y:F1},{l.vel.z:F1}) [{l.touch}] {l.state}");
            return sb.ToString();
        }

        private struct Escape
        {
            public float time, wasInside;
            public string side, touch, state;
            public Vector3 local, localVel;
        }

        private static MonoBehaviour _robot;
        private static readonly Dictionary<Rigidbody, Track> Tracks = new();
        private static readonly List<Escape> Escapes = new();
        private static int _fuelLayer;
        private static float _started;
        private static bool _watching;
        private static readonly Collider[] Hits = new Collider[64];

        // robot-local box the fuel is "inside" (frame is +-0.44 incl. bumpers; intake arm reaches z 0.66)
        private const float HalfX = 0.42f, MinZ = -0.43f, MaxZ = 0.62f, MinY = 0.02f, MaxY = 0.65f;

        public static string Start()
        {
            if (!EditorApplication.isPlaying) return "Not in play mode";
            _robot = Object.FindObjectsOfType<MonoBehaviour>().FirstOrDefault(m => m.GetType().Name == "Breakaway");
            if (_robot == null) return "No Breakaway robot in the scene";
            _fuelLayer = LayerMask.NameToLayer("GamePiece1");
            Tracks.Clear();
            Escapes.Clear();
            _started = Time.time;
            if (!_watching) EditorApplication.update += Sample;
            _watching = true;
            return $"Watching fuel around {_robot.name}";
        }

        public static string Stop()
        {
            if (_watching) EditorApplication.update -= Sample;
            _watching = false;
            return "Stopped";
        }

        private static void Sample()
        {
            if (!EditorApplication.isPlaying) { _robot = null; return; }
            if (EditorApplication.isPaused) return;
            if (_robot == null)
            {
                // the robot spawns after the selection screen: look for it once a second
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 1f;
                _robot = Object.FindObjectsOfType<MonoBehaviour>().FirstOrDefault(m => m.GetType().Name == "Breakaway");
                if (_robot == null) return;
                _fuelLayer = LayerMask.NameToLayer("GamePiece1");
                Tracks.Clear();
                Escapes.Clear();
                Launches.Clear();
                _started = Time.time;
            }
            Transform r = _robot.transform;
            int n = Physics.OverlapSphereNonAlloc(r.position + Vector3.up * 0.3f, 1.4f, Hits, 1 << _fuelLayer);
            var seen = new HashSet<Rigidbody>();
            for (int i = 0; i < n; i++)
            {
                var body = Hits[i].attachedRigidbody;
                if (body == null || !seen.Add(body)) continue;
                Vector3 local = r.InverseTransformPoint(body.position);
                Vector3 localVel = r.InverseTransformDirection(body.velocity - RobotVelocity(r, body.position));
                bool inside = Mathf.Abs(local.x) < HalfX && local.z > MinZ && local.z < MaxZ && local.y > MinY && local.y < MaxY;
                if (!Tracks.TryGetValue(body, out var t)) Tracks[body] = t = new Track { inside = inside, insideSince = Time.time };

                if (inside)
                {
                    if (!t.inside) t.insideSince = Time.time;
                    string touch = Touching(body);
                    t.lastTouch = touch ?? t.lastTouch;
                    // fast relative to the robot: who threw it?
                    if (localVel.magnitude > 2f && Time.time >= t.nextLaunchLog)
                    {
                        t.nextLaunchLog = Time.time + 0.5f;
                        Launches.Add((t.lastTouch ?? "(none)", local, localVel, State()));
                    }
                }
                else if (t.inside && Time.time - t.insideSince > 0.5f)
                {
                    // it had been in the robot for a while and just left: record how
                    string side = local.y >= MaxY ? "top" : local.z >= MaxZ ? "FRONT (intake)" : local.z <= MinZ ? "back" :
                                  local.x >= HalfX ? "right" : local.x <= -HalfX ? "left" : "bottom";
                    Escapes.Add(new Escape { time = Time.time - _started, wasInside = Time.time - t.insideSince, side = side,
                        touch = t.lastTouch, state = State(), local = t.lastLocal, localVel = t.lastLocalVel });
                }
                t.inside = inside;
                t.lastLocal = local;
                t.lastLocalVel = localVel;
            }
        }

        private static Vector3 RobotVelocity(Transform r, Vector3 at)
        {
            var rb = r.GetComponent<Rigidbody>();
            return rb ? rb.GetPointVelocity(at) : Vector3.zero;
        }

        // nearest robot collider within 3 cm of the ball's surface
        private static string Touching(Rigidbody ball)
        {
            var col = ball.GetComponentInChildren<SphereCollider>();
            float radius = col ? col.radius * ball.transform.lossyScale.x : 0.076f;
            int n = Physics.OverlapSphereNonAlloc(ball.position, radius + 0.03f, Hits, ~(1 << _fuelLayer), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (Hits[i].transform.IsChildOf(_robot.transform)) return Path(Hits[i].transform);
            return null;
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && p != _robot.transform; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts.Count > 4 ? parts.Take(2).Concat(new[] { "..." }).Concat(parts.Skip(parts.Count - 2)) : parts);
        }

        private static string State()
        {
            object F(string name) => _robot.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_robot);
            string P(string name)
            {
                for (var type = _robot.GetType(); type != null; type = type.BaseType)
                {
                    var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    if (prop != null) { var v = prop.GetValue(_robot); return v is null ? "null" : (v.GetType().GetMethod("op_Implicit", new[] { v.GetType() })?.Invoke(null, new[] { v }) ?? v).ToString(); }
                }
                return "?";
            }
            var rb = _robot.GetComponent<Rigidbody>();
            Vector3 v = rb ? _robot.transform.InverseTransformDirection(rb.velocity) : Vector3.zero;
            return $"intake={P("IntakeAction")} outtake={P("OuttakeAction")} deployed={F("_intakeDeployed")} agitating={F("_agitating")} " +
                   $"armTarget={F("_intakeTarget")} hopperOut={F("_hopperOut")} robotVel(local)=({v.x:F1},{v.y:F1},{v.z:F1})";
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            int inside = Tracks.Values.Count(t => t.inside);
            sb.AppendLine($"watching {_watching}, {Time.time - _started:F0}s, fuel inside robot now: {inside}, escapes: {Escapes.Count}");
            foreach (var g in Escapes.GroupBy(e => e.side).OrderByDescending(g => g.Count()))
                sb.AppendLine($"  {g.Key}: {g.Count()}");
            foreach (var g in Escapes.GroupBy(e => e.touch ?? "(no robot contact)").OrderByDescending(g => g.Count()).Take(8))
                sb.AppendLine($"  last touched {g.Key}: {g.Count()}");
            foreach (var e in Escapes.Skip(Mathf.Max(0, Escapes.Count - 15)))
                sb.AppendLine($"t={e.time:F1}s {e.side} after {e.wasInside:F1}s in robot, exit local ({e.local.x:F2},{e.local.y:F2},{e.local.z:F2}) " +
                              $"vel ({e.localVel.x:F1},{e.localVel.y:F1},{e.localVel.z:F1}) touched [{e.touch}] | {e.state}");
            return sb.ToString();
        }
    }
}
