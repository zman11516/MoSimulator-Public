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

// FRC 16 Bomb Squad: pivoting over-the-bumper intake, roller-bed hopper, fixed shooter (no turret, no adjustable hood),
// no climber in the CAD. Same layout as the base game's Competitive Concept (1114-style): the drivetrain turns to aim.
//
// Rollers: InteractionRoller spins about its own +X (right-hand rule), so the sign of each rpm below decides which way
// fuel moves. The defaults were worked out from the CAD: intake and roller bed run negative to pull fuel in toward
// the shooter at the back. If a mechanism pushes fuel the wrong way in play mode, flip the sign of its rpm field.
//
// Shooting ("Direct Launch", on by default, same as Breakaway): when fuel reaches the shooter throat and the robot is
// aimed, the ball is placed just above the shooter exit and given the exact ballistic velocity to the target. Fuel has
// no drag, so the shot is exact and robot motion is compensated automatically. The wheels still spin for looks.
// Turn Direct Launch off to use pure roller physics (flywheel rpm comes from the interpolation map).
public class BombSquad : RebuiltRobotBase
{
    [Header("Joints")]
    [SerializeField] private GenericJoint intakeJoint;          // "intake": GenericJoint (X axis), CAD pose = deployed
    [SerializeField] private GenericJoint hoodJoint;            // "Hood" (3 rods round the flywheel): pivots on the flywheel axle (X)

    [Header("Rollers")]
    [SerializeField] private RollerGroup intakeRollers;         // 2 intake rollers on the arm
    [SerializeField] private RollerGroup rollerBed;             // 4 roller-bed rollers (hopper floor)
    [SerializeField] private RollerGroup kickerLower;           // small roller at the front of the shooter throat
    [SerializeField] private RollerGroup kickerUpper;           // small roller above it
    [SerializeField] private RollerGroup flywheel;              // 4" shooter wheel

    [Header("PIDs")]
    [SerializeField] private PidConstants intakePid;
    [SerializeField] private PidConstants hoodPid;

    [Header("Interpolation")]
    [SerializeField] private RebuiltInterpolation flywheelInterpolation;   // distance (m) -> flywheel rpm
    // distance (m) -> hood angle (deg). 0 = CAD pose (steepest); negative swings the rods up over the flywheel (flatter)
    [SerializeField] private RebuiltInterpolation hoodInterpolation;

    [Header("Hood")]
    [SerializeField] private float hoodMaxAngle = 30f;          // |angle| limit either side of the CAD pose
    [SerializeField] private float manualHoodAngle = -10f;

    [Header("Intake")]
    // angles are measured from the prefab pose (0); deployed sits a little above it so the rollers clear the carpet
    [SerializeField] private float intakeDeployedAngle = -3f;
    [SerializeField] private float intakeStowedAngle = -106f;   // folded up and back inside the frame
    [SerializeField] private bool agitateWhileShooting = true;
    [SerializeField] private float agitateDelay = 1.5f;         // s after shooting starts
    [SerializeField] private float agitateAngle = -36f;         // arm angle at the top of each swing
    [SerializeField] private float agitateHalfPeriod = 0.4f;    // s per swing direction
    // the raised arm is taller than the trench bar: no agitating within this many m of the trench
    [SerializeField] private float trenchClearDistance = 1f;

    [Header("Roller speeds (sign = direction)")]
    [SerializeField] private float intakeRpm = -1400f;           // the Fuel Deflector over the intake turns lifted fuel into the hopper
    [SerializeField] private float intakeHoldRpm = -300f;       // deployed but not intaking: keeps fuel from rolling out
    [SerializeField] private float rollerBedRpm = -1200f;       // toward the shooter
    [SerializeField] private float rollerBedReverseRpm = 300f;  // not shooting: nudges fuel away from the throat
    [SerializeField] private float kickerRpm = 2500f;
    [SerializeField] private float flywheelSign = -1f;          // flywheel rpm = sign * interpolated rpm
    [SerializeField] private float manualShooterRpm = 3000f;
    [SerializeField] private float passRpmMultiplier = 1.2f;

    [Header("Aiming")]
    [SerializeField] private float shooterYawOffset = 180f;     // 0 = shooter faces the robot's front, 180 = back
    [SerializeField] private float aimTolerance = 3f;           // degrees of drivetrain error allowed while feeding

