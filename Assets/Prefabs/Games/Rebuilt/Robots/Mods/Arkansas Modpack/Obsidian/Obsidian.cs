using System.Collections.Generic;
using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

// FRC 5454 Obsidian: turret shooter with an adjustable hood, pivoting over-the-bumper intake (3937-style deploy,
// entrance blocker, and shooting agitation), spindexer (1706-style reverse hold when not feeding), kicker, and
// elevator climber. DriveController is configured on the prefab root and scaled here while shooting/passing.
public class Obsidian : RebuiltTurretRobot
{
    [Header("Joints")]
    [SerializeField] private GenericTurret turret;
    [SerializeField] private GenericJoint hoodJoint;
    [SerializeField] private GenericJoint intakeJoint;
    [SerializeField] private GenericJoint climberJoint;
    [SerializeField] private Transform intakeRampPivot;

    [Header("Rollers")]
    [SerializeField] private RollerGroup intakeRollers;
    [SerializeField] private RollerGroup spindexerRollers;
    [SerializeField] private RollerGroup kickerRollers;
    [SerializeField] private RollerGroup shooterWheels;

    [Header("PIDs")]
    [SerializeField] private PidConstants turretPid;
    [SerializeField] private PidConstants hoodPid;
    [SerializeField] private PidConstants intakePid;
    [SerializeField] private PidConstants climberPid;

    [Header("Interpolation")]
    [SerializeField] private RebuiltInterpolation flywheelInterpolation;
    [SerializeField] private RebuiltInterpolation hoodInterpolation;

    [Header("Intake Values")]
    [SerializeField] private float intakeDeployedDistance = 0f;
    [SerializeField] private float intakeStowedDistance = -9.5f;
    [SerializeField] private float intakeRampFoldAngle = -35f;
    [SerializeField] private float intakeRampFoldSpeed = 360f;
    [SerializeField] private bool addIntakeEntranceBlocker = true;
    [SerializeField] private Vector3 intakeBlockerCenter = new Vector3(0f, -0.07f, 0.30f);
    [SerializeField] private Vector3 intakeBlockerSize = new Vector3(0.56f, 0.24f, 0.03f);
    [Tooltip("Parts that slide in and out with the intake (e.g. hopper walls). Re-parented to the intake slide at start.")]
    [SerializeField] private Transform[] intakeFollowers;

    [Header("Hopper Agitation")]
    [Tooltip("While shooting, the intake strokes in and out, each in-stroke going deeper than the last, until it " +
             "reaches the stowed position and holds there.")]
    [SerializeField] private bool agitateWhileShooting = true;
    [SerializeField] private float agitateDelay = 1.5f;
    [Tooltip("Inches each in-stroke goes deeper than the previous one (same sign as intakeStowedDistance).")]
    [SerializeField] private float agitateStepDistance = -1.5f;
    [Tooltip("Inches each out-stroke backs off from the in-stroke before it.")]
    [SerializeField] private float agitateBackoffDistance = 1.5f;
    [SerializeField] private float agitateHalfPeriod = 0.4f;
    [SerializeField] private float agitateRollerDivider = 4f;

    [Header("Spindexer Values")]
    [SerializeField] private float spindexerReverseRpm = -1000f;

    [Header("Shooter Values")]
    [SerializeField] private float hoodMaxAngle = 35f;
    [SerializeField] private float hoodStowAngle = 0f;
    [SerializeField] private float manualHoodAngle = 15f;
    [SerializeField] private float manualShooterRpm = 3000f;
    [SerializeField] private float turretTolerance = 5f;
    [Tooltip("Once feeding has started, keep feeding until the turret error exceeds turretTolerance times this, so " +
             "small aim wobble while driving doesn't stop the kicker and reverse the spindexer.")]
    [SerializeField] private float feedHoldToleranceMultiplier = 3f;
    [SerializeField] private float trenchClearDistance = 0.25f;

