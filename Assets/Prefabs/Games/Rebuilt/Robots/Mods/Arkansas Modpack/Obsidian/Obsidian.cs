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
    [Tooltip("While agitating the ramp folds on each in-stroke and drops on each out-stroke, but only while the " +
             "intake is at least this far (intake joint units) from stowed. Closer than that the ramp would hit " +
             "the bumper, so it stays folded. Raise it if the ramp still clips the bumper.")]
    [SerializeField] private float agitateRampClearance = 4f;

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
    [SerializeField] private float feedHoldToleranceMultiplier = 1.5f;
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
    [Tooltip("How far each shot's speed and launch angle are pulled toward the running average of recent shots as " +
             "it clears the hood (0 = pure physics, 1 = every shot identical). The rigid sim ball leaves the " +
             "flywheel and hood 5.4 to 9.6 m/s and 56 to 80 deg shot to shot, which no amount of tuning removed. " +
             "The average is taken from the real shots, so the shot map's distances still hold; only the scatter " +
             "around them is removed. Speed is averaged per commanded RPM, and the angle average restarts when the " +
             "hood moves.")]
    [SerializeField, Range(0f, 1f)] private float launchConsistency = 0.85f;
    [Tooltip("Roughly how many recent shots the running average covers.")]
    [SerializeField] private float launchAverageShots = 8f;

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
    [Tooltip("The only way from the spindexer bowl into the kicker that a whole fuel fits through is around the " +
             "right spindexer wheel and along the back wall; the front-plate window is blocked by the middle wheel's " +
             "dome and centre cone. Pushing stalled fuel straight at the kicker drove it into the front plate (and " +
             "the divider), so instead a flow field is built from the robot's own colliders: every fuel-sized space " +
             "in the bowl knows the way to the kicker around the walls, and stalled fuel is nudged along it. Only " +
             "velocities change (with the reaction on the robot), so nothing is teleported.")]
    [SerializeField] private bool assistSpindexerFeed = true;
    [Tooltip("Robot-local point in the kicker entrance mouth that the flow field leads to: just past the gap " +
             "between the spindexer and the kicker rollers, where a whole fuel clears every solid collider. Fuel " +
             "left of this (and behind centre) is in the chute and left to the kicker rollers.")]
    [SerializeField] private Vector3 kickerEntryLocal = new Vector3(-0.06f, 0.22f, -0.075f);
    [Tooltip("Robot-local point inside the bottom of the kicker chute. Fuel at the mouth is nudged toward it.")]
    [SerializeField] private Vector3 kickerChuteLocal = new Vector3(-0.10f, 0.245f, -0.075f);
    [Tooltip("Robot-local bounds of the flow field grid (min corner). Keep the top below the turret: it turns, " +
             "so the field must not route fuel through space the turret only sometimes leaves open.")]
    [SerializeField] private Vector3 flowFieldMin = new Vector3(-0.36f, 0.10f, -0.16f);
    [Tooltip("Robot-local bounds of the flow field grid (max corner).")]
    [SerializeField] private Vector3 flowFieldMax = new Vector3(0.36f, 0.30f, 0.26f);
    [Tooltip("Flow field cell size in metres.")]
    [SerializeField] private float flowCellSize = 0.015f;
    [Tooltip("How far a fuel may squeeze into the robot's colliders and still count as fitting (metres). Fuel is " +
             "soft, and the tightest gap on the way to the kicker is a few millimetres under a full fuel.")]
    [SerializeField] private float flowSqueeze = 0.004f;
    [Tooltip("Fuel only queues behind a fuel closer to the kicker that is within this distance and in its way, " +
             "instead of being pushed into it. Stops two fuel being crammed into a gap that fits one.")]
    [SerializeField] private float flowQueueSpacing = 0.16f;
    [Tooltip("A fuel being nudged that makes no progress for this long is backed off once and tried again, the way " +
             "a jiggle re-seats a jammed ball.")]
    [SerializeField] private float flowUnjamTime = 0.8f;
    [Tooltip("Minimum seconds between fuel the assist sends into the kicker mouth. Leaves a gap between fuel so the " +
             "next one isn't pressed against the one the flywheel is grabbing, which scatters shots. 0 = no spacing.")]
    [SerializeField] private float minFeedInterval = 0.1f;
    [Tooltip("Robot-local bounds of the spindexer bowl (min corner).")]
    [SerializeField] private Vector3 spindexerZoneMin = new Vector3(-0.30f, 0.10f, -0.13f);
    [Tooltip("Robot-local bounds of the spindexer bowl (max corner). Kept behind the Cover hump so hopper fuel is " +
             "left to the intake agitation.")]
    [SerializeField] private Vector3 spindexerZoneMax = new Vector3(0.30f, 0.45f, 0.20f);
    [Tooltip("Speed relative to the chassis along the flow field that slow fuel in the bowl is brought up to.")]
    [SerializeField] private float spindexerAssistSpeed = 1.2f;

    [Header("Hopper Divider")]
    [Tooltip("Collider down the middle of the hopper that stops fuel hopping over and jamming other fuel. It helps " +
             "when the hopper is full but gets in the way when only a few fuel are left, so it is switched off then.")]
    [SerializeField] private Collider hopperDivider;
    [Tooltip("Switch the divider off when fewer than this many fuel are in the hopper.")]
    [SerializeField] private int dividerOffBelow = 8;
    [Tooltip("Switch the divider back on once at least this many fuel are in the hopper (higher than the off count " +
             "so it doesn't flicker). Fuel already straddling it passes through until clear instead of being launched.")]
    [SerializeField] private int dividerOnAtOrAbove = 12;
    [Tooltip("Robot-local bounds that count as inside the hopper (min corner).")]
    [SerializeField] private Vector3 hopperZoneMin = new Vector3(-0.40f, 0.05f, -0.20f);
    [Tooltip("Robot-local bounds that count as inside the hopper (max corner).")]
    [SerializeField] private Vector3 hopperZoneMax = new Vector3(0.40f, 0.60f, 0.50f);

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
    private readonly HashSet<Rigidbody> _shotMeasured = new HashSet<Rigidbody>();
    private readonly List<float> _shotSpeeds = new List<float>();
    private readonly List<float> _shotElevations = new List<float>();
    private readonly List<float> _shotTurretErr = new List<float>();

    private static string Range(List<float> v, string fmt)
    {
        float min = float.MaxValue, max = float.MinValue, sum = 0f;
        foreach (float x in v) { min = Mathf.Min(min, x); max = Mathf.Max(max, x); sum += x; }
        return $"{min.ToString(fmt)}..{max.ToString(fmt)} (avg {(sum / v.Count).ToString(fmt)})";
    }

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
        UpdateHopperDivider();
    }

    private int _hopperFuel;
    private float _nextDividerCheck;
    private readonly Collider[] _dividerHits = new Collider[32];
    private readonly HashSet<Collider> _dividerPassing = new HashSet<Collider>();
    private readonly HashSet<Collider> _dividerOverlapNow = new HashSet<Collider>();
    private readonly List<Collider> _dividerCleared = new List<Collider>();

    private void UpdateHopperDivider()
    {
        if (hopperDivider == null || Time.time < _nextDividerCheck) return;
        _nextDividerCheck = Time.time + 0.1f;
        ScanFuel();

        int count = 0;
        foreach (var body in _fuelBodies)
        {
            if (body == null) continue;
            Vector3 lp = transform.InverseTransformPoint(body.position);
            if (lp.x < hopperZoneMin.x || lp.y < hopperZoneMin.y || lp.z < hopperZoneMin.z ||
                lp.x > hopperZoneMax.x || lp.y > hopperZoneMax.y || lp.z > hopperZoneMax.z) continue;
            count++;
        }
        _hopperFuel = count;

        // An empty hopper (match start, or between loads) says nothing about how full it is about to get, so the
        // divider keeps whatever state it had.
        if (count == 0) return;

        if (hopperDivider.enabled)
        {
            if (count < dividerOffBelow)
            {
                hopperDivider.enabled = false;  // also drops every IgnoreCollision pair with it
                _dividerPassing.Clear();
                return;
            }
            ReleaseClearedDividerFuel();
        }
        else if (count >= dividerOnAtOrAbove)
        {
            // Fuel already straddling the divider would be launched if it collided the moment the divider appears,
            // so each of those passes through it until it has moved clear, then collides like the rest.
            hopperDivider.enabled = true;
            CollectDividerOverlaps();
            foreach (var c in _dividerOverlapNow)
            {
                Physics.IgnoreCollision(hopperDivider, c, true);
                _dividerPassing.Add(c);
            }
        }
    }

    private void ReleaseClearedDividerFuel()
    {
        if (_dividerPassing.Count == 0) return;
        CollectDividerOverlaps();
        _dividerCleared.Clear();
        foreach (var c in _dividerPassing)
            if (c == null || !_dividerOverlapNow.Contains(c)) _dividerCleared.Add(c);
        foreach (var c in _dividerCleared)
        {
            if (c != null) Physics.IgnoreCollision(hopperDivider, c, false);
            _dividerPassing.Remove(c);
        }
    }

    private void CollectDividerOverlaps()
    {
        _dividerOverlapNow.Clear();
        if (!(hopperDivider is BoxCollider box)) return;
        Transform t = box.transform;
        Vector3 scale = t.lossyScale;
        Vector3 half = Vector3.Scale(box.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))) * 0.5f;
        int n = Physics.OverlapBoxNonAlloc(t.TransformPoint(box.center), half, _dividerHits, t.rotation, ~0,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider c = _dividerHits[i];
            if (c != hopperDivider && c.GetComponentInParent<Games.Rebuilt.GamePieceSystem.RebuiltGamePieceController>() != null)
                _dividerOverlapNow.Add(c);
        }
    }

    private void AssistSpindexerFeed()
    {
        if (!assistSpindexerFeed || !_feeding || rb == null) return;
        ScanFuel();

        if (!_flowBuilt) BuildFlowFields();
        float[] dist = hopperDivider != null && hopperDivider.enabled && _flowDistDividerOn != null
            ? _flowDistDividerOn : _flowDistDividerOff;
        if (dist == null) return;

        Vector3 chassisVel = rb.GetPointVelocity(transform.TransformPoint(kickerEntryLocal));
        float cell = flowCellSize;

        _flowFuel.Clear();
        _flowInZone.Clear();
        foreach (var body in _fuelBodies)
        {
            if (body == null || body.isKinematic || _exitHold.ContainsKey(body)) continue;
            Vector3 lp = transform.InverseTransformPoint(body.position);
            if (lp.x < spindexerZoneMin.x || lp.y < spindexerZoneMin.y || lp.z < spindexerZoneMin.z ||
                lp.x > spindexerZoneMax.x || lp.y > spindexerZoneMax.y || lp.z > spindexerZoneMax.z) continue;
            // Already past the entrance mouth into the kicker chute: the kicker rollers have it.
            if (lp.x < kickerEntryLocal.x && lp.z < 0f) continue;
            // Above the field is the kicker top, turret and flywheel: the field knows nothing there, and a nudge
            // on fuel the flywheel is about to take sends the shot off line.
            if (lp.y > flowFieldMax.y) continue;
            int c = FlowLookup(lp, dist);
            if (c < 0) continue;
            _flowFuel.Add((body, lp, dist[c], c));
            _flowInZone.Add(body);
        }

        _flowStale.Clear();
        foreach (var b in _flowProgress.Keys) if (b == null || !_flowInZone.Contains(b)) _flowStale.Add(b);
        foreach (var b in _flowStale) _flowProgress.Remove(b);

        // The fuel being fed into the mouth has gone on into the chute (or away): start the spacing gap.
        if (_mouthBody != null && !_flowInZone.Contains(_mouthBody)) { _mouthBody = null; _lastMouthEntry = Time.time; }
        // It was knocked back out of the mouth: let another fuel have the turn.
        foreach (var f in _flowFuel)
            if (f.body == _mouthBody && f.dist > 4f * cell) { _mouthBody = null; break; }

        foreach (var f in _flowFuel)
        {
            Rigidbody body = f.body;
            Vector3 rel = body.velocity - chassisVel;
            if (rel.magnitude > handoffStallSpeed) { _flowProgress.Remove(body); continue; }

            Vector3 dirLocal;
            if (f.dist <= 2f * cell || (f.local - kickerEntryLocal).sqrMagnitude < 4f * cell * cell)
            {
                // One fuel at a time into the mouth, with a gap after the last one went in.
                if (_mouthBody != body)
                {
                    if (_mouthBody != null || Time.time - _lastMouthEntry < minFeedInterval) continue;
                    _mouthBody = body;
                }
                dirLocal = kickerChuteLocal - f.local;
            }
            else
                dirLocal = FlowTarget(f.cell, dist) - f.local;
            if (dirLocal.sqrMagnitude < 1e-8f) continue;
            dirLocal.Normalize();

            // Queue behind a fuel that is nearer the kicker and in the way, rather than cramming into it.
            bool queued = false;
            foreach (var o in _flowFuel)
            {
                if (o.body == body || o.dist >= f.dist - cell) continue;
                Vector3 d = o.local - f.local;
                if (d.sqrMagnitude > flowQueueSpacing * flowQueueSpacing) continue;
                if (Vector3.Dot(d, dirLocal) > 0f) { queued = true; break; }
            }
            if (queued) { _flowProgress.Remove(body); continue; }

            Vector3 dir = transform.TransformDirection(dirLocal);

            // No progress along the field for a while: back off once (up and away) and come at it again.
            if (!_flowProgress.TryGetValue(body, out var pr))
            {
                pr = (Time.time, f.dist);
                _flowProgress[body] = pr;
            }
            else if (f.dist < pr.dist - cell)
            {
                pr = (Time.time, f.dist);
                _flowProgress[body] = pr;
            }
            else if (Time.time - pr.since > flowUnjamTime)
            {
                PushFuel(body, chassisVel - dir * 0.6f + transform.up * 0.8f);
                _flowProgress[body] = (Time.time + 0.3f, f.dist);  // let it back off before nudging again
                continue;
            }
            if (pr.since > Time.time) continue;

            float progress = Vector3.Dot(rel, dir);
            if (progress >= spindexerAssistSpeed * 0.6f) continue;
            Vector3 side = rel - dir * progress;
            PushFuel(body, chassisVel + side * 0.5f + dir * spindexerAssistSpeed);
        }
    }

    // ---- Flow field: robot-local grid of fuel-sized spaces, each holding its path length to the kicker mouth ----

    private float[] _flowDistDividerOff, _flowDistDividerOn;
    private int _fnx, _fny, _fnz;
    private bool _flowBuilt;
    private readonly Collider[] _flowHits = new Collider[32];
    private readonly Dictionary<Rigidbody, (float since, float dist)> _flowProgress = new Dictionary<Rigidbody, (float since, float dist)>();
    private readonly List<(Rigidbody body, Vector3 local, float dist, int cell)> _flowFuel = new List<(Rigidbody body, Vector3 local, float dist, int cell)>();
    private readonly HashSet<Rigidbody> _flowInZone = new HashSet<Rigidbody>();
    private readonly List<Rigidbody> _flowStale = new List<Rigidbody>();
    private Rigidbody _mouthBody;
    private float _lastMouthEntry = -10f;

    private int FlowIndex(int x, int y, int z) => (x * _fny + y) * _fnz + z;
    private Vector3 FlowCenter(int x, int y, int z) => flowFieldMin + new Vector3(x, y, z) * flowCellSize;
    private void FlowCoords(int i, out int x, out int y, out int z) { z = i % _fnz; y = (i / _fnz) % _fny; x = i / (_fnz * _fny); }

    // Built once, the first time fuel needs feeding, from whatever colliders the robot has then, with the divider
    // both off and on so switching it never needs a rebuild.
    private void BuildFlowFields()
    {
        _flowBuilt = true;
        if (flowCellSize <= 0.001f) return;
        _fnx = Mathf.FloorToInt((flowFieldMax.x - flowFieldMin.x) / flowCellSize) + 1;
        _fny = Mathf.FloorToInt((flowFieldMax.y - flowFieldMin.y) / flowCellSize) + 1;
        _fnz = Mathf.FloorToInt((flowFieldMax.z - flowFieldMin.z) / flowCellSize) + 1;
        if (_fnx < 2 || _fny < 2 || _fnz < 2) return;

        if (hopperDivider == null)
        {
            Physics.SyncTransforms();
            _flowDistDividerOff = BuildFlowDistances(out int freeOff, out int reachOff);
            if (debugJams) Debug.Log($"[Obsidian] Flow field: {freeOff} fuel-sized cells, {reachOff} lead to the kicker.");
            return;
        }

        bool was = hopperDivider.enabled;
        hopperDivider.enabled = false;
        Physics.SyncTransforms();
        _flowDistDividerOff = BuildFlowDistances(out int free0, out int reach0);
        hopperDivider.enabled = true;
        Physics.SyncTransforms();
        _flowDistDividerOn = BuildFlowDistances(out int free1, out int reach1);
        hopperDivider.enabled = was;
        // Toggling the divider dropped its IgnoreCollision pairs; restore them for fuel still passing through it.
        if (was) foreach (var c in _dividerPassing) if (c != null) Physics.IgnoreCollision(hopperDivider, c, true);
        if (debugJams)
            Debug.Log($"[Obsidian] Flow field: divider off {free0} fuel-sized cells, {reach0} lead to the kicker; " +
                      $"divider on {free1} cells, {reach1} lead to the kicker.");
    }

    private float[] BuildFlowDistances(out int freeCount, out int reachCount)
    {
        int n = _fnx * _fny * _fnz;
        var free = new bool[n];
        var dist = new float[n];
        float probe = Mathf.Max(0.01f, fuelRadius - flowSqueeze);
        freeCount = 0;
        reachCount = 0;

        for (int x = 0; x < _fnx; x++)
        for (int y = 0; y < _fny; y++)
        for (int z = 0; z < _fnz; z++)
        {
            int i = FlowIndex(x, y, z);
            dist[i] = float.PositiveInfinity;
            int hits = Physics.OverlapSphereNonAlloc(transform.TransformPoint(FlowCenter(x, y, z)), probe, _flowHits, ~0,
                QueryTriggerInteraction.Ignore);
            bool open = true;
            for (int h = 0; h < hits && open; h++)
                if (_flowHits[h].GetComponentInParent<Games.Rebuilt.GamePieceSystem.RebuiltGamePieceController>() == null)
                    open = false;
            free[i] = open;
            if (open) freeCount++;
        }

        // Start from the open cell nearest the kicker mouth.
        int start = -1;
        float best = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (!free[i]) continue;
            FlowCoords(i, out int x, out int y, out int z);
            float d = (FlowCenter(x, y, z) - kickerEntryLocal).sqrMagnitude;
            if (d < best) { best = d; start = i; }
        }
        if (start < 0 || best > 0.05f * 0.05f) return dist;

        // Dijkstra over the 26 neighbours.
        var heap = new List<(float d, int i)>();
        dist[start] = 0f;
        HeapPush(heap, (0f, start));
        while (heap.Count > 0)
        {
            var (d, i) = HeapPop(heap);
            if (d > dist[i]) continue;
            reachCount++;
            FlowCoords(i, out int x, out int y, out int z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                int ax = x + dx, ay = y + dy, az = z + dz;
                if (ax < 0 || ay < 0 || az < 0 || ax >= _fnx || ay >= _fny || az >= _fnz) continue;
                int j = FlowIndex(ax, ay, az);
                if (!free[j]) continue;
                float nd = d + flowCellSize * Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                if (nd < dist[j]) { dist[j] = nd; HeapPush(heap, (nd, j)); }
            }
        }
        return dist;
    }

    // The reachable cell a fuel at this local point is in, or the nearest one around it (a fuel resting against a
    // wall can sit a little inside a blocked cell). -1 when there is none nearby.
    private int FlowLookup(Vector3 local, float[] dist)
    {
        Vector3 g = (local - flowFieldMin) / flowCellSize;
        int cx = Mathf.Clamp(Mathf.RoundToInt(g.x), 0, _fnx - 1);
        int cy = Mathf.Clamp(Mathf.RoundToInt(g.y), 0, _fny - 1);
        int cz = Mathf.Clamp(Mathf.RoundToInt(g.z), 0, _fnz - 1);
        int c = FlowIndex(cx, cy, cz);
        if (!float.IsInfinity(dist[c])) return c;

        int bestCell = -1;
        float bestD = float.PositiveInfinity;
        for (int r = 1; r <= 3 && bestCell < 0; r++)
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        for (int dz = -r; dz <= r; dz++)
        {
            int ax = cx + dx, ay = cy + dy, az = cz + dz;
            if (ax < 0 || ay < 0 || az < 0 || ax >= _fnx || ay >= _fny || az >= _fnz) continue;
            int j = FlowIndex(ax, ay, az);
            if (float.IsInfinity(dist[j])) continue;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; bestCell = j; }
        }
        return bestCell;
    }

    // Two steps down the field from this cell, so the direction follows the path round corners smoothly.
    private Vector3 FlowTarget(int cell, float[] dist)
    {
        int c = cell;
        for (int step = 0; step < 2; step++)
        {
            FlowCoords(c, out int x, out int y, out int z);
            int next = c;
            float bestD = dist[c];
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                int ax = x + dx, ay = y + dy, az = z + dz;
                if (ax < 0 || ay < 0 || az < 0 || ax >= _fnx || ay >= _fny || az >= _fnz) continue;
                int j = FlowIndex(ax, ay, az);
                if (dist[j] < bestD) { bestD = dist[j]; next = j; }
            }
            if (next == c) break;
            c = next;
        }
        FlowCoords(c, out int tx, out int ty, out int tz);
        return FlowCenter(tx, ty, tz);
    }

    private static void HeapPush(List<(float d, int i)> h, (float d, int i) item)
    {
        h.Add(item);
        int k = h.Count - 1;
        while (k > 0)
        {
            int p = (k - 1) / 2;
            if (h[p].d <= h[k].d) break;
            (h[p], h[k]) = (h[k], h[p]);
            k = p;
        }
    }

    private static (float d, int i) HeapPop(List<(float d, int i)> h)
    {
        var top = h[0];
        int last = h.Count - 1;
        h[0] = h[last];
        h.RemoveAt(last);
        int k = 0;
        while (true)
        {
            int l = 2 * k + 1, r = l + 1, m = k;
            if (l < h.Count && h[l].d < h[m].d) m = l;
            if (r < h.Count && h[r].d < h[m].d) m = r;
            if (m == k) break;
            (h[m], h[k]) = (h[k], h[m]);
            k = m;
        }
        return top;
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
            if (Mathf.Abs(side) >= 1e-4f)
            {
                rel -= axis * side;
                body.velocity = chassisVel + rel;
            }
            // Launch conditions, taken once the fuel has cleared the hood: what decides where the shot lands.
            if (!_shotMeasured.Contains(body) && (body.position - shaft).sqrMagnitude > zone2)
            {
                _shotMeasured.Add(body);
                float speed = rel.magnitude;
                float elev = speed > 1e-3f ? Mathf.Asin(Mathf.Clamp(Vector3.Dot(rel / speed, transform.up), -1f, 1f)) * Mathf.Rad2Deg : 0f;
                if (debugJams)
                {
                    _shotSpeeds.Add(speed);
                    _shotElevations.Add(elev);
                    if (turret != null) _shotTurretErr.Add(turret.GetError());
                }
                NormalizeLaunch(body, chassisVel, rel, speed, elev);
            }
        }
        foreach (var body in _exitExpired) { _exitHold.Remove(body); _shotMeasured.Remove(body); }
        if (debugJams) ReportJams(chassisVel);
    }

    private float _commandedRpm;
    private float _avgSpeedPerRpm, _avgElev, _avgElevHood;
    private int _avgSpeedCount, _avgElevCount;

    // Pull this shot's speed and launch angle part way toward the running average of real shots, then fold the
    // shot's own (uncorrected) values into that average so it keeps following what the shooter actually does.
    private void NormalizeLaunch(Rigidbody body, Vector3 chassisVel, Vector3 rel, float speed, float elev)
    {
        if (body.isKinematic || speed < exitMinSpeed * 1.5f || _commandedRpm < 1f) return;  // dribbles: leave alone
        Vector3 up = transform.up;
        Vector3 horiz = rel - up * Vector3.Dot(rel, up);
        if (horiz.sqrMagnitude < 1e-6f) return;

        // The hood moved: launch angles from before no longer apply.
        if (_avgElevCount > 0 && Mathf.Abs(_hoodTarget - _avgElevHood) > 1f) _avgElevCount = 0;

        float n = Mathf.Max(1f, launchAverageShots);
        float speedPerRpm = speed / _commandedRpm;
        bool haveSpeed = _avgSpeedCount >= 3, haveElev = _avgElevCount >= 3;
        if (launchConsistency > 0f && (haveSpeed || haveElev))
        {
            float s = haveSpeed ? Mathf.Lerp(speed, _avgSpeedPerRpm * _commandedRpm, launchConsistency) : speed;
            float e = (haveElev ? Mathf.Lerp(elev, _avgElev, launchConsistency) : elev) * Mathf.Deg2Rad;
            Vector3 dir = horiz.normalized * Mathf.Cos(e) + up * Mathf.Sin(e);
            body.velocity = chassisVel + dir * s;
        }

        _avgSpeedPerRpm = _avgSpeedCount == 0 ? speedPerRpm : Mathf.Lerp(_avgSpeedPerRpm, speedPerRpm, 1f / Mathf.Min(n, _avgSpeedCount + 1));
        _avgSpeedCount++;
        _avgElev = _avgElevCount == 0 ? elev : Mathf.Lerp(_avgElev, elev, 1f / Mathf.Min(n, _avgElevCount + 1));
        if (_avgElevCount == 0) _avgElevHood = _hoodTarget;
        _avgElevCount++;
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
        string divider = hopperDivider == null ? "" : $"hopper {_hopperFuel}, divider {(hopperDivider.enabled ? "on" : "off")}, ";
        string shots = "";
        if (_shotSpeeds.Count > 0)
        {
            shots = $"shots {_shotSpeeds.Count}: speed {Range(_shotSpeeds, "F2")} m/s, launch {Range(_shotElevations, "F1")} deg" +
                    (_shotTurretErr.Count > 0 ? $", turretErr at launch {Range(_shotTurretErr, "F1")} deg" : "") + ", ";
            _shotSpeeds.Clear();
            _shotElevations.Clear();
            _shotTurretErr.Clear();
        }
        Debug.Log($"[Obsidian] shooting: {_shotTimes.Count} BPS, {shots}{divider}aligned {Aligned()}, {turretState}, nearTrench {NearTrench()}, " +
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
            // While agitating the ramp flaps with the strokes: folded on each in-stroke, down again on each
            // out-stroke, which helps shake fuel loose. Once the arm has worked in close to the bumper there is no
            // room for it to drop, so it stays folded rather than passing through the bumper.
            bool roomToDrop = Mathf.Abs(_intakeTarget - intakeStowedDistance) >= agitateRampClearance;
            bool rampDown = _intakeDeployed && (!_agitating || (!_agitateUp && roomToDrop));
            Quaternion targetRot = Quaternion.Euler(rampDown ? 0f : intakeRampFoldAngle, 0f, 0f);
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
        _commandedRpm = Mathf.Abs(rpm);
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
