using System.Collections.Generic;
using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

// FRC 3937 Breakaway: turret shooter with an adjustable hood, pivoting over-the-bumper intake, roller-bed hopper with
// a telescoping front extension, feeder tower. No climber in the CAD. Built on the docs' Rebuilt turret template
// (RebuiltTurretRobot): zone dispatch (AllianceZone / NeutralZone) and shooter dispatch (Intaking / Shooting / ... / Feed)
// come from the base.
//
// Shooting is pure roller physics: the shooter wheels spin at the rpm from the interpolation map and the hood follows
// its map; once the turret is on target and the wheels are at speed, the feeder, kicker and belts push fuel into them.
// The wheels set the shot speed. The one scripted touch is the exit straightener: fuel reaches the turret from the
// chassis-fixed kicker belts still carrying sideways momentum (it changes with the turret's angle to the chassis), so
// as fuel leaves the hood its sideways velocity in the turret's frame is removed. Speed and pitch are left to physics.
public class Breakaway : RebuiltTurretRobot
{
    [Header("Joints")]
    [SerializeField] private GenericTurret turret;        // "Turret": GenericJoint (Y axis) + GenericTurret
    [SerializeField] private GenericJoint hoodJoint;      // "Hood" (on the turret): GenericJoint (X = SH-16 pivot shaft), CAD pose = 0
    [SerializeField] private GenericJoint intakeJoint;    // "Intake Arm": GenericJoint (X axis), CAD pose = deployed
    [SerializeField] private GenericJoint hopperJoint;    // "Hopper Extension": linear GenericJoint (Z = forward), starts retracted

    [Header("Rollers")]
    // Roller groups (made by Guess Interaction Roller; each roller's running rpm and motor are set in the Inspector)
    [SerializeField] private RollerGroup intakeRollers;   // intake tubes on the arm
    [SerializeField] private RollerGroup hopperRollers;   // roller-bed floor rollers
    [SerializeField] private RollerGroup feederRollers;   // feeder tower rollers + flex wheels
    [SerializeField] private RollerGroup kickerRollers;   // flex wheels feeding the shooter wheels
    [SerializeField] private RollerGroup kickerBelts;     // kicker belts: run only while shooting (same feed gate)
    [SerializeField] private RollerGroup shooterWheels;   // urethane shooter wheels, brass flywheel, compliant wheels

    [Header("PIDs")]
    [SerializeField] private PidConstants turretPid;
    [SerializeField] private PidConstants hoodPid;
    [SerializeField] private PidConstants intakePid;
    [SerializeField] private PidConstants hopperPid;

    [Header("Interpolation")]
    [SerializeField] private RebuiltInterpolation flywheelInterpolation;   // distance (m) -> shooter rpm
    [SerializeField] private RebuiltInterpolation hoodInterpolation;       // distance (m) -> hood angle (deg)

    [Header("Mechanism Values")]
    [SerializeField] private float intakeDeployedAngle = 0f;      // the CAD pose is deployed
    [SerializeField] private float intakeStowedAngle = -120f;     // folded back over the hopper: 20.4" tall (at -100 the arm
                                                                  // stood 21.6", ~1 cm under the trench bar, and wedged)
    [SerializeField] private float hopperExtendedDistance = 12f;  // inches the extension slides out (set from the CAD)
    [SerializeField] private float hoodMaxAngle = 40f;            // degrees from the CAD pose
    [SerializeField] private float hoodStowAngle = -25f;          // stowed hood angle when not shooting (to clear the trench)
    [SerializeField] private float manualHoodAngle = 15f;
    [SerializeField] private float manualShooterRpm = 3000f;
    [SerializeField] private float turretTolerance = 1.5f;        // degrees
    [SerializeField] private float trenchClearDistance = 0.25f;   // m before the trench bar: hood forced down, no firing