    [Header("Hood Arc")]
    [Tooltip("Replace the CAD hood colliders with a generated arc. The sim ball is rigid, so the hood has to clear " +
             "the roller's solid core plus a full ball diameter; the CAD hood is sized for a squishing foam ball.")]
    [SerializeField] private bool generateHoodArc = true;
    [Tooltip("Metres from the flywheel axis to the hood's inner face, after the entry flare.")]
    [SerializeField] private float hoodArcRadius = 0.20f;
    [Tooltip("Metres from the flywheel axis to the hood's inner face at the bottom of the entry flare.")]
    [SerializeField] private float hoodArcEntryRadius = 0.24f;
    [Tooltip("Degrees in the wheel plane: 0 points from the axis toward the hood side, +90 points up.")]
    [SerializeField] private float hoodArcStartAngle = -40f;
    [SerializeField] private float hoodArcFlareEndAngle = 0f;
    [SerializeField] private float hoodArcEndAngle = 15f;
    [SerializeField] private int hoodArcSegments = 10;
    [SerializeField] private float hoodArcWidth = 0.17f;
    [SerializeField] private float hoodArcThickness = 0.01f;
    [Tooltip("CAD hood colliders farther than this from the flywheel axis are disabled when the arc is generated.")]
    [SerializeField] private float hoodCadColliderMinRadius = 0.12f;
    [Tooltip("Turret colliders that a ball this size would hit on its way around the arc are disabled " +
             "(the shooter side plates 5105/5106 reach into the ball path).")]
    [SerializeField] private bool clearHoodBallPath = true;
    [SerializeField] private float fuelRadius = 0.075f;
    [Tooltip("Friction on the generated arc. With a frictionless hood the flywheel mostly spins the ball in place " +
             "instead of rolling it out, so the shot leaves slow and with random spin.")]
    [SerializeField, Range(0f, 1.5f)] private float hoodArcFriction = 0.6f;

    [Header("Exit Straightener")]
    [Tooltip("While a shot ball is leaving the hood, remove its velocity along the flywheel axis so it flies in " +
             "the plane the turret is aiming (same approach as Breakaway).")]
    [SerializeField] private bool straightenExit = true;
    [SerializeField] private float exitZoneRadius = 0.35f;   // m from the flywheel shaft
    [SerializeField] private float exitMinSpeed = 2.5f;      // m/s relative to the chassis: only fuel being shot
    [SerializeField] private float exitHoldTime = 0.3f;      // s a shot ball stays straightened
    [SerializeField] private float exitHoldRadius = 1.2f;    // m from the shaft: beyond this the ball is in free flight

    [Header("Kicker Handoff Assist")]
    [Tooltip("Above the top kicker rollers each roller pushes fuel sideways toward its own side, so fuel can drift off " +
             "the turret axis, get pinned under the turret plates and never reach the flywheel. While feeding, fuel " +
             "stalled in that zone is pushed back toward the turret axis, where the flywheel picks it up.")]
    [SerializeField] private bool assistKickerHandoff = true;
    [Tooltip("Zone height range in metres relative to the turret pivot (negative = below it).")]
    [SerializeField] private Vector2 handoffZoneHeight = new Vector2(-0.06f, 0.10f);
    [Tooltip("Metres from the turret axis: fuel farther out than this is not in the handoff zone.")]
    [SerializeField] private float handoffZoneRadius = 0.14f;
    [Tooltip("Fuel faster than this relative to the chassis is being shot and is left alone. Pinned fuel jitters " +
             "against the kicker roller, so this is well above zero.")]
    [SerializeField] private float handoffStallSpeed = 2.0f;
    [Tooltip("Speed relative to the chassis toward the turret axis (and slightly up) that slow fuel in the zone is " +
             "brought up to.")]
    [SerializeField] private float handoffAssistSpeed = 2.0f;

    [Header("Spindexer Feed Assist")]
    [Tooltip("The spindexer wheels' interaction capsules are shorter than they are wide, so each is a sphere that " +
             "domes up above the spindexer floor. Fuel resting near the top of a dome barely moves and never reaches " +
             "the kicker. While feeding, slow fuel in the spindexer bowl is pushed toward the kicker entrance, like a " +
             "floor sloped toward it.")]
    [SerializeField] private bool assistSpindexerFeed = true;
    [Tooltip("Robot-local point inside the bottom of the kicker chute that fuel is pushed toward.")]
    [SerializeField] private Vector3 kickerEntryLocal = new Vector3(-0.10f, 0.20f, -0.06f);
    [Tooltip("Robot-local bounds of the spindexer bowl (min corner).")]
    [SerializeField] private Vector3 spindexerZoneMin = new Vector3(-0.30f, 0.10f, -0.13f);
    [Tooltip("Robot-local bounds of the spindexer bowl (max corner). Kept behind the Cover hump so hopper fuel is " +
             "left to the intake agitation.")]
    [SerializeField] private Vector3 spindexerZoneMax = new Vector3(0.30f, 0.45f, 0.20f);
    [Tooltip("Speed relative to the chassis toward the kicker entrance that slow fuel in the bowl is brought up to.")]
    [SerializeField] private float spindexerAssistSpeed = 1.2f;

    [Header("Debug")]
    [Tooltip("While shooting, log feeder state and every fuel that has sat still inside the robot for over a second, " +
             "with what it is touching.")]
    [SerializeField] private bool debugJams;

    [Header("Climber Values")]
    [SerializeField] private JointAxis climberAxis = JointAxis.Y;
    [SerializeField] private float climberExtendedDistance = 9.5f;
    [SerializeField] private float climberRetractedDistance = 0f;