    [Header("Direct Launch")]
    [SerializeField] private bool directLaunch = true;
    [SerializeField] private LayerMask fuelLayers;              // empty = the "GamePiece1" layer (fuel)
    // robot-local box at the back of the roller bed / front of the shooter where fuel gets picked up
    [SerializeField] private Vector3 throatCenter = new Vector3(0f, 0.2f, -0.099f);
    [SerializeField] private Vector3 throatHalfExtents = new Vector3(0.27f, 0.12f, 0.11f);
    [SerializeField] private Vector3 launchPoint = new Vector3(0f, 0.61f, -0.169f);  // robot-local, above the shooter (centre lane)
    [SerializeField] private float launchInterval = 0.25f;      // s between balls in the same lane
    // multi-wide shooter: fuel is fired side by side across the shooter, one ball per lane per interval
    [SerializeField, Min(1)] private int laneCount = 4;
    [SerializeField] private float laneSpacing = 0.14f;         // m between lane centres
    [SerializeField] private float hubApexAboveTarget = 0.9f;   // m the arc peaks above the hub aim point
    [SerializeField] private float hubApexPerMeter = 0.25f;     // ...or this x distance if higher
    [SerializeField] private float passApexAboveTarget = 1.8f;
    [SerializeField] private float shotSpread = 0f;             // m of random miss at the target (0 = perfect)
    [SerializeField] private float manualShotSpeed = 9f;        // m/s out of the shooter (Manual Shoot)
    [SerializeField] private float manualShotElevation = 55f;   // degrees
    [SerializeField] private float ignoreRobotTime = 0.3f;      // s a launched ball ignores the robot's colliders

    [Header("Debug")]
    [SerializeField] private bool debugShooting = true;         // logs state twice a second while intake / shoot is held

    private float _intakeTarget;
    private float _hoodTarget;
    private bool _intakeDeployed;
    private bool _wasShooting;
    private float _shootStart;
    private bool _agitating;
    private bool _agitateUp;
    private float _agitateSwitch;
    private bool _passing;

    private RebuiltAutoAimController _aim;
    private Rigidbody _rb;
    private Collider[] _robotColliders;
    private readonly Collider[] _throatHits = new Collider[16];
    private readonly List<(Collider[] ball, float until)> _ignored = new();
    private float[] _laneNextLaunch;
    private readonly HashSet<Rigidbody> _volley = new();
    private int _launched;
    private float _nextDebug;

    private bool ShootHeld => OuttakeAction || ManualShoot || AutoPass;

    protected override void Init()
    {
        intakeJoint.SetPid(intakePid);
        hoodJoint.SetPid(hoodPid);
        flywheelInterpolation.Initialize(this);
        hoodInterpolation.Initialize(this);

        _aim = GetComponent<RebuiltAutoAimController>();
        _rb = GetComponent<Rigidbody>();
        _robotColliders = GetComponentsInChildren<Collider>(true);
        if (fuelLayers.value == 0) fuelLayers = 1 << LayerMask.NameToLayer("GamePiece1");

        _intakeDeployed = false;
        _intakeTarget = intakeStowedAngle;
        IgnoreSelfCollisions();

        Scheduler.AddSubsystems(Intake, Hopper, HandleAutoAim, Shooter);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    // The robot's own colliders collide with each other and a joint only ignores its two bodies, so the intake arm
    // and hood would hit the frame / shooter while moving. Fuel is unaffected (game-piece layers).
    private void IgnoreSelfCollisions()
    {
        Transform[] moving = { intakeJoint ? intakeJoint.transform : null, hoodJoint ? hoodJoint.transform : null };
        foreach (var a in _robotColliders)
        {
            Transform part = System.Array.Find(moving, m => m != null && a.transform.IsChildOf(m));
            if (part == null) continue;
            foreach (var b in _robotColliders)
                if (!b.transform.IsChildOf(part)) Physics.IgnoreCollision(a, b, true);
        }
    }

    private void UpdatePids()
    {
        intakeJoint.UpdatePid(intakePid);
        hoodJoint.UpdatePid(hoodPid);
    }

    private bool NearTrench() => UnderTrench(null, trenchClearDistance);

    private void Intake()
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
            WobbleIntake();
        }
        else
        {
            _agitateUp = false;
            _intakeTarget = _intakeDeployed ? intakeDeployedAngle : intakeStowedAngle;
        }