    [Header("Hopper Agitation")]
    // While shooting (and not intaking) for agitateDelay seconds, a deployed intake arm swings between deployed and
    // agitateAngle, pushing fuel in the hopper back toward the feeder; the intake rollers turn slowly inward meanwhile.
    [SerializeField] private bool agitateWhileShooting = true;
    [SerializeField] private float agitateDelay = 1.5f;           // s after shooting starts
    [SerializeField] private float agitateAngle = -40f;           // arm angle at the top of each swing (stowed side)
    [SerializeField] private float agitateHalfPeriod = 0.4f;      // s per swing direction
    [SerializeField] private float agitateRollerDivider = 4f;     // intake rollers at 1/4 speed while agitating
    // When deployed and not intaking, an invisible IntakeEntranceBlocker collider closes the intake mouth so fuel
    // cannot roll back out (or enter) while the intake rollers stay stopped (rollers only run while intaking or agitating).

    [Header("Fuel Backstop")]
    // Fuel rising through the turret centre could be flicked out the back by the flywheel's lower edge; this
    // frictionless wall closes that gap. Turret-local metres.
    [SerializeField] private bool addFuelBackstop = true;
    [SerializeField] private Vector3 backstopCenter = new Vector3(0.087f, 0.045f, 0f);
    [SerializeField] private Vector3 backstopSize = new Vector3(0.01f, 0.09f, 0.16f);

    [Header("Turret Centring Guides")]
    // Two frictionless walls on the turret either side of the shooter's centreline so fuel arriving from the chassis
    // is centred on the main wheel, and frictionless hood plates so the hood does not drag fuel sideways.
    [SerializeField] private bool addCentringGuides = true;
    [SerializeField] private float guideHalfGap = 0.098f;       // turret-local Z of each wall
    [SerializeField] private float guideCenterY = 0.08f;
    [SerializeField] private Vector3 guideSize = new Vector3(0.15f, 0.19f, 0.01f);
    [SerializeField] private bool frictionlessHoodPlates = true;

    [Header("Shoot On The Move")]
    [SerializeField] private bool improvedSotm = true;
    [SerializeField, Range(0f, 1f)] private float shootingTurnMultiplier = 0.35f;
    [SerializeField, Range(0f, 1f)] private float passingDriveMultiplier = 0.56f;
    [SerializeField, Range(0f, 1f)] private float passingTurnMultiplier = 0.56f;
    // horizontal distance from the turret to the target (m) -> ball flight time to the rim (s), measured with the shot mapper
    [SerializeField] private Vector2[] tofByDistance =
    {
        new Vector2(2.0f, 0.70f), new Vector2(2.5f, 0.80f), new Vector2(3.0f, 0.88f), new Vector2(3.5f, 0.97f),
        new Vector2(4.0f, 1.02f), new Vector2(4.5f, 1.06f), new Vector2(6.0f, 1.20f), new Vector2(8.0f, 1.40f)
    };
    private Vector3 _sotmVelocity;
    private Transform _mapReference;

    [Header("Exit Straightener")]
    // Turret-local zone above the shooter: while fuel is inside it, its velocity component along the turret's local Z
    // (sideways to the shot), measured relative to the chassis, is removed. Turret-local metres.
    [SerializeField] private bool straightenExit = true;
    [SerializeField] private float exitZoneMinHeight = 0.26f;   // above the turret pivot
    [SerializeField] private float exitZoneMaxHeight = 0.60f;
    [SerializeField] private float exitZoneRadius = 0.35f;      // horizontal, from the turret axis
    [SerializeField] private float exitMinSpeed = 2.5f;         // m/s relative to the chassis: only fuel being shot
    [SerializeField] private float exitHoldTime = 0.3f;         // s a shot ball stays straightened after entering the zone
                                                                // (it still brushes the hood plates and wheels above the zone)
    [SerializeField] private float exitHoldRadius = 1.2f;       // m from the turret: beyond this the ball is in free flight
    private readonly Dictionary<Rigidbody, float> _exitHold = new Dictionary<Rigidbody, float>();
    private readonly List<Rigidbody> _exitExpired = new List<Rigidbody>();
    private readonly List<Rigidbody> _fuelBodies = new List<Rigidbody>();
    private float _nextFuelScan;
    private readonly HashSet<Rigidbody> _exitDone = new HashSet<Rigidbody>();
    private readonly Dictionary<Rigidbody, float> _stillSince = new Dictionary<Rigidbody, float>();
    private float _nextJamReport;
    private float _lastExitTime;