    [Header("Drive & Shoot On The Move")]
    [SerializeField] private bool improvedSotm = true;
    [SerializeField, Range(0f, 1f)] private float shootingTurnMultiplier = 0.35f;
    [SerializeField, Range(0f, 1f)] private float passingDriveMultiplier = 0.56f;
    [SerializeField, Range(0f, 1f)] private float passingTurnMultiplier = 0.56f;
    [SerializeField] private Vector2[] tofByDistance =
    {
        new Vector2(2.0f, 0.70f), new Vector2(2.5f, 0.80f), new Vector2(3.0f, 0.88f), new Vector2(3.5f, 0.97f),
        new Vector2(4.0f, 1.02f), new Vector2(4.5f, 1.06f), new Vector2(6.0f, 1.20f), new Vector2(8.0f, 1.40f)
    };

    private Vector3 _sotmVelocity;
    private Transform _mapReference;

    private float _intakeTarget;
    private bool _intakeDeployed;
    private float _hoodTarget;
    private bool _deployClimb;
    private float _climberTarget;

    private bool _wasShooting;
    private float _shootStart;
    private bool _agitating;
    private bool _agitateUp;
    private float _agitateSwitch;
    private int _agitateStroke;

    private BoxCollider _intakeEntranceBlocker;
    private Transform _hoodArc;

    private readonly Dictionary<Rigidbody, float> _exitHold = new Dictionary<Rigidbody, float>();
    private readonly List<Rigidbody> _exitExpired = new List<Rigidbody>();
    private readonly List<Rigidbody> _fuelBodies = new List<Rigidbody>();
    private float _nextFuelScan;
    private readonly Dictionary<Rigidbody, (float since, Vector3 pos)> _stillSince = new Dictionary<Rigidbody, (float since, Vector3 pos)>();
    private float _nextJamReport;
    private bool _feedLatched;
    private bool _feeding;
    private readonly Queue<float> _shotTimes = new Queue<float>();
    private readonly Collider[] _jamHits = new Collider[16];

    private bool ShootHeld => OuttakeAction || ManualShoot || AutoPass;
    private bool NearTrench() => turret != null && UnderTrench(turret.transform, trenchClearDistance);

