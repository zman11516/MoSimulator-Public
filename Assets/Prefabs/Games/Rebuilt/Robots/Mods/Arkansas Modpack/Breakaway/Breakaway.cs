using Games.Rebuilt.Components;
using Games.Rebuilt.Robots;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

// FRC 3937 Breakaway: turret shooter (fixed hood angle), pivoting over-the-bumper intake, roller-bed hopper,
// feeder tower. No climber in the CAD. Built on the docs' Rebuilt turret template (RebuiltTurretRobot):
// zone dispatch (AllianceZone / NeutralZone) and shooter dispatch (Intaking / Shooting / ... / Feed) come from the base.
public class Breakaway : RebuiltTurretRobot
{
    [Header("Joints")]
    [SerializeField] private GenericTurret turret;        // "Turret": GenericJoint (Y axis) + GenericTurret
    [SerializeField] private GenericJoint intakeJoint;    // "Intake Arm": GenericJoint (X axis), CAD pose = deployed

    [Header("Rollers")]
    // Roller groups (made by Guess Interaction Roller; each roller's running rpm and motor are set in the Inspector)
    [SerializeField] private RollerGroup intakeRollers;   // intake tubes on the arm
    [SerializeField] private RollerGroup hopperRollers;   // roller-bed floor rollers
    [SerializeField] private RollerGroup feederRollers;   // feeder tower rollers + flex wheels
    [SerializeField] private RollerGroup kickerRollers;   // flex wheels feeding the shooter wheels
    [SerializeField] private RollerGroup shooterWheels;   // urethane shooter wheels, brass flywheel, compliant wheels

    [Header("PIDs")]
    [SerializeField] private PidConstants turretPid;
    [SerializeField] private PidConstants intakePid;

    [Header("Interpolation")]
    [SerializeField] private RebuiltInterpolation flywheelInterpolation;   // distance (m) -> shooter rpm

    [Header("Mechanism Values")]
    [SerializeField] private float intakeDeployedAngle = 0f;      // the CAD pose is deployed
    [SerializeField] private float intakeStowedAngle = -100f;     // folded up over the hopper (flip the sign if it swings down)
    [SerializeField] private float manualShooterRpm = 3000f;
    [SerializeField] private float turretTolerance = 5f;          // degrees

    private float _intakeTarget;
    private bool _intakeDeployed;

    protected override void Init()
    {
        // Registers HandleSwm, zone dispatch, and shooter-state dispatch exactly once.
        base.Init();
        turret.SetPid(turretPid);
        intakeJoint.SetPid(intakePid);
        flywheelInterpolation.Initialize(this);
        _intakeDeployed = false;
        _intakeTarget = intakeStowedAngle;

        Scheduler.AddSubsystems(IntakeArm);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    private void UpdatePids()
    {
        turret.UpdatePid(turretPid);
        intakeJoint.UpdatePid(intakePid);
    }

    private void IntakeArm()
    {
        if (IntakeAction) _intakeDeployed = true;
        if (StowAction) _intakeDeployed = false;
        _intakeTarget = _intakeDeployed ? intakeDeployedAngle : intakeStowedAngle;
    }

    private void UpdateJoints()
    {
        turret.Align();
        // the prefab holds the arm in its CAD (deployed) pose: that pose reads as intakeDeployedAngle
        intakeJoint.SetTargetAngle(_intakeTarget).withAxis(JointAxis.X).useCustomStartingOffset(intakeDeployedAngle);
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
    }

    protected override void NeutralZone()
    {
        turret.SetPassMapClosest();
        flywheelInterpolation.SetPassMapClosest();
    }

    // ---- rollers: each runs at its Inspector rpm unless stopped here (Stop() lasts one frame)
    protected override void Intaking()
    {
        // intake + hopper run; nothing reaches the shooter
        feederRollers.Stop();
        kickerRollers.Stop();
        shooterWheels.SetRpm(0f);
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
        shooterWheels.SetRpm(0f);
    }

    protected override bool Aligned()
    {
        float error = turret.GetError();
        bool atSpeed = shooterWheels.AtSpeed();
        return Mathf.Abs(error) < turretTolerance && atSpeed;
    }

    private void SpinUpAndHold()
    {
        float rpm = ManualShoot ? manualShooterRpm : (float)flywheelInterpolation.Interpolate();
        shooterWheels.SetRpm(rpm);
        if (!Aligned())
        {
            // hold fuel until the turret is on target and the wheels are at speed
            feederRollers.Stop();
            kickerRollers.Stop();
        }
    }
}
