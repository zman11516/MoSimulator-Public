using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

// FRC 3937 Breakaway: turret shooter with an adjustable hood, pivoting over-the-bumper intake, roller-bed hopper with
// a telescoping front extension, feeder tower. No climber in the CAD. Built on the docs' Rebuilt turret template
// (RebuiltTurretRobot): zone dispatch (AllianceZone / NeutralZone) and shooter dispatch (Intaking / Shooting / ... / Feed)
// come from the base.
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
    [SerializeField] private float intakeStowedAngle = -100f;     // folded up over the hopper (flip the sign if it swings down)
    [SerializeField] private float hopperExtendedDistance = 12f;  // inches the extension slides out (set from the CAD)
    [SerializeField] private float hoodMaxAngle = 30f;            // degrees from the CAD pose (flip the map's sign if it moves the wrong way)
    [SerializeField] private float manualHoodAngle = 15f;
    [SerializeField] private float manualShooterRpm = 3000f;
    [SerializeField] private float turretTolerance = 5f;          // degrees

    private float _intakeTarget;
    private bool _intakeDeployed;
    private float _hoodTarget;
    private bool _hopperOut;
    private bool _hopperLocked;

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
        _intakeDeployed = false;
        _intakeTarget = intakeStowedAngle;
        _hoodTarget = 0f;

        _hopperOut = false;
        _hopperLocked = false;
        IgnoreSelfCollisions();

        Scheduler.AddSubsystems(IntakeArm);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
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
        _intakeTarget = _intakeDeployed ? intakeDeployedAngle : intakeStowedAngle;
        // the hopper extension goes out the first time the intake deploys (start of match) and stays out
        if (_intakeDeployed) _hopperOut = true;
        DebugState();
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

    protected override void HandleSwm()
    {
        // Assign the inherited swmRef (with a parent) and swmSettings in the Inspector.
        if (rb != null && swmRef != null && swmRef.parent != null)
        {
            base.HandleSwm();
        }
    }

    // ---- aiming: the turret tracks the hub in the alliance zone and the closest pass target elsewhere
    protected override void AllianceZone()
    {
        turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
        flywheelInterpolation.SetDefaultMap(DefaultMap.Hub);
        hoodInterpolation.SetDefaultMap(DefaultMap.Hub);
    }

    protected override void NeutralZone()
    {
        turret.SetPassMapClosest();
        flywheelInterpolation.SetPassMapClosest();
        hoodInterpolation.SetPassMapClosest();
    }

    // ---- rollers: each runs at its Inspector rpm unless stopped here (Stop() lasts one frame)
    protected override void Intaking()
    {
        // intake + hopper run; nothing reaches the shooter
        feederRollers.Stop();
        kickerRollers.Stop();
        kickerBelts.Stop();
        shooterWheels.SetRpm(0f);
        _hoodTarget = 0f;
    }

    protected override void IntakingAndShooting()
    {
        SpinUpAndHold();
    }

    protected override void Shooting()
    {
        intakeRollers.Stop();
        SpinUpAndHold();
    }

    protected override void Feed()
    {
        // Aligned(): leave the feeder, kicker and hopper running (no Stop this frame)
    }

    protected override void Idling()
    {
        intakeRollers.Stop();
        hopperRollers.Stop();
        feederRollers.Stop();
        kickerRollers.Stop();
        kickerBelts.Stop();
        shooterWheels.SetRpm(0f);
        _hoodTarget = 0f;
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
        if (ManualShoot) return true;   // manual shot: feed without waiting for the turret
        float error = turret.GetError();
        bool atSpeed = shooterWheels.AtSpeed();
        return Mathf.Abs(error) < turretTolerance && atSpeed;
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
        Debug.Log($"[Breakaway] intake={(bool)IntakeAction} deployed={_intakeDeployed} arm={(float)intakeJoint.GetAngle():F0}/{_intakeTarget:F0} " +
                  $"hopperOut={_hopperOut} locked={_hopperLocked} hopperPos={hopperIn:F1}in/{hopperExtendedDistance:F1} | " +
                  $"outtake={(bool)OuttakeAction} manual={(bool)ManualShoot} pass={(bool)AutoPass} turretErr={err:F1} aligned={Aligned()} " +
                  $"hood={_hoodTarget:F1} | intake {Rpm(intakeRollers)} | hopper {Rpm(hopperRollers)} | feeder {Rpm(feederRollers)} | " +
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
        var parts = new System.Collections.Generic.List<string>();
        foreach (var inst in list)
        {
            var roller = (inst as RollerGroup.RollerInstance)?.GetInteractionRoller();
            if (roller != null) parts.Add($"{roller.realRpm:F0}/{roller.TargetRpm:F0}");
        }
        return string.Join(" ", parts);
    }

    private void SpinUpAndHold()
    {
        float rpm = ManualShoot ? manualShooterRpm : (float)flywheelInterpolation.Interpolate();
        float hood = ManualShoot ? manualHoodAngle : (float)hoodInterpolation.Interpolate();
        shooterWheels.SetRpm(rpm);
        _hoodTarget = Mathf.Clamp(hood, -hoodMaxAngle, hoodMaxAngle);
        if (!Aligned())
        {
            // hold fuel until the turret is on target and the wheels are at speed
            feederRollers.Stop();
            kickerRollers.Stop();
            kickerBelts.Stop();
        }
    }
}
