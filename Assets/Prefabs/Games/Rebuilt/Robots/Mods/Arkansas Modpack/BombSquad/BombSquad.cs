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
// Shooting is pure roller physics: the flywheel spins at the rpm from the interpolation map, and once the robot is
// aimed and the flywheel is at speed the kickers feed fuel into it. Fuel is never moved or given a velocity by script.
public class BombSquad : RebuiltRobotBase
{
    [Header("Joints")]
    [SerializeField] private GenericJoint intakeJoint;          // "intake": GenericJoint (X axis), CAD pose = deployed
    [SerializeField] private GenericJoint hoodJoint;            // "Hood" (3 rods round the flywheel): pivots on the flywheel axle (X)

    [Header("Rollers")]
    [SerializeField] private RollerGroup intakeRollers;         // 2 intake rollers on the arm
    [SerializeField] private RollerGroup rollerBed;             // 4 roller-bed rollers (hopper floor)
    [SerializeField] private RollerGroup kicker;           // small roller at the front of the shooter throat
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
    // fake solid backer behind the three hood rods (see AddHoodBacker)
    [SerializeField] private bool addHoodBacker = true;
    [SerializeField] private float hoodBackerThickness = 0.006f;  // m
    [SerializeField] private float hoodBackerInset = 0f;          // m the backer moves in toward the flywheel (0 = just behind the rods)

    [Header("Shot Trims")]
    // Interpolation Adjustment trims (docs: Scripting > Snippets): applied on top of the map values while shooting
    // from the maps (not to the manual shot), so a whole map can be nudged without re-entering every point.
    [SerializeField, Min(0f)] private float flywheelScale = 1f;   // multiplies the map rpm
    [SerializeField] private float flywheelOffsetRpm = 0f;        // added after the scale
    [SerializeField] private float hoodOffsetDegrees = 0f;        // added to the map hood angle (still clamped to hoodMaxAngle)

    [Header("Intake")]
    // angles are measured from the prefab pose (0)
    [SerializeField] private float intakeDeployedAngle = 0f;
    [SerializeField] private float intakeStowedAngle = -106f;   // folded up and back inside the frame
    [SerializeField] private bool agitateWhileShooting = true;
    [SerializeField] private float agitateDelay = 1.5f;         // s after shooting starts
    [SerializeField] private float agitateAngle = -65f;         // arm angle at the top of each swing
    [SerializeField] private float agitateHalfPeriod = 0.4f;    // s per swing direction
    // the raised arm is taller than the trench bar: no agitating within this many m of the trench
    [SerializeField] private float trenchClearDistance = 1f;
    [SerializeField] private float intakeMaxTorque = 400f;       // N*m cap so the intake stalls on trapped fuel instead of lifting the robot

    [Header("Roller speeds (sign = direction)")]
    [SerializeField] private float intakeRpm = -3000f;          // the Fuel Deflector over the intake turns lifted fuel into the hopper
    [SerializeField] private float intakeHoldRpm = -600f;       // deployed but not intaking: keeps fuel from rolling out
    [SerializeField] private float rollerBedRpm = -2500f;       // toward the shooter
    [SerializeField] private float rollerBedReverseRpm = 50f;   // not shooting: gently nudges fuel away from the throat
    [SerializeField] private float kickerRpm = 2500f;
    [SerializeField] private float flywheelSign = -1f;          // flywheel rpm = sign * interpolated rpm
    [SerializeField] private float manualShooterRpm = 3000f;
    [SerializeField] private float passRpmMultiplier = 1.2f;

    [Header("Aiming")]
    [SerializeField] private float shooterYawOffset = 180f;     // 0 = shooter faces the robot's front, 180 = back
    [SerializeField] private float aimTolerance = 3f;           // degrees of drivetrain error allowed while feeding
    // fuel knocks the hood rods off their angle; hold the kicker until the hood is back within this many degrees
    [SerializeField] private float hoodFeedTolerance = 1.5f;