    private float _intakeTarget;
    private bool _intakeDeployed;
    private float _hoodTarget;
    private bool _hopperOut;
    private bool _hopperLocked;
    private bool _passing;
    private bool _wasShooting;
    private float _shootStart;
    private bool _agitating;
    private bool _agitateUp;
    private float _agitateSwitch;

    private BoxCollider _intakeEntranceBlocker;

    private bool ShootHeld =>OuttakeAction || ManualShoot || AutoPass;
    private bool NearTrench() => turret != null && UnderTrench(turret.transform, trenchClearDistance);


    protected override void Init()
    {
        // Registers HandleSwm, zone dispatch, and shooter-state dispatch exactly once.
        base.Init();
        turret.SetPid(turretPid);
        hoodJoint.SetPid(hoodPid);
        intakeJoint.SetPid(intakePid);
        hopperJoint.SetPid(hopperPid);
        flywheelInterpolation.Initialize(this);
        hoodInterpolation.Initialize(this);
        _mapReference = flywheelInterpolation.transformOverride;
        _intakeDeployed = false;
        _intakeTarget = intakeStowedAngle;
        _hoodTarget = hoodStowAngle;

        _hopperOut = false;
        _hopperLocked = false;
        AddFuelBackstop();
        AddIntakeEntranceBlocker();

        Scheduler.AddSubsystems(IntakeArm);
        Scheduler.AddProtectedSystem(UpdateJoints, UpdateDriveSpeed);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    protected override void LateInit()
    {
        base.LateInit();
        // Run after every Start() so InteractionRoller's runtime Wheel_InternalCore colliders are ignored too.
        IgnoreSelfCollisions();
        AddCentringGuides();
    }

    private static PhysicMaterial Frictionless(string name) => new PhysicMaterial(name)
    {
        dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
        frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum
    };

    private void AddCentringGuides()
    {
        if (turret == null) return;
        var robotCols = GetComponentsInChildren<Collider>(true);
        if (frictionlessHoodPlates && hoodJoint != null)
        {
            var mat = Frictionless("Breakaway Hood Plate");
            foreach (var c in hoodJoint.GetComponentsInChildren<Collider>(true)) c.sharedMaterial = mat;
        }
        if (!addCentringGuides) return;
        var guideMat = Frictionless("Breakaway Centring Guide");
        var existing = turret.GetComponentInChildren<Collider>(true);
        int layer = existing != null ? existing.gameObject.layer : turret.gameObject.layer;
        foreach (float side in new[] { 1f, -1f })
        {
            var go = new GameObject(side > 0f ? "Centring Guide L" : "Centring Guide R");
            go.layer = layer;
            go.transform.SetParent(turret.transform, false);
            go.transform.localPosition = new Vector3(0.015f, guideCenterY, side * guideHalfGap);
            go.transform.localRotation = Quaternion.identity;
            var box = go.AddComponent<BoxCollider>();
            box.size = guideSize;
            box.sharedMaterial = guideMat;
            // fuel only: never push on the robot's own parts
            foreach (var c in robotCols) if (c != null) Physics.IgnoreCollision(box, c, true);
        }
    }

    private void AddFuelBackstop()
    {
        if (!addFuelBackstop || turret == null) return;
        var wall = new GameObject("Fuel Backstop");
        wall.transform.SetParent(turret.transform, false);
        wall.transform.localPosition = backstopCenter;
        wall.transform.localRotation = Quaternion.identity;
        // same layer as the turret's own colliders, so fuel collides with it exactly like the shooter plates
        var existing = turret.GetComponentInChildren<Collider>(true);
        wall.layer = existing != null ? existing.gameObject.layer : turret.gameObject.layer;
        var box = wall.AddComponent<BoxCollider>();
        box.size = backstopSize;
        box.material = new PhysicMaterial("Fuel Backstop")
        {
            dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
            frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum
        };
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
        wall.transform.localPosition = new Vector3(0f, -0.07f, 0.30f);
        wall.transform.localRotation = Quaternion.identity;
        _intakeEntranceBlocker = wall.AddComponent<BoxCollider>();
        _intakeEntranceBlocker.size = new Vector3(0.56f, 0.24f, 0.03f);
        _intakeEntranceBlocker.enabled = false;
    }

    // The Robot layer collides with itself and a joint only ignores its own two bodies, so the moving parts (turret,
    // hood, intake arm, hopper extension) would hit the robot's other colliders - e.g. the arm folding over the
    // retracted hopper walls. Fuel is unaffected (game-piece layers).
    private void IgnoreSelfCollisions()
    {
        var all = GetComponentsInChildren<Collider>(true);
        Transform[] moving = { turret ? turret.transform : null, hoodJoint ? hoodJoint.transform : null,
                               intakeJoint ? intakeJoint.transform : null, hopperJoint ? hopperJoint.transform : null };
        int pairs = 0;
        foreach (var a in all)
        {
            Transform part = System.Array.Find(moving, m => m != null && a.transform.IsChildOf(m));
            if (part == null) continue;
            foreach (var b in all)
            {
                if (a == b || b.transform.IsChildOf(part)) continue;
                Physics.IgnoreCollision(a, b, true);
                pairs++;
            }
        }
        if (debugShooting) Debug.Log($"[Breakaway] ignoring {pairs} robot self-collision pairs");
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        StraightenExitingFuel();
    }

    private void StraightenExitingFuel()
    {
        if (!straightenExit || turret == null) return;
        if (Time.time >= _nextFuelScan)
        {
            // fuel is spawned and destroyed during a match: refresh the body list often enough that a ball
            // fed straight in (human player, fast intake) is known before it reaches the hood (>0.1 s away)
            _nextFuelScan = Time.time + 0.1f;
            _fuelBodies.Clear();
            foreach (var gp in FindObjectsByType<Games.Rebuilt.GamePieceSystem.RebuiltGamePieceController>(FindObjectsSortMode.None))
            {
                var body = gp.GetComponent<Rigidbody>();
                if (body == null) body = gp.GetComponentInChildren<Rigidbody>();
                if (body != null) _fuelBodies.Add(body);
            }
            if (_exitDone.Count > 256) _exitDone.Clear();
        }
        Transform t = turret.transform;
        // the ball inherits the velocity of the chassis at the turret (driving and spinning)
        Vector3 chassisVel = rb != null ? rb.GetPointVelocity(t.position) : Vector3.zero;
        float r2 = exitZoneRadius * exitZoneRadius;
        foreach (var body in _fuelBodies)
        {
            if (body == null || body.isKinematic) continue;
            Vector3 lp = t.InverseTransformPoint(body.position);
            if (lp.x * lp.x + lp.z * lp.z > r2 || lp.y < -0.1f || lp.y > exitZoneMaxHeight + 0.3f) continue;
            Vector3 rel = body.velocity - chassisVel;
            if (_exitHold.ContainsKey(body)) continue; // handled by the hold pass below
            if (lp.y < exitZoneMinHeight || lp.y > exitZoneMaxHeight) continue;
            if (rel.magnitude < exitMinSpeed || Vector3.Dot(rel, t.up) <= 0f) continue;
            Vector3 lv = t.InverseTransformDirection(rel);
            _exitHold[body] = Time.time + exitHoldTime;
            _lastExitTime = Time.time;
            bool first = debugShooting && _exitDone.Add(body);
            float sideBefore = lv.z;
            lv.z = 0f;
            body.velocity = chassisVel + t.TransformDirection(lv);
            if (first)
            {
                // shot heading in the turret frame vs the turret's target (0 = on line)
                Vector3 shot = Vector3.ProjectOnPlane(t.TransformDirection(lv), Vector3.up);
                Vector3 toTarget = Vector3.ProjectOnPlane((Vector3)turret.GetCurrentTarget() - body.position, Vector3.up);
                Debug.Log($"[Breakaway] exit straightened: side {sideBefore:+0.00;-0.00} m/s, off-centre {lp.z * 100f:+0.0;-0.0} cm, " +
                          $"speed {rel.magnitude:F2} m/s, aim err {Vector3.SignedAngle(toTarget, shot, Vector3.up):+0.0;-0.0} deg");
            }
        }
        // hold pass: keep each shot ball on the turret's vertical plane until it is clear of the hood
        _exitExpired.Clear();
        foreach (var kv in _exitHold)
        {
            Rigidbody body = kv.Key;
            if (body == null || Time.time > kv.Value ||
                (body.position - t.position).sqrMagnitude > exitHoldRadius * exitHoldRadius)
            {
                _exitExpired.Add(body);
                continue;
            }
            if (body.isKinematic) continue;
            Vector3 lv = t.InverseTransformDirection(body.velocity - chassisVel);
            if (Mathf.Abs(lv.z) < 1e-4f) continue;
            lv.z = 0f;
            body.velocity = chassisVel + t.TransformDirection(lv);
        }
        foreach (var body in _exitExpired) _exitHold.Remove(body);
        if (debugShooting) ReportJams(chassisVel);
    }

    // debug: while shooting, once a second, report fuel held in the robot and any ball that has sat still >1 s
    private void ReportJams(Vector3 chassisVel)
    {
        if (!ShootHeld) { _stillSince.Clear(); return; }
        int inRobot = 0;
        var stuck = new List<string>();
        var where = new List<string>();
        foreach (var body in _fuelBodies)
        {
            if (body == null) continue;
            Vector3 lp = transform.InverseTransformPoint(body.position);
            if (Mathf.Abs(lp.x) > 0.45f || Mathf.Abs(lp.z) > 0.75f || lp.y < -0.05f || lp.y > 0.9f) { _stillSince.Remove(body); continue; }
            inRobot++;
            where.Add($"({lp.x:F2},{lp.y:F2},{lp.z:F2})v{(body.velocity - chassisVel).magnitude:F1}");
            if ((body.velocity - chassisVel).magnitude > 0.1f) { _stillSince.Remove(body); continue; }
            if (!_stillSince.TryGetValue(body, out float since)) { _stillSince[body] = Time.time; continue; }
            if (Time.time - since > 1f) stuck.Add(lp.ToString("F2"));
        }
        if (Time.time < _nextJamReport) return;
        _nextJamReport = Time.time + 1f;
        Debug.Log($"[Breakaway] feed: {inRobot} fuel in robot, {stuck.Count} still >1s" + (stuck.Count > 0 ? $" at robot-local {string.Join(" ", stuck)}" : ""));
        // stalled: fuel aboard but nothing has left for 2 s -> dump every ball (robot-local x,y,z and speed)
        if (inRobot > 0 && Time.time - _lastExitTime > 2f)
            Debug.Log($"[Breakaway] feed stall: {string.Join(" ", where)}");
    }

    private void UpdatePids()
    {
        turret.UpdatePid(turretPid);
        hoodJoint.UpdatePid(hoodPid);
        intakeJoint.UpdatePid(intakePid);
        hopperJoint.UpdatePid(hopperPid);
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
                _agitateSwitch = Time.time + agitateHalfPeriod;
            }
            _intakeTarget = _agitateUp ? agitateAngle : intakeDeployedAngle;
        }
        else
        {
            _agitateUp = false;
            _intakeTarget = _intakeDeployed ? intakeDeployedAngle : intakeStowedAngle;
        }
        // the hopper extension goes out the first time the intake deploys (start of match) and stays out
        if (_intakeDeployed) _hopperOut = true;
        DebugState();
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