    protected override void Init()
    {
        base.Init();

        if (turret != null) turret.SetPid(turretPid);
        if (hoodJoint != null) hoodJoint.SetPid(hoodPid);
        if (intakeJoint != null) intakeJoint.SetPid(intakePid);
        if (climberJoint != null) climberJoint.SetPid(climberPid);

        if (flywheelInterpolation != null)
        {
            flywheelInterpolation.Initialize(this);
            _mapReference = flywheelInterpolation.transformOverride;
        }
        if (hoodInterpolation != null) hoodInterpolation.Initialize(this);

        _intakeDeployed = false;
        _intakeTarget = intakeStowedDistance;
        _hoodTarget = hoodStowAngle;
        _deployClimb = false;
        _climberTarget = climberRetractedDistance;

        if (intakeJoint != null && intakeFollowers != null)
        {
            foreach (var part in intakeFollowers)
                if (part != null && part != intakeJoint.transform && !intakeJoint.transform.IsChildOf(part))
                    part.SetParent(intakeJoint.transform, true);
        }

        if (addIntakeEntranceBlocker) AddIntakeEntranceBlocker();

        Scheduler.AddSubsystems(IntakeArm, Climber);
        Scheduler.AddProtectedSystem(UpdateJoints, UpdateDriveSpeed);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    protected override void LateInit()
    {
        base.LateInit();
        if (generateHoodArc) BuildHoodArc();
        IgnoreSelfCollisions();
        if (hoodJoint != null)
        {
            var mat = new PhysicMaterial("Obsidian Hood")
            {
                dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum
            };
            foreach (var c in hoodJoint.GetComponentsInChildren<Collider>(true)) c.sharedMaterial = mat;
        }
        if (_hoodArc != null)
        {
            // Maximum combine so the fuel's own low friction (0.1, Average) doesn't water it down.
            var arcMat = new PhysicMaterial("Obsidian Hood Arc")
            {
                dynamicFriction = hoodArcFriction, staticFriction = hoodArcFriction, bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Maximum, bounceCombine = PhysicMaterialCombine.Minimum
            };
            foreach (var c in _hoodArc.GetComponentsInChildren<Collider>(true)) c.sharedMaterial = arcMat;
        }
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        StraightenExitingFuel();
        AssistKickerHandoff();
        AssistSpindexerFeed();
    }

    private void AssistSpindexerFeed()
    {
        if (!assistSpindexerFeed || !_feeding || rb == null) return;
        ScanFuel();

        Vector3 entry = transform.TransformPoint(kickerEntryLocal);
        Vector3 chassisVel = rb.GetPointVelocity(entry);

        foreach (var body in _fuelBodies)
        {
            if (body == null || body.isKinematic || _exitHold.ContainsKey(body)) continue;
            Vector3 lp = transform.InverseTransformPoint(body.position);
            if (lp.x < spindexerZoneMin.x || lp.y < spindexerZoneMin.y || lp.z < spindexerZoneMin.z ||
                lp.x > spindexerZoneMax.x || lp.y > spindexerZoneMax.y || lp.z > spindexerZoneMax.z) continue;
            // Already in the kicker chute (left of centre, behind the front plate): the kicker rollers have it.
            if (lp.x < 0f && lp.z < 0f) continue;

            Vector3 rel = body.velocity - chassisVel;
            if (rel.magnitude > handoffStallSpeed) continue;
            Vector3 toEntry = entry - body.position;
            if (toEntry.sqrMagnitude < 1e-6f) continue;
            Vector3 dir = toEntry.normalized;
            float progress = Vector3.Dot(rel, dir);
            if (progress >= spindexerAssistSpeed * 0.6f) continue;
            Vector3 side = rel - dir * progress;
            PushFuel(body, chassisVel + side * 0.5f + dir * spindexerAssistSpeed);
        }
    }

    // Change a fuel's velocity and give the robot the equal and opposite impulse. Without the reaction the assists
    // inject momentum: fuel driven into the robot's own walls pushes the robot, which then creeps while shooting.
    private void PushFuel(Rigidbody body, Vector3 newVelocity)
    {
        Vector3 impulse = body.mass * (newVelocity - body.velocity);
        body.velocity = newVelocity;
        if (rb != null) rb.AddForceAtPosition(-impulse, body.position, ForceMode.Impulse);
    }

    private void AssistKickerHandoff()
    {
        if (!assistKickerHandoff || !_feeding || turret == null || rb == null) return;
        ScanFuel();

        Transform pivot = turret.transform;
        Vector3 axisPoint = pivot.position;
        Vector3 up = transform.up;
        Vector3 chassisVel = rb.GetPointVelocity(axisPoint);

        foreach (var body in _fuelBodies)
        {
            if (body == null || body.isKinematic || _exitHold.ContainsKey(body)) continue;
            Vector3 off = body.position - axisPoint;
            float height = Vector3.Dot(off, up);
            if (height < handoffZoneHeight.x || height > handoffZoneHeight.y) continue;
            Vector3 radial = off - up * height;
            float r = radial.magnitude;
            if (r > handoffZoneRadius || r < 0.01f) continue;
            Vector3 rel = body.velocity - chassisVel;
            if (rel.magnitude > handoffStallSpeed) continue;

            // Judge by progress toward the axis, not total speed: fuel pinned between a kicker roller and the turret
            // plates jitters but goes nowhere. Keep half its sideways motion so it can still work free.
            Vector3 dir = (-radial / r + up * 0.5f).normalized;
            float progress = Vector3.Dot(rel, dir);
            if (progress >= handoffAssistSpeed * 0.6f) continue;
            Vector3 side = rel - dir * progress;
            PushFuel(body, chassisVel + side * 0.5f + dir * handoffAssistSpeed);
        }
    }

    private void ScanFuel()
    {
        if (Time.time < _nextFuelScan) return;
        _nextFuelScan = Time.time + 0.1f;
        _fuelBodies.Clear();
        foreach (var gp in FindObjectsByType<Games.Rebuilt.GamePieceSystem.RebuiltGamePieceController>(FindObjectsSortMode.None))
        {
            var body = gp.GetComponent<Rigidbody>();
            if (body == null) body = gp.GetComponentInChildren<Rigidbody>();
            if (body != null) _fuelBodies.Add(body);
        }
    }

    private void StraightenExitingFuel()
    {
        if (!straightenExit || hoodJoint == null) return;
        ScanFuel();

        Transform hood = hoodJoint.transform;
        Vector3 shaft = hood.position;
        Vector3 axis = hood.right;
        Vector3 chassisVel = rb != null ? rb.GetPointVelocity(shaft) : Vector3.zero;
        float zone2 = exitZoneRadius * exitZoneRadius;

        foreach (var body in _fuelBodies)
        {
            if (body == null || body.isKinematic || _exitHold.ContainsKey(body)) continue;
            Vector3 off = body.position - shaft;
            // above the flywheel shaft only, so fuel speeding through the kicker below is neither bent nor counted
            if (off.sqrMagnitude > zone2 || Vector3.Dot(off, transform.up) <= 0f) continue;
            Vector3 rel = body.velocity - chassisVel;
            if (rel.magnitude < exitMinSpeed || Vector3.Dot(rel, transform.up) <= 0f) continue;
            _exitHold[body] = Time.time + exitHoldTime;
            _shotTimes.Enqueue(Time.time);
        }
        while (_shotTimes.Count > 0 && Time.time - _shotTimes.Peek() > 1f) _shotTimes.Dequeue();

        _exitExpired.Clear();
        foreach (var kv in _exitHold)
        {
            Rigidbody body = kv.Key;
            if (body == null || Time.time > kv.Value || (body.position - shaft).sqrMagnitude > exitHoldRadius * exitHoldRadius)
            {
                _exitExpired.Add(body);
                continue;
            }
            if (body.isKinematic) continue;
            Vector3 rel = body.velocity - chassisVel;
            float side = Vector3.Dot(rel, axis);
            if (Mathf.Abs(side) < 1e-4f) continue;
            body.velocity = chassisVel + rel - axis * side;
        }
        foreach (var body in _exitExpired) _exitHold.Remove(body);
        if (debugJams) ReportJams(chassisVel);
    }

    private void ReportJams(Vector3 chassisVel)
    {
        if (!ShootHeld) { _stillSince.Clear(); return; }
        var stuck = new List<string>();
        foreach (var body in _fuelBodies)
        {
            if (body == null) continue;
            Vector3 lp = transform.InverseTransformPoint(body.position);
            if (Mathf.Abs(lp.x) > 0.45f || Mathf.Abs(lp.z) > 0.75f || lp.y < -0.05f || lp.y > 0.9f) { _stillSince.Remove(body); continue; }
            // Stuck = hasn't left a 3 cm ball in a second, so fuel jittering in place against a roller still counts.
            if (!_stillSince.TryGetValue(body, out var still) || (lp - still.pos).sqrMagnitude > 0.03f * 0.03f)
            {
                _stillSince[body] = (Time.time, lp);
                continue;
            }
            if (Time.time - still.since < 1f) continue;

            int n = Physics.OverlapSphereNonAlloc(body.position, fuelRadius + 0.01f, _jamHits, ~0, QueryTriggerInteraction.Ignore);
            var touching = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Collider c = _jamHits[i];
                if (c.attachedRigidbody == body || c.GetComponentInParent<Games.Rebuilt.GamePieceSystem.RebuiltGamePieceController>() != null) continue;
                var roller = c.GetComponentInParent<InteractionRoller>();
                string owner = roller != null ? roller.name : (c.transform.parent != null ? c.transform.parent.name + "/" : "") + c.name;
                if (!touching.Contains(owner)) touching.Add(owner);
            }
            string turretRel = "";
            if (turret != null)
            {
                Vector3 off = body.position - turret.transform.position;
                float h = Vector3.Dot(off, transform.up);
                turretRel = $" turret h {h:F3} r {(off - transform.up * h).magnitude:F3}";
            }
            stuck.Add($"({lp.x:F3}, {lp.y:F3}, {lp.z:F3}) speed {(body.velocity - chassisVel).magnitude:F2}{turretRel} " +
                      $"touching [{string.Join(", ", touching)}]");
        }
        if (Time.time < _nextJamReport) return;
        _nextJamReport = Time.time + 1f;

        string turretState = "no turret";
        if (turret != null)
        {
            float err = turret.GetError();
            bool wrapping = turret.IsWrapping();
            turretState = $"turretErr {err:F1} deg, wrapping {wrapping}";
        }
        float flywheelRpm = 0f;
        if (shooterWheels != null) flywheelRpm = shooterWheels.GetAverageTargetRpm();
        Debug.Log($"[Obsidian] shooting: {_shotTimes.Count} BPS, aligned {Aligned()}, {turretState}, nearTrench {NearTrench()}, " +
                  $"flywheel {flywheelRpm:F0}, " +
                  $"stuck {stuck.Count}" + (stuck.Count > 0 ? ":\n  " + string.Join("\n  ", stuck) : ""));
    }