    [Header("Shoot On The Move")]
    // A ball leaves with the robot's velocity added, so 16 aims (and picks range from the maps) at a virtual target:
    // the real target minus the robot's velocity times the ball's time of flight. Time of flight = distance / speed.
    [SerializeField] private bool shootOnTheMove = true;
    [SerializeField] private float sotmBallSpeed = 6f;           // m/s, average horizontal ball speed (lower = more lead)
    [SerializeField, Range(0.01f, 1f)] private float sotmVelocitySmoothing = 0.2f;  // per physics step

    [Header("Debug")]
    [SerializeField] private bool debugShooting = true;         // logs state twice a second while intake / shoot is held

    private float _intakeTarget;
    private float _hoodTarget;
    private bool _intakeDeployed;
    private bool _wasShooting;
    private float _shootStart;
    private bool _agitating;
    private bool _passing;
    private bool _wasEnabled;
    private Vector3 _smoothedVelocity;
    private Vector3 _virtualTarget;

    private RebuiltAutoAimController _aim;
    private Rigidbody _rb;
    private ConfigurableJoint _intakeCfg;
    private BoxCollider _intakeEntranceBlocker;
    private Collider[] _robotColliders;
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
        _intakeCfg = intakeJoint ? intakeJoint.GetComponent<ConfigurableJoint>() : null;
        _intakeEntranceBlocker = intakeJoint ? intakeJoint.transform.Find("IntakeEntranceBlocker")?.GetComponent<BoxCollider>() : null;
        AddHoodBacker();   // before collecting the robot's colliders, so the backer gets the same self-collision ignores
        _robotColliders = GetComponentsInChildren<Collider>(true);

        _intakeDeployed = false;
        _intakeTarget = intakeStowedAngle;
        IgnoreSelfCollisions();