    private void UpdateJoints()
    {
        turret.Align();
        hoodJoint.SetTargetAngle(_hoodTarget).withAxis(JointAxis.X);
        // the prefab holds the arm in its CAD (deployed) pose: that pose reads as intakeDeployedAngle
        intakeJoint.SetTargetAngle(_intakeTarget).withAxis(JointAxis.X).useCustomStartingOffset(intakeDeployedAngle);
        // the extension starts retracted inside the frame (0), slides out once, then locks there (like CC's hopper)
        if (!_hopperLocked)
        {
            hopperJoint.SetLinearTarget(_hopperOut ? hopperExtendedDistance : 0f).withAxis(JointAxis.Z);
            float outInches = Mathf.Abs((float)hopperJoint.GetAxisLocation(JointAxis.Z)) / 0.0254f;
            if (_hopperOut && outInches >= hopperExtendedDistance - 0.5f)
            {
                hopperJoint.lockAllAxis();
                _hopperLocked = true;
            }
        }
    }

    // Shoot on the move: a ball leaves with the chassis velocity added, so it is aimed as if fired from where the
    // shooter will be one time-of-flight later (swmRef = anchor + v * tof). The turret aims from swmRef and the
    // flywheel / hood maps measure distance from it, so both direction and range are corrected. Time of flight comes
    // from the measured table and is solved against the shifted distance (a few fixed-point steps converge).
    protected override void HandleSwm()
    {
        // Assign the inherited swmRef (with a parent) and swmSettings in the Inspector.
        if (rb == null || swmRef == null || swmRef.parent == null) return;
        if (!improvedSotm)
        {
            flywheelInterpolation.transformOverride = _mapReference;
            hoodInterpolation.transformOverride = _mapReference;
            base.HandleSwm();
            return;
        }
        flywheelInterpolation.transformOverride = swmRef;
        hoodInterpolation.transformOverride = swmRef;

        Vector3 anchor = swmRef.parent.position;
        Vector3 v = rb.GetPointVelocity(anchor);   // includes the chassis spinning, not just driving
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

    // ---- aiming: the turret tracks the hub in the alliance zone and the closest pass target elsewhere
    protected override void AllianceZone()
    {
        _passing = false;
        turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
        flywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
        hoodInterpolation.SetDefaultMap(DefaultMap.Hub);
    }

    protected override void NeutralZone()
    {
        _passing = true;
        turret.SetPassMapClosest();
        flywheelInterpolation.SetPassMapClosest();
        hoodInterpolation.SetPassMapClosest();
    }

    // ---- rollers: each runs at its Inspector rpm unless stopped here (Stop() lasts one frame)
    protected override void Intaking()
    {
        // intake + gentle hopper run; nothing reaches the shooter
        hopperRollers.DivideSpeed(2f);
        feederRollers.Stop();
        kickerRollers.Stop();
        kickerBelts.Stop();
        shooterWheels.SetRpm(0f);
        _hoodTarget = hoodStowAngle;
    }

    protected override void IntakingAndShooting()
    {
        SpinUpAndHold();
    }

    protected override void Shooting()
    {
        if (_agitating) intakeRollers.DivideSpeed(agitateRollerDivider);
        else HoldIntake();
        SpinUpAndHold();
    }

    // intake rollers only run when intaking or agitating
    private void HoldIntake()
    {
        intakeRollers.Stop();
    }

    protected override void Feed()
    {
        // Aligned(): the feeder, kicker, belts and hopper keep running (no Stop this frame) and push fuel into the wheels
    }

    protected override void Idling()
    {
        HoldIntake();
        hopperRollers.Stop();
        feederRollers.Stop();
        kickerRollers.Stop();
        kickerBelts.Stop();
        shooterWheels.SetRpm(0f);
        _hoodTarget = hoodStowAngle;
    }

    // The base dispatch only treats OuttakeAction as shooting: Manual Shoot / Auto Pass alone fell through to Idling()
    // (feeder, kicker and belts stopped, shooter at 0). Same dispatch, all three shooting inputs.
    protected override void ShooterHandler()
    {
        bool shoot = OuttakeAction || ManualShoot || AutoPass;
        if (shoot)
        {
            if (IntakeAction) IntakingAndShooting();
            else Shooting();
            if (Aligned()) Feed();
        }
        else if (IntakeAction) Intaking();
        else Idling();
    }

    protected override bool Aligned()
    {
        if (NearTrench()) return false; // hood is coming down for the trench: hold fuel
        if (ManualShoot) return true;   // manual shot: feed without waiting for the turret
        if (turret.IsWrapping()) return false; // hold fuel while the turret unwinds
        float error = Mathf.Abs(turret.GetError());
        return error < turretTolerance && shooterWheels.AtSpeed();
    }

    private void SpinUpAndHold()
    {
        // wheels and hood follow the maps: they make the shot
        float rpm = ManualShoot ? manualShooterRpm : (float)flywheelInterpolation.Interpolate();
        float hood = ManualShoot ? manualHoodAngle : (float)hoodInterpolation.Interpolate();
        shooterWheels.SetRpm(rpm);
        _hoodTarget = NearTrench() ? hoodStowAngle : Mathf.Clamp(hood, -hoodMaxAngle, hoodMaxAngle);
        if (!Aligned())
        {
            // hold fuel until the turret is on target and the wheels are at speed
            feederRollers.Stop();
            kickerRollers.Stop();
            kickerBelts.Stop();
        }
    }

    [Header("Debug")]
    [SerializeField] private bool debugShooting = true;   // logs the state twice a second while intake / shoot is held
    private float _nextDebug;

    private void DebugState()
    {
        bool shoot = OuttakeAction || ManualShoot || AutoPass;
        if (!debugShooting || !(shoot || IntakeAction) || Time.time < _nextDebug) return;
        _nextDebug = Time.time + 0.5f;
        float err = turret.GetError();
        float hopperIn = (float)hopperJoint.GetAxisLocation(JointAxis.Z) / 0.0254f;
        Vector3 target = turret.GetCurrentTarget();
        float dist = Vector3.Distance(new Vector3(target.x, 0f, target.z),
                                      new Vector3(turret.transform.position.x, 0f, turret.transform.position.z));
        Debug.Log($"[Breakaway] intake={(bool)IntakeAction} deployed={_intakeDeployed} arm={(float)intakeJoint.GetAngle():F0}/{_intakeTarget:F0} " +
                  $"hopperOut={_hopperOut} locked={_hopperLocked} hopperPos={hopperIn:F1}in/{hopperExtendedDistance:F1} | " +
                  $"outtake={(bool)OuttakeAction} manual={(bool)ManualShoot} pass={(bool)AutoPass} passing={_passing} " +
                  $"agitating={_agitating} nearTrench={NearTrench()} turretErr={err:F1} aligned={Aligned()} dist={dist:F2}m | " +
                  $"hood={Mathf.DeltaAngle(0f, (float)hoodJoint.GetAngle()):F1}/{_hoodTarget:F1} | intake {Rpm(intakeRollers)} | hopper {Rpm(hopperRollers)} | feeder {Rpm(feederRollers)} | " +
                  $"belts {Rpm(kickerBelts)} | kicker {Rpm(kickerRollers)} | shooter {Rpm(shooterWheels)}");
    }

    // per-roller real/target rpm (RollerGroup.GetAverage*Rpm returns the multiplier, not the rpm)
    private static readonly System.Reflection.FieldInfo RollersField =
        typeof(RollerGroup).GetField("rollers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static string Rpm(RollerGroup g)
    {
        if (g == null) return "UNLINKED";
        var list = RollersField?.GetValue(g) as System.Array;
        if (list == null) return "?";
        var parts = new List<string>();
        foreach (var inst in list)
        {
            var roller = (inst as RollerGroup.RollerInstance)?.GetInteractionRoller();
            if (roller != null) parts.Add($"{roller.realRpm:F0}/{roller.TargetRpm:F0}");
        }
        return string.Join(" ", parts);
    }
}