    // The hood joint's origin sits on the flywheel shaft and its local X is the shaft axis, so the arc is built
    // around that and parented to the hood so it follows the hood angle.
    private void BuildHoodArc()
    {
        if (hoodJoint == null || hoodArcSegments < 1) return;
        Transform hood = hoodJoint.transform;
        if (hood.Find("Generated Hood Arc") != null) return;

        Vector3 center = hood.position;
        Vector3 axis = hood.right;

        // Hood side = where the CAD hood colliders sit around the shaft; those are then replaced by the arc.
        Vector3 sideSum = Vector3.zero;
        foreach (var c in hood.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            Vector3 radial = Vector3.ProjectOnPlane(c.bounds.center - center, axis);
            if (radial.magnitude < hoodCadColliderMinRadius) continue;
            sideSum += radial;
            c.enabled = false;
        }
        if (sideSum.sqrMagnitude < 1e-8f) return;

        Vector3 outDir = sideSum.normalized;
        Vector3 upDir = Vector3.Cross(axis, outDir);
        if (Vector3.Dot(upDir, transform.up) < 0f) upDir = -upDir;

        var arc = new GameObject("Generated Hood Arc");
        arc.layer = hood.gameObject.layer;
        arc.transform.SetParent(hood, false);
        _hoodArc = arc.transform;

        float RadiusAt(float deg)
        {
            if (deg >= hoodArcFlareEndAngle || hoodArcFlareEndAngle <= hoodArcStartAngle) return hoodArcRadius;
            float t = Mathf.InverseLerp(hoodArcStartAngle, hoodArcFlareEndAngle, deg);
            return Mathf.Lerp(hoodArcEntryRadius, hoodArcRadius, t);
        }
        Vector3 PointAt(float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            return center + RadiusAt(deg) * (Mathf.Cos(rad) * outDir + Mathf.Sin(rad) * upDir);
        }

        float step = (hoodArcEndAngle - hoodArcStartAngle) / hoodArcSegments;
        for (int i = 0; i < hoodArcSegments; i++)
        {
            Vector3 a = PointAt(hoodArcStartAngle + step * i);
            Vector3 b = PointAt(hoodArcStartAngle + step * (i + 1));
            Vector3 chord = b - a;
            Vector3 mid = (a + b) * 0.5f;
            Vector3 inward = Vector3.ProjectOnPlane(center - mid, axis).normalized;

            var seg = new GameObject("Hood Arc " + i);
            seg.layer = arc.layer;
            seg.transform.SetParent(arc.transform, true);
            seg.transform.SetPositionAndRotation(mid - inward * (hoodArcThickness * 0.5f),
                Quaternion.LookRotation(chord.normalized, inward));
            var box = seg.AddComponent<BoxCollider>();
            box.size = new Vector3(hoodArcWidth, hoodArcThickness, chord.magnitude * 1.05f);
        }

        if (clearHoodBallPath) ClearHoodBallPath(arc.transform, center, outDir, upDir, RadiusAt);
    }

