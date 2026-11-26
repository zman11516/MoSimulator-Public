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
    [SerializeField] private bool startIntakeStowed = true;       // spawn with the arm folded inside the frame (not the CAD pose)
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

    [Header("Intake While Shooting")]
    // While shooting, the intake rollers run inward at their Inspector rpm divided by this (1 = full speed), so fuel
    // picked up on the move keeps coming in. Agitation still uses agitateRollerDivider.
    [SerializeField] private bool runIntakeWhileShooting = true;
    [SerializeField] private float shootingIntakeDivider = 1f;

    [Header("Intake Pivot Plates")]
    // Printed plates on the fixed intake (under "Intake / Model", not the arm) that turn on the arm's pivot shaft. They
    // start up inside the frame with the stowed arm; the first deploy pushes them down with the arm and they stay down
    // (they never follow the arm back up: stow and agitation leave them in place). Matched by exact object name.
    [SerializeField] private string[] pivotPlateNames = { "IN-40 3d Printed", "IN-39 3d Printed", "Part 57", "Part 58" };
    [SerializeField] private float pivotPlateLatchTolerance = 2f; // deg from intakeDeployedAngle at which they lock down

    [Header("Bumper Guide")]
    // The hanging guide under the intake arm (side plates Part 79 / 79.001, the two bottom hex rods, IN-08.001 and the
    // Part 81 / 76 brackets) pivots on the IN-36 Spacer.002 / .003 bolts. It starts swung back (bottom toward the robot)
    // by bumperGuideStartAngle and stays fully back while the arm swings down. When the arm gets within
    // bumperGuidePushRange degrees of deployed it touches the bumper, which pushes it into its CAD pose (no faster than
    // bumperGuidePushSpeed, so the push is visible even though the arm moves fast); it stays there for good.
    // "collider-complex-<name>" colliders move with their part.
    [SerializeField] private string[] bumperGuideNames =
    {
        "1/2\" Rounded Hex (22.125\" L, 13.75mm OD)", "1/2\" Rounded Hex (22.125\" L, 13.75mm OD).001",
        "IN-08 Stiffener/Guide.001", "Part 79", "Part 79.001", "Part 81", "Part 76"
    };
    [SerializeField] private string[] bumperGuidePivotNames = { "IN-36 Spacer.002", "IN-36 Spacer.003" };
    [SerializeField, Tooltip("Degrees the guide starts swung back (negative = the other way).")]
    private float bumperGuideStartAngle = 15f;
    [SerializeField, Tooltip("Contact point: the guide stays fully back until the arm is this many degrees from deployed, " +
                             "then the bumper pushes it in over the rest of the arm's travel. Small = late contact.")]
    private float bumperGuidePushRange = 15f;
    [SerializeField, Tooltip("Fastest the guide swings in once pushed, degrees per second (0 = follow the arm exactly).")]
    private float bumperGuidePushSpeed = 90f;

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

    private struct PivotPlate
    {
        public Transform t;
        public Vector3 cadPos;        // local pose in the CAD (deployed) position
        public Quaternion cadRot;
        public Matrix4x4 inArm;       // pose in the intake arm's frame (parent space -> arm space)
    }
    private readonly List<PivotPlate> _pivotPlates = new List<PivotPlate>();
    private bool _platesPushed;       // the first deploy has started pushing them down
    private bool _platesLatched;      // down for good
    private float _platesBestAngle;   // most-deployed arm angle seen since the first deploy
    private Matrix4x4 _platesArmPose; // arm pose (in the plates' parent space) the plates sit at

    private struct GuidePart
    {
        public Transform t;
        public Vector3 cadPos;
        public Quaternion cadRot;
        public Vector3 pivot;         // pivot point in the part's parent space
        public Vector3 axis;          // "swing back" axis in the part's parent space (positive angle = back)
    }
    private readonly List<GuidePart> _guideParts = new List<GuidePart>();
    private float _guideBack;         // 1 = at bumperGuideStartAngle, 0 = CAD pose; only ever decreases
    private bool _guideLatched;


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
        FindPivotPlates();
        FindBumperGuide();
        AddFuelBackstop();

        Scheduler.AddSubsystems(IntakeArm);
        Scheduler.AddProtectedSystem(UpdateJoints, UpdateDriveSpeed);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    protected override void LateInit()
    {
        base.LateInit();
        StowIntakeAtSpawn();
        // Run after every Start() so InteractionRoller's runtime Wheel_InternalCore colliders are ignored too.
        IgnoreSelfCollisions();
        AddCentringGuides();
    }

    // The prefab holds the arm in its CAD (deployed) pose and GenericJoint.Start() records that pose as the angle
    // reference, so the prefab stays as it is: after every Start() the arm is turned about its joint (free X axis,
    // anchor on the pivot shaft) to the stowed angle, so the robot spawns with the intake inside the frame instead of
    // swinging up from deployed. Everything on the arm moves with it.
    private void StowIntakeAtSpawn()
    {
        if (!startIntakeStowed || intakeJoint == null) return;
        Transform arm = intakeJoint.transform;
        if (arm.parent == null) return;
        var joint = arm.GetComponent<ConfigurableJoint>();
        Vector3 pivot = arm.TransformPoint(joint != null ? joint.anchor : Vector3.zero);

        // GenericJoint reads the angle about the parent's X axis (localRotation), CAD pose = intakeDeployedAngle
        Quaternion newLocal = Quaternion.AngleAxis(intakeStowedAngle - intakeDeployedAngle, Vector3.right) * arm.localRotation;
        Quaternion newWorld = arm.parent.rotation * newLocal;
        Quaternion delta = newWorld * Quaternion.Inverse(arm.rotation);
        Vector3 newPos = pivot + delta * (arm.position - pivot);

        arm.SetPositionAndRotation(newPos, newWorld);
        var armRb = arm.GetComponent<Rigidbody>();
        if (armRb != null)
        {
            armRb.position = newPos;
            armRb.rotation = newWorld;
            armRb.velocity = Vector3.zero;
            armRb.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
        if (debugShooting) Debug.Log($"[Breakaway] intake spawned stowed: arm turned {intakeStowedAngle - intakeDeployedAngle:F0} deg from the CAD pose");
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
        UpdatePivotPlates();
        UpdateBumperGuide();
    }

    // GenericJoint.GetAngle() wraps to 0..360 (stowed -120 reads 240): bring it into -180..180 around 0 so
    // intakeStowedAngle / intakeDeployedAngle comparisons work
    private float ArmAngle() => Mathf.DeltaAngle(0f, (float)intakeJoint.GetAngle());

    // ---- bumper guide: starts swung back, the bumper pushes it into place on the first deploy, then it stays
    private static Vector3 RendererCentre(Transform t)
    {
        var r = t.GetComponentInChildren<Renderer>(true);
        if (r != null) return r.bounds.center;
        var c = t.GetComponentInChildren<Collider>(true);
        return c != null ? c.bounds.center : t.position;
    }

    // one name: that object's position, hinge parallel to the arm's pivot; two names: the line through both centres
    private static bool FindGuidePivot(Transform[] all, string[] pivotNames, Vector3 armAxis, out Vector3 pivot,
        out Vector3 axis)
    {
        pivot = Vector3.zero;
        axis = armAxis.normalized;
        if (pivotNames == null || pivotNames.Length == 0) return false;
        Transform pa = System.Array.Find(all, t => t.name == pivotNames[0]);
        if (pa == null) return false;
        Transform pb = pivotNames.Length > 1 ? System.Array.Find(all, t => t.name == pivotNames[1]) : null;
        if (pivotNames.Length > 1 && pb == null) return false;
        if (pb == null)
        {
            // an empty marks the hinge by its position; a part marks it by its mesh centre
            pivot = pa.GetComponentInChildren<Renderer>(true) != null ? RendererCentre(pa) : pa.position;
            return true;
        }
        Vector3 a = RendererCentre(pa), b = RendererCentre(pb);
        pivot = (a + b) * 0.5f;
        if ((b - a).sqrMagnitude > 1e-8f) axis = (b - a).normalized;
        return true;
    }

    private void FindBumperGuide()
    {
        _guideParts.Clear();
        _guideLatched = false;
        _guideBack = 1f;
        if (intakeJoint == null || bumperGuideNames == null || bumperGuideNames.Length == 0)
        {
            Debug.LogWarning("[Breakaway] bumper guide: no intake joint or no part names set");
            return;
        }
        Transform arm = intakeJoint.transform;
        var all = arm.GetComponentsInChildren<Transform>(true);
        // the arm turns about its parent's X axis (see StowIntakeAtSpawn); the guide's hinge is parallel to it
        Vector3 armAxis = arm.parent != null ? arm.parent.TransformDirection(Vector3.right) : arm.right;
        if (!FindGuidePivot(all, bumperGuidePivotNames, armAxis, out Vector3 pivot, out Vector3 axis))
        {
            var fallback = new[] { "IN-36 Spacer.002", "IN-36 Spacer.003" };
            Debug.LogWarning($"[Breakaway] bumper guide pivot '{string.Join("', '", bumperGuidePivotNames ?? new string[0])}' " +
                             "not found under the intake arm: using the IN-36 Spacer.002 / .003 bolts");
            if (!FindGuidePivot(all, fallback, armAxis, out pivot, out axis))
            {
                Debug.LogWarning("[Breakaway] bumper guide: no pivot found, guide left in its CAD pose");
                return;
            }
        }

        var names = new HashSet<string>();
        foreach (var n in bumperGuideNames) { names.Add(n); names.Add("collider-complex-" + n); }
        var parts = System.Array.FindAll(all, t => names.Contains(t.name));
        if (parts.Length == 0)
        {
            Debug.LogWarning("[Breakaway] bumper guide: none of the part names were found under the intake arm");
            return;
        }

        // "back" = the guide's bottom swings toward the robot's centre of mass (inside the frame)
        Vector3 bottom = Vector3.zero;
        foreach (var t in parts) bottom += RendererCentre(t);
        bottom /= parts.Length;
        Vector3 inward = (rb != null ? rb.worldCenterOfMass : transform.position) - pivot;
        inward -= Vector3.Dot(inward, transform.up) * transform.up;   // horizontal
        Vector3 moved = Quaternion.AngleAxis(5f, axis) * (bottom - pivot) - (bottom - pivot);
        if (Vector3.Dot(moved, inward) < 0f) axis = -axis;

        foreach (var t in parts)
        {
            _guideParts.Add(new GuidePart
            {
                t = t, cadPos = t.localPosition, cadRot = t.localRotation,
                pivot = t.parent.InverseTransformPoint(pivot),
                axis = t.parent.InverseTransformDirection(axis).normalized
            });
        }
        if (debugShooting) Debug.Log($"[Breakaway] bumper guide parts found: {_guideParts.Count} (incl. colliders)");
        ApplyBumperGuide();
    }

    private void UpdateBumperGuide()
    {
        if (_guideLatched || _guideParts.Count == 0) return;
        // held back until the first deploy; then the bumper pushes it as the arm comes down, and it never swings back
        if (_platesPushed || _intakeDeployed)
        {
            float s = Mathf.Sign(intakeDeployedAngle - intakeStowedAngle);
            float fromDeployed = Mathf.Max(0f, s * Mathf.DeltaAngle(ArmAngle(), intakeDeployedAngle));
            float push = bumperGuidePushRange > 0f ? Mathf.Clamp01(fromDeployed / bumperGuidePushRange) : 0f;
            float target = Mathf.Min(_guideBack, push);
            if (bumperGuidePushSpeed > 0f && Mathf.Abs(bumperGuideStartAngle) > 0.01f)
            {
                // the bumper can only push it so fast: limit the swing per frame
                float maxStep = bumperGuidePushSpeed * Time.deltaTime / Mathf.Abs(bumperGuideStartAngle);
                target = Mathf.Max(target, _guideBack - maxStep);
            }
            _guideBack = target;
            if (_guideBack <= 0.01f)
            {
                _guideBack = 0f;
                _guideLatched = true;
            }
        }
        ApplyBumperGuide();
    }

    private void ApplyBumperGuide()
    {
        float angle = bumperGuideStartAngle * _guideBack; // any sign: the start angle sets the direction
        foreach (var g in _guideParts)
        {
            if (Mathf.Approximately(angle, 0f))
            {
                g.t.localPosition = g.cadPos;
                g.t.localRotation = g.cadRot;
                continue;
            }
            Quaternion q = Quaternion.AngleAxis(angle, g.axis);
            g.t.localPosition = g.pivot + q * (g.cadPos - g.pivot);
            g.t.localRotation = q * g.cadRot;
        }
    }

    // ---- intake pivot plates: ride up with the arm until the first deploy, then ratchet down with it and lock
    private void FindPivotPlates()
    {
        _pivotPlates.Clear();
        _platesPushed = false;
        _platesLatched = false;
        if (intakeJoint == null || pivotPlateNames == null) return;
        Transform arm = intakeJoint.transform;
        var names = new HashSet<string>(pivotPlateNames);
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (!names.Contains(t.name) || t.parent == null || t.IsChildOf(arm)) continue;
            // the prefab holds the arm in its CAD (deployed) pose, so this is the plate's pose in the arm frame
            Matrix4x4 armInParent = t.parent.worldToLocalMatrix * arm.localToWorldMatrix;
            _pivotPlates.Add(new PivotPlate
            {
                t = t, cadPos = t.localPosition, cadRot = t.localRotation,
                inArm = armInParent.inverse * Matrix4x4.TRS(t.localPosition, t.localRotation, Vector3.one)
            });
        }
        if (debugShooting) Debug.Log($"[Breakaway] intake pivot plates found: {_pivotPlates.Count}/{pivotPlateNames.Length}");
    }

    private void UpdatePivotPlates()
    {
        if (_platesLatched || _pivotPlates.Count == 0) return;
        Transform arm = intakeJoint.transform;
        Transform parent = _pivotPlates[0].t.parent;
        Matrix4x4 armNow = parent.worldToLocalMatrix * arm.localToWorldMatrix;
        float angle = ArmAngle();
        float s = Mathf.Sign(intakeDeployedAngle - intakeStowedAngle); // +1 when "deployed" is the larger angle

        if (!_platesPushed)
        {
            // before the first deploy the plates rest on the arm: up inside the frame while it is stowed
            _platesArmPose = armNow;
            if (_intakeDeployed)
            {
                _platesPushed = true;
                _platesBestAngle = angle;
            }
        }
        else if (s * angle > s * _platesBestAngle)
        {
            // the arm is pushing them further down; they never come back up
            _platesBestAngle = angle;
            _platesArmPose = armNow;
        }

        if (_platesPushed && s * (intakeDeployedAngle - _platesBestAngle) <= pivotPlateLatchTolerance)
        {
            foreach (var p in _pivotPlates)
            {
                p.t.localPosition = p.cadPos;
                p.t.localRotation = p.cadRot;
            }
            _platesLatched = true;
            return;
        }

        foreach (var p in _pivotPlates)
        {
            if (p.t.parent != parent) continue; // all four share "Intake / Model"
            Matrix4x4 m = _platesArmPose * p.inArm;
            p.t.localPosition = m.GetPosition();
            p.t.localRotation = m.rotation;
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
        else if (runIntakeWhileShooting) intakeRollers.DivideSpeed(Mathf.Max(1f, shootingIntakeDivider));
        else HoldIntake();
        SpinUpAndHold();
    }

    // intake rollers run when intaking, shooting (runIntakeWhileShooting) or agitating
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
        Debug.Log($"[Breakaway] intake={(bool)IntakeAction} deployed={_intakeDeployed} arm={ArmAngle():F0}/{_intakeTarget:F0} guide={bumperGuideStartAngle * _guideBack:F0} " +
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