        if (IntakeAction) intakeRollers.SetRpm(intakeRpm);
        else if (_intakeDeployed) intakeRollers.SetRpm(_agitating || shoot ? intakeRpm * 0.5f : intakeHoldRpm);
        else intakeRollers.SetRpm(0f);
    }

    private void Hopper()
    {
        // the bed runs toward the shooter while intaking or shooting, otherwise backs fuel off the throat slightly
        if (ShootHeld && Aligned()) rollerBed.SetRpm(rollerBedRpm);
        else if (IntakeAction) rollerBed.SetRpm(rollerBedRpm * 0.5f);
        else rollerBed.SetRpm(ShootHeld ? 0f : rollerBedReverseRpm);
    }

    private void HandleAutoAim()
    {
        if (!ShootHeld) return;
        SetTargets();
        if (ManualShoot) return;   // manual shot: the driver aims

        Vector3 target = _aim.GetCurrentTarget();
        float theta = Mathf.Atan2(target.x - transform.position.x, target.z - transform.position.z) * Mathf.Rad2Deg;
        _aim.Aim(theta + shooterYawOffset, 0f);
    }

    private void SetTargets()
    {
        _passing = (OuttakeAction && !InAllianceZone()) || AutoPass;
        if (_passing)
        {
            _aim.SetPassMapClosest();
            flywheelInterpolation.SetPassMapClosest();
            hoodInterpolation.SetPassMapClosest();
        }
        else
        {
            _aim.SetDefaultMap(DefaultMap.Hub);
            flywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
            hoodInterpolation.SetDefaultMap(DefaultMap.Hub);
        }
    }

    private bool Aligned()
    {
        if (ManualShoot) return true;
        if (Mathf.Abs(_aim.GetRotationError()) > aimTolerance) return false;
        return directLaunch || flywheel.AtSpeed();
    }

    private void Shooter()
    {
        RestoreRobotCollisions();

        if (!ShootHeld)
        {
            flywheel.SetRpm(0f);
            kickerLower.SetRpm(0f);
            kickerUpper.SetRpm(0f);
            _hoodTarget = 0f;   // CAD pose: lowest, fits under the trench
            DebugState();
            return;
        }

        float rpm = ManualShoot ? manualShooterRpm : (float)flywheelInterpolation.Interpolate();
        if (_passing && !ManualShoot) rpm *= passRpmMultiplier;
        flywheel.SetRpm(flywheelSign * rpm);

        float hood = ManualShoot ? manualHoodAngle : (float)hoodInterpolation.Interpolate();
        _hoodTarget = NearTrench() ? 0f : Mathf.Clamp(hood, -hoodMaxAngle, hoodMaxAngle);

        bool feed = Aligned();
        kickerLower.SetRpm(feed ? kickerRpm : 0f);
        kickerUpper.SetRpm(feed ? kickerRpm : 0f);
        if (feed && directLaunch) TryLaunch();
        DebugState();
    }

    private void UpdateJoints()
    {
        // the prefab pose reads as 0
        intakeJoint.SetTargetAngle(_intakeTarget).withAxis(JointAxis.X).useCustomStartingOffset(0f);
        hoodJoint.SetTargetAngle(_hoodTarget).withAxis(JointAxis.X).useCustomStartingOffset(0f);
    }

    private void WobbleIntake()
    {
        if (Time.time >= _agitateSwitch)
        {
            _agitateUp = !_agitateUp;
            _agitateSwitch = Time.time + agitateHalfPeriod;
        }
        _intakeTarget = _agitateUp ? agitateAngle : intakeDeployedAngle;
    }

    // ---- direct launch (multi-wide: each lane fires the ball nearest to it, side by side)
    private void TryLaunch()
    {
        if (BaseGameManager.Instance == null || BaseGameManager.Instance.RobotState != RobotState.Enabled) return;
        if (_laneNextLaunch == null || _laneNextLaunch.Length != laneCount) _laneNextLaunch = new float[laneCount];

        int n = Physics.OverlapBoxNonAlloc(transform.TransformPoint(throatCenter), throatHalfExtents, _throatHits,
                                           transform.rotation, fuelLayers, QueryTriggerInteraction.Ignore);
        _volley.Clear();
        for (int lane = 0; lane < laneCount; lane++)
        {
            if (Time.time < _laneNextLaunch[lane]) continue;
            float laneX = (lane - (laneCount - 1) * 0.5f) * laneSpacing;

            // the closest ball (across the shooter) that no other lane has taken this frame
            Rigidbody ball = null;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var body = _throatHits[i].attachedRigidbody;
                if (body == null || body.isKinematic || body == _rb || _volley.Contains(body)) continue;
                float dx = Mathf.Abs(transform.InverseTransformPoint(body.position).x - laneX);
                if (dx < best) { best = dx; ball = body; }
            }
            if (ball == null) break;

            _volley.Add(ball);
            Launch(ball, laneX);
            _laneNextLaunch[lane] = Time.time + launchInterval;
        }
    }

    private void Launch(Rigidbody ball, float laneX)
    {
        Vector3 start = transform.TransformPoint(launchPoint + new Vector3(laneX, 0f, 0f));
        Vector3 velocity;
        if (ManualShoot)
        {
            Vector3 flat = Quaternion.AngleAxis(shooterYawOffset, Vector3.up) *
                           Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            velocity = Quaternion.AngleAxis(-manualShotElevation, Vector3.Cross(Vector3.up, flat)) * flat * manualShotSpeed;
        }
        else
        {
            Vector3 target = _aim.GetCurrentTarget();
            if (target == Vector3.zero) return;   // no target assigned yet
            if (shotSpread > 0f)
            {
                Vector2 r = Random.insideUnitCircle * shotSpread;
                target += new Vector3(r.x, 0f, r.y);
            }
            float range = Vector3.ProjectOnPlane(target - start, Vector3.up).magnitude;
            float apexAbove = _passing ? passApexAboveTarget : Mathf.Max(hubApexAboveTarget, hubApexPerMeter * range);
            velocity = BallisticVelocity(start, target, apexAbove);
        }

        // keep the ball off the robot's colliders while it leaves
        var ballColliders = ball.GetComponentsInChildren<Collider>();
        SetIgnore(ballColliders, true);
        _ignored.Add((ballColliders, Time.time + ignoreRobotTime));

        ball.position = start;
        ball.transform.position = start;
        ball.velocity = velocity;
        ball.angularVelocity = Vector3.zero;
        _launched++;
    }

    // Peak the arc `apexAbove` m over the higher of start / target, then come down onto the target.
    private static Vector3 BallisticVelocity(Vector3 start, Vector3 target, float apexAbove)
    {
        float g = Mathf.Max(0.01f, -Physics.gravity.y);
        float apex = Mathf.Max(start.y, target.y) + Mathf.Max(0.05f, apexAbove);
        float vy = Mathf.Sqrt(2f * g * (apex - start.y));
        float flightTime = vy / g + Mathf.Sqrt(2f * (apex - target.y) / g);
        Vector3 flat = target - start;
        flat.y = 0f;
        return flat / flightTime + Vector3.up * vy;
    }

    private void RestoreRobotCollisions()
    {
        for (int i = _ignored.Count - 1; i >= 0; i--)
        {
            if (Time.time < _ignored[i].until) continue;
            SetIgnore(_ignored[i].ball, false);
            _ignored.RemoveAt(i);
        }
    }

    private void SetIgnore(Collider[] ball, bool ignore)
    {
        foreach (var b in ball)
        {
            if (b == null) continue;
            foreach (var c in _robotColliders)
                if (c != null && !c.isTrigger) Physics.IgnoreCollision(b, c, ignore);
        }
    }

    private void DebugState()
    {
        if (!debugShooting || !(ShootHeld || IntakeAction) || Time.time < _nextDebug) return;
        _nextDebug = Time.time + 0.5f;
        Debug.Log($"[BombSquad] intake={(bool)IntakeAction} deployed={_intakeDeployed} " +
                  $"arm={(float)intakeJoint.GetAngle():F0}/{_intakeTarget:F0} agitating={_agitating} | " +
                  $"outtake={(bool)OuttakeAction} manual={(bool)ManualShoot} pass={(bool)AutoPass} passing={_passing} " +
                  $"aimErr={_aim.GetRotationError():F1} aligned={Aligned()} launched={_launched} " +
                  $"hood={Mathf.DeltaAngle(0f, (float)hoodJoint.GetAngle()):F1}/{_hoodTarget:F1} nearTrench={NearTrench()}");
    }
}