    // Sweep a fuel-sized sphere around the arc (riding the hood face, and a little inside it) and disable any
    // turret-mounted collider it touches, other than the arc itself and the rollers.
    private void ClearHoodBallPath(Transform arc, Vector3 center, Vector3 outDir, Vector3 upDir,
        System.Func<float, float> radiusAt)
    {
        Transform scope = turret != null ? turret.transform : hoodJoint.transform.parent;
        if (scope == null) return;
        Physics.SyncTransforms();

        var hits = new Collider[32];
        var cleared = new List<string>();
        float probe = fuelRadius - 0.002f;
        for (float deg = hoodArcStartAngle; deg <= hoodArcEndAngle + 0.01f; deg += 2.5f)
        {
            float rad = deg * Mathf.Deg2Rad;
            Vector3 dir = Mathf.Cos(rad) * outDir + Mathf.Sin(rad) * upDir;
            foreach (float inset in new[] { 0f, 0.015f })
            {
                Vector3 p = center + dir * (radiusAt(deg) - fuelRadius - inset);
                int n = Physics.OverlapSphereNonAlloc(p, probe, hits, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    Collider c = hits[i];
                    if (!c.enabled || !c.transform.IsChildOf(scope) || c.transform.IsChildOf(arc)) continue;
                    if (c.GetComponentInParent<InteractionRoller>() != null) continue;
                    c.enabled = false;
                    cleared.Add(c.transform.parent != null ? c.transform.parent.name + "/" + c.name : c.name);
                }
            }
        }
        if (cleared.Count > 0) Debug.Log($"[Obsidian] Cleared {cleared.Count} collider(s) from the hood ball path: {string.Join(", ", cleared)}");
    }

    private void AddIntakeEntranceBlocker()
    {
        if (intakeJoint == null) return;
        Transform existing = intakeJoint.transform.Find("IntakeEntranceBlocker");
        if (existing != null)
        {
            _intakeEntranceBlocker = existing.GetComponent<BoxCollider>();
            if (_intakeEntranceBlocker != null) _intakeEntranceBlocker.enabled = false;
            return;
        }

        var wall = new GameObject("IntakeEntranceBlocker");
        wall.transform.SetParent(intakeJoint.transform, false);
        wall.transform.localPosition = intakeBlockerCenter;
        wall.transform.localRotation = Quaternion.identity;
        _intakeEntranceBlocker = wall.AddComponent<BoxCollider>();
        _intakeEntranceBlocker.size = intakeBlockerSize;
        _intakeEntranceBlocker.enabled = false;
    }

    private void IgnoreSelfCollisions()
    {
        var all = GetComponentsInChildren<Collider>(true);
        Transform[] moving =
        {
            turret ? turret.transform : null,
            hoodJoint ? hoodJoint.transform : null,
            intakeJoint ? intakeJoint.transform : null,
            climberJoint ? climberJoint.transform : null
        };
        foreach (var a in all)
        {
            Transform part = System.Array.Find(moving, m => m != null && a.transform.IsChildOf(m));
            if (part == null) continue;
            foreach (var b in all)
            {
                if (a == b || b.transform.IsChildOf(part)) continue;
                Physics.IgnoreCollision(a, b, true);
            }
        }
    }

