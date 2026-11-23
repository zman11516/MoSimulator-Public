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
using UnityEngine.Audio;

// FRC 3937 Breakaway: turret shooter with an adjustable hood, pivoting over-the-bumper intake, roller-bed hopper with
// a telescoping front extension, feeder tower. No climber in the CAD. Built on the docs' Rebuilt turret template
// (RebuiltTurretRobot): zone dispatch (AllianceZone / NeutralZone) and shooter dispatch (Intaking / Shooting / ... / Feed)
// come from the base.
//
// Shooting ("Direct Launch", on by default): the CAD shooter feeds fuel up the turret centre, ~1.5" off the flywheel /
// hood-wheel pinch, and the roller physics never launched it consistently (jams, short shots, 2-3 deg side scatter,
// balls out the back). Instead, when a ball reaches the turret throat and the shot is ready, it is placed just above
// the shooter exit and given the exact ballistic velocity to the target - the same way the field's Outpost launches
// fuel (fuel has no drag). The world velocity is set directly, so robot motion is compensated automatically. The
// wheels and hood still move for looks. Turn Direct Launch off to go back to pure roller physics.
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
    [SerializeField] private float turretTolerance = 1.5f;        // degrees (roller-physics mode only)
    [SerializeField] private float trenchClearDistance = 0.8f;    // m before the trench bar: hood forced down, no firing

    [Header("Hopper Agitation")]
    // While shooting (and not intaking) for agitateDelay seconds, a deployed intake arm swings between deployed and
    // agitateAngle, pushing fuel in the hopper back toward the feeder; the intake rollers turn slowly inward meanwhile.
    [SerializeField] private bool agitateWhileShooting = true;
    [SerializeField] private float agitateDelay = 1.5f;           // s after shooting starts
    [SerializeField] private float agitateAngle = -40f;           // arm angle at the top of each swing (stowed side)
    [SerializeField] private float agitateHalfPeriod = 0.4f;      // s per swing direction
    [SerializeField] private float agitateRollerDivider = 4f;     // intake rollers at 1/4 speed while agitating
    // A deployed intake whose rollers were fully stopped left the front of the hopper open: fuel rolled back out of
    // it while driving / agitating with a full hopper. Like 1706, a deployed intake keeps turning slowly inward.
    // Play-mode probe: fuel left lying in the intake mouth (on the floor under the arm) when intake was released; at
    // 1/5 speed the rollers could not lift it over, and it rolled out when the robot backed up.
    [SerializeField] private float holdRollerDivider = 2f;        // deployed, not intaking: intake rollers at 1/2 speed
    [SerializeField] private float intakeRunOn = 1f;              // s at full speed after intake is released (clears the mouth)
    [SerializeField] private float hopperPulseInterval = 0.9f;    // s between brief hopper-roller pauses while shooting
    [SerializeField] private float hopperPulsePause = 0.12f;      // s pause to relieve bridging pressure at the feeder mouth

    [Header("Direct Launch")]
    [SerializeField] private bool directLaunch = true;
    [SerializeField] private LayerMask fuelLayers;                // empty = the "GamePiece1" layer (fuel)
    // turret-local metres (+Y up, -X = the way the shooter faces). The throat is where fuel rises into the turret,
    // before it reaches the flywheel; the launch point is above the hood wheel, clear of every roller.
    // the probe catches any ball that TOUCHES it, so a 2 cm probe takes ball centres within ~9.5 cm: the column only
    [SerializeField] private Vector3 throatCenter = new Vector3(0f, 0.06f, 0f);
    [SerializeField] private float throatRadius = 0.02f;
    [SerializeField] private Vector3 launchPoint = new Vector3(-0.03f, 0.28f, 0f);
    [SerializeField] private float launchInterval = 0.12f;        // s between balls (~8 balls/s)
    [SerializeField] private float hubApexAboveTarget = 0.9f;     // m the arc peaks above the hub aim point (steeper entry)
    [SerializeField] private float hubApexPerMeter = 0.25f;       // ...or this x distance if higher: keeps long shots
                                                                  // dropping in at ~50 deg instead of skimming the rim
    [SerializeField] private float passApexAboveTarget = 1.8f;    // m the arc peaks above a pass target
    [SerializeField] private float shotSpread = 0f;               // m of random miss at the target (0 = perfect)
    [SerializeField] private float launchTurretTolerance = 8f;    // degrees: the turret must point roughly at the target
    [SerializeField] private float manualShotSpeed = 9f;          // m/s straight out of the turret (Manual Shoot)
    [SerializeField] private float manualShotElevation = 55f;     // degrees
    [SerializeField] private float ignoreTurretTime = 0.3f;       // s a launched ball ignores the turret's colliders

    [Header("Audio")]
    [SerializeField] private AudioClip shotClip;
    [SerializeField] private float shotVolume = 0.25f;
    [SerializeField] private float shotPitch = 1.15f;

    [Header("Fuel Backstop")]
    // Roller-physics mode: fuel rising through the turret centre could be flicked out the back by the flywheel's
    // lower edge; this frictionless wall closes that gap. Turret-local metres. (Harmless in Direct Launch.)
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

    private float _intakeRunOnUntil;

    private bool ShootHeld =>OuttakeAction || ManualShoot || AutoPass;
    private bool NearTrench() => turret != null && UnderTrench(turret.transform, trenchClearDistance);

    private float _nextLaunch;
    private float _lastFlightTime;
    private int _launched;
    private AudioSource _shotSource;
    private Collider[] _turretColliders;
    private readonly Collider[] _throatHits = new Collider[16];
    private readonly List<(Collider[] ball, float until)> _ignored = new();

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
        if (fuelLayers.value == 0) fuelLayers = 1 << LayerMask.NameToLayer("GamePiece1");

        _hopperOut = false;
        _hopperLocked = false;
        AddFuelBackstop();          // before IgnoreSelfCollisions so the wall ignores the robot's own colliders
        IgnoreSelfCollisions();
        InitAudio();

        Scheduler.AddSubsystems(IntakeArm);
        Scheduler.AddProtectedSystem(UpdateJoints);
        Scheduler.AddLateRecursive(UpdatePids);
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

    private void UpdatePids()
    {
        turret.UpdatePid(turretPid);
        hoodJoint.UpdatePid(hoodPid);
        intakeJoint.UpdatePid(intakePid);
        hopperJoint.UpdatePid(hopperPid);
    }

    private void IntakeArm()
    {
        if (IntakeAction)
        {
            _intakeDeployed = true;
            _intakeRunOnUntil = Time.time + intakeRunOn;   // keeps refreshing while held; counts down on release
        }
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
            // the turret leads a moving target by the real flight time of the last direct-launch shot
            if (directLaunch && _lastFlightTime > 0f) swmSettings.timeOfFlight = _lastFlightTime;
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

    // deployed: turn slowly inward so fuel can't roll back out the front; stowed: stopped
    private void HoldIntake()
    {
        if (!_intakeDeployed) intakeRollers.Stop();
        else if (Time.time >= _intakeRunOnUntil) intakeRollers.DivideSpeed(holdRollerDivider);
        // else: just released - full speed (no override) to pull in what is still in the mouth
    }

    protected override void Feed()
    {
        // Aligned(): leave the feeder, kicker and hopper running (no Stop this frame) and launch what reaches the throat
        if (directLaunch) TryLaunch();
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
        RestoreTurretCollisions();
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
        if (directLaunch) return error < launchTurretTolerance;
        return error < turretTolerance && shooterWheels.AtSpeed();
    }

    private void SpinUpAndHold()
    {
        // wheels and hood follow the maps (the real shot in roller-physics mode, looks only in Direct Launch)
        float rpm = ManualShoot ? manualShooterRpm : (float)flywheelInterpolation.Interpolate();
        float hood = ManualShoot ? manualHoodAngle : (float)hoodInterpolation.Interpolate();
        shooterWheels.SetRpm(rpm);
        _hoodTarget = NearTrench() ? hoodStowAngle : Mathf.Clamp(hood, -hoodMaxAngle, hoodMaxAngle);
        if (!Aligned())
        {
            // hold fuel until the turret is on target (and, for roller physics, the wheels are at speed)
            feederRollers.Stop();
            kickerRollers.Stop();
            kickerBelts.Stop();
        }
        else if (hopperPulseInterval > 0f && hopperPulsePause > 0f)
        {
            float elapsed = Time.time - _shootStart;
            if (elapsed >= hopperPulseInterval && (elapsed % hopperPulseInterval) < hopperPulsePause)
                hopperRollers.Stop();
        }
    }

    // ---- direct launch
    private void TryLaunch()
    {
        if (BaseGameManager.Instance == null || BaseGameManager.Instance.RobotState != RobotState.Enabled) return;
        if (Time.time < _nextLaunch) return;

        Transform t = turret.transform;
        int n = Physics.OverlapSphereNonAlloc(t.TransformPoint(throatCenter), throatRadius, _throatHits, fuelLayers,
                                              QueryTriggerInteraction.Ignore);
        Rigidbody ball = null;
        for (int i = 0; i < n && ball == null; i++)
        {
            var body = _throatHits[i].attachedRigidbody;
            if (body != null && !body.isKinematic && body != rb) ball = body;
        }
        if (ball == null) return;

        Vector3 start = t.TransformPoint(launchPoint);
        Vector3 velocity;
        if (ManualShoot)
        {
            // straight out of the shooter: the turret's -X, tilted up
            Vector3 flat = Vector3.ProjectOnPlane(-t.right, Vector3.up).normalized;
            velocity = Quaternion.AngleAxis(-manualShotElevation, Vector3.Cross(Vector3.up, flat)) * flat * manualShotSpeed;
        }
        else
        {
            Vector3 target = turret.GetCurrentTarget();
            if (target == Vector3.zero) return;   // no target assigned yet
            if (shotSpread > 0f)
            {
                Vector2 r = Random.insideUnitCircle * shotSpread;
                target += new Vector3(r.x, 0f, r.y);
            }
            float range = Vector3.ProjectOnPlane(target - start, Vector3.up).magnitude;
            float apexAbove = _passing ? passApexAboveTarget : Mathf.Max(hubApexAboveTarget, hubApexPerMeter * range);
            velocity = BallisticVelocity(start, target, apexAbove, out _lastFlightTime);
        }

        // keep the ball off the turret / hood / rollers' colliders while it leaves
        var ballColliders = ball.GetComponentsInChildren<Collider>();
        _turretColliders ??= t.GetComponentsInChildren<Collider>(true);
        SetIgnore(ballColliders, true);
        _ignored.Add((ballColliders, Time.time + ignoreTurretTime));

        ball.position = start;
        ball.transform.position = start;
        ball.velocity = velocity;
        ball.angularVelocity = Vector3.zero;
        _nextLaunch = Time.time + launchInterval;
        _launched++;
        if (_shotSource != null && shotClip != null)
        {
            _shotSource.pitch = shotPitch * Random.Range(0.95f, 1.05f);
            _shotSource.PlayOneShot(shotClip, shotVolume);
        }
    }

    private void InitAudio()
    {
        if (shotClip == null) return;
        _shotSource = gameObject.AddComponent<AudioSource>();
        _shotSource.playOnAwake = false;
        _shotSource.loop = false;
        _shotSource.spatialBlend = 0.4f;
        var mixer = Resources.Load<AudioMixerGroup>("MainAudioMixer");
        if (mixer != null)
        {
            var groups = mixer.audioMixer.FindMatchingGroups("RobotSounds");
            if (groups.Length > 0) _shotSource.outputAudioMixerGroup = groups[0];
        }
    }

    // Peak the arc `apexAbove` m over the higher of start / target, then come down onto the target: steep entry into the
    // hub at any range. Fuel has no drag, so plain projectile motion is exact.
    private static Vector3 BallisticVelocity(Vector3 start, Vector3 target, float apexAbove, out float flightTime)
    {
        float g = Mathf.Max(0.01f, -Physics.gravity.y);
        float apex = Mathf.Max(start.y, target.y) + Mathf.Max(0.05f, apexAbove);
        float vy = Mathf.Sqrt(2f * g * (apex - start.y));
        float tUp = vy / g;
        float tDown = Mathf.Sqrt(2f * (apex - target.y) / g);
        flightTime = tUp + tDown;
        Vector3 flat = target - start;
        flat.y = 0f;
        return flat / flightTime + Vector3.up * vy;
    }

    private void RestoreTurretCollisions()
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
        if (_turretColliders == null) return;
        foreach (var b in ball)
        {
            if (b == null) continue;
            foreach (var c in _turretColliders)
                if (c != null && !c.isTrigger) Physics.IgnoreCollision(b, c, ignore);
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
                  $"agitating={_agitating} nearTrench={NearTrench()} turretErr={err:F1} aligned={Aligned()} dist={dist:F2}m flight={_lastFlightTime:F2}s launched={_launched} | " +
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
