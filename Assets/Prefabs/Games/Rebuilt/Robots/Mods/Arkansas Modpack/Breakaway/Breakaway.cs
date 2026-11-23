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
// Fuel is never moved or given a velocity by script.
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
        _intakeDeployed = false;
        _intakeTarget = intakeStowedAngle;
        _hoodTarget = hoodStowAngle;

        _hopperOut = false;
        _hopperLocked = false;
        AddFuelBackstop();
        AddIntakeEntranceBlocker();

        Scheduler.AddSubsystems(IntakeArm);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    protected override void LateInit()
    {
        base.LateInit();
        // Run after every Start() so InteractionRoller's runtime Wheel_InternalCore colliders are ignored too.
        IgnoreSelfCollisions();
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