    private void UpdatePids()
    {
        if (turret != null) turret.UpdatePid(turretPid);
        if (hoodJoint != null) hoodJoint.UpdatePid(hoodPid);
        if (intakeJoint != null) intakeJoint.UpdatePid(intakePid);
        if (climberJoint != null) climberJoint.UpdatePid(climberPid);
    }

    private void UpdateDriveSpeed()
    {
        bool shoot = ShootHeld;
        bool isShooting = shoot && InAllianceZone() && !AutoPass;
        bool isPassing = shoot && (!InAllianceZone() || AutoPass);

        float driveMp = 1f;
        float turnMp = 1f;

        if (isShooting)
        {
            driveMp = swmSettings.shootingDriveMultiplier;
            turnMp = shootingTurnMultiplier;
        }
        else if (isPassing)
        {
            driveMp = passingDriveMultiplier;
            turnMp = passingTurnMultiplier;
        }

        DriveController.SetDriveMp(driveMp);
        DriveController.SetTurnMp(turnMp);
    }

    private void IntakeArm()
    {
        if (IntakeAction) _intakeDeployed = true;
        if (StowAction) _intakeDeployed = false;
        if (_intakeEntranceBlocker != null) _intakeEntranceBlocker.enabled = _intakeDeployed && !IntakeAction;

        bool shoot = ShootHeld;
        if (shoot && !_wasShooting) _shootStart = Time.time;
        _wasShooting = shoot;

        _agitating = agitateWhileShooting && shoot && !IntakeAction && _intakeDeployed &&
                     Time.time - _shootStart >= agitateDelay && !NearTrench();
        if (_agitating)
        {
            if (Time.time >= _agitateSwitch)
            {
                _agitateUp = !_agitateUp;
                if (_agitateUp) _agitateStroke++;
                _agitateSwitch = Time.time + agitateHalfPeriod;
            }

            // In-stroke k reaches k * step past deployed; the out-stroke backs off from it. Both are clamped
            // between deployed and stowed, so once an in-stroke reaches stowed the arm stays there.
            float lo = Mathf.Min(intakeDeployedDistance, intakeStowedDistance);
            float hi = Mathf.Max(intakeDeployedDistance, intakeStowedDistance);
            float inStroke = Mathf.Clamp(intakeDeployedDistance + agitateStepDistance * _agitateStroke, lo, hi);
            bool fullyIn = Mathf.Approximately(inStroke, intakeStowedDistance);
            float backoffDir = Mathf.Sign(intakeDeployedDistance - intakeStowedDistance);
            float outStroke = Mathf.Clamp(inStroke + backoffDir * Mathf.Abs(agitateBackoffDistance), lo, hi);
            _intakeTarget = _agitateUp || fullyIn ? inStroke : outStroke;
        }
        else
        {
            _agitateUp = false;
            _agitateStroke = 0;
            _intakeTarget = _intakeDeployed ? intakeDeployedDistance : intakeStowedDistance;
        }
    }

    private void Climber()
    {
        if (ClimbAction.WasTriggered() && BaseGameManager.Instance.RobotState != RobotState.Disabled)
        {
            _deployClimb = !_deployClimb;
        }

        _climberTarget = _deployClimb ? climberExtendedDistance : climberRetractedDistance;
    }

    private void UpdateJoints()
    {
        if (turret != null) turret.Align();
        if (hoodJoint != null) hoodJoint.SetTargetAngle(_hoodTarget).withAxis(JointAxis.X).useAutomaticStartingOffset();
        if (intakeJoint != null) intakeJoint.SetLinearTarget(_intakeTarget).withAxis(JointAxis.Z).useAutomaticStartingOffset();
        if (intakeRampPivot != null)
        {
            Quaternion targetRot = Quaternion.Euler(_intakeDeployed ? 0f : intakeRampFoldAngle, 0f, 0f);
            intakeRampPivot.localRotation = Quaternion.RotateTowards(intakeRampPivot.localRotation, targetRot, intakeRampFoldSpeed * Time.fixedDeltaTime);
        }
        if (climberJoint != null) climberJoint.SetLinearTarget(_climberTarget).withAxis(climberAxis).useAutomaticStartingOffset();
    }