        Scheduler.AddSubsystems(Intake, Hopper, HandleAutoAim, Shooter);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    // Fake solid backer: the hood is three rods with gaps between them. Invisible box colliders bridge each pair of
    // neighbouring rods on the outside of the arc (away from the flywheel), so the hood acts like a solid plate behind
    // the rods. Built from the rods' capsule colliders and the hood's pivot (the flywheel axle), on the hood body.
    private void AddHoodBacker()
    {
        if (!addHoodBacker || hoodJoint == null) return;
        Transform hood = hoodJoint.transform;
        var cj = hood.GetComponent<ConfigurableJoint>();
        Vector3 pivot = cj != null ? cj.anchor : Vector3.zero;   // hood-local flywheel axle

        // rods: capsules on the hood that lie along the hood's X axis (the axle direction)
        var rods = new List<(Vector3 c, float r, float len, CapsuleCollider col)>();
        foreach (var cap in hood.GetComponentsInChildren<CapsuleCollider>(true))
        {
            if (cap.isTrigger) continue;
            Vector3 dir = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 axis = hood.InverseTransformDirection(cap.transform.TransformDirection(dir)).normalized;
            if (Mathf.Abs(axis.x) < 0.95f) continue;
            Vector3 s = cap.transform.lossyScale;
            float radius = cap.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float len = cap.height * Mathf.Abs(Vector3.Dot(s, new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z))));
            rods.Add((hood.InverseTransformPoint(cap.transform.TransformPoint(cap.center)), radius, len, cap));
        }
        if (rods.Count < 2)
        {
            if (debugShooting) Debug.LogWarning($"[BombSquad] hood backer: found {rods.Count} rod colliders, need 2+");
            return;
        }
        // order the rods round the axle
        rods.Sort((a, b) => Mathf.Atan2(a.c.z - pivot.z, a.c.y - pivot.y)
            .CompareTo(Mathf.Atan2(b.c.z - pivot.z, b.c.y - pivot.y)));

        int made = 0;
        for (int i = 0; i + 1 < rods.Count; i++)
        {
            var a = rods[i];
            var b = rods[i + 1];
            Vector3 chord = new Vector3(0f, b.c.y - a.c.y, b.c.z - a.c.z);
            float chordLen = chord.magnitude;
            if (chordLen < 1e-4f) continue;
            Vector3 along = chord / chordLen;
            Vector3 mid = (a.c + b.c) * 0.5f;
            Vector3 normal = new Vector3(0f, -along.z, along.y);          // perpendicular in the YZ plane
            Vector3 outward = new Vector3(0f, mid.y - pivot.y, mid.z - pivot.z);
            if (Vector3.Dot(normal, outward) < 0f) normal = -normal;      // point away from the flywheel
            float rodR = Mathf.Max(a.r, b.r);
            float t = Mathf.Max(0.001f, hoodBackerThickness);
            // inner face at the rods' outer surface minus the inset (0 = just behind the rods, 2 x radius = flush with their inside)
            float innerOffset = rodR - Mathf.Clamp(hoodBackerInset, 0f, 2f * rodR);

            var go = new GameObject($"Hood Backer {made + 1}");
            go.layer = a.col.gameObject.layer;
            go.transform.SetParent(hood, false);
            go.transform.localPosition = mid + normal * (innerOffset + t * 0.5f);
            go.transform.localRotation = Quaternion.LookRotation(along, normal);   // local Z = chord, local Y = outward
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(Mathf.Min(a.len, b.len), t, chordLen + 2f * rodR);   // overlaps the rods: no seam
            box.sharedMaterial = a.col.sharedMaterial != null ? a.col.sharedMaterial : b.col.sharedMaterial;
            made++;
        }
        if (debugShooting) Debug.Log($"[BombSquad] hood backer: {made} colliders across {rods.Count} rods");
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
            _intakeTarget = _intakeDeployed ? intakeDeployedAngle : intakeStowedAngle;
        }

        if (IntakeAction || (_intakeDeployed && (_agitating || shoot))) intakeRollers.SetRpm(intakeRpm);
        else if (_intakeDeployed) intakeRollers.SetRpm(intakeHoldRpm);
        else intakeRollers.SetRpm(0f);

        if (_intakeEntranceBlocker != null) _intakeEntranceBlocker.enabled = _agitating;
    }

    private void Hopper()
    {
        // the bed runs toward the shooter while intaking or shooting, otherwise backs fuel off the throat slightly
        if (ShootHeld && Aligned()) rollerBed.SetRpm(rollerBedRpm);
        else if (IntakeAction) rollerBed.SetRpm(rollerBedRpm);
        else rollerBed.SetRpm(ShootHeld ? 0f : rollerBedReverseRpm);
    }

    private void HandleAutoAim()
    {
        if (BaseGameManager.Instance == null || BaseGameManager.Instance.RobotState != RobotState.Enabled) return;
        // keep the velocity estimate warm so it is settled the moment shooting starts
        Vector3 v = _rb != null ? _rb.velocity : Vector3.zero;
        v.y = 0f;
        _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, v, sotmVelocitySmoothing);

        if (!ShootHeld) return;
        SetTargets();
        if (ManualShoot) return;   // manual shot: the driver aims

        Vector3 target = _aim.GetCurrentTarget();
        _virtualTarget = target;
        if (shootOnTheMove && sotmBallSpeed > 0.1f)
        {
            // iterate: time of flight depends on the distance to the virtual target
            for (int i = 0; i < 3; i++)
            {
                Vector3 d = _virtualTarget - transform.position;
                d.y = 0f;
                float tof = d.magnitude / sotmBallSpeed;
                _virtualTarget = target - _smoothedVelocity * tof;
            }
            // the maps read range to the virtual target, so the shot is lengthened / shortened for the motion too
            flywheelInterpolation.SetCustomTarget(_virtualTarget);
            hoodInterpolation.SetCustomTarget(_virtualTarget);
        }

        float theta = Mathf.Atan2(_virtualTarget.x - transform.position.x, _virtualTarget.z - transform.position.z) * Mathf.Rad2Deg;
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
        if (Mathf.Abs(Mathf.DeltaAngle((float)hoodJoint.GetAngle(), _hoodTarget)) > hoodFeedTolerance) return false;
        return flywheel.AtSpeed();
    }

    private void Shooter()
    {
        if (!ShootHeld)
        {
            flywheel.SetRpm(0f);
            kicker.SetRpm(0f);
            _hoodTarget = 0f;   // CAD pose: lowest, fits under the trench
            DebugState();
            return;
        }

        float rpm = ManualShoot
            ? manualShooterRpm
            : Mathf.Max(0f, (float)flywheelInterpolation.Interpolate() * flywheelScale + flywheelOffsetRpm);
        if (_passing && !ManualShoot) rpm *= passRpmMultiplier;
        flywheel.SetRpm(flywheelSign * rpm);

        float hood = ManualShoot ? manualHoodAngle : (float)hoodInterpolation.Interpolate() + hoodOffsetDegrees;
        _hoodTarget = NearTrench() ? 0f : Mathf.Clamp(hood, -hoodMaxAngle, hoodMaxAngle);

        bool feed = Aligned();
        kicker.SetRpm(feed ? kickerRpm : 0f);
        DebugState();
    }

    private void UpdateJoints()
    {
        bool enabled = BaseGameManager.Instance != null && BaseGameManager.Instance.RobotState == RobotState.Enabled;
        if (!enabled)
        {
            if (_wasEnabled && _rb != null)
            {
                _rb.angularVelocity = Vector3.zero;
            }
            else if (_rb != null && _rb.angularVelocity.sqrMagnitude > 1e-4f)
            {
                _rb.angularVelocity *= Mathf.Exp(-10f * Time.fixedDeltaTime);
            }
        }
        _wasEnabled = enabled;

        // the prefab pose reads as 0
        intakeJoint.SetTargetAngle(_intakeTarget).withAxis(JointAxis.X).useCustomStartingOffset(0f);
        if (_intakeCfg != null && enabled)
        {
            var drive = _intakeCfg.angularXDrive;
            drive.maximumForce = _agitating && intakeMaxTorque > 0f ? intakeMaxTorque : Mathf.Infinity;
            _intakeCfg.angularXDrive = drive;
        }
        hoodJoint.SetTargetAngle(_hoodTarget).withAxis(JointAxis.X).useCustomStartingOffset(0f);
    }

    private void WobbleIntake()
    {
        float elapsed = Mathf.Max(0f, Time.time - (_shootStart + agitateDelay));
        float phase = elapsed / Mathf.Max(0.05f, agitateHalfPeriod);
        float t = 0.5f - 0.5f * Mathf.Cos(phase * Mathf.PI);
        _intakeTarget = Mathf.Lerp(intakeDeployedAngle, agitateAngle, t);
    }

    private void DebugState()
    {
        if (!debugShooting || !(ShootHeld || IntakeAction) || Time.time < _nextDebug) return;
        _nextDebug = Time.time + 0.5f;
        Debug.Log($"[BombSquad] intake={(bool)IntakeAction} deployed={_intakeDeployed} " +
                  $"arm={(float)intakeJoint.GetAngle():F0}/{_intakeTarget:F0} agitating={_agitating} | " +
                  $"outtake={(bool)OuttakeAction} manual={(bool)ManualShoot} pass={(bool)AutoPass} passing={_passing} " +
                  $"aimErr={_aim.GetRotationError():F1} aligned={Aligned()} " +
                  $"hood={Mathf.DeltaAngle(0f, (float)hoodJoint.GetAngle()):F1}/{_hoodTarget:F1} nearTrench={NearTrench()} " +
                  $"speed={_smoothedVelocity.magnitude:F1}m/s lead={(_virtualTarget - _aim.GetCurrentTarget()).magnitude:F2}m");
    }
}