    protected override void HandleSwm()
    {
        if (rb == null || swmRef == null || swmRef.parent == null) return;
        if (!improvedSotm || turret == null)
        {
            if (flywheelInterpolation != null) flywheelInterpolation.transformOverride = _mapReference;
            if (hoodInterpolation != null) hoodInterpolation.transformOverride = _mapReference;
            base.HandleSwm();
            return;
        }

        if (flywheelInterpolation != null) flywheelInterpolation.transformOverride = swmRef;
        if (hoodInterpolation != null) hoodInterpolation.transformOverride = swmRef;

        Vector3 anchor = swmRef.parent.position;
        Vector3 v = rb.GetPointVelocity(anchor);
        v.y = 0f;
        _sotmVelocity = Vector3.Lerp(_sotmVelocity, v, swmSettings.velocityDampeningValue);

        Vector3 target = (Vector3)turret.GetCurrentTarget();
        if (target == Vector3.zero) { swmRef.position = anchor; return; }

        Vector3 offset = Vector3.zero;
        for (int i = 0; i < 4; i++)
        {
            Vector3 toTarget = target - (anchor + offset);
            toTarget.y = 0f;
            offset = _sotmVelocity * TimeOfFlight(toTarget.magnitude);
        }
        swmRef.position = anchor + offset;
    }

    private float TimeOfFlight(float horizontalDistance)
    {
        if (tofByDistance == null || tofByDistance.Length == 0) return swmSettings.timeOfFlight;
        if (horizontalDistance <= tofByDistance[0].x) return tofByDistance[0].y;
        for (int i = 1; i < tofByDistance.Length; i++)
        {
            if (horizontalDistance <= tofByDistance[i].x)
            {
                Vector2 a = tofByDistance[i - 1], b = tofByDistance[i];
                return Mathf.Lerp(a.y, b.y, (horizontalDistance - a.x) / Mathf.Max(1e-4f, b.x - a.x));
            }
        }
        return tofByDistance[tofByDistance.Length - 1].y;
    }

    protected override void AllianceZone()
    {
        if (turret != null) turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
        if (flywheelInterpolation != null) flywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
        if (hoodInterpolation != null) hoodInterpolation.SetDefaultMap(DefaultMap.Hub);
    }

    protected override void NeutralZone()
    {
        if (turret != null) turret.SetPassMapClosest();
        if (flywheelInterpolation != null) flywheelInterpolation.SetPassMapClosest();
        if (hoodInterpolation != null) hoodInterpolation.SetPassMapClosest();
    }

    protected override void ShooterHandler()
    {
        _feeding = false;
        if (ShootHeld)
        {
            if (IntakeAction) IntakingAndShooting();
            else Shooting();
            _feeding = Aligned();
            if (_feeding) Feed();
        }
        else if (IntakeAction) Intaking();
        else Idling();
    }

    protected override void Intaking()
    {
        HoldFeeders();
        if (shooterWheels != null) shooterWheels.SetRpm(0f);
        _hoodTarget = hoodStowAngle;
    }

    protected override void IntakingAndShooting()
    {
        SpinUpAndHold();
    }

    protected override void Shooting()
    {
        if (intakeRollers != null)
        {
            if (_agitating) intakeRollers.DivideSpeed(agitateRollerDivider);
            else intakeRollers.Stop();
        }
        SpinUpAndHold();
    }

    protected override void Feed()
    {
        // Aligned(): spindexerRollers and kickerRollers run at their Inspector-configured RPMs.
    }

    protected override void Idling()
    {
        if (intakeRollers != null) intakeRollers.Stop();
        HoldFeeders();
        if (shooterWheels != null) shooterWheels.SetRpm(0f);
        _hoodTarget = hoodStowAngle;
    }

    protected override bool Aligned()
    {
        if (!ShootHeld) _feedLatched = false;
        if (NearTrench()) return _feedLatched = false;
        if (ManualShoot) return true;
        if (turret == null || turret.IsWrapping()) return _feedLatched = false;
        float error = Mathf.Abs(turret.GetError());
        bool wheelsReady = shooterWheels == null || shooterWheels.AtSpeed();
        float tolerance = _feedLatched ? turretTolerance * Mathf.Max(1f, feedHoldToleranceMultiplier) : turretTolerance;
        _feedLatched = error < tolerance && (wheelsReady || _feedLatched);
        return _feedLatched;
    }

    private void SpinUpAndHold()
    {
        float rpm = ManualShoot || flywheelInterpolation == null ? manualShooterRpm : (float)flywheelInterpolation.Interpolate();
        float hood = ManualShoot || hoodInterpolation == null ? manualHoodAngle : (float)hoodInterpolation.Interpolate();
        if (shooterWheels != null) shooterWheels.SetRpm(rpm);
        _hoodTarget = NearTrench() ? hoodStowAngle : Mathf.Clamp(hood, 0f, hoodMaxAngle);

        if (!Aligned())
        {
            HoldFeeders();
        }
    }

    private void HoldFeeders()
    {
        if (kickerRollers != null) kickerRollers.Stop();
        if (spindexerRollers != null) spindexerRollers.OverrideRpm(spindexerReverseRpm);
    }
}
