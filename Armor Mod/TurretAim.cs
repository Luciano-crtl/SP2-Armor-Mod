using System;
using System.Text;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.Weapons;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Combat;
using Assets.Scripts.Flight.Cameras;
using Assets.Scripts.Input;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace ArmorFragments
{
    internal enum TurretFireMouse { Left, Middle }

    // How mouse aim looks and zooms: World of Tanks (two-layer reticle, notched zoom, the wheel into the sight, the sight view turns with the
    // mouse) or War Thunder (a ring and a dot per gun, smooth zoom, Shift into the sight, the right button zooms more, the sight view stays on
    // the gun).

    // Mouse aim for turrets, in the style of War Thunder's third person view.
    //
    //  - Rotators marked [TUR] (the designer's "Turret" toggle) are turned by this code; cannons marked [PTR] ("Pointer" toggle)
    //    are the ones it points. The mouse turns the game's orbit camera, and the turret turns until the pointer cannon points
    //    at what is under the circle in the middle of the screen. A small marker shows where the cannon really points.
    //  - Hold Alt: the cursor comes back for the game's own interface. Hold C: the camera is freed from the turret, which keeps
    //    pointing where it was while you look around; let go and the camera comes back gradually. Right button: gradual zoom.
    //    Left button: fire.
    //
    // How the turret is turned. A rotator already is a position servo with a speed limit (JointRotatorScript turns its angle
    // toward _targetAngle at its own speed), so what is missing is only which angle to ask for. Every physics step this works out
    // the rotation that takes the cannon's barrel onto the wanted direction, splits it between the marked rotators by their
    // world axes (a small least-squares problem, so any layout of turret ring and elevation works), and asks each rotator for
    // its current angle plus its share of the rotation. The barrel is measured every step, so a vehicle that pitches or rolls
    // is corrected at once (the gun is stabilized). Which way each rotator turns the barrel is not assumed: when the aim
    // starts, each rotator is moved a few degrees and the barrel's motion is measured.
    internal static class TurretAim
    {
        public static bool Active { get; private set; }

        // Shown in the panel.
        public static string Status = "Off";

        private sealed class Turret
        {
            public JointRotatorScript Script;
            public string Name;
            public float Range;          // degrees either way (the larger of Low and High; a rotator that wraps has no limit)
            public float Low, High;      // the angles (the game's _angle) the rotator can be asked for: what its input controller allows, not just its Range
            public bool Wraps;
            public string LimitSource = "";   // for the log
            public float DirectV;        // DirectControl: the speed (degrees a second, in the rotator's angle) the rotator is commanded to turn at
            public bool Marked;          // has the old [TUR] mark (a rotator that only some turret links is not a candidate for the turrets that link nothing)
            public bool Done;            // calibrated (or given up on)
            public bool Usable;          // moves at least one of the guns
            public float[] Signs;        // per gun: +1 / -1 the gun's barrel turns this way about Axis when the angle grows, 0 = does not move it
            public float[] Scales;       // per gun: how many degrees the barrel turns for a degree of the angle (measured; a bit under 1)
            public float[] Lags;         // per gun: how long (s) the barrel takes to follow the angle (measured; 0 = not known)
            public float GapFilter;      // the smoothed gap this rotator is closing (radians), while several turrets share the hull
            public bool GapFilterKnown;

            // The game turns a rotator to (what its input controller says, times Range): _targetAngle = Value * Range, and Value is the input axis scaled
            // by the controller's min and max. A player who feeds it a Funky Trees expression (x/90 with Range 90: the angle x itself) can ask for far more
            // than +-Range, and the rotator follows (nothing in the game clamps _targetAngle). So the limits come from the controller:
            //  - a plain axis: -1..1 through min and max (with the invert);
            //  - clamp(expression, lo, hi): lo..hi the same way;
            //  - any other expression: unknown, so no limit (the game's own setup is what the player wants).
            // Returns where the limits came from, for the log.
            public Turret WithLimits(PartScript part, JointRotatorScript script)
            {
                Turret turret = this;
                float range = script.JointRotator.Range;
                turret.Low = -range;
                turret.High = range;
                turret.Wraps = script.JointRotator.ShortestAngle;
                turret.LimitSource = turret.Wraps ? "shortest-angle rotator" : "the rotator's own range";
                try
                {
                    InputControllerScript controller = part != null ? part.GetModifier<InputControllerScript>() : null;
                    InputControllerData input = controller != null ? controller.InputController : null;
                    if (input != null && !turret.Wraps)
                    {
                        string expression = (input.Input ?? "").Trim();
                        float lo = -1f, hi = 1f;
                        bool known = false, unlimited = false;
                        if (expression.Length == 0 || string.Equals(expression, "Disabled", StringComparison.OrdinalIgnoreCase)) { }
                        else if (Array.Exists(PlainAxes, name => string.Equals(name, expression.TrimStart('-'), StringComparison.OrdinalIgnoreCase))) known = true;
                        else
                        {
                            System.Text.RegularExpressions.Match match = ClampCall.Match(expression);
                            float a, b;
                            if (SweepAxis(expression, out a, out b))
                            {
                                lo = a;
                                hi = b;
                                known = true;
                            }
                            else if (match.Success &&
                                float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a) &&
                                float.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b))
                            {
                                lo = Mathf.Min(a, b);
                                hi = Mathf.Max(a, b);
                                known = true;
                            }
                            else unlimited = true;
                        }
                        if (known)
                        {
                            if (input.Invert && input.InvertType == InvertType.Axis) { float swap = lo; lo = -hi; hi = -swap; }
                            float v1 = ControllerValue(input, lo), v2 = ControllerValue(input, hi);
                            float low = Mathf.Min(v1, v2), high = Mathf.Max(v1, v2);
                            if (lo <= 0f && hi >= 0f) { low = Mathf.Min(low, 0f); high = Mathf.Max(high, 0f); }
                            turret.Low = low * range;
                            turret.High = high * range;
                            turret.LimitSource = "from its input '" + expression + "'";
                            if (turret.High - turret.Low > 360.5f)
                            {
                                turret.Wraps = true;   // it can be asked for more than a full turn: no limit
                                turret.LimitSource = "its input '" + expression + "' reaches " + turret.Low.ToString("F0") + " to " + turret.High.ToString("F0") + " deg: not limited";
                            }
                        }
                        else if (unlimited)
                        {
                            turret.Wraps = true;   // no limit: the barrel's own geometry decides which way is shorter
                            turret.LimitSource = "its input '" + expression + "' is an expression with no clamp: not limited";
                        }
                    }
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: couldn't read the input controller of '" + turret.Name + "' (" + e.Message + "); using its range");
                }
                turret.Range = Mathf.Max(Mathf.Abs(turret.Low), Mathf.Abs(turret.High));
                return turret;
            }
            // A rotator picked as a gun's Traverse or Elevation: its limits are only the game's own sliders (Range, and the Min and Max of its input),
            // or none at all when it is Unlimited (the game's shortest-angle mode). An expression on its input is for driving without mouse aim and is
            // not read here.
            public Turret WithStockLimits(PartScript part, JointRotatorScript script)
            {
                Turret turret = this;
                float range = script.JointRotator.Range;
                turret.Low = -range;
                turret.High = range;
                turret.Wraps = script.JointRotator.ShortestAngle;
                turret.LimitSource = turret.Wraps ? "Unlimited" : "its Range";
                if (!turret.Wraps)
                {
                    try
                    {
                        InputControllerScript controller = part != null ? part.GetModifier<InputControllerScript>() : null;
                        InputControllerData input = controller != null ? controller.InputController : null;
                        if (input != null)
                        {
                            float lo = -1f, hi = 1f;
                            if (input.Invert && input.InvertType == InvertType.Axis) { float swap = lo; lo = -hi; hi = -swap; }
                            float v1 = ControllerValue(input, lo), v2 = ControllerValue(input, hi);
                            float low = Mathf.Min(Mathf.Min(v1, v2), 0f), high = Mathf.Max(Mathf.Max(v1, v2), 0f);
                            turret.Low = low * range;
                            turret.High = high * range;
                            turret.LimitSource = "its Range and its input's Min and Max";
                        }
                    }
                    catch (Exception e)
                    {
                        ArmorFragmentsPlugin.Log.LogWarning("Turret aim: couldn't read the input controller of '" + turret.Name + "' (" + e.Message + "); using its range");
                    }
                }
                turret.Range = turret.Wraps ? Mathf.Max(180f, range) : Mathf.Max(Mathf.Abs(turret.Low), Mathf.Abs(turret.High));
                return turret;
            }

            public bool Solo;            // the batch test could not place it: it is turned alone
            public HashSet<int> Expect;  // the guns it should move, from the way the bodies are joined (null = can't tell)
            public readonly List<float> HistoryTime = new List<float>();    // the angle the rotator had, step by step (the last second)
            public readonly List<float> HistoryAngle = new List<float>();

            // Where the part really is (see GuardRotator): the game's own "Current Angle" of the joint, turned into the units of the rotator's angle by
            // a sign and an anchor measured when the rotator is calibrated. PhysSign 0 = not known (the guard is off for this rotator).
            public float PhysSign;
            public float PhysAngle;      // the part's real angle, in the rotator's own units (unwrapped)
            public float PhysBeta;       // the joint's measured angle the last time
            public float PhysRate;       // how fast the part really turns (deg/s), smoothed
            public float CommandRate;    // how fast the commanded angle turns (deg/s), smoothed
            public float PreviousCommand;
            public bool PhysKnown;
            public float StallSince = -1f;
            public bool Blocked;         // an obstacle: the rotator has stopped pushing
            public float BlockDirection; // which way it was being turned when it met it
            public float BlockAngle;     // where the part stopped
            public float BlockedAt;
            public float LastLogged = -10f;
            public float AheadBase;      // how far the asked-for angle sits from the part's when nothing moves (a heavy barrel sags on its drive): not a push
            public int Strikes;          // blocked again at the same place: each time it waits twice as long before trying again
            public float LastBlockAngle, LastBlockEnd = -100f;
        }

        // A turret: a cannon the mouse aim points, in a turret group (the turrets of the active group are aimed at the same point at the same time;
        // they are equals). Each is turned by its own rotators.
        private sealed class Gun
        {
            public CannonScript Cannon;
            public int GroupId;
            public string TurretName;  // the turret's own name (null = none)
            public HashSet<JointRotatorScript> Linked;   // the rotators the designer linked to this turret ([PTR r=]); null = none linked, the mod works out which ones move it
            public bool FiresByKey;    // fires with a key instead of the mouse button
            public Key FireKey;        // that key (its own, or the fire key of the config)
            public float NextFire;     // Time.time of the next shot
            public float ReloadTotal;  // the wait set by the last shot (s), for the reload ring
            public float RangeToHit = -1f;   // meters from the muzzle to what the barrel points at (-1 = nothing within reach), for the reticle
            public float CircleRadius = -1f; // the reticle's gun circle, eased (pixels; -1 = not drawn yet)
            public CameraVantageScript Sight;
            public bool GunAsSight;    // its own barrel is its sight (when it has no Sight)
            public bool RangingGun;    // the gun a shared sight's range marks are worked out for
            public float[] RangeDrop;  // War Thunder style range marks: how far (degrees) below the barrel line a target at each RangeSteps distance sits (-1 = out of reach)
            public float Caliber;      // mm, for choosing a shared sight's ranging gun
            public readonly List<FixedCannon> FixedCannons = new List<FixedCannon>();
            public readonly List<Turret> Drivers = new List<Turret>();   // the rotators that turn this turret and no earlier turret of its group
            public readonly List<Turret> Chain = new List<Turret>();     // the rotators between this gun and the hull, the one nearest the gun first (found by FindHull)
            public Rigidbody Hull;
            public Vector3 PreviousBarrel;
            public bool BarrelKnown;
            public Vector3 Spin;       // the barrel's angular velocity (rad/s), smoothed
            public Vector3 HullSpin;   // the hull's angular velocity (rad/s), smoothed
            public Vector3 FastSpin;   // the barrel's angular velocity (rad/s), hardly smoothed: the braking term works from it
            public Vector3 Want;       // where the gun should point (the cursor), world direction: what firing is judged against
            public float AimError;     // degrees between the barrel and Want
            public float AimStamp = -10f;   // Time.fixedTime of the last measure
            public bool LineBlocked;        // the barrel points at a part of its own vehicle (checked a few times a second for the flight-screen bars)
            public bool Pinned;             // a rotator of this turret is held at its limit (it can't reach what it is asked to: it does not push the hull, so it is not counted in the share)
            public float Lag;          // how long the barrel takes to follow its rotators (s), the slowest of them; 0 = not measured

            // Swinging (each turret has its own): the error changing side over and over cuts this turret's gain.
            public float PreviousTheta;   // the last signed gap (degrees) of the rotator with the biggest one
            public int Share = 1;         // turrets of the active group on the same hull body (itself included)
            public float GainScale = 1f;
            public float LagBoost;        // seconds added to the measured lag after the turret swung (the measured one was too short at the speed it swings)
            public float LastSwing, LastRecover;
            public readonly List<float> Flips = new List<float>();
        }

        // A cannon fixed to a turret: it moves with it and fires with its own input, but nothing aims it by itself.
        private sealed class FixedCannon
        {
            public CannonScript Cannon;
            public bool ByKey;
            public float NextFire;
        }

        // The vehicle's main set: every Mouse aim gun that is in no group. It is aimed and fired like a group (and is the only one of a tank).
        internal const int MainSet = 0;

        private static readonly List<int> groupIds = new List<int>();
        private static readonly Dictionary<int, string> groupNames = new Dictionary<int, string>();   // the name each group shows (from the tags)

        // What a group is called on screen.
        private static string NameOf(int id)
        {
            string name;
            return groupNames.TryGetValue(id, out name) ? name : GroupNames.Default(id);
        }

        // The automatic aim a group is set to: ground targets, air targets, or both (the two buttons lit together). A group that follows the mouse
        // aim has none.
        [Flags]
        internal enum GroupAuto { Off = 0, Ground = 1, Air = 2, Both = 3 }

        private static readonly Dictionary<int, GroupAuto> autoModes = new Dictionary<int, GroupAuto>();

        internal static GroupAuto AutoModeOf(int id)
        {
            GroupAuto mode;
            return autoModes.TryGetValue(id, out mode) ? mode : GroupAuto.Off;
        }

        // A group on automatic aim: its target, and where its barrels must point to hit it (worked out by AutoAim a few times a second; the
        // turrets are turned toward it every physics step like toward the cursor).
        private sealed class AutoState
        {
            public AutoTarget Target;           // null = nothing to shoot at
            public bool Has;                    // Desired and Distance are a solution for Target
            public Vector3 Desired;             // the lead turret's barrel direction, as of the last solution (SolvedAt)
            public float Distance;              // from the lead's muzzle to the virtual aim point: what the other turrets of the group aim at
            public Vector3 Omega;               // how fast Desired turns (rad/s, world axis x rate), smoothed: the turrets turn at that rate ahead of the gap they close
            public float SolvedAt;              // Time.fixedTime of the last solution
            public bool Solved;                 // Desired is a solution for the current target (Omega can be worked out from the next one)
            public float TargetDistance, FlightTime;
            public int Reach, Live;             // how many of the group's turrets can point at the target within their rotators' arcs, out of how many are alive
            public float NextScan, NextSolve, RetargetAt, Gate;
            public int FireStart;
            public List<Shooter> Shooters;      // the group's cannons (built the first time they are needed)
            public readonly List<float> HistoryTime = new List<float>();             // where the aim pointed in the last second (for the staggered start)
            public readonly List<Vector3> HistoryDirection = new List<Vector3>();
        }

        private static readonly Dictionary<int, AutoState> autoStates = new Dictionary<int, AutoState>();
        private static readonly List<AutoTarget> scanBuffer = new List<AutoTarget>();

        // The group is on automatic aim now (not controlled by hand; not while a single turret is under test).
        private static bool IsAuto(int group)
        {
            return !controlled.Contains(group) && AutoModeOf(group) != GroupAuto.Off && !ArmorFragmentsPlugin.TurretTestSingle.Value;
        }

        private static Gun AutoLeadOf(int group)
        {
            foreach (Gun gun in guns)
                if (gun.GroupId == group && Live(gun)) return gun;
            return null;
        }

        // A turret is being aimed now: it follows the mouse aim, or its group is on automatic and has a target.
        private static bool Engaged(Gun gun)
        {
            if (InActiveGroup(gun)) return true;
            AutoState state;
            return autoStates.TryGetValue(gun.GroupId, out state) && state.Has && IsAuto(gun.GroupId);
        }
        private static readonly HashSet<int> controlled = new HashSet<int>();   // the groups that follow the mouse aim now (the others hold)
        private static Gun sightGun;                    // the turret whose sight camera is the view now

        // The groups controlled now, for texts: "1", "1+3".
        private static string ControlledLabel
        {
            get
            {
                StringBuilder text = new StringBuilder();
                foreach (int id in groupIds)
                    if (controlled.Contains(id)) text.Append(text.Length > 0 ? " + " : "").Append(NameOf(id));
                return text.Length > 0 ? text.ToString() : "-";
            }
        }
        private static bool Live(Gun gun) { return gun != null && gun.Cannon != null && gun.Cannon.PartScript != null; }
        // In the test mode only one turret of the active group counts as active: it is the only one controlled, mirrored in the share of the
        // gain, and fired; the others hold where they are.
        private static int testIndex;   // the position of the turret under test among the turrets of the active group

        private static int GroupCount()
        {
            int n = 0;
            foreach (Gun gun in guns) if (controlled.Contains(gun.GroupId)) n++;
            return n;
        }

        private static Gun GroupGun(int position)
        {
            int n = 0;
            foreach (Gun gun in guns)
            {
                if (!controlled.Contains(gun.GroupId)) continue;
                if (n == position) return gun;
                n++;
            }
            return null;
        }

        private static bool InActiveGroup(Gun gun)
        {
            if (!controlled.Contains(gun.GroupId)) return false;
            if (!ArmorFragmentsPlugin.TurretTestSingle.Value) return true;
            return gun == GroupGun(Mathf.Clamp(testIndex, 0, Mathf.Max(0, GroupCount() - 1)));
        }

        public static string TestStatus
        {
            get
            {
                if (!ArmorFragmentsPlugin.TurretTestSingle.Value) return "Off (all the turrets of the group are controlled)";
                int n = GroupCount();
                return n == 0 ? "No turret" : "Turret " + (Mathf.Clamp(testIndex, 0, n - 1) + 1) + " of " + n + " in the group(s) " + ControlledLabel;
            }
        }

        // The < and > buttons: the next turret of the group is the one controlled (and the mode is switched on).
        public static void TestStep(int delta)
        {
            int n = GroupCount();
            if (n == 0) return;
            ArmorFragmentsPlugin.TurretTestSingle.Value = true;
            testIndex = ((Mathf.Clamp(testIndex, 0, n - 1) + delta) % n + n) % n;
            ResetForTestChange();
            if (Active) Notice("testing turret " + (testIndex + 1) + " of " + n);
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: only turret " + (testIndex + 1) + " of " + n + " in the group(s) " + ControlledLabel + " is controlled now");
        }

        private static bool testWasOn;

        private static void ResetForTestChange()
        {
            CameraManagerScript cm = CameraManagerScript.Instance;
            if (sight && cm != null) LeaveSight(cm);
            foreach (Gun gun in guns) { gun.BarrelKnown = false; ResetSwing(gun); }
            desiredTime.Clear();
            desiredDirection.Clear();
        }

        // The turret the screen follows: the one whose sight camera is on, else the first live one of the active group.
        private static Gun Lead()
        {
            if (sight && Live(sightGun) && InActiveGroup(sightGun)) return sightGun;
            foreach (Gun gun in guns)
                if (InActiveGroup(gun) && Live(gun)) return gun;
            return null;
        }

        // The velocities the control uses are smoothed over this long. A vibrating engine makes the hull's measured angular velocity flip sign
        // almost every physics step (tens of degrees a second); fed straight into the targets, that is a jitter bigger than the small errors
        // the turret is trying to close (found in a recording), so small angles were never followed.
        private const float VelocityFilterSeconds = 0.12f;

        // The hull's own spin is smoothed more: it only feeds the stabilizer, and the turret can't follow a shake faster than about a turn a
        // second anyway (a rotator turns at 10 degrees a second or so). A ship's hull shaking at 2 Hz fed through a short filter made the
        // turrets chase it back and forth at their speed limit, all fourteen together, which shook the ship more (found in a recording).
        private const float HullFilterSeconds = 0.4f;

        // The gain never goes below this per physics step (0.4 a second), however many turrets share a hull and however often the swing detector cut it.
        private const float MinimumGain = 0.004f;

        // The lag the predictor assumes is never less than this, whatever was measured. A turret whose joint is springy measures a tiny lag in the
        // slow calibration move (20 ms in a recording) yet rings at 2-3 Hz when asked to reverse (its barrel moved three times as far as the ring
        // angle, and the lone turret swung for 7 s until the swing detector had added enough lag by itself). In a model of a springy joint, a
        // predictor that assumes about 130 ms ends the swing for every damping from 0.1 to 0.9 and costs a quarter of a second on a 5 degree move.
        private const float MinimumAssumedLag = 0.13f;
        private const float SmallestGapDegrees = 0.12f;   // below this gap the turret rests (the settled gap is noisy by about a tenth of a degree)

        // The view's field of view (degrees), as of the last frame: when zoomed in, the turret settles finer than its usual rest band, so the gun
        // stays on the middle of a narrow sight (at 50x the whole view is about a degree tall).
        private static float viewFovDegrees = 60f;

        private static float ZoomedRestDegrees(float shareOfView)
        {
            return Mathf.Max(viewFovDegrees * shareOfView, 0.0005f);
        }
        private const float MicroStepDegrees = 0.02f;     // the least a step asks for above it

        // The braking term needs the gun's speed without much delay (a delay in it is what lets a swing grow): only a few physics steps of smoothing.
        private const float BrakeFilterSeconds = 0.03f;

        private static Vector3 Smooth(Vector3 current, Vector3 sample, bool first, float dt, float tau = VelocityFilterSeconds)
        {
            if (first) return sample;
            float a = 1f - Mathf.Exp(-dt / tau);
            return current + (sample - current) * a;
        }

        // One rotator being turned a few degrees to see what it moves.
        private sealed class Jog
        {
            public Turret Turret;
            public float Angle0, Delta;
            public float Beta0;          // the joint's measured angle before the move (for the obstacle guard's sign)
            public bool BetaOk;
            public Vector3 Axis;
            public HashSet<int> Expect;   // the guns it should move (from the way the bodies are joined); null = any (it is turned alone)
        }

        private struct Sample
        {
            public float Time;
            public float[] Angles;        // per jog
            public Vector3[] Barrels;     // per gun
        }

        // A batch of rotators that move different guns is turned all at once (a ship with many turrets would take half a minute one by one);
        // a rotator the batch can't place is turned alone, as before.
        private sealed class Calibration
        {
            public readonly List<Jog> Jogs = new List<Jog>();
            public int Phase;            // 0: moving, 1: settling
            public float Timer, Elapsed;
            public Vector3[] Barrels0;   // per gun
            public readonly List<Sample> Samples = new List<Sample>();
        }

        private static AircraftScript craft;
        private static readonly List<Gun> guns = new List<Gun>();
        private static readonly List<Turret> turrets = new List<Turret>();
        private static readonly Dictionary<JointRotatorScript, float> commands = new Dictionary<JointRotatorScript, float>();
        private static Calibration calibration;
        private static bool calibrated;
        private static float lastStep = -1f;
        private static bool stepFailed;

        // The wanted direction of the barrel (world space) and how far the aim point is.
        private static Vector3 desired = Vector3.forward;
        private static bool haveDesired;
        private static float aimDistance = 1000f;
        private static readonly float[] sightDistances = new float[3];   // the last three raw aim distances through the sight (median)
        private static int sightDistanceAt;
        private static float errorDegrees;
        private static bool atLimit;

        // Camera and controls.
        private static InteractiveCameraController controller;
        private static CameraController previousController;
        private static bool decoupled, returning;
        private static Vector2 aimAngles;              // FreeCursor: where the ring points, as (pitch, yaw) in degrees, the orbit camera's own convention (pitch down is +, yaw right is +)
        private static bool aimKnown, ownsOrbitCursor;
        private static float lastCursorMove;   // Time.unscaledTime of the last mouse movement that moved the ring (the camera recenters on it when the mouse rests)   // aimAngles is set from the camera; this code (not the game) looks after the cursor in the orbit view
        private static Vector2 savedRotation;
        private static float returnVelocityX, returnVelocityY, fovVelocity;
        private static bool fireMouse, fireKey;   // the two fire inputs, as read this frame
        private static float nextStatus;

        // ---- Access to the game's private members (looked up once) ----------------------------------------------

        private static AccessTools.FieldRef<JointRotatorScript, ConfigurableJoint> jointRef;
        private static AccessTools.FieldRef<JointRotatorScript, float> angleRef, targetRef;
        private static AccessTools.FieldRef<JointRotatorScript, float> speedRef;   // the rotator's top speed (degrees a second); null = not readable
        private static AccessTools.FieldRef<JointRotatorScript, bool> freeSpinRef, floppyRef;
        private static AccessTools.FieldRef<JointRotatorScript, Vector3> perpendicularRef, connectedPerpendicularRef;
        private static bool guardRefsOk;
        private static readonly Dictionary<JointRotatorScript, Turret> turretOf = new Dictionary<JointRotatorScript, Turret>();
        private static AccessTools.FieldRef<InteractiveCameraController, Vector2> rotationRef;
        private static AccessTools.FieldRef<InteractiveCameraController, float> sensitivityRef;
        private static System.Reflection.MethodInfo selectCamera;
        private static AccessTools.FieldRef<OrbitCameraController, Func<Transform>> orbitTargetRef;
        private static AccessTools.FieldRef<OrbitCameraController, bool> orbitCenterRef;
        private static AccessTools.FieldRef<CameraManagerScript, CameraController> orbitCameraRef;   // the game types it as CameraController
        private static AccessTools.FieldRef<CameraVantageScript, CameraController> vantageControllerRef;
        private static AccessTools.FieldRef<InteractiveCameraController, float> fovTargetRef;
        private static bool refsTried, refsOk, orbitRefsOk;

        private static bool EnsureRefs()
        {
            if (refsTried) return refsOk;
            refsTried = true;
            try
            {
                jointRef = AccessTools.FieldRefAccess<JointRotatorScript, ConfigurableJoint>("_joint");
                angleRef = AccessTools.FieldRefAccess<JointRotatorScript, float>("_angle");
                targetRef = AccessTools.FieldRefAccess<JointRotatorScript, float>("_targetAngle");
                freeSpinRef = AccessTools.FieldRefAccess<JointRotatorScript, bool>("_freeSpin");
                floppyRef = AccessTools.FieldRefAccess<JointRotatorScript, bool>("_floppyJoint");
                try { speedRef = AccessTools.FieldRefAccess<JointRotatorScript, float>("_speed"); }
                catch (Exception) { speedRef = null; }   // only DirectControl needs it (it falls back to the data's speed)
                try
                {
                    // For the obstacle guard (where the part really is). If the game changes these, only the guard is lost.
                    perpendicularRef = AccessTools.FieldRefAccess<JointRotatorScript, Vector3>("_perpendicularVector");
                    connectedPerpendicularRef = AccessTools.FieldRefAccess<JointRotatorScript, Vector3>("_connectedPerpendicularVector");
                    guardRefsOk = true;
                }
                catch (Exception e)
                {
                    guardRefsOk = false;
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the rotators' measured angle can't be read, the obstacle guard is off (" + e.Message + ")");
                }
                rotationRef = AccessTools.FieldRefAccess<InteractiveCameraController, Vector2>("_deltaRotation");
                sensitivityRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_rotationSensitivity");
                selectCamera = AccessTools.Method(typeof(CameraManagerScript), "SelectCamera");
                refsOk = selectCamera != null;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the game's rotator or camera changed, it is off (" + e.Message + ")");
                refsOk = false;
            }
            try
            {
                vantageControllerRef = AccessTools.FieldRefAccess<CameraVantageScript, CameraController>("_cameraController");
                fovTargetRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_fovZoomTarget");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the sight camera can't be used (" + e.Message + ")");
            }
            try
            {
                orbitTargetRef = AccessTools.FieldRefAccess<OrbitCameraController, Func<Transform>>("_targetTransform");
                orbitCenterRef = AccessTools.FieldRefAccess<OrbitCameraController, bool>("_centerOnRigidBody");
                orbitRefsOk = true;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the orbit camera's pivot can't be moved (" + e.Message + "), it stays where the game puts it");
            }
            try
            {
                orbitCameraRef = AccessTools.FieldRefAccess<CameraManagerScript, CameraController>("_orbitCamera");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: a stale orbit camera pivot can't be repaired (" + e.Message + ")");
            }
            return refsOk;
        }

        // ---- The orbit camera's pivot ---------------------------------------------------------------------------
        // The point the orbit camera turns around (where it sits at full zoom) is, in the game, the vehicle's own position. Here it is a point of
        // our own, centered on the vehicle and TurretAim/CameraHeight meters above the highest point of the vehicle, so the camera looks over
        // the turret from above.

        private static OrbitCameraController orbit;
        private static Func<Transform> originalTarget;
        private static bool originalCenter;
        private static GameObject pivot;
        private static readonly List<Collider> bodyColliders = new List<Collider>();
        private static float nextColliders;

        // What the camera's Update gets for one call: the game's own target and centering are put back at the end of the call.
        internal sealed class PivotSwap
        {
            public Func<Transform> Target;
            public bool Center;
        }

        private static Func<Transform> pivotTarget;

        private static void TakePivot(OrbitCameraController controllerToMove)
        {
            if (!orbitRefsOk || controllerToMove == null) return;
            orbit = controllerToMove;
            pivot = new GameObject("ArmorFragments turret camera pivot");
            pivot.transform.position = craft.transform.position;
            Transform pivotTransform = pivot.transform;
            pivotTarget = () => pivotTransform;
            nextColliders = 0f;
            pivotYKnown = false;
            UpdatePivot();
        }

        private static void ReleasePivot()
        {
            if (pivot != null) UnityEngine.Object.Destroy(pivot);
            pivot = null;
            pivotTarget = null;
            orbit = null;
            bodyColliders.Clear();
        }

        internal static PivotSwap SwapPivotIn(OrbitCameraController instance)
        {
            if (!Active || pivot == null || pivotTarget == null || !orbitRefsOk || instance != orbit) return null;
            PivotSwap swap = new PivotSwap { Target = orbitTargetRef(instance), Center = orbitCenterRef(instance) };
            orbitTargetRef(instance) = pivotTarget;
            orbitCenterRef(instance) = false;
            return swap;
        }

        internal static void SwapPivotOut(OrbitCameraController instance, PivotSwap swap)
        {
            if (swap == null || instance == null) return;
            orbitTargetRef(instance) = swap.Target;
            orbitCenterRef(instance) = swap.Center;
        }

        // An earlier copy of the plugin (before the pivot was swapped only for the duration of a call) replaced the orbit camera's target for
        // good, and when it was reloaded without taking it back, the camera kept following a pivot that stayed where it was while the vehicle
        // drove away. Whatever target comes from this plugin's code is put back as the game makes it: the main cockpit, centered on its body.
        private static float nextRepair;

        private static void RepairOrbitCamera(CameraManagerScript cm)
        {
            if (!orbitRefsOk || cm == null || orbitCameraRef == null) return;
            OrbitCameraController found = orbitCameraRef(cm) as OrbitCameraController;
            if (found == null) return;
            Func<Transform> target = orbitTargetRef(found);
            if (target == null || target.Method == null || target.Method.DeclaringType == null) return;
            if (target.Method.DeclaringType.Assembly.GetName().Name != typeof(TurretAim).Assembly.GetName().Name) return;

            FlightSceneScript scene = FlightSceneScript.Instance;
            AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
            PartScript cockpit = aircraft != null ? aircraft.MainCockpit : null;
            if (cockpit == null) return;
            orbitTargetRef(found) = () => cockpit.transform;
            orbitCenterRef(found) = true;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the orbit camera was still following a pivot an earlier copy of the plugin left behind; it follows the vehicle again");
        }

        private static void UpdatePivot()
        {
            if (pivot == null || craft == null) return;

            // The vehicle's colliders, looked up again now and then (parts get destroyed and torn off).
            if (Time.unscaledTime >= nextColliders)
            {
                nextColliders = Time.unscaledTime + 2f;
                bodyColliders.Clear();
                // The gun (the body the pointer cannons are in) is left out of the highest point: its top moves with the elevation, and the pivot
                // that follows it moves the camera, which changes where the gun is told to point, which moves its top... With the highest point
                // jumping between the barrel's tip and another part, the aim flipped between two positions.
                HashSet<Rigidbody> gunBodies = new HashSet<Rigidbody>();
                foreach (Gun gun in guns)
                {
                    Rigidbody body = gun.Cannon != null ? BodyOf(gun.Cannon.PartScript) : null;
                    if (body != null) gunBodies.Add(body);
                }
                foreach (PartData data in craft.Aircraft.Assembly.Parts)
                {
                    PartScript part = data != null ? data.PartScript : null;
                    if (part == null) continue;
                    foreach (Collider collider in part.GetComponentsInChildren<Collider>())
                    {
                        if (collider == null || collider.isTrigger) continue;
                        Rigidbody attached = collider.attachedRigidbody;
                        if (attached != null && gunBodies.Contains(attached)) continue;
                        bodyColliders.Add(collider);
                    }
                }
            }

            float top = float.NegativeInfinity;
            Vector3 center = Vector3.zero;
            int count = 0;
            foreach (Collider collider in bodyColliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Bounds bounds = collider.bounds;
                if (bounds.max.y > top) top = bounds.max.y;
                center += bounds.center;
                count++;
            }
            if (count == 0) return;
            center /= count;

            // The height eases toward its target (about half a second), so even a jump of the highest point from one part to another moves the
            // camera gently and can't set the aim swinging.
            float wanted = top + ArmorFragmentsPlugin.TurretCameraHeight.Value;
            pivotY = pivotYKnown ? Mathf.Lerp(pivotY, wanted, 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.5f)) : wanted;
            pivotYKnown = true;

            // Centered on the vehicle: the hull's centre of mass once it is known, else the middle of its colliders.
            // The body the turrets turn on can shake (a hull part on soft joints rocking 5 degrees moves its centre of mass, and the camera with it);
            // the centre of mass of all the bodies joined to it does not move when they only push each other.
            Vector3 middle = hullBody != null ? hullBody.worldCenterOfMass : center;
            if (pivotBodies.Count > 0)
            {
                Vector3 sum = Vector3.zero;
                float mass = 0f;
                foreach (Rigidbody body in pivotBodies)
                {
                    if (body == null || !body.gameObject.activeInHierarchy) continue;
                    sum += body.worldCenterOfMass * body.mass;
                    mass += body.mass;
                }
                if (mass > 0f) middle = sum / mass;
            }
            // The rise with the zoom step follows the camera's own distance, which the game eases toward the step.
            float raise = SteppedZoom && !sight ? OrbitPivotRaise() : 0f;
            pivot.transform.position = new Vector3(middle.x, pivotY + raise, middle.z);
            PlaceBarrelPivot();
        }

        // A barrel sight: the orbit camera sits its distance behind the pivot, looking at it, so the pivot is put that far ahead of the muzzle
        // along the aim: the camera is then at the muzzle, looking down the barrel's line.
        private static void PlaceBarrelPivot()
        {
            if (!barrelSight || pivot == null || !Live(sightGun)) return;
            CameraManagerScript cm = CameraManagerScript.Instance;
            float distance = cm != null ? Mathf.Max(cm.SharedCameraDistance, 0.5f) : 1.5f;
            Vector3 along = sightLook;   // the sight's own line of view (the mouse turns it; the gun catches up)
            pivot.transform.position = sightGun.Cannon.TipPosition + along * (distance + 0.3f);
        }

        private static float pivotY;
        private static bool pivotYKnown;
        private static readonly List<Rigidbody> pivotBodies = new List<Rigidbody>();   // every body joined to the turrets' hull (the vehicle)

        // ---- The optical sight --------------------------------------------------------------------------------
        // The camera part picked on the pointer cannon in the designer (Pointer > Pick sight camera). The sight key switches to its view (the game's
        // own controller for that camera part, a First Person one looks along the part) and back to the orbit view. Through the sight the mouse
        // turns the wanted direction and the turret follows it; the view is the camera on the gun, so it shows where the gun really points.

        private static CameraVantageScript sightScript;
        private static InteractiveCameraController sightController;
        private static bool sight;
        // World of Tanks style (stepped orbit zoom, two-layer reticle, automatic drop): switched off; the aim is War Thunder style.
        // The stepped zoom (from the World of Tanks style, on in both): the wheel steps the orbit camera through fixed distances, the camera floats higher as it
        // steps out, in from the closest goes into the gun sight and out below the sight's 2x comes back to the orbit view.
        internal static bool SteppedZoom
        {
            get { return true; }
        }

        internal static bool WotStyle
        {
            get { return false; }
        }

        // World of Tanks style: the orbit camera's distances, stepped with the wheel (in from the closest goes into the gun sight).
        private static readonly float[] OrbitDistances = { 3f, 5f, 8f, 13f, 20f, 32f };
        private static int orbitLevel = 3;

        private static void SetOrbitLevel(int level)
        {
            orbitLevel = Mathf.Clamp(level, 0, OrbitDistances.Length - 1);
            if (controller == null) return;
            if (targetDistanceRef == null)
            {
                try { targetDistanceRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_targetDistance"); }
                catch (Exception) { return; }
            }
            targetDistanceRef(controller) = OrbitDistances[orbitLevel];
        }

        // World of Tanks style: the game's own wheel zoom is left out (the mod steps the zoom itself).
        internal static bool BlocksGameZoom(InteractiveCameraController instance)
        {
            if (!Active || instance == null) return false;
            if (sight) return instance == sightController || instance == controller;   // the sight's zoom is stepped by the mod (a barrel sight is the orbit camera)
            return SteppedZoom && instance == controller;
        }

        // War Thunder style: the sight's normal field of view, set with the wheel (-1: the game's).
        private static float sightBase = -1f;

        // World of Tanks style: where the sight camera looks (the mouse turns it); the gun aims at what that line meets.
        private static Vector3 sightLook = Vector3.forward;

        // The sight's magnifications, stepped with the wheel (doubling from 2x to 32x); sightZoom is the one in use, kept between visits.
        private static readonly float[] SightMagnifications = { 2f, 4f, 8f, 16f, 32f };
        private static int sightZoom;
        private static bool sightWheelUsed;   // this frame's wheel already took the view into the sight

        // The field of view that magnifies 'times' over the normal one.
        private static float FovFor(float baseFov, float times)
        {
            return 2f * Mathf.Atan(Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(times, 1f)) * Mathf.Rad2Deg;
        }

        // ---- World of Tanks style: ranging and shell drop ---------------------------------------------------------------------
        //
        // The range is that of what is under the reticle (the line of sight meets the ground, a building, a vehicle). The gun is then raised above
        // the line of sight by as much as its shell falls on the way there (gravity and the shell's drag, flown like the game's physics does it:
        // AutoAim.Solve), so the shell lands on the reticle. The range is held when the line of sight meets nothing (the sky) or jumps a long way
        // (sweeping across a ridge): the last good range for a second, then it eases to the new one. The range lock key keeps the range as it is
        // until it is pressed again (for leading a fast target, whose line of sight runs on to the ground far behind it).

        private static bool wotRanging;           // this frame's aim was raised for the drop
        private static float wotRange = -1f;      // the range in use (m)
        private static float wotDrop;             // degrees the gun is raised above the line of sight
        private static bool wotHeld, wotLocked;
        private static float wotJumpSince = -1f, wotSkySince = -1f;
        private static Vector3 wotLine = Vector3.forward;   // the line of sight from the muzzle, before the drop
        private static Vector3 wotReticlePoint;            // what is under the reticle (world)
        private static float nextDropLog;

        private const float WotHoldSeconds = 1f;
        private const float WotEaseSeconds = 0.4f;
        private const float WotSkyRange = 2000f;  // what the range eases to when there is only sky under the reticle

        private static void ApplyWotDrop(Gun lead, Vector3 muzzle, Keyboard keys)
        {
            if (!Live(lead)) { wotRanging = false; return; }
            float now = Time.unscaledTime;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            wotLine = desired;
            wotReticlePoint = muzzle + desired * aimDistance;
            bool sky = aimDistance > 2900f;
            float raw = aimDistance;

            // The lock key: lock the range in use, or let it go.
            Key lockKey = ArmorFragmentsPlugin.TurretRangeLockKey.Value;
            if (keys != null && lockKey != Key.None && keys[lockKey].wasPressedThisFrame)
            {
                wotLocked = !wotLocked && wotRange > 0f;
                Notice(wotLocked ? "range locked at " + RangeText(wotRange) : "range unlocked");
            }

            if (wotRange <= 0f) wotRange = sky ? WotSkyRange : raw;
            wotHeld = false;
            if (wotLocked) wotHeld = true;
            else if (sky)
            {
                if (wotSkySince < 0f) wotSkySince = now;
                wotHeld = true;
                if (now - wotSkySince > WotHoldSeconds) wotRange = Mathf.Lerp(wotRange, WotSkyRange, 1f - Mathf.Exp(-dt / WotEaseSeconds));
            }
            else
            {
                wotSkySince = -1f;
                bool jump = Mathf.Abs(raw - wotRange) > Mathf.Max(50f, 0.3f * wotRange);
                if (!jump) { wotRange = raw; wotJumpSince = -1f; }
                else
                {
                    if (wotJumpSince < 0f) wotJumpSince = now;
                    wotHeld = true;
                    if (now - wotJumpSince > WotHoldSeconds)
                    {
                        wotRange = Mathf.Lerp(wotRange, raw, 1f - Mathf.Exp(-dt / WotEaseSeconds));
                        if (Mathf.Abs(raw - wotRange) < Mathf.Max(50f, 0.3f * wotRange)) wotJumpSince = -1f;
                    }
                }
            }

            // The gun raised for the drop at that range, along the line of sight.
            ShellModel shell = AutoAim.ShellOf(lead.Cannon, Vector3.zero);
            AimSolution solution = AutoAim.Solve(shell, muzzle + wotLine * Mathf.Max(wotRange, 5f), Vector3.zero, false, true);
            wotRanging = solution.Ok;
            if (!solution.Ok)
            {
                wotDrop = 0f;   // out of the shell's reach: aimed along the line of sight
                return;
            }
            desired = solution.Direction;
            aimDistance = solution.Range;
            wotDrop = solution.DropDegrees;

            // For checking that the elevation really follows: what the gun is told and where its barrel is, now and then in the log.
            if (now >= nextDropLog && calibrated)
            {
                nextDropLog = now + 5f;
                Vector3 barrel = lead.Cannon.transform.forward;
                float barrelAbove = Mathf.Asin(Mathf.Clamp(barrel.y, -1f, 1f)) * Mathf.Rad2Deg - Mathf.Asin(Mathf.Clamp(wotLine.y, -1f, 1f)) * Mathf.Rad2Deg;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: range " + RangeText(wotRange) + (wotLocked ? " (locked)" : wotHeld ? " (held)" : "") + ", the gun should be " +
                    wotDrop.ToString("F2") + " deg above the line of sight and its barrel is " + barrelAbove.ToString("F2") + " deg above it (aim error " + lead.AimError.ToString("F2") + " deg)");
            }
        }

        // The barrel's direction turned down by the drop: where the shell is going, as a straight line to the range in use.
        private static Vector3 Depressed(Vector3 barrel, float degrees)
        {
            Vector3 right = Vector3.Cross(Vector3.up, barrel);
            if (right.sqrMagnitude < 1e-6f) return barrel;
            return Quaternion.AngleAxis(degrees, right.normalized) * barrel;
        }

        // World of Tanks style, orbit view: the point the camera turns around rises as the camera steps out (none at the closest step, 10 m at the
        // furthest), so the camera floats higher and looks over the tank: the reticle stays in the middle and far targets can be aimed at over
        // its roof. Follows the camera's own distance, so it rises smoothly between the steps.
        private const float OrbitPivotRaiseMax = 10f;

        private static float OrbitPivotRaise()
        {
            CameraManagerScript cm = CameraManagerScript.Instance;
            float distance = cm != null ? cm.SharedCameraDistance : OrbitDistances[orbitLevel];
            float t = Mathf.Clamp01((distance - OrbitDistances[0]) / (OrbitDistances[OrbitDistances.Length - 1] - OrbitDistances[0]));
            return OrbitPivotRaiseMax * t;
        }

        // The orbit camera is as close as it goes (the game stops it at 1.5 m).
        private static bool OrbitAtClosest()
        {
            if (controller == null) return false;
            if (targetDistanceRef == null)
            {
                try { targetDistanceRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_targetDistance"); }
                catch (Exception) { return false; }
            }
            return targetDistanceRef(controller) <= 1.6f;
        }
        private static string notice;
        private static float noticeUntil;

        private static void Notice(string text)
        {
            notice = text;
            noticeUntil = Time.unscaledTime + 4f;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + text);
        }

        // The game asks its camera controller to look after the cursor every frame; in the sight this code does.
        internal static bool OwnsCursor(InteractiveCameraController asked)
        {
            // Through the sight always; and in the orbit view while the camera comes back to the aim after C is let go (the game's mouse look is
            // off then, so that the mouse does not fight the camera's return, and its controller would show the cursor every frame).
            return Active && ((sight && asked == sightController) || (!sight && ownsOrbitCursor && asked == controller));
        }

        // The sights of the guns being aimed, one entry per sight (guns of one group that share a Gun Sight count once): a picked sight part, or
        // the barrel of a gun marked "Gun as gun sight".
        private static readonly List<Gun> sightChoices = new List<Gun>();
        private static int sightIndex;
        private static bool barrelSight;          // the sniper view is on the barrel: the orbit camera, pulled to the muzzle (see FollowSight)
        private static float savedOrbitDistance = -1f;
        private static AccessTools.FieldRef<InteractiveCameraController, float> targetDistanceRef;

        private static List<Gun> SightChoices()
        {
            sightChoices.Clear();
            HashSet<CameraVantageScript> seen = new HashSet<CameraVantageScript>();
            foreach (Gun gun in guns)
            {
                if (!InActiveGroup(gun) || !Live(gun)) continue;
                if (gun.Sight != null) { if (seen.Add(gun.Sight)) sightChoices.Add(gun); }
                else if (gun.GunAsSight) sightChoices.Add(gun);
            }
            return sightChoices;
        }

        // The next sight key: the next gun sight of the guns being aimed (only when there is more than one).
        private static void NextSight(CameraManagerScript cm)
        {
            int count = SightChoices().Count;
            if (count < 2) { Notice(count == 0 ? "no gun sight on these guns" : "only one gun sight"); return; }
            sightIndex = (sightIndex + 1) % count;
            if (sight)
            {
                LeaveSight(cm, false);
                EnterSight(cm);
            }
            else Notice("sight " + (sightIndex + 1) + " of " + count + " picked");
        }

        private static void EnterSight(CameraManagerScript cm)
        {
            List<Gun> choices = SightChoices();
            if (choices.Count == 0)
            {
                Notice("no gun sight on these guns: in the designer select the gun and pick a camera sight, or turn on Gun as gun sight");
                return;
            }
            Gun sightTurret = choices[((sightIndex % choices.Count) + choices.Count) % choices.Count];

            if (sightTurret.Sight == null)
            {
                // The barrel as the sight: the orbit camera stays, pulled in to its closest and moved to the muzzle every frame.
                if (targetDistanceRef == null)
                {
                    try { targetDistanceRef = AccessTools.FieldRefAccess<InteractiveCameraController, float>("_targetDistance"); }
                    catch (Exception e) { Notice("the barrel sight can't be used (" + e.Message + ")"); return; }
                }
                savedOrbitDistance = targetDistanceRef(controller);
                targetDistanceRef(controller) = 1.5f;
                sightScript = null;
                sightController = controller;
                sightGun = sightTurret;
                sight = true;
                barrelSight = true;
                controller.MouseLook = false;
                if (!haveDesired)
                {
                    desired = sightTurret.Cannon.transform.forward;
                    haveDesired = true;
                }
                decoupled = returning = false; aimKnown = false;
                fovVelocity = 0f;
                sightLook = desired;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: looking down the barrel");
                return;
            }

            sightScript = sightTurret.Sight;
            if (vantageControllerRef == null || fovTargetRef == null)
            {
                Notice("the sight camera can't be used in this version of the game");
                return;
            }
            InteractiveCameraController target = vantageControllerRef(sightScript) as InteractiveCameraController;
            if (target == null)
            {
                Notice("the sight camera's view can't be used (the camera part's view mode should be First Person)");
                return;
            }
            selectCamera.Invoke(cm, new object[] { target, false, false });
            if (cm.Controller != target)
            {
                Notice("the game did not switch to the sight camera");
                return;
            }
            sightController = target;
            sightGun = sightTurret;
            sight = true;
            followBadSince = -1f;
            // The sight starts looking at what the gun is aimed at.
            Vector3 aimAt = haveDesired ? sightTurret.Cannon.TipPosition + desired * aimDistance : sightTurret.Cannon.TipPosition + sightTurret.Cannon.transform.forward * 500f;
            sightLook = (aimAt - sightScript.transform.position).sqrMagnitude > 1f ? (aimAt - sightScript.transform.position).normalized : sightTurret.Cannon.transform.forward;
            target.MouseLook = false;
            rotationRef(target) = Vector2.zero;
            if (!haveDesired)
            {
                desired = sightTurret.Cannon.transform.forward;
                haveDesired = true;
            }
            decoupled = returning = false; aimKnown = false;
            fovVelocity = 0f;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: looking through the sight camera");
        }

        // Back to the orbit view, looking where the gun is told to point.
        private static void LeaveSight(CameraManagerScript cm)
        {
            LeaveSight(cm, true);
        }

        private static void LeaveSight(CameraManagerScript cm, bool log)
        {
            if (!sight) return;
            // The sight view goes back to looking along the gun for next time (a barrel sight is the orbit camera itself: it is put back below).
            if (sightController != null && !barrelSight)
            {
                sightController.MouseLook = false;
                rotationRef(sightController) = Vector2.zero;
            }
            if (barrelSight && targetDistanceRef != null && savedOrbitDistance > 0f) targetDistanceRef(controller) = savedOrbitDistance;
            barrelSight = false;
            decoupled = returning = false; aimKnown = false;
            sight = false;
            sightController = null;
            sightGun = null;
            fovVelocity = 0f;
            try
            {
                if (cm.Controller != controller) selectCamera.Invoke(cm, new object[] { controller, false, false });
                Vector3 direction = haveDesired ? desired : Vector3.forward;
                Vector2 now = rotationRef(controller);
                float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
                rotationRef(controller) = new Vector2(Mathf.Clamp(pitch, -89f, 89f), now.y + Mathf.DeltaAngle(now.y, yaw));
                controller.MouseLook = true;
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: could not leave the sight: " + e.Message);
            }
            if (log) ArmorFragmentsPlugin.Log.LogInfo("Turret aim: back to the orbit view");
        }

        // ---- Switching on and off -------------------------------------------------------------------------------

        public static void Toggle()
        {
            if (Active) Deactivate("switched off");
            else Activate();
        }

        private static void Activate()
        {
            try
            {
                FlightSceneScript scene = FlightSceneScript.Instance;
                AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
                if (aircraft == null) { Refuse("no vehicle in flight"); return; }
                if (!EnsureRefs()) { Refuse("the game changed, mouse aim can't run"); return; }

                Collect(aircraft);
                if (guns.Count == 0) { Refuse("no gun has Mouse aim: in the designer select a cannon and turn on Mouse aim"); return; }
                BuildRangeTables();
                int unaimed = 0;
                foreach (Gun gun in guns) if (gun.Linked == null) unaimed++;
                string startNote = turrets.Count == 0 ? "no Traverse or Elevation picked on the guns: they fire on click and you aim them yourself"
                    : unaimed > 0 ? unaimed + (unaimed == 1 ? " gun has" : " guns have") + " no Traverse or Elevation picked: aim " + (unaimed == 1 ? "it" : "them") + " yourself"
                    : null;
                controlled.Clear();
                controlled.Add(groupIds[0]);
                autoModes.Clear();
                autoStates.Clear();
                AutoAim.Forget();
                sightGun = null;

                CameraManagerScript cm = CameraManagerScript.Instance;
                if (cm == null) { Refuse("no camera"); return; }
                previousController = cm.Controller;
                cm.SelectCameraType<OrbitCameraController>();
                controller = cm.Controller as InteractiveCameraController;
                if (controller == null)
                {
                    RestoreCamera(cm);
                    Refuse("the camera view can't be mouse-aimed");
                    return;
                }

                craft = aircraft;
                TakePivot(controller as OrbitCameraController);

                commands.Clear();
                calibration = null;
                calibrated = false;
                graphTried = false;
                edges = null;
                calBase = null;
                hullBody = null;
                sight = false;
                sightController = null;
                sightGun = null;
                notice = null;
                foreach (Gun gun in guns) ResetSwing(gun);
                hulls.Clear();
                desiredTime.Clear();
                desiredDirection.Clear();
                lastStep = -1f;
                stepFailed = false;
                haveDesired = false;
                decoupled = returning = false; aimKnown = false;
                fireMouse = fireKey = false;
                fovVelocity = 0f;
                atLimit = false;
                wotRange = -1f; wotLocked = false; wotRanging = false; wotJumpSince = wotSkySince = -1f;
                errorDegrees = 0f;
                nextStatus = 0f;
                controller.MouseLook = true;
                if (SteppedZoom) SetOrbitLevel(3);
                MakeHud();
                Active = true;
                Status = "Calibrating the turret...";
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim on: " + turrets.Count + " turret rotator(s), " + guns.Count + " gun(s) in " + groupIds.Count +
                                                 " group(s)");
                if (startNote != null)
                {
                    Notice(startNote);
                    ShowFlightMessage("Mouse aim: " + startNote);
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret aim could not start: " + e);
                Deactivate("error");
            }
        }

        // Mouse aim could not start: the reason goes to the panel's status, the log and the flight screen.
        private static void Refuse(string why)
        {
            Status = "Off: " + why;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim can't start: " + why);
            ShowFlightMessage("Mouse aim: " + why);
        }

        private static void ShowFlightMessage(string text)
        {
            try
            {
                FlightSceneScript scene = FlightSceneScript.Instance;
                if (scene != null && scene.FlightUI != null) scene.FlightUI.ShowMessage(text);
            }
            catch (Exception) { }
        }

        // ---- Switching on by itself when a vehicle with Mouse aim guns is spawned -----------------------------------------------

        private static AircraftScript autoStartedFor;   // the vehicle mouse aim last switched on for (or was switched off on) by itself
        private static float autoStartAt = -1f;

        // Called every frame while the aim is off: once per spawned vehicle, a moment after it appears, the aim is switched on if the vehicle has
        // Mouse aim guns. Switching it off (the button or the key) is not undone until the next vehicle.
        private static void TickAutoStart()
        {
            if (!ArmorFragmentsPlugin.TurretAutoStart.Value) return;
            FlightSceneScript scene = FlightSceneScript.Instance;
            AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
            if (aircraft == null || aircraft == autoStartedFor) { autoStartAt = -1f; return; }
            if (autoStartAt < 0f) { autoStartAt = Time.unscaledTime + 1f; return; }   // let the vehicle settle (parts, joints, camera) first
            if (Time.unscaledTime < autoStartAt) return;
            autoStartAt = -1f;
            autoStartedFor = aircraft;
            bool any = false;
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
                if (data != null && data.PartScript != null && TagCodec.Parse(data.Name).Pointer != null && data.PartScript.GetModifier<CannonScript>() != null) { any = true; break; }
            if (!any) return;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the vehicle has Mouse aim guns, switching the aim on");
            Activate();
        }

        // The aim went off because the player switched the camera to another view (cockpit, chase...): it comes back as soon as the game's orbit
        // view is back, for the same vehicle.
        private static AircraftScript resumeFor;

        private static void TickResume()
        {
            if (resumeFor == null || Active) return;
            FlightSceneScript scene = FlightSceneScript.Instance;
            CameraManagerScript cm = CameraManagerScript.Instance;
            AircraftScript aircraft = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
            if (aircraft != resumeFor) { resumeFor = null; return; }
            if (cm == null || !(cm.Controller is OrbitCameraController)) return;
            resumeFor = null;
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: back in the orbit view, switching the aim on again");
            Activate();
        }

        // Calibration found no rotator that moves the guns: it measures again (see FinishCalibration).
        private static int calibrationRetries;
        private static float recalibrateAt;   // a new measurement waits until then

        // The mouse button of the flight screen and the aim key: on or off. Off by hand counts as the player's choice for this vehicle.
        public static void ToggleByHand()
        {
            FlightSceneScript scene = FlightSceneScript.Instance;
            autoStartedFor = scene != null && scene.LocalPlayer != null ? scene.LocalPlayer.Aircraft : null;
            Toggle();
        }

        // The inputs of the game that are plain axes from -1 to 1 (the others are Funky Trees expressions).
        private static readonly string[] PlainAxes = { "Trim", "VTOL", "Throttle", "Brake", "Roll", "Aileron", "Pitch", "Yaw", "Flaps" };
        private static readonly System.Text.RegularExpressions.Regex ClampCall = new System.Text.RegularExpressions.Regex(
            @"^\s*clamp\s*\(.*,\s*([-+]?(?:\d+\.?\d*|\.\d+))\s*,\s*([-+]?(?:\d+\.?\d*|\.\d+))\s*\)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // A variable the sweep moves (the expression's own free variables, such as ang, all follow this one number).
        public sealed class SweepVariable
        {
            public float V;
            public float Get() { return V; }
        }

        // The range of values an input expression can give, found with the game's own expression evaluator: the expression is compiled in a
        // fresh context where every name it uses that is not a function or constant is a variable, and evaluated for that variable from
        // -360 to 360 (the angles a player's expression is written for; clamp(), ?: and the like show up as the arc they allow, x/90 as a
        // value of +-4). False when it does not compile (then the other readings are tried).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool SweepAxis(string expression, out float lo, out float hi)
        {
            lo = 0f;
            hi = 0f;
            try
            {
                Jundroo.Common.Expressions.Context context = new Jundroo.Common.Expressions.Context(true, new object[0]);
                SweepVariable holder = new SweepVariable();
                System.Reflection.MethodInfo getter = typeof(SweepVariable).GetMethod("Get");
                HashSet<string> names = new HashSet<string>();
                foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(expression, "[A-Za-z_][A-Za-z0-9_]*"))
                {
                    string name = word.Value;
                    if (context.Functions.ContainsKey(name) || context.SpecialFunctions.ContainsKey(name) ||
                        context.Constants.ContainsKey(name) || context.Properties.ContainsKey(name)) continue;
                    names.Add(name);
                }
                foreach (string name in names) context.AddVariable(name, getter, holder);
                Func<float> function = Jundroo.Common.Expressions.Parser.Process<float>(expression, context);
                if (function == null) return false;
                float low = float.MaxValue, high = float.MinValue;
                for (int t = -360; t <= 360; t++)
                {
                    holder.V = t;
                    float value = function();
                    if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                    if (value < low) low = value;
                    if (value > high) high = value;
                }
                if (low > high) return false;
                lo = low;
                hi = high;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // What the input controller makes of an axis value (the game's InputControllerScript.UpdateValue, without the activation).
        private static float ControllerValue(InputControllerData input, float axis)
        {
            float value = axis < 0f ? -axis * input.MinValue : axis * input.MaxValue;
            if (input.Invert && input.InvertType == InvertType.Output) value = -value;
            return value;
        }

        private static void Collect(AircraftScript aircraft)
        {
            turrets.Clear();
            guns.Clear();
            groupIds.Clear();
            groupNames.Clear();
            sightScript = null;
            sightGun = null;
            Dictionary<int, PartData> byId = new Dictionary<int, PartData>();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
                if (data != null && !byId.ContainsKey(data.Id)) byId.Add(data.Id, data);

            List<PointerTag> pointerTags = new List<PointerTag>();
            foreach (PartData data in aircraft.Aircraft.Assembly.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null) continue;
                PartTags tags = TagCodec.Parse(data.Name);
                if (tags.Turret != null)
                {
                    JointRotatorScript script = part.GetModifier<JointRotatorScript>();
                    if (script != null && script.JointRotator != null)
                        turrets.Add(new Turret
                        {
                            Script = script,
                            Name = string.IsNullOrEmpty(tags.Label) ? data.PartType.Name : tags.Label,
                            Marked = true,
                        }.WithLimits(part, script));
                }
                if (tags.Pointer != null)   // a gun in no group is in the main set
                {
                    CannonScript cannon = part.GetModifier<CannonScript>();
                    if (cannon != null)
                    {
                        int groupId = tags.Pointer.Group >= 1 ? tags.Pointer.Group : MainSet;
                        Key fireKey;
                        if (!TryParseKey(tags.Pointer.FireKey, out fireKey)) fireKey = ArmorFragmentsPlugin.TurretFireKey.Value;
                        guns.Add(new Gun { Cannon = cannon, GroupId = groupId, FiresByKey = tags.Pointer.ByKey, FireKey = fireKey, TurretName = tags.Pointer.TurretName });
                        if (!groupNames.ContainsKey(groupId))
                            groupNames[groupId] = groupId == MainSet ? "Main" : GroupNames.Display(tags.Pointer.Name, groupId);
                        pointerTags.Add(tags.Pointer);
                    }
                }
            }

            // The sight cameras and the fixed cannons of each turret (a cannon is in one place only: a turret, or fixed to one turret).
            HashSet<CannonScript> used = new HashSet<CannonScript>();
            foreach (Gun gun in guns) used.Add(gun.Cannon);
            for (int i = 0; i < guns.Count; i++)
            {
                PointerTag tag = pointerTags[i];
                Gun gun = guns[i];
                PartData cameraPart;
                if (tag.Camera.HasValue && byId.TryGetValue(tag.Camera.Value, out cameraPart) && cameraPart.PartScript != null)
                    gun.Sight = cameraPart.PartScript.GetModifier<CameraVantageScript>();
                gun.GunAsSight = tag.GunAsSight;
                gun.RangingGun = tag.RangingGun;
                // The Traverse and Elevation slots: limited only by the rotators' own sliders.
                foreach (int? slot in new[] { tag.Traverse, tag.Elevation })
                {
                    PartData rotatorPart;
                    if (!slot.HasValue || !byId.TryGetValue(slot.Value, out rotatorPart) || rotatorPart.PartScript == null) continue;
                    JointRotatorScript rotator = rotatorPart.PartScript.GetModifier<JointRotatorScript>();
                    if (rotator == null || rotator.JointRotator == null) continue;
                    if (gun.Linked == null) gun.Linked = new HashSet<JointRotatorScript>();
                    gun.Linked.Add(rotator);
                    if (!turrets.Exists(t => t.Script == rotator))
                        turrets.Add(new Turret
                        {
                            Script = rotator,
                            Name = string.IsNullOrEmpty(TagCodec.Parse(rotatorPart.Name).Label) ? rotatorPart.PartType.Name : TagCodec.Parse(rotatorPart.Name).Label,
                        }.WithStockLimits(rotatorPart.PartScript, rotator));
                }
                // Rotators linked the older way ([PTR r=]): their limits are read as before, input expressions included.
                foreach (int id in tag.Rotators)
                {
                    PartData rotatorPart;
                    if (!byId.TryGetValue(id, out rotatorPart) || rotatorPart.PartScript == null) continue;
                    JointRotatorScript rotator = rotatorPart.PartScript.GetModifier<JointRotatorScript>();
                    if (rotator == null || rotator.JointRotator == null) continue;
                    if (gun.Linked == null) gun.Linked = new HashSet<JointRotatorScript>();
                    gun.Linked.Add(rotator);
                    if (!turrets.Exists(t => t.Script == rotator))
                        turrets.Add(new Turret
                        {
                            Script = rotator,
                            Name = string.IsNullOrEmpty(TagCodec.Parse(rotatorPart.Name).Label) ? rotatorPart.PartType.Name : TagCodec.Parse(rotatorPart.Name).Label,
                        }.WithLimits(rotatorPart.PartScript, rotator));
                }
                foreach (int id in tag.Fixed)
                {
                    PartData fixedPart;
                    if (!byId.TryGetValue(id, out fixedPart) || fixedPart.PartScript == null) continue;
                    CannonScript fixedCannon = fixedPart.PartScript.GetModifier<CannonScript>();
                    if (fixedCannon == null || !used.Add(fixedCannon)) continue;
                    gun.FixedCannons.Add(new FixedCannon { Cannon = fixedCannon, ByKey = tag.FixedByKey.Contains(id) });
                }
                if (!groupIds.Contains(gun.GroupId)) groupIds.Add(gun.GroupId);
            }
            groupIds.Sort();
            turretOf.Clear();
            foreach (Turret turret in turrets)
            {
                turret.Signs = new float[guns.Count];
                turret.Scales = new float[guns.Count];
                turret.Lags = new float[guns.Count];
                turretOf[turret.Script] = turret;
            }
        }

        // The key in the config: ONLY the next turret group takes the mouse aim (the groups controlled together become that one).
        private static void NextGroup(CameraManagerScript cm)
        {
            if (sight) LeaveSight(cm);
            int current = -1;   // (none controlled: the first group is next)
            for (int i = 0; i < groupIds.Count; i++) if (controlled.Contains(groupIds[i])) { current = i; break; }
            controlled.Clear();
            controlled.Add(groupIds[(current + 1) % groupIds.Count]);
            autoModes.Remove(groupIds[(current + 1) % groupIds.Count]);
            ResetControlState();
            int count = GroupCount();
            Notice("turret group " + ControlledLabel + " (" + count + (count == 1 ? " turret)" : " turrets)"));
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: turret group " + ControlledLabel + " is aimed now (" + count + " turret(s))");
        }

        // Whatever changed which turrets are controlled: nothing carries over (the barrels' speeds, the swing detectors, the hulls' watches, the
        // history of the cursor for the staggered start).
        private static void ResetControlState()
        {
            foreach (Gun gun in guns) { gun.BarrelKnown = false; ResetSwing(gun); }
            hulls.Clear();
            desiredTime.Clear();
            desiredDirection.Clear();
            foreach (AutoState state in autoStates.Values)
            {
                state.HistoryTime.Clear();
                state.HistoryDirection.Clear();
            }
        }

        // ---- The groups, for the flight screen (TurretGroupsHud) -------------------------------------------------------------------------

        internal struct GroupHud
        {
            public int Id;
            public string Name;
            public GroupAuto Auto;
            public int Total, Live, OnTarget, Blocked;
            public bool Controlled;
            public string AutoText;   // what a group on automatic is doing: "ground 850 m", "searching"
        }

        private static string RangeText(float meters)
        {
            return meters >= 1000f ? (meters / 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " km"
                                   : (Mathf.Round(meters / 10f) * 10f).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m";
        }

        private static string AutoTextOf(int id)
        {
            if (!calibrated) return "calibrating";
            AutoState state;
            if (!autoStates.TryGetValue(id, out state) || state.Target == null || !state.Has) return "searching";
            return (state.Target.Air ? "air " : "ground ") + RangeText(state.TargetDistance) +
                   (ArmorFragmentsPlugin.TurretAutoReach.Value && state.Live > 1 ? "  " + state.Reach + "/" + state.Live : "");
        }

        private static float nextBlockedCheck;

        internal static void GetGroups(List<GroupHud> output)
        {
            output.Clear();
            float tolerance = ArmorFragmentsPlugin.TurretFireTolerance.Value;
            if (Time.unscaledTime >= nextBlockedCheck)
            {
                nextBlockedCheck = Time.unscaledTime + 0.3f;
                foreach (Gun gun in guns) gun.LineBlocked = controlled.Contains(gun.GroupId) && Live(gun) && !LineClear(gun.Cannon);
            }
            foreach (int id in groupIds)
            {
                GroupHud group = new GroupHud { Id = id, Name = NameOf(id), Auto = AutoModeOf(id), Controlled = controlled.Contains(id), AutoText = "" };
                if (!group.Controlled && group.Auto != GroupAuto.Off) group.AutoText = AutoTextOf(id);
                foreach (Gun gun in guns)
                {
                    if (gun.GroupId != id) continue;
                    group.Total++;
                    if (!Live(gun)) continue;
                    group.Live++;
                    if (group.Controlled && calibrated && Time.fixedTime - gun.AimStamp < 0.25f && gun.AimError <= tolerance) group.OnTarget++;
                    if (group.Controlled && gun.LineBlocked) group.Blocked++;
                }
                output.Add(group);
            }
        }

        // A click on one of a group's two options (ground, air; both lit together = both): a group that is controlled by hand leaves the manual
        // control and is set to just that option; a group already on automatic has that option switched on or off (all off = the group holds).
        // (Every group may be on automatic or held: none has to follow the mouse aim.)
        internal static void SetAuto(int id, GroupAuto option)
        {
            if (!Active || !groupIds.Contains(id)) return;
            GroupAuto mode;
            if (controlled.Contains(id))
            {
                controlled.Remove(id);
                CameraManagerScript cm = CameraManagerScript.Instance;
                if (sight && cm != null) LeaveSight(cm);
                ResetControlState();
                mode = option;
            }
            else mode = AutoModeOf(id) ^ option;
            if (mode == GroupAuto.Off) autoModes.Remove(id); else autoModes[id] = mode;
            AutoState state;
            if (autoStates.TryGetValue(id, out state))
            {
                // A new mode looks for its target again at once.
                state.Target = null;
                state.Has = false;
                state.NextScan = 0f;
            }
            string what = mode == GroupAuto.Off ? "held" : "automatic aim at " + (mode == GroupAuto.Both ? "ground and air targets" : mode == GroupAuto.Ground ? "ground targets" : "air targets");
            Notice(NameOf(id) + ": " + what);
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: group " + NameOf(id) + " is now " + what);
        }

        // A click on a group's bar: it joins the groups that follow the mouse aim, or leaves them (all may leave).
        internal static void ToggleGroup(int id)
        {
            if (!Active || !groupIds.Contains(id)) return;
            if (controlled.Contains(id)) controlled.Remove(id);   // (no group at all may be controlled by hand)
            else
            {
                controlled.Add(id);
                autoModes.Remove(id);   // a group that follows the mouse aim is not on automatic
            }
            CameraManagerScript cm = CameraManagerScript.Instance;
            if (sight && cm != null) LeaveSight(cm);
            ResetControlState();
            int count = GroupCount();
            Notice(controlled.Count == 0 ? "no group is controlled by hand" : "controlling group " + ControlledLabel + " (" + count + (count == 1 ? " turret)" : " turrets)"));
            ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the groups controlled now: " + ControlledLabel + " (" + count + " turret(s))");
        }

        public static void Deactivate(string reason)
        {
            bool was = Active;
            Active = false;
            commands.Clear();
            calibration = null;
            haveDesired = false;
            fireMouse = fireKey = false;
            autoStates.Clear();
            try
            {
                if (sight && sightController != null && !barrelSight)
                {
                    sightController.MouseLook = false;
                    rotationRef(sightController) = Vector2.zero;
                }
                if (barrelSight && controller != null && targetDistanceRef != null && savedOrbitDistance > 0f) targetDistanceRef(controller) = savedOrbitDistance;
            }
            catch (Exception) { }
            sight = false;
            barrelSight = false;
            sightController = null;
            sightGun = null;
            DestroyHud();
            ReleasePivot();

            CameraManagerScript cm = CameraManagerScript.Instance;
            try
            {
                if (controller != null)
                {
                    controller.MouseLook = false;
                    if (sensitivityRef != null) sensitivityRef(controller) = 1f;
                }
            }
            catch (Exception) { }
            try
            {
                if (cm != null)
                {
                    cm.SetCameraFov(cm.CurrentCameraFieldOfView);
                    if (was) RestoreCamera(cm);
                }
            }
            catch (Exception) { }

            controller = null;
            previousController = null;
            craft = null;
            if (was)
            {
                Status = "Off (" + reason + ")";
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim off: " + reason);
            }
        }

        private static void RestoreCamera(CameraManagerScript cm)
        {
            try
            {
                if (previousController != null && cm.Controller != previousController && selectCamera != null)
                    selectCamera.Invoke(cm, new object[] { previousController, false, false });
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: could not put the camera view back: " + e.Message);
            }
        }

        // ---- Every frame ----------------------------------------------------------------------------------------


        // ---- Changing a key from the panel: the next key pressed is the new one (Escape cancels) --------------------

        private static ConfigEntry<Key> capturing;

        public static void CaptureKey(ConfigEntry<Key> entry)
        {
            capturing = entry;
        }

        public static string KeyText(ConfigEntry<Key> entry)
        {
            return capturing == entry ? "Press the new key... (Esc cancels)" : entry.Value.ToString();
        }

        // A key typed as text: a key's name (Space, LeftCtrl, Tab, F), a single letter, or a single digit (the digit key of the top row).
        public static bool TryParseKey(string text, out Key key)
        {
            key = Key.None;
            string clean = TagCodec.CleanKey(text);
            if (clean == null) return false;
            if (clean.Length == 1 && char.IsDigit(clean[0])) clean = "Digit" + clean;
            if (string.Equals(clean, "ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(clean, "control", StringComparison.OrdinalIgnoreCase)) clean = "LeftCtrl";
            if (string.Equals(clean, "shift", StringComparison.OrdinalIgnoreCase)) clean = "LeftShift";
            if (string.Equals(clean, "alt", StringComparison.OrdinalIgnoreCase)) clean = "LeftAlt";
            try
            {
                key = (Key)Enum.Parse(typeof(Key), clean, true);
            }
            catch (Exception)
            {
                return false;
            }
            return key != Key.None && Enum.IsDefined(typeof(Key), key);
        }

        private static void TickCapture(Keyboard keyboard)
        {
            if (keyboard == null) return;
            foreach (UnityEngine.InputSystem.Controls.KeyControl control in keyboard.allKeys)
            {
                if (!control.wasPressedThisFrame) continue;
                if (control.keyCode != Key.Escape)
                {
                    capturing.Value = control.keyCode;
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + capturing.Definition.Key + " is now " + control.keyCode);
                }
                capturing = null;
                return;
            }
        }

        // A menu of the flight screen (the menu, spawn craft, locations... the game's flyouts), a dialog, or one of the mod's panels is open: the
        // player needs the cursor to use it, so the aim lets go of the mouse while it is open (as when Alt is held).
        private static bool MenuWantsPointer()
        {
            try
            {
                if (UI.ArmorPanel.IsOpen || UI.ArmorPanel.MouseAimOpen) return true;
                Assets.Scripts.Game game = Assets.Scripts.Game.Instance;
                if (game != null && game.UserInterface != null && game.UserInterface.AnyDialogsOpen) return true;
                FlightSceneScript scene = FlightSceneScript.Instance;
                return scene != null && scene.FlightUI != null && scene.FlightUI.Flyouts != null && scene.FlightUI.Flyouts.Selected != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A text box has the keyboard (the multiplayer chat, a field in the mod's panels or the designer): typing must not switch the aim on or off,
        // fire, or change group. The game's own chat input is a TextMeshPro input field that the event system has selected while it is open.
        private static bool TextFieldFocused()
        {
            try
            {
                UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
                GameObject selected = events != null ? events.currentSelectedGameObject : null;
                if (selected == null || !selected.activeInHierarchy) return false;
                TMPro.TMP_InputField field = selected.GetComponent<TMPro.TMP_InputField>();
                if (field != null) return field.isFocused;
                UnityEngine.UI.InputField legacy = selected.GetComponent<UnityEngine.UI.InputField>();
                return legacy != null && legacy.isFocused;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Tick()
        {
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (capturing != null)
                {
                    TickCapture(keyboard);
                    return;
                }
                if (keyboard != null)
                {
                    Key key = ArmorFragmentsPlugin.TurretAimKey.Value;
                    if (key != Key.None && keyboard[key].wasPressedThisFrame && !TextFieldFocused()) ToggleByHand();
                }
                if (Active) Frame();
                else
                {
                    TickAutoStart();
                    TickResume();
                    if (!Active && Time.unscaledTime >= nextRepair)
                    {
                        nextRepair = Time.unscaledTime + 2f;
                        if (EnsureRefs()) RepairOrbitCamera(CameraManagerScript.Instance);
                    }
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret aim failed: " + e);
                Deactivate("error");
            }
        }

        private static void Frame()
        {
            FlightSceneScript scene = FlightSceneScript.Instance;
            CameraManagerScript cm = CameraManagerScript.Instance;
            if (scene == null || scene.LocalPlayer == null || craft == null || scene.LocalPlayer.Aircraft != craft || cm == null)
            {
                Deactivate("the vehicle is gone");
                return;
            }
            if (cm.Controller != (sight ? (CameraController)sightController : controller))
            {
                resumeFor = craft;
                Deactivate("the camera view was changed" + (cm.Controller != null ? " to " + cm.Controller.GetType().Name + (cm.Controller.CameraVantage != null && cm.Controller.CameraVantage.PartScript != null ? " (" + cm.Controller.CameraVantage.PartScript.Part.Name + ")" : "") : ""));
                return;
            }
            if (ArmorFragmentsPlugin.TurretTestSingle.Value != testWasOn)
            {
                testWasOn = ArmorFragmentsPlugin.TurretTestSingle.Value;
                ResetForTestChange();
            }
            Camera cam = cm.MainCamera;
            if (cam == null) return;
            // The game's mouse-as-joystick (a saved setting the right button toggles in its other views) fights the mouse aim: off while the aim
            // is on.
            try
            {
                if (scene.FlightUI != null && scene.FlightUI.MouseAsJoystick)
                {
                    scene.FlightUI.MouseAsJoystick = false;
                    Notice("mouse as joystick switched off (it fights the mouse aim)");
                }
            }
            catch (Exception) { }
            UpdatePivot();

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            // Alt, or a menu or panel open: the cursor is the player's (the mouse doesn't aim, the wheel doesn't zoom, nothing fires).
            bool alt = (keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed)) || MenuWantsPointer();
            bool typing = TextFieldFocused();   // the chat (or any text box) has the keyboard: none of this code's keys act
            Key groupKey = ArmorFragmentsPlugin.TurretGroupKey.Value;
            if (keyboard != null && !typing && groupKey != Key.None && groupIds.Count > 1 && keyboard[groupKey].wasPressedThisFrame) NextGroup(cm);
            Key sightKey = ArmorFragmentsPlugin.TurretSightKey.Value;
            if (keyboard != null && !typing && sightKey != Key.None && keyboard[sightKey].wasPressedThisFrame)
            {
                if (sight) LeaveSight(cm);
                else EnterSight(cm);
                if (!Active) return;
            }
            Key nextSightKey = ArmorFragmentsPlugin.TurretNextSightKey.Value;
            if (keyboard != null && !typing && nextSightKey != Key.None && keyboard[nextSightKey].wasPressedThisFrame) NextSight(cm);

            sightWheelUsed = false;
            // World of Tanks style, orbit view: the wheel steps the camera's distance; in from the closest goes into the gun sight.
            if (SteppedZoom && !sight && mouse != null && !alt && !PauseManager.Paused)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0.01f)
                {
                    if (orbitLevel == 0) { if (SightChoices().Count > 0) EnterSight(cm); }
                    else SetOrbitLevel(orbitLevel - 1);
                    sightWheelUsed = true;
                }
                else if (scroll < -0.01f)
                {
                    SetOrbitLevel(orbitLevel + 1);
                    sightWheelUsed = true;
                }
            }

            // With no group controlled by hand there is no lead turret: the camera still orbits, and only the groups on automatic turn.
            Gun leadGun = Lead();
            Gun anyGun = leadGun;
            if (anyGun == null)
                foreach (Gun gun in guns)
                    if (Live(gun)) { anyGun = gun; break; }
            if (anyGun == null || (leadGun == null && controlled.Count > 0))
            {
                Deactivate("the cannons of the turret group are gone");
                return;
            }
            CannonScript pointer = leadGun != null ? leadGun.Cannon : null;
            hullBody = anyGun.Hull;
            if (leadGun == null)
            {
                haveDesired = false;
                if (sight) LeaveSight(cm);
            }

            Key lookKey = ArmorFragmentsPlugin.TurretLookKey.Value;
            // Free look: the look key (C); in World of Tanks style also the right mouse button, held (it does not zoom in that style).
            bool look = (keyboard != null && !typing && lookKey != Key.None && keyboard[lookKey].isPressed)
                        || (WotStyle && mouse != null && mouse.rightButton.isPressed && !alt);
            InteractiveCameraController viewer = sight ? sightController : controller;   // the view C frees
            bool paused = PauseManager.Paused;
            bool free = alt || paused;

            // C: the camera is freed from the turret, which keeps its wanted direction. Let go: the camera comes back to where it was,
            // gradually, and the aim follows it again once it is there.
            if (look && !decoupled)
            {
                decoupled = true;
                returning = false;
                // Where the view comes back to: along the sight part in War Thunder style (the view stays on the gun), else where it is now.
                savedRotation = sight && followBroken && !barrelSight ? Vector2.zero : rotationRef(sight ? sightController : controller);
            }
            else if (!look && decoupled && !returning)
            {
                returning = true;
                returnVelocityX = returnVelocityY = 0f;
            }
            if (returning)
            {
                Vector2 current = rotationRef(viewer);
                float targetY = current.y + Mathf.DeltaAngle(current.y, savedRotation.y);
                float x = Mathf.SmoothDamp(current.x, savedRotation.x, ref returnVelocityX, 0.3f);
                float y = Mathf.SmoothDamp(current.y, targetY, ref returnVelocityY, 0.3f);
                rotationRef(viewer) = new Vector2(x, y);
                if (Mathf.Abs(x - savedRotation.x) < 0.4f && Mathf.Abs(Mathf.DeltaAngle(y, savedRotation.y)) < 0.4f)
                {
                    returning = false;
                    decoupled = false;
                }
            }

            bool cursorMode = ArmorFragmentsPlugin.TurretFreeCursor.Value && !WotStyle && !sight && pointer != null;   // World of Tanks: the reticle stays in the middle
            if (!cursorMode) aimKnown = false;
            if (!sight) sightDistanceAt = 0;   // the median of the sight's aim distances starts afresh with each visit

            // The mouse turns the camera through the game's own mouse look (it also hides and locks the cursor); Alt gives the cursor back.
            // Through the sight the game's mouse look is off: this code turns the aim with the mouse and looks after the cursor itself.
            if (sight)
            {
                ownsOrbitCursor = false;
                Cursor.lockState = alt ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = alt;
                // Held C: the game's own mouse look turns the view about the sight camera (to look round the inside of the tank) while the turret
                // keeps its aim. Otherwise the view stays on the gun and the mouse turns the aim.
                bool wantLook = look && !returning && !alt;
                if (sightController.MouseLook != wantLook) sightController.MouseLook = wantLook;
            }
            else
            {
                // FreeCursor: the mouse moves the ring and this code turns the camera (softly) after it, so the game's mouse look is off, except
                // while C holds the view free (then it orbits as usual and the ring keeps where it is in the world).
                bool wantLook = cursorMode ? decoupled && !returning && !alt : !alt && !returning;
                ownsOrbitCursor = returning || (cursorMode && !decoupled);
                if (controller.MouseLook != wantLook) controller.MouseLook = wantLook;
                // While the game's mouse look is off here (the camera coming back after C, or the free cursor) the cursor stays where the mod keeps
                // it: hidden and locked, like with the mouse look on (the operating system's cursor showed until the camera had come back, as if
                // Alt were held).
                if (ownsOrbitCursor && !paused)
                {
                    Cursor.lockState = alt ? CursorLockMode.None : CursorLockMode.Locked;
                    Cursor.visible = alt;
                }
            }

            // The game reads the wheel only while its mouse look is on or the pointer is inside its view: with the free cursor the mouse look is
            // off and the cursor is locked, so the wheel would do nothing. Zoom the orbit camera here then, the way the game does.
            if (!SteppedZoom && !sight && !free && !returning && !controller.MouseLook) WheelZoom(controller);

            // War Thunder style: the right button zooms in gradually while held (and the mouse is slower while zoomed, so the aim stays fine).
            float baseFov = cm.CurrentCameraFieldOfView;
            // In the sight the wheel steps the zoom instead (the right button does not zoom there).
            bool zoom = !WotStyle && !sight && mouse != null && mouse.rightButton.isPressed && !free && !returning;
            float restFov = baseFov;
            float easeSeconds = 0.35f;
            if (sight)
            {
                float wheel = mouse != null && !free && !sightWheelUsed ? mouse.scroll.ReadValue().y : 0f;
                // The wheel steps through the magnifications (2x, 4x, 8x, 16x, 32x), each a quick ease; in World of Tanks style out below the
                // first goes back to the orbit view.
                sightZoom = Mathf.Clamp(sightZoom, 0, SightMagnifications.Length - 1);
                if (wheel > 0.01f) sightZoom = Mathf.Min(sightZoom + 1, SightMagnifications.Length - 1);
                else if (wheel < -0.01f)
                {
                    if (sightZoom == 0 && SteppedZoom)
                    {
                        LeaveSight(cm);
                        SetOrbitLevel(0);
                        return;
                    }
                    sightZoom = Mathf.Max(sightZoom - 1, 0);
                }
                restFov = FovFor(baseFov, SightMagnifications[sightZoom]);
                easeSeconds = 0.1f;
            }
            float zoomTimes = sight ? ArmorFragmentsPlugin.TurretSightZoom.Value : ArmorFragmentsPlugin.TurretZoom.Value;
            float targetFov = zoom ? Mathf.Max(restFov / zoomTimes, 2f) : restFov;
            // The sight jumps straight to its step; the orbit view's right-button zoom eases in and out.
            float fov = sight ? targetFov : Mathf.SmoothDamp(cam.fieldOfView, targetFov, ref fovVelocity, easeSeconds, float.PositiveInfinity, Time.unscaledDeltaTime);
            cm.SetCameraFov(fov);
            viewFovDegrees = fov;
            if (sight)
            {
                fovTargetRef(sightController) = fov;   // the first-person view eases its own zoom toward this: keep it where it is
                sensitivityRef(sightController) = Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.01f, 1f);   // for the free look
            }
            else sensitivityRef(controller) = Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.08f, 1f);

            // Left button: fire (read by the override of the Fire Weapons input).
            // The turret group's cannons are fired here, one by one, by the fire inputs: the game's own weapon selection is not used (see
            // TurretGameFirePatch).
            bool canFire = !free && !returning;
            ButtonControl fireButton = mouse == null ? null
                : ArmorFragmentsPlugin.TurretFireMouseButton.Value == TurretFireMouse.Middle ? mouse.middleButton : mouse.leftButton;
            fireMouse = canFire && fireButton != null && fireButton.isPressed;
            fireKeyboard = canFire && !typing ? keyboard : null;   // each gun that fires by key reads its own key from it
            fireKey = AnyFireKeyHeld();
            if (fireMouse || fireKey) FireGuns();
            FireAuto();   // the groups on automatic aim fire by themselves (the player's fire input does not matter to them)

            // The wanted direction: from the cannon's muzzle to what the camera sees at the middle of the screen (the camera is behind and above the
            // gun, so the gun must aim from where it is, not parallel to the camera).
            Vector3 muzzle = pointer != null ? pointer.TipPosition : Vector3.zero;
            if (pointer == null)
            {
                // nothing is aimed by the cursor
            }
            else if (sight)
            {
                // Through the sight the mouse turns the wanted direction itself (the sight camera sits on the gun, so it can't say where to aim):
                // right turns it right and up turns it up, about the sight camera's own axes, less when zoomed in.
                // World of Tanks style (a Gun Sight part whose view follows the mouse): the mouse turns the sight's own line of view, and the gun
                // aims from its muzzle at what that line meets, so the gun's circle lands exactly on the fixed sight in the middle of the view
                // (a line parallel to the view, drawn from the muzzle, would miss it by the distance between the sight and the gun).
                // The gun then catches up at its own speed, and each gun's ring shows where it really points.
                bool lookAims = !barrelSight && !followBroken && sightScript != null;
                // On the barrel: the mouse turns the line of sight too (in World of Tanks style the gun is raised above it for the drop).
                bool barrelLook = barrelSight;
                if (mouse != null && !free && !decoupled)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    float degrees = ArmorFragmentsPlugin.TurretSightSensitivity.Value * Mathf.Clamp(fov / Mathf.Max(baseFov, 1f), 0.01f, 1f);
                    Transform view = cam.transform;
                    Quaternion turn = Quaternion.AngleAxis(delta.x * degrees, view.up) * Quaternion.AngleAxis(-delta.y * degrees, view.right);
                    if (lookAims || barrelLook) sightLook = (turn * sightLook).normalized;
                    else desired = (turn * desired).normalized;
                }
                if (lookAims)
                {
                    Vector3 eye = sightScript.transform.position;
                    Vector3 seen = AimPoint(eye, sightLook);
                    Vector3 toSeen = seen - muzzle;
                    if (toSeen.sqrMagnitude > 1f && Vector3.Dot(toSeen, sightLook) > 0f) desired = toSeen.normalized;
                    else desired = sightLook;
                }
                else if (barrelLook) desired = sightLook;
                else if (barrelSight) sightLook = desired;
                haveDesired = true;
                if (!decoupled && !returning) FollowSight(cam);
                float rawDistance = Mathf.Max(5f, (AimPoint(muzzle, desired) - muzzle).magnitude);
                sightDistances[sightDistanceAt++ % 3] = rawDistance;
                if (sightDistanceAt < 3) { sightDistances[0] = sightDistances[1] = sightDistances[2] = rawDistance; sightDistanceAt = 3; }
                aimDistance = Mathf.Max(Mathf.Min(sightDistances[0], sightDistances[1]), Mathf.Min(Mathf.Max(sightDistances[0], sightDistances[1]), sightDistances[2]));
            }
            else if (!decoupled)
            {
                Vector3 from = cam.transform.position, forward = cursorMode ? FreeCursorDirection(cam, mouse, free) : cam.transform.forward;
                Vector3 point = AimPoint(from, forward);
                Vector3 to = point - muzzle;
                float distance = to.magnitude;
                if (distance < 3f || Vector3.Dot(to, forward) <= 0f)
                {
                    desired = forward;
                    aimDistance = 1000f;
                }
                else
                {
                    desired = to / distance;
                    aimDistance = distance;
                }
                haveDesired = true;

                // For the log if the aim swings: how far the aim point jumped about in the last couple of seconds.
                if (Time.unscaledTime >= aimWindowEnd)
                {
                    aimWindowEnd = Time.unscaledTime + 2f;
                    aimMin = aimMax = aimDistance;
                }
                aimMin = Mathf.Min(aimMin, aimDistance);
                aimMax = Mathf.Max(aimMax, aimDistance);
            }

            // World of Tanks style: the range of what is under the reticle, and the gun raised for its shell's drop at that range.
            if (WotStyle && pointer != null && haveDesired && !decoupled) ApplyWotDrop(leadGun, muzzle, keyboard != null && !typing ? keyboard : null);
            else if (!WotStyle) wotRanging = false;

            UpdateHud(cam, muzzle, look || returning);

            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 0.25f;
                string state = !calibrated ? "Calibrating the turret..."
                    : leadGun == null ? "No group is controlled by hand"
                    : decoupled ? "Free look (the turret holds its aim)"
                    : atLimit ? "At the turret's limit"
                    : errorDegrees < 0.5f ? "On target"
                    : "Turning (" + errorDegrees.ToString("F1") + " deg off)";
                Status = notice != null && Time.unscaledTime < noticeUntil ? notice
                    : (leadGun != null && groupIds.Count > 1 ? "Group " + ControlledLabel + ": " : "") + (sight ? "Sight: " : "") + state;
            }

        }

        // SightFollowsMouse: the sight camera's view is turned (its own look-around, the one C uses) so that it looks along the wanted direction.
        // The fixed crosshair in the middle of the view is then the aim, and the gun circle shows where the gun really is while it catches up.
        // The look-around is pitch and yaw about the camera part. If the view still doesn't look where it is told after a second (the game's
        // first-person view works differently than assumed), this is switched off for the session and the view stays on the gun.
        private static bool followBroken;
        private static float followBadSince = -1f;

        private static void FollowSight(Camera cam)
        {
            if (barrelSight)
            {
                // The orbit camera's own angles (pitch down is +, yaw right is +, in the world), as when leaving the sight.
                Vector2 now = rotationRef(controller);
                Vector3 look = sightLook;   // the line of sight, not the gun (which catches up)
                float yawWorld = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                float pitchWorld = -Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;
                rotationRef(controller) = new Vector2(Mathf.Clamp(pitchWorld, -89f, 89f), now.y + Mathf.DeltaAngle(now.y, yawWorld));
                PlaceBarrelPivot();
                return;
            }
            if (followBroken || sightScript == null || sightController == null) return;
            Vector3 local = Quaternion.Inverse(sightScript.transform.rotation) * sightLook;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            rotationRef(sightController) = new Vector2(Mathf.Clamp(pitch, -89f, 89f), yaw);

            // The check: the view should end up close to the wanted direction (it eases there, so give it time).
            float off = Vector3.Angle(cam.transform.forward, sightLook);
            if (off > 4f)
            {
                if (followBadSince < 0f) followBadSince = Time.unscaledTime;
                else if (Time.unscaledTime - followBadSince > 1.5f)
                {
                    followBroken = true;
                    rotationRef(sightController) = Vector2.zero;
                    Notice("the sight view can't follow the mouse here: it stays on the gun");
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the sight view stayed " + off.ToString("F1") + " deg off the aim while following the mouse (yaw " + yaw.ToString("F1") +
                                                        ", pitch " + pitch.ToString("F1") + "); following is off for this session");
                }
            }
            else followBadSince = -1f;
        }

        private static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // FreeCursor, in the orbit view. The ring is an aim direction of its own ('aimAngles', pitch and yaw in the same degrees the orbit camera uses
        // for where it is being sent, '_deltaRotation'); the mouse moves it (as the game's mouse look moves the camera) and the camera follows it
        // softly: it does nothing while the ring is inside the zone, starts to follow a little inside the zone's edge (at 60% of it), follows
        // fully a little outside it (at 140%), by a smooth step in between, so there is no wall; and as the camera turns, the ring drifts back toward the middle
        // of the screen by itself, because the ring stays where it is in the world. Near the very edge of the screen the mouse is slowed outward, so
        // the ring does not leave the view. Returns the world direction of the ring's ray from the camera.
        private static Vector3 FreeCursorDirection(Camera cam, Mouse mouse, bool free)
        {
            Vector2 sent = rotationRef(controller);   // where the camera is being sent (it follows this smoothly)
            if (!aimKnown)
            {
                aimAngles = sent;
                aimKnown = true;
                lastCursorMove = Time.unscaledTime;
            }
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
            float halfHeight = Mathf.Max(cam.fieldOfView * 0.5f, 1f);
            float halfWidth = Mathf.Atan(Mathf.Tan(halfHeight * Mathf.Deg2Rad) * Mathf.Max(cam.aspect, 0.5f)) * Mathf.Rad2Deg;

            Vector2 off = new Vector2(aimAngles.x - sent.x, Mathf.DeltaAngle(sent.y, aimAngles.y));   // ring minus camera: x down +, y right +
            if (mouse != null && !free)
            {
                // Where the ring is on the screen is measured from where the camera really looks now (it lags behind where it is being sent).
                Vector3 look = cam.transform.forward;
                Vector2 cameraNow = new Vector2(-Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg);
                float across = Mathf.Cos(cameraNow.x * Mathf.Deg2Rad);
                Vector2 seen = new Vector2(aimAngles.x - cameraNow.x, Mathf.DeltaAngle(cameraNow.y, aimAngles.y) * across);

                Vector2 delta = mouse.delta.ReadValue();
                if (delta.sqrMagnitude > 0.25f) lastCursorMove = Time.unscaledTime;
                float degrees = ArmorFragmentsPlugin.TurretFreeCursorSensitivity.Value * Mathf.Clamp(sensitivityRef(controller), 0.08f, 1f);
                Vector2 move = new Vector2(-delta.y * degrees, delta.x * degrees);   // the same signs as the game's mouse look
                move.x *= EdgeSlowdown(seen.x, move.x, halfHeight);
                move.y *= EdgeSlowdown(seen.y, move.y * across, halfWidth);
                aimAngles = new Vector2(Mathf.Clamp(aimAngles.x + move.x, -89f, 89f), aimAngles.y + move.y);

                // The ring never leaves the screen, whatever the mouse did in one frame (this is the screen's edge, not the zone).
                seen = new Vector2(aimAngles.x - cameraNow.x, Mathf.DeltaAngle(cameraNow.y, aimAngles.y) * across);
                float limitX = 0.95f * halfHeight, limitY = 0.95f * halfWidth;
                if (Mathf.Abs(seen.x) > limitX) aimAngles.x = Mathf.Clamp(cameraNow.x + Mathf.Sign(seen.x) * limitX, -89f, 89f);
                if (Mathf.Abs(seen.y) > limitY) aimAngles.y = cameraNow.y + Mathf.Sign(seen.y) * limitY / Mathf.Max(across, 0.2f);
                off = new Vector2(aimAngles.x - sent.x, Mathf.DeltaAngle(sent.y, aimAngles.y));
            }

            // The camera follows. The distance is measured to where the camera is being sent (not to where it is now, which lags behind), so what is
            // already on its way is not asked for twice.
            float zone = Mathf.Max(ArmorFragmentsPlugin.TurretFreeCursorZone.Value * 0.01f * halfHeight, 0.2f);
            float away = new Vector2(off.x, off.y * Mathf.Cos(sent.x * Mathf.Deg2Rad)).magnitude;
            float pull = Smooth01((away - 0.6f * zone) / (0.8f * zone));
            float step = pull * (1f - Mathf.Exp(-dt / Mathf.Max(ArmorFragmentsPlugin.TurretFreeCursorFollow.Value, 0.03f)));
            // The mouse at rest (for half a second, then fading in over 0.7 s): the camera slowly takes the ring back to the middle, whatever the zone
            // says. The aim does not change (the ring stays where it is in the world); only the view creeps. Not with Alt held or the game paused.
            float recenter = ArmorFragmentsPlugin.TurretFreeCursorRecenter.Value;
            if (recenter > 0.01f && !free)
            {
                float resting = Smooth01((Time.unscaledTime - lastCursorMove - 0.5f) / 0.7f);
                step += (1f - pull) * resting * (1f - Mathf.Exp(-dt / recenter));
            }
            else if (free) lastCursorMove = Time.unscaledTime;
            step = Mathf.Min(step, 1f);
            rotationRef(controller) = new Vector2(Mathf.Clamp(sent.x + off.x * step, -89f, 89f), sent.y + off.y * step);
            return Quaternion.Euler(aimAngles.x, aimAngles.y, 0f) * Vector3.forward;
        }

        // The game's own wheel zoom (InteractiveCameraController.Update): the Camera Zoom input times 0.5, or the raw wheel when the wheel always
        // zooms. Only when the game will not do it itself (its pointer-inside-view test fails), so it never zooms twice.
        private static void WheelZoom(InteractiveCameraController viewer)
        {
            try
            {
                Assets.Scripts.Game game = Assets.Scripts.Game.Instance;
                if (game == null || !game.UserInterface.AllowKeyboardInputs || game.UIInfo.IsInteracting) return;
                FlightSceneScript flight = FlightSceneScript.Instance;
                if (flight == null || flight.FlightUI == null || flight.FlightUI.IsPointerInsideGameView) return;
                GameInputs inputs = GameInputs.Instance;
                if (inputs == null) return;
                float axis = inputs.CameraLookZoom.GetAxisIfEnabled() * 0.5f;
                if (axis == 0f && inputs.MouseWheelAlwaysZooms) axis = UnityEngine.Input.mouseScrollDelta.y;
                if (axis != 0f) viewer.Zoom(axis);
            }
            catch (Exception e)
            {
                if (!wheelZoomWarned)
                {
                    wheelZoomWarned = true;
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the wheel zoom of the orbit camera could not be read: " + e.Message);
                }
            }
        }

        private static bool wheelZoomWarned;

        // The mouse is slowed when it pushes the ring toward the edge of the screen (from 70% of the way out, to nothing at 90%).
        private static float EdgeSlowdown(float offset, float move, float half)
        {
            if (offset * move <= 0f) return 1f;
            return 1f - Smooth01((Mathf.Abs(offset) / half - 0.7f) / 0.2f);
        }

        // What the camera sees along a ray, ignoring this vehicle (the camera sits outside it, so the first hits are usually its own parts).
        private static Vector3 AimPoint(Vector3 from, Vector3 direction)
        {
            const float Far = 3000f;
            int mask = ~LayerMask.GetMask("WorldSpaceUI");
            Vector3 origin = from;
            float travelled = 0f;
            for (int i = 0; i < 24 && travelled < Far; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, direction, out hit, Far - travelled, mask, QueryTriggerInteraction.Ignore))
                    break;
                PartScript part = hit.collider.GetComponentInParent<PartScript>();
                // A shell in flight is not scenery: the one that has just left the muzzle must not become "what the camera sees".
                bool shell = part == null && hit.collider.GetComponentInParent<CannonProjectileScript>() != null;
                if (!shell && (part == null || part.Aircraft != craft)) return hit.point;
                origin = hit.point + direction * 0.05f;
                travelled += hit.distance + 0.05f;
            }
            return from + direction * Far;
        }

        // While the aim is on, the game's keys that switch the camera view (C by default) do nothing: C is the free look key, and a view switch
        // would end the aim. (CameraManagerScript.Update reads them with GetButtonDownIfEnabled.)
        internal static void BlockViewKeys(object input, ref bool result)
        {
            if (!result || !Active) return;
            GameInputs inputs = GameInputs.Instance;
            if (inputs == null) return;
            // The right button toggles the game's "mouse as joystick" (a saved setting!): it is the zoom here.
            if (ReferenceEquals(input, inputs.NextView) || ReferenceEquals(input, inputs.PreviousView) || ReferenceEquals(input, inputs.ToggleMouseJoystick))
                result = false;
        }

        // Smoothstep between two edges (Mathf.SmoothStep is not that: it eases between two values by a 0..1 amount).
        private static float Edge(float from, float to, float x)
        {
            float t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        // Fires each cannon of the active group (the turrets, and the cannons fixed to them) whose input is held and whose own delay is over: the
        // Firing Delay (stretched by the crew's load) and the game's checks (ammo, barrel clear of the last shell; a rack-fed cannon needs a round
        // and a loader in its own patch of CannonScript.Fire). On top of that: only turrets that point at the cursor fire (the others hold),
        // none fires into its own vehicle, and a salvo goes one cannon at a time (a ripple) with the order rotating, so the same turrets do not
        // always go first.
        private sealed class Shooter
        {
            public CannonScript Cannon;
            public bool ByKey;
            public Key Key;          // the key it fires with when ByKey
            public Gun Owner;
            public FixedCannon Fixed;
        }

        private static Keyboard fireKeyboard;   // this frame's keyboard when the fire keys may be read (null: not now)

        private static bool KeyHeld(Key key)
        {
            return fireKeyboard != null && key != Key.None && fireKeyboard[key].isPressed;
        }

        // Is the key of any gun of the controlled groups that fires by key held?
        private static bool AnyFireKeyHeld()
        {
            if (fireKeyboard == null) return false;
            foreach (Gun gun in guns)
            {
                if (!InActiveGroup(gun)) continue;
                if (gun.FiresByKey && KeyHeld(gun.FireKey)) return true;
                foreach (FixedCannon fixedCannon in gun.FixedCannons)
                    if (fixedCannon.ByKey && KeyHeld(ArmorFragmentsPlugin.TurretFireKey.Value)) return true;
            }
            return false;
        }

        private static readonly List<Shooter> shooters = new List<Shooter>();
        private static int fireStart;
        private static float gateMouse, gateKey;   // no cannon of that input fires before this time (the ripple)
        private static readonly RaycastHit[] lineHits = new RaycastHit[16];
        private const float LineOfFireMeters = 20f;
        private const float LineOfFireIgnoreMeters = 0.3f;   // what is this close to the muzzle is the barrel's own fittings

        // Why a held fire button did not fire (counted over one call of FireGuns, said once in a while in the log and in the panel's status).
        private static int refuseTarget, refuseLine, refuseReload, refuseAmmo;
        private static float nextRefuseLog;

        private static void FireGuns()
        {
            float now = Time.time;
            refuseTarget = refuseLine = refuseReload = refuseAmmo = 0;
            int firedNow = 0;
            shooters.Clear();
            foreach (Gun gun in guns)
            {
                if (!InActiveGroup(gun)) continue;
                shooters.Add(new Shooter { Cannon = gun.Cannon, ByKey = gun.FiresByKey, Key = gun.FireKey, Owner = gun });
                foreach (FixedCannon fixedCannon in gun.FixedCannons)
                    shooters.Add(new Shooter { Cannon = fixedCannon.Cannon, ByKey = fixedCannon.ByKey, Key = ArmorFragmentsPlugin.TurretFireKey.Value, Owner = gun, Fixed = fixedCannon });
            }
            int count = shooters.Count;
            if (count == 0) return;
            if (fireStart >= count) fireStart = 0;
            float stagger = ArmorFragmentsPlugin.TurretFireStagger.Value;
            for (int k = 0; k < count; k++)
            {
                int index = (fireStart + k) % count;
                Shooter shooter = shooters[index];
                if (!(shooter.ByKey ? KeyHeld(shooter.Key) : fireMouse)) continue;
                if (stagger > 0f && now < (shooter.ByKey ? gateKey : gateMouse)) continue;
                bool fired = shooter.Fixed != null ? TryFire(shooter.Cannon, shooter.ByKey, shooter.Owner, now, ref shooter.Fixed.NextFire)
                                                   : TryFire(shooter.Cannon, shooter.ByKey, shooter.Owner, now, ref shooter.Owner.NextFire);
                if (!fired) continue;
                firedNow++;
                fireStart = (index + 1) % count;
                if (stagger > 0f)
                {
                    if (shooter.ByKey) gateKey = now + stagger; else gateMouse = now + stagger;
                }
            }

            if (firedNow == 0 && refuseTarget + refuseLine + refuseAmmo > 0 && Time.unscaledTime >= nextRefuseLog)
            {
                nextRefuseLog = Time.unscaledTime + 2f;
                string why = (refuseTarget > 0 ? refuseTarget + " not on target (more than " + ArmorFragmentsPlugin.TurretFireTolerance.Value.ToString("F1") + " deg off)" : "") +
                             (refuseLine > 0 ? (refuseTarget > 0 ? ", " : "") + refuseLine + " pointing into its own vehicle" : "") +
                             (refuseAmmo > 0 ? (refuseTarget + refuseLine > 0 ? ", " : "") + refuseAmmo + " with no round, no crew or no ammo" : "");
                Notice("not firing: " + why);
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the fire button is held and nothing fires: " + why + (refuseReload > 0 ? "; " + refuseReload + " reloading" : ""));
            }
        }

        // A turret may fire when its gun is within the tolerance of the cursor (the cannons fixed to it follow its turret's state).
        // How far a gun is off where it should point, in degrees. A gun turned by a single rotator can only correct about that rotator's axis (a ring
        // alone can't raise the gun), so only that part of the error counts: the rest is the player's (or the vehicle's) to fix.
        private static float AimErrorOf(Gun gun, Vector3 want)
        {
            Vector3 barrel = gun.Cannon.transform.forward;
            if (calibrated && gun.Drivers.Count == 1 && gun.Drivers[0].Script != null)
            {
                Vector3 axis = AxisOf(gun.Drivers[0].Script);
                Vector3 a = Vector3.ProjectOnPlane(barrel, axis), b = Vector3.ProjectOnPlane(want, axis);
                if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f) return Vector3.Angle(a, b);
            }
            return Vector3.Angle(barrel, want);
        }

        private static bool OnTarget(Gun owner)
        {
            if (!ArmorFragmentsPlugin.TurretFireOnTarget.Value) return true;
            if (calibrated && owner.Drivers.Count == 0) return true;   // nothing turns this gun: it fires when you click, aimed however you aim it
            return calibrated && Time.fixedTime - owner.AimStamp < 0.25f && owner.AimError <= ArmorFragmentsPlugin.TurretFireTolerance.Value;
        }

        private static bool OnTargetStrict(Gun owner)
        {
            return calibrated && Time.fixedTime - owner.AimStamp < 0.25f && owner.AimError <= ArmorFragmentsPlugin.TurretFireTolerance.Value;
        }

        // Nothing of the cannon's own vehicle within a few meters in front of the muzzle (a turret's roof, another turret).
        private static bool LineClear(CannonScript cannon)
        {
            if (!ArmorFragmentsPlugin.TurretLineOfFire.Value || craft == null) return true;
            Vector3 direction = cannon.transform.forward;
            int hits = Physics.RaycastNonAlloc(cannon.TipPosition, direction, lineHits, LineOfFireMeters, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                if (lineHits[i].distance < LineOfFireIgnoreMeters) continue;
                PartScript part = lineHits[i].collider != null ? lineHits[i].collider.GetComponentInParent<PartScript>() : null;
                if (part != null && part != cannon.PartScript && part.Aircraft == craft) return false;
            }
            return true;
        }

        // 'automatic': a group on automatic aim fires without the fire input, and always only when on target.
        // The game shows the muzzle flash (and the sound, and the flash other players see) when a cannon's own update finds the barrel clear after
        // having seen it occupied. A shell fired from here, after that update of the frame, has usually left the barrel by the next one and was
        // never seen in it: no flash, no smoke. So the cannon is told that its barrel was not clear.
        private static readonly System.Reflection.FieldInfo ClearedLastFrame = AccessTools.Field(typeof(CannonScript), "_projectileClearedLastFrame");

        private static bool TryFire(CannonScript cannon, bool byKey, Gun owner, float now, ref float nextFire, bool automatic = false)
        {
            if (cannon == null || cannon.PartScript == null) return false;
            if (now < nextFire) { refuseReload++; return false; }
            IWeapon weapon = cannon;
            if (weapon.IsDestroyed || weapon.CurrentAmmo <= 0 || !cannon.CanFire || !AmmoRacks.CanFire(cannon)) { refuseAmmo++; return false; }
            if (automatic ? !OnTargetStrict(owner) : !OnTarget(owner)) { refuseTarget++; return false; }
            if (!LineClear(cannon)) { refuseLine++; return false; }
            try
            {
                weapon.Fire(null);
                if (ClearedLastFrame != null) ClearedLastFrame.SetValue(cannon, false);
                nextFire = now + Mathf.Max(cannon.FiringDelay * Crew.ReloadFactor(cannon), 0.02f);
                if (owner != null && cannon == owner.Cannon) owner.ReloadTotal = nextFire - now;
                return true;
            }
            catch (Exception e)
            {
                nextFire = now + 1f;
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: a cannon could not fire: " + e.Message);
                return false;
            }
        }

        // While the aim is on, the game's weapon systems fire no cannon of this vehicle (the selection of the game is ignored: only the cannons
        // of the turret group fire, by their own inputs).
        internal static bool BlocksGameCannons(CannonWeaponSystem system)
        {
            if (!Active || craft == null || system == null) return false;
            foreach (WeaponPart weapon in system.Weapons)
                return weapon.Part != null && weapon.Part.Aircraft == craft;
            return false;
        }

        // ---- The turret ------------------------------------------------------------------------------------------

        // Called from every rotator's physics step (see TurretRotatorPatch): the first one of a step works everything out, each marked rotator
        // then takes its own angle.
        internal static void OnRotatorFixedUpdate(JointRotatorScript script)
        {
            if (!Active || craft == null) return;
            float now = Time.fixedTime;
            if (now != lastStep)
            {
                lastStep = now;
                try
                {
                    Step(Time.fixedDeltaTime);
                }
                catch (Exception e)
                {
                    if (!stepFailed) ArmorFragmentsPlugin.Log.LogError("Turret aim step failed: " + e);
                    stepFailed = true;
                }
            }
            float target;
            if (commands.TryGetValue(script, out target) && targetRef != null)
            {
                Turret owner;
                if (guardRefsOk && turretOf.TryGetValue(script, out owner)) target = GuardRotator(owner, target, Time.fixedDeltaTime);
                targetRef(script) = target;
            }
        }

        // ---- Obstacle guard ---------------------------------------------------------------------------------------------------------
        //
        // A rotator's angle (JointRotatorScript._angle) is only what it ASKS the joint for: it moves toward the target at the rotator's speed, and the
        // joint's drive pulls the part there however hard it takes. A barrel that meets something (the deck, another turret) is not stopped by
        // that: the asked-for angle keeps running ahead and the drive pushes harder and harder (turrets of a ship were tearing it apart). The part's
        // REAL angle is what the game shows as the rotator's "Current Angle": the angle between a direction fixed to each side of the joint. Here
        // that is followed (turned into the units of _angle by a sign found when the rotator is calibrated), and when the asked-for angle runs
        // ahead of the real one and the part does not follow, the rotator is blocked: its asked-for angle is put back where the part is (nothing
        // pushes any more), it may turn away from the obstacle but not further into it, and after a few seconds it tries again.

        private const float StallSeconds = 0.1f;

        // The joint's own measured angle, as the game computes its "Current Angle" output (JointRotatorScript.UpdateOutputs).
        private static bool MeasureBeta(JointRotatorScript script, out float beta)
        {
            beta = 0f;
            ConfigurableJoint joint = jointRef(script);
            if (joint == null || joint.connectedBody == null) return false;
            Vector3 axis = joint.transform.TransformVector(-joint.axis);
            Vector3 own = Vector3.ProjectOnPlane(joint.transform.TransformVector(perpendicularRef(script)), axis);
            Vector3 other = Vector3.ProjectOnPlane(joint.connectedBody.transform.TransformVector(connectedPerpendicularRef(script)), axis);
            if (own.sqrMagnitude < 1e-8f || other.sqrMagnitude < 1e-8f) return false;
            beta = Vector3.SignedAngle(own, other, axis);
            return true;
        }

        private static float AheadOf(Turret turret, float from, float to)
        {
            return turret.Wraps ? Mathf.DeltaAngle(from, to) : to - from;
        }

        // May turn away from the obstacle, not further into it.
        private static float KeepOutOf(Turret turret, float target)
        {
            return AheadOf(turret, turret.BlockAngle, target) * turret.BlockDirection > 0f ? turret.BlockAngle : target;
        }

        // Called each physics step for each marked rotator, with the angle the control wants; returns the angle to ask for.
        private static float GuardRotator(Turret turret, float target, float dt)
        {
            JointRotatorScript script = turret.Script;
            float beta;
            if (turret.PhysSign == 0f || dt < 1e-4f || !MeasureBeta(script, out beta))
            {
                turret.PhysKnown = false;
                return target;
            }
            float command = angleRef(script);
            float moved = Mathf.DeltaAngle(turret.PhysBeta, beta);
            turret.PhysBeta = beta;
            if (!turret.PhysKnown || Mathf.Abs(moved) > 30f)
            {
                // The first reading, or the joint was rebuilt: start from the angle that was asked for.
                turret.PhysAngle = command;
                turret.PreviousCommand = command;
                turret.PhysRate = turret.CommandRate = 0f;
                turret.AheadBase = 0f;
                turret.StallSince = -1f;
                turret.PhysKnown = true;
                return target;
            }
            float previous = turret.PhysAngle;
            turret.PhysAngle += turret.PhysSign * moved;
            float blend = 1f - Mathf.Exp(-dt / 0.05f);
            turret.PhysRate += ((turret.PhysAngle - previous) / dt - turret.PhysRate) * blend;
            turret.CommandRate += (AheadOf(turret, turret.PreviousCommand, command) / dt - turret.CommandRate) * blend;
            turret.PreviousCommand = command;

            if (calibration != null || !ArmorFragmentsPlugin.TurretObstacleStop.Value || !Moves(turret))
            {
                turret.Blocked = false;
                turret.StallSince = -1f;
                return target;
            }

            float now = Time.fixedTime;
            if (turret.Blocked)
            {
                float into = AheadOf(turret, turret.BlockAngle, turret.PhysAngle) * turret.BlockDirection;   // + further in, - backed off
                // The wait doubles each time it is blocked again at the same place (up to 30 s), so a turret whose target is behind the
                // superstructure does not keep tapping it.
                float wait = Mathf.Min(30f, ArmorFragmentsPlugin.TurretObstacleRetry.Value * Mathf.Pow(2f, Mathf.Max(0, turret.Strikes - 1)));
                if (now - turret.BlockedAt > wait || into < -2f)
                {
                    turret.LastBlockAngle = turret.BlockAngle;
                    turret.LastBlockEnd = now;
                    turret.Blocked = false;
                    turret.StallSince = -1f;
                    turret.AheadBase = AheadOf(turret, turret.PhysAngle, command);
                    if (now - turret.LastLogged > 2f)
                    {
                        turret.LastLogged = now;
                        ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + turret.Name + "' tries to turn again (" + (into < -2f ? "it backed off" : "time is up") + ")");
                    }
                    return target;
                }
                angleRef(script) = turret.PhysAngle;   // nothing pushes: the joint is asked to stay where the part is
                turret.DirectV = 0f;
                turret.PreviousCommand = turret.PhysAngle;
                return KeepOutOf(turret, target);
            }

            // The asked-for angle is ahead of the part by more than the threshold, and the part is not catching up with it: something is in the way.
            // (A heavy barrel sags on its drive, so the two angles are not exactly equal at rest: that offset is learned while nothing moves, and
            // only the asked-for angle running ahead of it counts.)
            float ahead = AheadOf(turret, turret.PhysAngle, command);
            if (Mathf.Abs(turret.CommandRate) < 1.5f && Mathf.Abs(turret.PhysRate) < 1.5f) turret.AheadBase += (ahead - turret.AheadBase) * (1f - Mathf.Exp(-dt / 0.5f));
            float excess = ahead - turret.AheadBase;
            float direction = Mathf.Sign(excess);
            bool pushing = Mathf.Abs(excess) > ArmorFragmentsPlugin.TurretObstacleDegrees.Value;
            float partToward = turret.PhysRate * direction, askedToward = turret.CommandRate * direction;
            bool stalled = pushing && partToward < 0.3f * Mathf.Max(askedToward, 3f);
            if (!stalled)
            {
                turret.StallSince = -1f;
                return target;
            }
            if (turret.StallSince < 0f) turret.StallSince = now;
            if (now - turret.StallSince < StallSeconds) return target;

            turret.Strikes = now - turret.LastBlockEnd < 40f && Mathf.Abs(AheadOf(turret, turret.LastBlockAngle, turret.PhysAngle)) < 3f ? turret.Strikes + 1 : 1;
            turret.Blocked = true;
            turret.BlockDirection = direction;
            turret.BlockAngle = turret.PhysAngle;
            turret.BlockedAt = now;
            turret.StallSince = -1f;
            angleRef(script) = turret.PhysAngle;
            turret.DirectV = 0f;
            turret.PreviousCommand = turret.PhysAngle;
            if (now - turret.LastLogged > 2f)
            {
                turret.LastLogged = now;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + turret.Name + "' is blocked by something: it was asked to turn to " + command.ToString("F1") + " deg, the part is at " +
                                                 turret.PhysAngle.ToString("F1") + " deg and not moving (" + turret.PhysRate.ToString("F1") + " deg/s); it stops pushing for up to " +
                                                 Mathf.Min(30f, ArmorFragmentsPlugin.TurretObstacleRetry.Value * Mathf.Pow(2f, turret.Strikes - 1)).ToString("F0") + " s" +
                                                 (turret.Strikes > 1 ? " (blocked here " + turret.Strikes + " times)" : ""));
            }
            return KeepOutOf(turret, target);
        }

        // For the panel: how many rotators the guard can watch, and how many are blocked now.
        public static string ObstacleStatus
        {
            get
            {
                if (!Active) return "Off";
                int watched = 0, blocked = 0, total = 0;
                foreach (Turret turret in turrets)
                {
                    if (!turret.Usable) continue;
                    total++;
                    if (turret.PhysSign != 0f) watched++;
                    if (turret.Blocked) blocked++;
                }
                return !ArmorFragmentsPlugin.TurretObstacleStop.Value ? "Off in the settings"
                    : !calibrated ? "Calibrating"
                    : watched + " of " + total + " rotators watched, " + blocked + " blocked now";
            }
        }

        private static bool Moves(Turret turret)
        {
            JointRotatorScript script = turret.Script;
            return script != null && !script.IsDamaged && jointRef(script) != null && !freeSpinRef(script) && !floppyRef(script);
        }

        private static Vector3 AxisOf(JointRotatorScript script)
        {
            ConfigurableJoint joint = jointRef(script);
            return joint.transform.TransformVector(-joint.axis).normalized;
        }

        private static void HoldAll()
        {
            foreach (Turret turret in turrets)
                if (turret.Script != null) { commands[turret.Script] = angleRef(turret.Script); turret.DirectV = 0f; }
        }

        // What angle a rotator had at a time (the last second is kept): the angle it was asked for a moment ago is the part of the barrel's
        // travel that has not happened yet.
        private static void RememberAngles(float now)
        {
            foreach (Turret turret in turrets)
            {
                if (turret.Script == null) continue;
                turret.HistoryTime.Add(now);
                turret.HistoryAngle.Add(angleRef(turret.Script));
                int old = 0;
                while (old < turret.HistoryTime.Count - 2 && now - turret.HistoryTime[old] > 1f) old++;
                if (old > 0)
                {
                    turret.HistoryTime.RemoveRange(0, old);
                    turret.HistoryAngle.RemoveRange(0, old);
                }
            }
        }

        private static float AngleAt(Turret turret, float time, float fallback)
        {
            List<float> times = turret.HistoryTime, angles = turret.HistoryAngle;
            int n = times.Count;
            if (n == 0) return fallback;
            if (time <= times[0]) return angles[0];
            for (int i = n - 1; i > 0; i--)
            {
                if (times[i - 1] > time) continue;
                float span = times[i] - times[i - 1];
                float f = span > 1e-6f ? Mathf.Clamp01((time - times[i - 1]) / span) : 1f;
                return angles[i - 1] + (angles[i] - angles[i - 1]) * f;
            }
            return angles[0];
        }

        // Where the cursor pointed a moment ago (the last second), for the turrets that start late.
        private static readonly List<float> desiredTime = new List<float>();
        private static readonly List<Vector3> desiredDirection = new List<Vector3>();

        private static bool aimIsMoving;   // the cursor moved more than a degree in the last 0.3 s: a gap that changes side is the cursor, not a swing

        // (The history is the cursor's for the controlled groups and each automatic group's own for that group.)
        private static void RememberDesired(List<float> times, List<Vector3> directions, Vector3 value, float now)
        {
            times.Add(now);
            directions.Add(value);
            int old = 0;
            while (old < times.Count - 2 && now - times[old] > 1f) old++;
            if (old > 0)
            {
                times.RemoveRange(0, old);
                directions.RemoveRange(0, old);
            }
        }

        private static Vector3 DesiredAt(List<float> times, List<Vector3> directions, Vector3 fallback, float time)
        {
            int n = times.Count;
            if (n == 0) return fallback;
            if (time <= times[0]) return directions[0];
            for (int i = n - 1; i > 0; i--)
            {
                if (times[i - 1] > time) continue;
                float span = times[i] - times[i - 1];
                float f = span > 1e-6f ? Mathf.Clamp01((time - times[i - 1]) / span) : 1f;
                return Vector3.Slerp(directions[i - 1], directions[i], f).normalized;
            }
            return directions[0];
        }

        private static void Step(float dt)
        {
            RememberAngles(Time.fixedTime);
            Gun lead = Lead();   // null when no group is controlled by hand (the groups on automatic still work)

            if (!calibrated)
            {
                Calibrate(dt);
                return;
            }
            HashSet<Turret> driven = new HashSet<Turret>();
            bool limit = false;
            float now = Time.fixedTime;

            // The groups on automatic aim look for a target and work out where to point (a few times a second); the turrets then follow it the same
            // way they follow the cursor.
            UpdateAutoGroups(now);

            // The turrets being aimed now (the controlled groups, and the groups on automatic that have a target) are on one vehicle, and the bodies
            // of a vehicle are joined (in a recording of 14 turrets on a ship, 13 sat on one body and the lead on another joined to it: the lone one
            // kept the full gain and was the only one that swung), so the share of the hull is all of them.
            // A turret held at its limit (it can't reach what it is asked to: a recording had seven of fourteen on the wrong side of the ship for
            // a plane) pushes nothing, so it is left out of the count: counting it made the others half as quick as they needed to be.
            int share = 0;
            foreach (Gun gun in guns)
                if (Live(gun) && Engaged(gun) && !gun.Pinned) share++;
            foreach (Gun gun in guns)
                if (Live(gun) && Engaged(gun)) gun.Share = Mathf.Max(1, share);

            // Every turret of a set is turned toward the same point: the lead along the wanted direction, the others from where they are to the
            // point the lead's line reaches at the aim distance. The sets: the controlled groups (the cursor), then each group on automatic (its
            // target). The rotators of the groups that are held keep their place.
            if (haveDesired && lead != null)
            {
                RememberDesired(desiredTime, desiredDirection, desired, now);
                aimIsMoving = Vector3.Angle(desired, DesiredAt(desiredTime, desiredDirection, desired, now - 0.3f)) > 1f;
                StepSet(lead, -1, desired, aimDistance, desiredTime, desiredDirection, true, Vector3.zero, now, dt, driven, ref limit);
            }
            foreach (KeyValuePair<int, AutoState> pair in autoStates)
            {
                AutoState state = pair.Value;
                if (!state.Has || !IsAuto(pair.Key)) continue;
                Gun autoLead = AutoLeadOf(pair.Key);
                if (autoLead == null) continue;
                // The aim between two solutions keeps turning at its rate (so it moves smoothly, not in steps of 0.05 s, and the turrets can be told
                // how fast to turn).
                Vector3 effective = state.Desired;
                float ahead = Mathf.Min(now - state.SolvedAt, 0.12f);
                float omega = state.Omega.magnitude;
                if (omega > 1e-5f && ahead > 0f) effective = Quaternion.AngleAxis(omega * ahead * Mathf.Rad2Deg, state.Omega / omega) * state.Desired;
                RememberDesired(state.HistoryTime, state.HistoryDirection, effective, now);
                aimIsMoving = Vector3.Angle(effective, DesiredAt(state.HistoryTime, state.HistoryDirection, effective, now - 0.3f)) > 1f;
                StepSet(autoLead, pair.Key, effective, state.Distance, state.HistoryTime, state.HistoryDirection, false, state.Omega, now, dt, driven, ref limit);
            }

            foreach (Turret turret in turrets)
                if (!driven.Contains(turret) && turret.Script != null) { commands[turret.Script] = angleRef(turret.Script); turret.DirectV = 0f; }
            atLimit = limit;
        }

        // One set of turrets aimed together: group -1 = the controlled groups (the cursor), otherwise the one group on automatic.
        // 'omega': how fast the wanted direction turns (world, rad/s) for a set on automatic aim; the turrets turn at that rate and only close the
        // rest of the gap. Zero for the cursor.
        private static void StepSet(Gun setLead, int group, Vector3 wanted, float distance, List<float> historyTime, List<Vector3> historyDirection,
                                    bool manual, Vector3 omega, float now, float dt, HashSet<Turret> driven, ref bool limit)
        {
            Vector3 leadTip = setLead.Cannon.TipPosition;
            Vector3 aimPoint = leadTip + wanted * distance;
            float stagger = ArmorFragmentsPlugin.TurretMoveStagger.Value;
            int rank = 0;
            for (int g = 0; g < guns.Count; g++)
            {
                Gun gun = guns[g];
                if (!(group < 0 ? InActiveGroup(gun) : gun.GroupId == group) || !Live(gun)) continue;

                // Where the gun should point, for judging whether it may fire: the cursor (or the target's aim point) itself.
                Vector3 want = wanted;
                if (gun != setLead)
                {
                    Vector3 toPoint = aimPoint - gun.Cannon.TipPosition;
                    if (toPoint.sqrMagnitude > 1f) want = toPoint.normalized;
                }
                gun.Want = want;
                gun.AimError = AimErrorOf(gun, want);
                gun.AimStamp = now;

                // The turrets of a group start one after another: each follows the aim as it was a few hundredths of a second earlier than the
                // one before it (the lead follows it live), so fourteen of them do not push the hull at the same instant.
                int mine = gun == setLead ? 0 : ++rank;
                if (gun.Drivers.Count == 0) continue;
                Vector3 follow = want;
                if (stagger > 0f && mine > 0)
                {
                    Vector3 toPoint = leadTip + DesiredAt(historyTime, historyDirection, wanted, now - mine * stagger) * distance - gun.Cannon.TipPosition;
                    if (toPoint.sqrMagnitude > 1f) follow = toPoint.normalized;
                }
                StepGun(g, gun, manual && gun == setLead, follow, dt, driven, ref limit, omega, !manual);
            }
        }

        // ---- Automatic aim ----------------------------------------------------------------------------------------------------

        // For each group on automatic: keep its target, and every 0.05 s work out where its lead turret's barrel must point (AutoAim.Solve:
        // the shell's flight, the target's motion). A target is looked for four times a second (the nearest the group's mode allows and the shell
        // can reach), kept while it is valid, and changed for another only when that one is much nearer (and not more than once in 3 s).
        private static void UpdateAutoGroups(float now)
        {
            bool lead = ArmorFragmentsPlugin.TurretAutoLead.Value, drop = ArmorFragmentsPlugin.TurretAutoDrop.Value;
            foreach (int id in groupIds)
            {
                AutoState state;
                if (!autoStates.TryGetValue(id, out state)) autoStates[id] = state = new AutoState();
                Gun autoLead = IsAuto(id) ? AutoLeadOf(id) : null;
                if (autoLead == null)
                {
                    if (state.Target != null || state.Has || state.HistoryTime.Count > 0)
                    {
                        state.Target = null;
                        state.Has = false;
                        state.Solved = false;
                        state.HistoryTime.Clear();
                        state.HistoryDirection.Clear();
                    }
                    continue;
                }

                Vector3 muzzle = autoLead.Cannon.TipPosition;
                if (state.Target != null)
                {
                    Vector3 position, velocity;
                    AircraftScript aircraft;
                    if (AutoAim.Read(state.Target.Source, craft, out position, out velocity, out aircraft))
                    {
                        state.Target.Position = position;
                        state.Target.Velocity = velocity;
                    }
                    else
                    {
                        state.Target = null;
                        state.Has = false;
                        state.Solved = false;
                        state.NextScan = 0f;
                    }
                }
                if (now >= state.NextScan)
                {
                    state.NextScan = now + 0.25f;
                    ChooseTarget(id, state, autoLead, muzzle, lead, drop, now);
                }
                if (state.Target == null)
                {
                    state.Has = false;
                    continue;
                }
                if (now >= state.NextSolve)
                {
                    state.NextSolve = now + 0.05f;
                    AutoTarget target = state.Target;
                    AimSolution solution = AutoAim.Solve(AutoAim.ShellOf(autoLead.Cannon, craft.Velocity), target.Position, target.Velocity, lead, drop);
                    if (!solution.Ok)
                    {
                        state.Target = null;
                        state.Has = false;
                        state.Solved = false;
                        state.NextScan = 0f;
                        continue;
                    }
                    // How fast the aim is turning, from this solution and the one before (for the same target): a turret that is only told where to
                    // point closes the gap at its gain and trails a moving target by about the target's rate times its time constant (a recording
                    // of a ship's turrets on a plane: 4 degrees behind a target moving 2 degrees a second). Smoothed over 0.2 s, the solutions being
                    // 0.05 s apart and the target's own position and velocity arriving in steps.
                    float span = now - state.SolvedAt;
                    if (state.Solved && span > 1e-3f && span < 0.5f)
                    {
                        Vector3 raw = RotationVector(state.Desired, solution.Direction) / span;
                        float blend = 1f - Mathf.Exp(-span / 0.2f);
                        state.Omega += (raw - state.Omega) * blend;
                    }
                    else state.Omega = Vector3.zero;
                    state.Solved = true;
                    state.SolvedAt = now;
                    state.Has = true;
                    state.Desired = solution.Direction;
                    state.Distance = solution.Range;
                    state.FlightTime = solution.FlightTime;
                    state.TargetDistance = (target.Position - muzzle).magnitude;
                }
            }
        }

        private const float ReachToleranceDegrees = 3f;

        // How many of the group's live turrets can bring their barrel onto the aim of this solution with their rotators, within their arcs.
        private static int ReachCount(int id, Gun autoLead, Vector3 muzzle, AimSolution solution)
        {
            Vector3 aimPoint = muzzle + solution.Direction * solution.Range;
            int count = 0;
            for (int g = 0; g < guns.Count; g++)
            {
                Gun gun = guns[g];
                if (gun.GroupId != id || !Live(gun)) continue;
                Vector3 want = gun == autoLead ? solution.Direction : (aimPoint - gun.Cannon.TipPosition).normalized;
                if (CanReach(gun, g, want)) count++;
            }
            return count;
        }

        // Can this turret's rotators, each within its own arc, bring the barrel onto 'want'? The rotators are worked through from the one farthest
        // from the gun (the ring) to the one nearest it (the elevation), a few times round: each turns the barrel about its own axis as far as is
        // needed to bring it nearest the wanted direction, or as far as its arc lets it, and the axes of the rotators nearer the gun turn with it.
        private static bool CanReach(Gun gun, int index, Vector3 want)
        {
            Vector3 barrel = gun.Cannon.transform.forward;
            List<Turret> chain = new List<Turret>();
            foreach (Turret turret in gun.Chain) if (gun.Drivers.Contains(turret)) chain.Add(turret);
            foreach (Turret turret in gun.Drivers) if (!chain.Contains(turret)) chain.Add(turret);
            if (chain.Count == 0) return Vector3.Angle(barrel, want) <= ReachToleranceDegrees;
            int n = chain.Count;
            Vector3[] axes = new Vector3[n];
            float[] angle = new float[n];
            for (int i = 0; i < n; i++)
            {
                axes[i] = AxisOf(chain[i].Script);
                angle[i] = angleRef(chain[i].Script);
            }
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    Turret turret = chain[i];
                    float sign = turret.Signs[index];
                    if (sign == 0f) continue;
                    float scale = turret.Scales[index] > 0.05f ? Mathf.Clamp(turret.Scales[index], 0.3f, 3f) : 1f;
                    Vector3 projectedBarrel = Vector3.ProjectOnPlane(barrel, axes[i]);
                    Vector3 projectedWant = Vector3.ProjectOnPlane(want, axes[i]);
                    if (projectedBarrel.sqrMagnitude < 1e-6f || projectedWant.sqrMagnitude < 1e-6f) continue;
                    float needed = Vector3.SignedAngle(projectedBarrel, projectedWant, axes[i]);   // degrees the barrel must turn about this axis
                    float wanted = angle[i] + sign * needed / scale;
                    float next = turret.Wraps ? wanted : Mathf.Clamp(wanted, turret.Low, turret.High);
                    float turned = sign * scale * (next - angle[i]);
                    if (Mathf.Abs(turned) < 1e-4f) continue;
                    Quaternion rotation = Quaternion.AngleAxis(turned, axes[i]);
                    barrel = rotation * barrel;
                    for (int j = 0; j < i; j++) axes[j] = rotation * axes[j];
                    angle[i] = next;
                }
            }
            return Vector3.Angle(barrel, want) <= ReachToleranceDegrees;
        }

        private static void ChooseTarget(int id, AutoState state, Gun autoLead, Vector3 muzzle, bool lead, bool drop, float now)
        {
            AutoAim.Scan(craft, muzzle, AutoModeOf(id), scanBuffer);
            ShellModel shell = AutoAim.ShellOf(autoLead.Cannon, craft.Velocity);

            bool byReach = ArmorFragmentsPlugin.TurretAutoReach.Value;
            int live = 0;
            if (byReach) foreach (Gun gun in guns) if (gun.GroupId == id && Live(gun)) live++;

            AutoTarget keep = null;
            int keepReach = 0;
            if (state.Target != null)
                foreach (AutoTarget candidate in scanBuffer)
                    if (candidate.Source == state.Target.Source) { keep = candidate; break; }
            if (keep != null)
            {
                AimSolution keepSolution = AutoAim.Solve(shell, keep.Position, keep.Velocity, lead, drop);
                if (!keepSolution.Ok) keep = null;
                else if (byReach) keepReach = ReachCount(id, autoLead, muzzle, keepSolution);
            }

            // The nearest few that the shell can reach; with PreferReachable, the one that most of the group's turrets can turn onto (the nearest of
            // those that tie). A recording of 14 turrets on one ship: the group went for the nearest target, 7 of the turrets could not turn far enough
            // to point at it (it was behind them), and the others were left to do all the shooting.
            AutoTarget best = null;
            int bestReach = -1;
            int tried = 0, solved = 0;
            foreach (AutoTarget candidate in scanBuffer)
            {
                if (++tried > 12) break;
                AimSolution solution = AutoAim.Solve(shell, candidate.Position, candidate.Velocity, lead, drop);
                if (!solution.Ok) continue;
                if (!byReach) { best = candidate; bestReach = 0; break; }
                int reach = ReachCount(id, autoLead, muzzle, solution);
                if (reach > bestReach) { best = candidate; bestReach = reach; }
                if (++solved >= 6 || reach >= live) break;   // enough looked at, or one that all of them can reach
            }

            // Keep the target unless another one is clearly better: more turrets can reach it (and by a margin, so two targets that are about equal
            // do not trade places), or as many can and it is much nearer. Never within 3 s of the last change.
            bool switchTarget = best != null && best != keep && now >= state.RetargetAt && (keep == null ||
                                (byReach && bestReach > keepReach && (keepReach == 0 || bestReach >= keepReach * 1.3f)) ||
                                (bestReach == keepReach && best.Distance < keep.Distance * 0.6f) ||
                                (!byReach && best.Distance < keep.Distance * 0.6f));
            AutoTarget chosen = keep != null && !switchTarget ? keep : best;
            int chosenReach = chosen == keep ? keepReach : bestReach;
            state.Live = live;
            state.Reach = byReach ? Mathf.Max(chosenReach, 0) : live;
            if (chosen != null && (state.Target == null || chosen.Source != state.Target.Source))
            {
                state.RetargetAt = now + 3f;
                state.Solved = false;   // a new target: the aim's rate of turning starts again
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: group " + NameOf(id) + " now aims at '" + chosen.Name + "' (" + (chosen.Air ? "air" : "ground") + ", " +
                                                 chosen.Distance.ToString("F0") + " m, " + chosen.Velocity.magnitude.ToString("F0") + " m/s)" +
                                                 (byReach ? "; " + Mathf.Max(chosenReach, 0) + " of " + live + " turrets can turn onto it" : ""));
            }
            else if (chosen == null && state.Target != null)
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: group " + NameOf(id) + " has no target now");
            state.Target = chosen;
            if (chosen == null)
            {
                state.Has = false;
                state.Solved = false;
            }
        }

        // The groups on automatic fire at their targets by themselves (AutoFire): a cannon at a time, when its barrel is within the tolerance of
        // the aim (always required here, whatever FireOnlyOnTarget says: nothing shoots while still turning), clear of the vehicle, in range.
        private static void FireAuto()
        {
            if (!ArmorFragmentsPlugin.TurretAutoFire.Value || PauseManager.Paused) return;
            float now = Time.time;
            float range = ArmorFragmentsPlugin.TurretAutoRange.Value;
            float stagger = ArmorFragmentsPlugin.TurretFireStagger.Value;
            foreach (KeyValuePair<int, AutoState> pair in autoStates)
            {
                AutoState state = pair.Value;
                if (!state.Has || state.Target == null || !IsAuto(pair.Key) || state.TargetDistance > range) continue;
                if (state.Shooters == null)
                {
                    state.Shooters = new List<Shooter>();
                    foreach (Gun gun in guns)
                    {
                        if (gun.GroupId != pair.Key) continue;
                        state.Shooters.Add(new Shooter { Cannon = gun.Cannon, ByKey = gun.FiresByKey, Owner = gun });
                        foreach (FixedCannon fixedCannon in gun.FixedCannons)
                            state.Shooters.Add(new Shooter { Cannon = fixedCannon.Cannon, ByKey = fixedCannon.ByKey, Owner = gun, Fixed = fixedCannon });
                    }
                }
                int count = state.Shooters.Count;
                if (count == 0) continue;
                if (state.FireStart >= count) state.FireStart = 0;
                for (int k = 0; k < count; k++)
                {
                    int index = (state.FireStart + k) % count;
                    Shooter shooter = state.Shooters[index];
                    if (!Live(shooter.Owner)) continue;
                    if (stagger > 0f && now < state.Gate) break;
                    bool fired = shooter.Fixed != null ? TryFire(shooter.Cannon, shooter.ByKey, shooter.Owner, now, ref shooter.Fixed.NextFire, true)
                                                       : TryFire(shooter.Cannon, shooter.ByKey, shooter.Owner, now, ref shooter.Owner.NextFire, true);
                    if (!fired) continue;
                    state.FireStart = (index + 1) % count;
                    if (stagger > 0f) state.Gate = now + stagger;
                }
            }
        }

        // DirectControl: the speed (degrees a second of the rotator's angle) this step. 'gap' is the rotation (rad, about this rotator's axis) that
        // takes the barrel, as it will settle, onto the aim, so the angle still to go is that over the measured scale (degrees the barrel turns for a
        // degree of the angle) with the calibrated sign. It speeds up gently (DirectRampSeconds from rest to top speed) and brakes late and hard
        // (DirectBrakeRampSeconds from top speed to rest): the speed is the one that stops exactly with the braking (v = sqrt(2 a d)), never above
        // the rotator's own top speed, never more than what ends the gap in one step, plus the rate the aim itself turns at (a target), and it
        // changes by at most the ramp's acceleration a step, so there is no jolt (the game's own rotator stops dead at its target).
        private static float DirectVelocity(Turret turret, int index, float gap, float feed, float dt, float gainScale)
        {
            float scale = turret.Scales[index] > 0.05f ? Mathf.Clamp(turret.Scales[index], 0.3f, 3f) : 1f;
            float top = 10f;
            try { top = speedRef != null ? speedRef(turret.Script) : top; } catch (Exception) { }
            if (!(top > 0.5f)) top = 10f;
            float accel = top / Mathf.Max(ArmorFragmentsPlugin.TurretDirectRamp.Value, 0.02f);
            float decel = top / Mathf.Max(ArmorFragmentsPlugin.TurretDirectBrakeRamp.Value, 0.02f);
            float remaining = turret.Signs[index] * gap * Mathf.Rad2Deg / scale;
            float feedDegrees = turret.Signs[index] * feed * Mathf.Rad2Deg;
            float distance = Mathf.Abs(remaining);
            float closing = 0f;
            if (distance > Mathf.Min(ArmorFragmentsPlugin.TurretDirectDeadband.Value, ZoomedRestDegrees(0.001f)))
            {
                // Near the aim the speed is a gain times the angle to go (a linear zone up to decel / gain^2); beyond it, the speed that stops
                // exactly with the braking, joined to the linear part without a step. A pure square root has an unlimited gain at a
                // small gap: a springy joint whose barrel rings at 3-5 Hz showed a gap of a degree every few hundredths of a second, and the
                // rotator was sent at its top speed back and forth (a recording: the elevation swinging at 4.5 Hz; a model reproduces it, and
                // with this zone the swing is gone for joints from 2.5 to 4.5 Hz and a damping from 0.08 to 0.17).
                // The gain near the aim is the configured one divided like the gain-based control's (by the turrets on the hull, and cut by the swing
                // detectors), never below 0.3 a second (or the configured value, if that is lower).
                float configured = Mathf.Max(ArmorFragmentsPlugin.TurretDirectGain.Value, 0.1f);
                float gain = Mathf.Max(configured * Mathf.Clamp01(gainScale), Mathf.Min(configured, 0.3f));
                float linearDistance = decel / (gain * gain);
                float speed = distance <= linearDistance ? gain * distance : Mathf.Sqrt(2f * decel * (distance - 0.5f * linearDistance));
                closing = Mathf.Sign(remaining) * Mathf.Min(Mathf.Min(top, speed), distance / Mathf.Max(dt, 1e-4f));
            }
            float wanted = Mathf.Clamp(feedDegrees + closing, -top, top);
            // Speeding up (the same way, faster) at the gentle ramp; slowing down or turning back at the braking one.
            bool speedingUp = Mathf.Abs(wanted) > Mathf.Abs(turret.DirectV) && wanted * turret.DirectV >= 0f;
            turret.DirectV = Mathf.MoveTowards(turret.DirectV, wanted, (speedingUp ? accel : decel) * dt);
            return turret.DirectV;
        }

        // 'feedRate': how fast 'want' turns in the world (rad/s); the rotators are told to turn at that rate besides closing the gap (zero for the
        // cursor). 'catchUp': a big gap closes faster than the gentle gain a shared hull needs (see below).
        private static void StepGun(int index, Gun gun, bool isLead, Vector3 want, float dt, HashSet<Turret> driven, ref bool limit, Vector3 feedRate, bool catchUp)
        {
            Vector3 barrel = gun.Cannon.transform.forward;
            bool pinned = false;

            // The real error (for the marker) and the one the control works from: the barrel is still moving by its own momentum and the joint's
            // lag, so the control aims from where the barrel will be a moment from now (its own angular velocity times the damping time). Without
            // this the angle keeps being asked for what the barrel is already on its way to, and it overshoots and swings.
            Vector3 trueRotation = RotationVector(barrel, want);
            Vector3 rotation = trueRotation;
            float lead = ArmorFragmentsPlugin.TurretDamping.Value;
            if (gun.BarrelKnown && dt > 1e-4f)
            {
                Vector3 turning = RotationVector(gun.PreviousBarrel, barrel) / dt;   // rad/s, world
                gun.Spin = Smooth(gun.Spin, turning, false, dt);
                gun.FastSpin = Smooth(gun.FastSpin, turning, false, dt, BrakeFilterSeconds);
            }
            else gun.FastSpin = Vector3.zero;
            if (gun.Hull != null)
                gun.HullSpin = Smooth(gun.HullSpin, gun.Hull.angularVelocity, !gun.BarrelKnown, dt, HullFilterSeconds);

            List<Turret> use = new List<Turret>();
            foreach (Turret turret in gun.Drivers)
                if (turret.Usable && Moves(turret) && !driven.Contains(turret)) use.Add(turret);   // (a ring that two controlled groups share goes to the first)

            Vector3[] axes = new Vector3[use.Count];
            for (int i = 0; i < use.Count; i++) axes[i] = AxisOf(use[i].Script);

            // The barrel follows the angle late (it was measured when the aim started: a heavy turret takes a tenth of a second or more). The
            // travel the barrel still has to make is what the rotators were asked for in that time, so the control works from where the barrel
            // WILL be once it has arrived. Without this it keeps asking for what is already on its way, overshoots, and the turret swings.
            float pendingDegrees = 0f;
            float now = Time.fixedTime;
            float lagUsed = Mathf.Max(gun.Lag, MinimumAssumedLag) + gun.LagBoost;
            if (gun.Lag > 0.005f && use.Count > 0)
            {
                Vector3 settled = barrel;
                for (int i = 0; i < use.Count; i++)
                {
                    float angleNow = angleRef(use[i].Script);
                    float pending = angleNow - AngleAt(use[i], now - lagUsed, angleNow);
                    float sign = use[i].Signs[index];
                    float scale = use[i].Scales[index] > 0.05f ? use[i].Scales[index] : 1f;
                    float travel = sign * scale * pending;
                    settled = Quaternion.AngleAxis(travel, axes[i]) * settled;
                    pendingDegrees += Mathf.Abs(travel);
                }
                rotation = RotationVector(settled, want);
            }
            else if (lead > 0f && gun.BarrelKnown && dt > 1e-4f)
            {
                // A turret whose lag could not be measured: the barrel's own velocity times a damping time (the old way).
                Vector3 spin = gun.Spin;
                float spinSpeed = spin.magnitude;
                if (spinSpeed > 1e-5f)
                {
                    Vector3 predicted = Quaternion.AngleAxis(spinSpeed * lead * Mathf.Rad2Deg, spin / spinSpeed) * barrel;
                    rotation = RotationVector(predicted, want);
                }
            }
            gun.PreviousBarrel = barrel;
            gun.BarrelKnown = true;
            if (isLead) errorDegrees = trueRotation.magnitude * Mathf.Rad2Deg;
            if (use.Count == 0)
            {
                gun.Pinned = false;
                return;
            }

            // The rotation split between the rotators by their axes: the small least-squares problem  sum(theta_i * axis_i) = rotation.
            double[] theta = rotation.magnitude * Mathf.Rad2Deg < 0.02f ? new double[use.Count] : Solve(axes, rotation);

            // Stabilizer: while the hull turns, pitches or rolls, the rotators must turn the other way at the same rate, or the barrel would
            // always trail behind it (the error alone only catches up). The same split, for the hull's angular velocity.
            double[] hull = new double[use.Count];
            if (gun.Hull != null) hull = Solve(axes, -gun.HullSpin);

            // The braking (derivative) term: the gun's own speed in the world, split between the rotators the same way, taken off the gap.
            // Hull and turrets pushed each other into a growing swing (a recording of a 14-turret ship: 5 deg/s to 95 deg/s of hull spin in three
            // seconds); in a model fitted to it, braking by the gun's speed kept it stable over every coupling and delay tried and took the
            // overshoot of a 20 degree move from 18 degrees to none, where lowering the gain did not help.
            float brake = ArmorFragmentsPlugin.TurretDerivative.Value;
            bool directBraking = ArmorFragmentsPlugin.TurretDirect.Value && gun.Share >= 2 && ArmorFragmentsPlugin.TurretDirectBrake.Value > 0f;
            double[] speed = (brake > 0f || directBraking) && gun.BarrelKnown ? Solve(axes, gun.FastSpin) : new double[use.Count];

            // Following a moving aim (a target): the rotators turn at the rate the wanted direction turns, and the braking is of the barrel's speed
            // RELATIVE to it (a barrel that keeps up with the target is not being braked for it).
            double[] feed = feedRate.sqrMagnitude > 1e-10f ? Solve(axes, feedRate) : new double[use.Count];

            // The gap's sign, for the swing detector: the rotator with the biggest gap (the signed value of the one that turns it most).
            int biggest = 0;
            for (int i = 1; i < use.Count; i++) if (Math.Abs(theta[i]) > Math.Abs(theta[biggest])) biggest = i;
            WatchForSwinging(gun, (float)theta[biggest] * Mathf.Rad2Deg, trueRotation.magnitude * Mathf.Rad2Deg, isLead);

            // Turrets on one hull body push it together: the more of them, the gentler each is (the hull rocked 5 degrees at 2 Hz in a recording of
            // 14 turrets, even with the ship on land, and a model fitted to it says the push grows with the number of turrets that turn at once).
            float share = ArmorFragmentsPlugin.TurretShareHull.Value ? Mathf.Min(1f, ArmorFragmentsPlugin.TurretShareStrength.Value / Mathf.Max(1, gun.Share)) : 1f;
            float baseGain = 1f - Mathf.Exp(-dt / Mathf.Max(ArmorFragmentsPlugin.TurretResponse.Value, 0.02f));
            // The hull rocking at 2 Hz (all the turrets on it pushing it and being pushed): a detector on the hull's own spin, which does not need
            // the cursor to be still (the ring detector below is switched off while the cursor moves, and the player moves it all the time).
            float hullShake = 1f;
            if (gun.Hull != null && gun.BarrelKnown)
            {
                double[] hullRawSpin = Solve(axes, -gun.Hull.angularVelocity);
                int strongest = 0;
                for (int i = 1; i < use.Count; i++) if (Math.Abs(hullRawSpin[i]) > Math.Abs(hullRawSpin[strongest])) strongest = i;
                hullShake = WatchHull(gun.Hull, (float)hullRawSpin[strongest] * Mathf.Rad2Deg);
            }
            float gain = Mathf.Max(baseGain * share * gun.GainScale * hullShake, Mathf.Min(baseGain, MinimumGain));

            // With several turrets on one hull, the gap each closes is smoothed: the hull rocks at about 2 Hz (as the barrel's gap does with it), the
            // turrets cannot follow that and chasing it rocks the hull more (a model of 14 turrets: a gain of 2 a second rocks it for ever, and with
            // the gap smoothed over 0.4 s the same gain is steady, for hull delays from 50 to 80 ms). One turret is never smoothed.
            bool direct = ArmorFragmentsPlugin.TurretDirect.Value;
            // (In direct control the protections are full from two turrets on: a model of N turrets on a hull was stable for N = 2 to 20 and delays of 30 to
            // 120 ms with the full braking and smoothing, and N = 2 and 3 were not with the partial ones the gain-based control uses.)
            float smoothing = ArmorFragmentsPlugin.TurretErrorFilter.Value * (direct ? (gun.Share >= 2 ? 1f : 0f) : Mathf.Clamp01((gun.Share - 1) / 3f));
            float smoothingStep = smoothing > 0.001f ? 1f - Mathf.Exp(-dt / smoothing) : 1f;
            for (int i = 0; i < use.Count; i++)
            {
                Turret turret = use[i];
                float rawGap = (float)theta[i];
                if (smoothing > 0.001f)
                {
                    turret.GapFilter = turret.GapFilterKnown ? turret.GapFilter + (rawGap - turret.GapFilter) * smoothingStep : rawGap;
                    turret.GapFilterKnown = true;
                }
                else turret.GapFilterKnown = false;
                float gapUsed = smoothing > 0.001f ? turret.GapFilter : rawGap;
                float current = angleRef(turret.Script);
                // A very small error asks for a very small step (gain x error), which the joint may not answer at all (friction, the game's own
                // rounding): the turret then stops short of the cursor. So a step is never smaller than a hundredth of a degree, or the error
                // itself when that is smaller (it never overshoots). It was a twentieth, which made every turret creep at 5 degrees a second
                // all the time (fourteen of them shook the ship).
                // The gain is gentle on purpose when turrets share a hull (they rocked it), which is far too slow for a big gap: a turret that is
                // more than a few degrees off (a new target, a plane that has outrun it) closes it faster, up to the undiluted gain (less, if the
                // hull has been rocking or this turret has swung), and goes back to the gentle gain as it arrives.
                float stepGain = gain;
                if (catchUp)
                {
                    float gapDegrees = Mathf.Abs(rawGap) * Mathf.Rad2Deg;
                    float ceiling = baseGain * hullShake * gun.GainScale;
                    stepGain = Mathf.Max(gain, Mathf.Min(gain * (1f + gapDegrees / 3f), ceiling));
                }
                float step = (gapUsed - brake * ((float)speed[i] - (float)feed[i])) * stepGain;
                float smallest = Mathf.Min(Mathf.Abs((float)theta[i]), 0.01f * Mathf.Deg2Rad);
                if (brake <= 0f && Mathf.Abs(step) < smallest) step = Mathf.Sign((float)theta[i]) * smallest;

                // Small errors: with the gain divided among many turrets a gap of a fraction of a degree asks for a step the joint does not answer
                // (a hundredth of a degree), and the turret sat still short of the cursor. So while the gap is bigger than a twentieth of a degree
                // and the step goes the right way, a step is at least 0.02 degrees (2 degrees a second), or the gap itself when that is smaller.
                // The gap is the settled one (what is still on its way is already taken off), so this ends where the gap does and does not creep.
                float gap = (float)theta[i];
                // Only when the turret has its hull to itself (or nearly): with many on a hull this step is a relay (always 2 degrees a second
                // whichever way the gap points), and a relay on a rocking hull keeps it rocking (the recording of 14 turrets: the rings moved
                // 0.3 degrees and the hull spun 23 degrees a second for as long as it lasted).
                if (gun.Share <= 2 && Mathf.Abs(gap) > Mathf.Min(SmallestGapDegrees, ZoomedRestDegrees(0.0015f)) * Mathf.Deg2Rad && Mathf.Sign(step) == Mathf.Sign(gap) && Mathf.Abs(step) < MicroStepDegrees * Mathf.Deg2Rad)
                    step = Mathf.Sign(gap) * Mathf.Min(Mathf.Abs(gap), MicroStepDegrees * Mathf.Deg2Rad);
                float target = current + turret.Signs[index] * (step + (float)hull[i] * dt * share + (float)feed[i] * dt) * Mathf.Rad2Deg;
                if (direct)
                {
                    float directGap = gapUsed;
                    if (directBraking) directGap -= ArmorFragmentsPlugin.TurretDirectBrake.Value * ((float)speed[i] - (float)feed[i]);
                    target = current + DirectVelocity(turret, index, directGap, (float)feed[i], dt, share * hullShake * gun.GainScale) * dt;
                }
                if (!turret.Wraps)
                {
                    float clamped = Mathf.Clamp(target, turret.Low, turret.High);
                    if (Mathf.Abs(clamped - target) > 0.001f && Mathf.Abs(current - clamped) < 0.1f && Mathf.Abs((float)theta[i]) * Mathf.Rad2Deg > 0.3f)
                    {
                        limit = true;
                        pinned = true;
                    }
                    target = clamped;
                    if (direct) turret.DirectV = (target - current) / Mathf.Max(dt, 1e-4f);   // the end of its travel: the speed it really has now
                }
                // A rotator that has met an obstacle and is asked to go further into it is held (see GuardRotator): that turret can't reach, so it
                // is not counted in the hull share either.
                if (turret.Blocked && AheadOf(turret, turret.BlockAngle, target) * turret.BlockDirection > 0.001f) pinned = true;
                commands[turret.Script] = target;
                driven.Add(turret);
            }
            gun.Pinned = pinned;
        }


        // ---- Swinging: if the aim swings back and forth around the target, the gain is lowered by itself ----------------

        private static float aimMin, aimMax, aimWindowEnd;

        // One per hull body: the sides its spin takes while the turrets on it work. The spin of a hull that is rocked by its turrets changes side
        // 4 times a second or more (2 Hz) and by more than a few degrees a second; a hull that only turns because a turret slews changes side
        // once at most. Each time it happens, the gain of every turret on that hull is cut by 30% (down to 15%), and it comes back slowly when
        // the hull has been quiet for a few seconds. (Recording of 14 turrets: hull spin 20 deg/s growing to 46 deg/s over 12 s at 2 Hz, the rings
        // swinging with it, while the cursor was being moved, so the ring detector was off.)
        private sealed class HullState
        {
            public float Scale = 1f, Previous, LastEvent, LastRecover, LastStep = -1f;
            public readonly List<float> Flips = new List<float>();
        }

        private static readonly Dictionary<Rigidbody, HullState> hulls = new Dictionary<Rigidbody, HullState>();

        private static float WatchHull(Rigidbody hull, float spinDegrees)
        {
            HullState state;
            if (!hulls.TryGetValue(hull, out state)) hulls[hull] = state = new HullState();
            float fixedNow = Time.fixedTime;
            if (state.LastStep == fixedNow) return state.Scale;   // the first turret of the step does the work, the others take the answer
            state.LastStep = fixedNow;

            float now = Time.unscaledTime;
            if (Mathf.Abs(spinDegrees) > 4f)
            {
                if (state.Previous != 0f && Mathf.Sign(spinDegrees) != Mathf.Sign(state.Previous)) state.Flips.Add(now);
                state.Previous = spinDegrees;
            }
            state.Flips.RemoveAll(t => now - t > 1f);
            if (state.Flips.Count >= 4)
            {
                state.Flips.Clear();
                state.LastEvent = now;
                float next = Mathf.Max(0.15f, state.Scale * 0.7f);
                if (next < state.Scale)
                {
                    state.Scale = next;
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the hull '" + hull.name + "' is rocking (spin " + spinDegrees.ToString("F0") + " deg/s), the gain of the turrets on it is now " +
                                                     (state.Scale * 100f).ToString("F0") + "%");
                }
            }
            else if (state.Scale < 1f && now - state.LastEvent > 6f && now - state.LastRecover > 3f)
            {
                state.LastRecover = now;
                state.Scale = Mathf.Min(1f, state.Scale + 0.1f);
            }
            return state.Scale;
        }

        private static void ResetSwing(Gun gun)
        {
            gun.PreviousTheta = 0f;
            gun.GainScale = 1f;
            gun.LagBoost = 0f;
            gun.Flips.Clear();
        }

        // The gap closing (the rotator with the biggest gap) changing side over and over (4 times in a second, each time more than a third of a
        // degree: two swings of 2 Hz) is swinging. Each turret has its own detector: a ship's turrets are not all alike.
        //
        // It only counts when the turret is close to the cursor and the cursor is still: a gap that changes side because the cursor was swept
        // across the sky (or flicked to the other side, where the turret is stopped at its limit and the gap jumps by 180 degrees) is not a
        // swing. Counting those cut every turret's gain to a fifth and added 200 ms to every assumed lag in a recording where the player only
        // moved the mouse, which made the turrets slow and gave them a slow wobble of their own.
        //
        // The measured lag comes from a slow 3 degree move; at the speed a turret swings the barrel follows later (a recording of a turret that
        // swung: the barrel trailed the angle by about 180 ms, the measure said 70 ms), and a predictor that thinks the barrel is quicker than
        // it is makes the swing. In a model, raising the assumed lag to the real one ends the swing, while lowering the gain does not (and too
        // much lag makes a small ripple). So a swing first adds 30 ms to the lag the turret assumes (up to 120 ms more); only when that is used up
        // is the gain cut by 30% (down to 30%). When it has been calm for a few seconds the gain creeps back up (the added lag stays).
        private static void WatchForSwinging(Gun gun, float theta, float errorDegrees, bool isLead)
        {
            float now = Time.unscaledTime;
            bool counts = !aimIsMoving && errorDegrees < 8f && Mathf.Abs(theta) < 8f;
            if (!counts)
            {
                gun.Flips.Clear();
                gun.PreviousTheta = 0f;
            }
            else if (Mathf.Abs(theta) > 0.3f)
            {
                if (gun.PreviousTheta != 0f && Mathf.Sign(theta) != Mathf.Sign(gun.PreviousTheta)) gun.Flips.Add(now);
                gun.PreviousTheta = theta;
            }

            gun.Flips.RemoveAll(t => now - t > 1f);
            if (gun.Flips.Count >= 4)
            {
                gun.Flips.Clear();
                gun.LastSwing = now;
                string what;
                // (It used to add 30 ms to the assumed lag first. With the 130 ms floor that only made it too much: in a recording a lone turret
                // that was calm at 130 ms swung at 2 Hz for ten seconds after being raised to 190 ms, which the model also gives.)
                float next = Mathf.Max(0.3f, gun.GainScale * 0.7f);
                if (next >= gun.GainScale) return;
                gun.GainScale = next;
                what = "its gain is now " + (gun.GainScale * 100f).ToString("F0") + "%";
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: turret " + (guns.IndexOf(gun) + 1) + " was swinging (error " + errorDegrees.ToString("F2") + " deg, measured lag " +
                    (gun.Lag * 1000f).ToString("F0") + " ms, " + gun.Share + " turret(s) share the hull" +
                    (isLead ? ", aim point " + aimDistance.ToString("F0") + " m away, between " + aimMin.ToString("F0") + " and " + aimMax.ToString("F0") +
                              " m in the last 2 s, camera pivot height " + pivotY.ToString("F2") : "") + "), " + what);
            }
            else if (gun.GainScale < 1f && now - gun.LastSwing > 3f && now - gun.LastRecover > 1.5f)
            {
                gun.LastRecover = now;
                gun.GainScale = Mathf.Min(1f, gun.GainScale + 0.1f);
            }
        }

        // Which way does each rotator turn each gun's barrel, and how late does the barrel follow it? Move the rotator a few degrees, watch
        // the barrels. A rotator that doesn't move any gun (not between the hull and a barrel, broken) is left out; one that moves several (a
        // turret ring that carries two guns) goes to the first gun it moves.
        //
        // A ship with fourteen turrets would take half a minute one rotator at a time. The way the bodies are joined says which guns each
        // rotator moves (see Predict), so rotators that move different guns are turned together, in a batch. What is measured is checked against
        // what was expected: if a gun that should not have moved did, or one that should have did not, the rotators of that batch are turned
        // alone, the slow way, so a wrong guess costs time and never a wrong answer.
        private const float JogDegrees = 3f;          // how far a rotator is turned to see what it moves
        private const float SettleSeconds = 0.3f;     // how long the barrels get to arrive after that
        private static bool graphTried;
        private static List<BodyEdge> edges;
        private static Rigidbody calBase;   // the vehicle's heaviest body: while calibrating, the barrels are watched from it (a ship shaking at 2 Hz is not a turret turning)

        private static Vector3 LocalDir(Vector3 world)
        {
            return calBase != null ? Quaternion.Inverse(calBase.rotation) * world : world;
        }

        private static Vector3 BarrelLocal(int gun)
        {
            return guns[gun].Cannon != null ? LocalDir(guns[gun].Cannon.transform.forward) : Vector3.forward;
        }

        private sealed class BodyEdge
        {
            public Rigidbody A, B;
            public Turret Turret;   // the marked rotator this joint is, if it is one
        }

        // Every joint between two bodies of the scene (other vehicles are other islands of the same graph and never reached).
        private static void BuildGraph()
        {
            edges = null;
            try
            {
                List<BodyEdge> found = new List<BodyEdge>();
                foreach (Joint joint in UnityEngine.Object.FindObjectsByType<Joint>(FindObjectsSortMode.None))
                {
                    if (joint == null) continue;
                    Rigidbody own = joint.GetComponent<Rigidbody>(), other = joint.connectedBody;
                    if (own == null || other == null || own == other) continue;
                    BodyEdge edge = new BodyEdge { A = own, B = other };
                    foreach (Turret turret in turrets)
                        if (turret.Script != null && jointRef(turret.Script) == joint) { edge.Turret = turret; break; }
                    found.Add(edge);
                }
                calBase = null;
                foreach (Gun gun in guns)
                {
                    Rigidbody body = gun.Cannon != null ? BodyOf(gun.Cannon.PartScript) : null;
                    if (body == null) continue;
                    foreach (Rigidbody candidate in Reach(found, body, null))
                        if (candidate != null && (calBase == null || candidate.mass > calBase.mass)) calBase = candidate;
                    break;
                }
                foreach (Turret turret in turrets) turret.Expect = Predict(found, turret);
                edges = found;
                int known = 0;
                foreach (Turret turret in turrets) if (turret.Expect != null) known++;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: " + found.Count + " joints between bodies; " + known + " of " + turrets.Count +
                                                 " rotator(s) have a known place in the vehicle (they can be calibrated together)");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim: the joints could not be read, the rotators are calibrated one by one (" + e.Message + ")");
                foreach (Turret turret in turrets) turret.Expect = null;
            }
        }

        // The guns a rotator turns: cut the joint; if the vehicle falls into two pieces, the piece without the heaviest body (the hull) is what it
        // turns. A joint with another joint in parallel (a loop) or one the graph does not know: null, "can't tell", and it is turned alone.
        private static HashSet<int> Predict(List<BodyEdge> all, Turret turret)
        {
            BodyEdge mine = null;
            foreach (BodyEdge edge in all) if (edge.Turret == turret) { mine = edge; break; }
            if (mine == null) return null;
            HashSet<Rigidbody> sideA = Reach(all, mine.A, mine), sideB = Reach(all, mine.B, mine);
            if (sideA.Contains(mine.B)) return null;
            HashSet<Rigidbody> moving = HeaviestMass(sideA) >= HeaviestMass(sideB) ? sideB : sideA;
            HashSet<int> result = new HashSet<int>();
            for (int g = 0; g < guns.Count; g++)
            {
                Rigidbody body = guns[g].Cannon != null ? BodyOf(guns[g].Cannon.PartScript) : null;
                if (body != null && moving.Contains(body)) result.Add(g);
            }
            return result.Count > 0 ? result : null;
        }

        private static HashSet<Rigidbody> Reach(List<BodyEdge> all, Rigidbody start, BodyEdge skip)
        {
            HashSet<Rigidbody> seen = new HashSet<Rigidbody> { start };
            Queue<Rigidbody> queue = new Queue<Rigidbody>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Rigidbody at = queue.Dequeue();
                foreach (BodyEdge edge in all)
                {
                    if (edge == skip) continue;
                    Rigidbody next = edge.A == at ? edge.B : edge.B == at ? edge.A : null;
                    if (next != null && seen.Add(next)) queue.Enqueue(next);
                }
            }
            return seen;
        }

        private static float HeaviestMass(HashSet<Rigidbody> bodies)
        {
            float heaviest = 0f;
            foreach (Rigidbody body in bodies) if (body != null && body.mass > heaviest) heaviest = body.mass;
            return heaviest;
        }

        private static void Calibrate(float dt)
        {
            if (Time.unscaledTime < recalibrateAt) return;   // waiting before measuring again
            if (calibration == null)
            {
                if (!graphTried)
                {
                    graphTried = true;
                    BuildGraph();
                }

                Turret first = null;
                foreach (Turret turret in turrets)
                {
                    if (turret.Done) continue;
                    if (!Moves(turret) || turret.Range < 1f)
                    {
                        turret.Done = true;
                        ArmorFragmentsPlugin.Log.LogWarning("Turret aim: rotator '" + turret.Name + "' can't be driven (broken, free spin, floppy or no range)");
                        continue;
                    }
                    first = turret;
                    break;
                }
                if (first == null)
                {
                    calibrated = true;
                    FinishCalibration();
                    return;
                }

                // The batch: the first rotator left, and every later one that either moves other guns than those already in it, or moves a gun
                // that one of them moves too but about a clearly different axis (a turret ring and its elevation: the barrel's rotation then
                // splits between them, see Along).
                List<Turret> members = new List<Turret> { first };
                if (!first.Solo && first.Expect != null)
                {
                    foreach (Turret other in turrets)
                    {
                        if (other == first || other.Done || other.Solo || other.Expect == null || !Moves(other) || other.Range < 1f) continue;
                        Vector3 axisOther = AxisOf(other.Script);
                        bool fits = true;
                        int sharing = 0;
                        foreach (Turret member in members)
                        {
                            if (!member.Expect.Overlaps(other.Expect)) continue;
                            sharing++;
                            if (Mathf.Abs(Vector3.Dot(axisOther, AxisOf(member.Script))) > 0.5f) { fits = false; break; }
                        }
                        if (fits && sharing < 2) members.Add(other);   // at most two rotators share a gun in a batch
                    }
                }
                bool together = members.Count > 1 || (!first.Solo && first.Expect != null);

                Calibration made = new Calibration();
                made.Barrels0 = new Vector3[guns.Count];
                for (int g = 0; g < guns.Count; g++) made.Barrels0[g] = BarrelLocal(g);
                foreach (Turret turret in members)
                {
                    float angle0 = angleRef(turret.Script);
                    made.Jogs.Add(new Jog
                    {
                        Turret = turret,
                        Angle0 = angle0,
                        Delta = turret.Wraps || angle0 + JogDegrees <= turret.Range ? JogDegrees : -JogDegrees,
                        Axis = LocalDir(AxisOf(turret.Script)),
                        Expect = together ? turret.Expect : null,
                    });
                }
                if (guardRefsOk)
                    foreach (Jog jog in made.Jogs) jog.BetaOk = MeasureBeta(jog.Turret.Script, out jog.Beta0);
                calibration = made;
                if (together)
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: calibrating " + members.Count + " rotator(s) at once (" +
                                                     (turrets.FindAll(t => !t.Done).Count - members.Count) + " more after this)");
            }

            Calibration c = calibration;
            foreach (Turret turret in turrets)
            {
                if (turret.Script == null) continue;
                float command = angleRef(turret.Script);
                foreach (Jog jog in c.Jogs) if (jog.Turret == turret) command = jog.Angle0 + jog.Delta;
                commands[turret.Script] = command;
            }

            c.Timer += dt;
            c.Elapsed += dt;
            Sample sample = new Sample { Time = c.Elapsed, Angles = new float[c.Jogs.Count], Barrels = new Vector3[guns.Count] };
            for (int j = 0; j < c.Jogs.Count; j++) sample.Angles[j] = angleRef(c.Jogs[j].Turret.Script);
            for (int g = 0; g < guns.Count; g++) sample.Barrels[g] = BarrelLocal(g);
            c.Samples.Add(sample);

            if (c.Phase == 0)
            {
                bool reached = true;
                for (int j = 0; j < c.Jogs.Count; j++)
                    if (Mathf.Abs(sample.Angles[j] - (c.Jogs[j].Angle0 + c.Jogs[j].Delta)) >= 0.05f) reached = false;
                if (reached || c.Timer > 4f) { c.Phase = 1; c.Timer = 0f; }
                return;
            }
            if (c.Timer < SettleSeconds) return;

            EvaluateJogs(c);
            calibration = null;
        }

        // How far (radians) a jog's rotator turned the gun's barrel, when the barrel has gone from where it was to `barrelNow`. A gun moved by one
        // rotator of the batch: the rotation along its axis. A gun moved by two (ring and elevation): the rotation split between the two axes.
        private static float Along(Calibration c, int jogIndex, int gun, Vector3 barrelNow)
        {
            Vector3 rotation = RotationVector(c.Barrels0[gun], barrelNow);
            Jog jog = c.Jogs[jogIndex];
            if (jog.Expect == null) return Vector3.Dot(rotation, jog.Axis);
            List<int> sharing = new List<int>();
            for (int j = 0; j < c.Jogs.Count; j++) if (c.Jogs[j].Expect != null && c.Jogs[j].Expect.Contains(gun)) sharing.Add(j);
            if (sharing.Count < 2) return Vector3.Dot(rotation, jog.Axis);
            Vector3[] axes = new Vector3[sharing.Count];
            for (int k = 0; k < sharing.Count; k++) axes[k] = c.Jogs[sharing[k]].Axis;
            double[] split = Solve(axes, rotation);
            return (float)split[sharing.IndexOf(jogIndex)];
        }

        private static void EvaluateJogs(Calibration c)
        {
            Vector3[] moved = new Vector3[guns.Count];
            for (int g = 0; g < guns.Count; g++)
                moved[g] = guns[g].Cannon != null ? RotationVector(c.Barrels0[g], BarrelLocal(g)) : Vector3.zero;

            bool together = c.Jogs[0].Expect != null;
            bool unexpected = false;
            if (together)
            {
                HashSet<int> expectedGuns = new HashSet<int>();
                float smallest = float.MaxValue;
                foreach (Jog jog in c.Jogs) { expectedGuns.UnionWith(jog.Expect); smallest = Mathf.Min(smallest, Mathf.Abs(jog.Delta) * Mathf.Deg2Rad); }
                for (int g = 0; g < guns.Count; g++)
                    if (!expectedGuns.Contains(g) && moved[g].magnitude > 0.5f * smallest) unexpected = true;
                if (unexpected)
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: a gun that was not expected to move did, so the batch of " + c.Jogs.Count +
                                                     " rotator(s) is measured again one by one");
            }

            for (int j = 0; j < c.Jogs.Count; j++)
            {
                Jog jog = c.Jogs[j];
                Turret turret = jog.Turret;
                float expected = jog.Delta * Mathf.Deg2Rad;

                float[] signs = new float[guns.Count], scales = new float[guns.Count], lags = new float[guns.Count];
                bool anyMoved = false, missing = unexpected;
                string effects = "";
                for (int g = 0; g < guns.Count; g++)
                {
                    if (guns[g].Cannon == null) continue;
                    bool wanted = jog.Expect != null && jog.Expect.Contains(g);
                    if (jog.Expect != null && !wanted) continue;
                    float along = Along(c, j, g, BarrelLocal(g));
                    if (Mathf.Abs(along) < 0.3f * Mathf.Abs(expected))
                    {
                        if (wanted) missing = true;
                        continue;
                    }
                    signs[g] = along / expected > 0f ? 1f : -1f;
                    scales[g] = Mathf.Clamp(Mathf.Abs(along / expected), 0.3f, 1.5f);
                    lags[g] = MeasureLag(c, j, g, along);
                    anyMoved = true;
                    effects += (effects.Length > 0 ? ", " : "") + "turret " + (g + 1) + " (group " + guns[g].GroupId + ")" + " " +
                               (along * Mathf.Rad2Deg).ToString("F1") + " deg (sign " + (signs[g] > 0f ? "+" : "-") + ", lag " + (lags[g] * 1000f).ToString("F0") + " ms)";
                }

                if (together && missing)
                {
                    turret.Solo = true;   // the guess was wrong or the rotator did not do what was expected: it is turned alone
                    continue;
                }

                turret.Done = true;
                if (guardRefsOk) FindPhysicalSign(turret, jog);
                if (anyMoved)
                {
                    turret.Usable = true;
                    for (int g = 0; g < guns.Count; g++) { turret.Signs[g] = signs[g]; turret.Scales[g] = scales[g]; turret.Lags[g] = lags[g]; }
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + turret.Name + "' for " + jog.Delta.ToString("F0") + " deg turns: " + effects +
                        "; range " + (turret.Wraps ? "all the way round" : turret.Low.ToString("F0") + " to " + turret.High.ToString("F0") + " deg") + " (" + turret.LimitSource + ")");
                }
                else
                {
                    if (calibrationRetries == 0 || calibrationRetries % 10 == 0) ArmorFragmentsPlugin.Log.LogWarning("Turret aim: rotator '" + turret.Name + "' did not move any of the cannons for " +
                        jog.Delta.ToString("F0") + " deg: it is left out");
                }
            }
        }

        // Which way the joint's measured angle turns when the rotator's angle grows (the game's two conventions are not assumed to agree): from the
        // calibration move. A rotator whose part did not follow the move (about as far as asked) is not watched by the obstacle guard.
        private static void FindPhysicalSign(Turret turret, Jog jog)
        {
            turret.PhysSign = 0f;
            turret.PhysKnown = false;
            float after;
            if (!jog.BetaOk || !MeasureBeta(turret.Script, out after))
            {
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + turret.Name + "' has no measured angle: the obstacle guard does not watch it");
                return;
            }
            float asked = angleRef(turret.Script) - jog.Angle0;   // how far the angle that was asked for went
            float measured = Mathf.DeltaAngle(jog.Beta0, after);   // how far the joint says the part went
            if (Mathf.Abs(asked) < 0.5f * Mathf.Abs(jog.Delta) || Mathf.Abs(measured) < 0.5f * Mathf.Abs(asked) || Mathf.Abs(measured) > 1.6f * Mathf.Abs(asked))
            {
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: rotator '" + turret.Name + "' was asked for " + asked.ToString("F1") + " deg and the joint measured " + measured.ToString("F1") +
                                                 " deg: the obstacle guard does not watch it");
                return;
            }
            turret.PhysSign = measured * asked > 0f ? 1f : -1f;
        }

        // How late the barrel follows the rotator: the time the barrel needed to get half way, minus the time the angle needed (the angle is
        // what the control asks for; the barrel gets there by the joint, which has its own delay and softness).
        private static float MeasureLag(Calibration c, int jogIndex, int gun, float finalAlong)
        {
            Jog jog = c.Jogs[jogIndex];
            float halfAngle = 0.5f * Mathf.Abs(jog.Delta);
            float halfBarrel = 0.5f * Mathf.Abs(finalAlong);
            float sign = Mathf.Sign(finalAlong);
            float timeAngle = -1f, timeBarrel = -1f;
            float previousTime = 0f, previousAngle = 0f, previousBarrel = 0f;
            foreach (Sample sample in c.Samples)
            {
                float angle = Mathf.Abs(sample.Angles[jogIndex] - jog.Angle0);
                float barrel = sign * Along(c, jogIndex, gun, sample.Barrels[gun]);
                if (timeAngle < 0f && angle >= halfAngle)
                    timeAngle = previousTime + (sample.Time - previousTime) * Mathf.Clamp01((halfAngle - previousAngle) / Mathf.Max(angle - previousAngle, 1e-6f));
                if (timeBarrel < 0f && barrel >= halfBarrel)
                    timeBarrel = previousTime + (sample.Time - previousTime) * Mathf.Clamp01((halfBarrel - previousBarrel) / Mathf.Max(barrel - previousBarrel, 1e-6f));
                previousTime = sample.Time;
                previousAngle = angle;
                previousBarrel = barrel;
            }
            if (timeAngle < 0f || timeBarrel < 0f) return 0f;
            return Mathf.Clamp(timeBarrel - timeAngle, 0.02f, 0.8f);
        }

        // After the last rotator: within each group, each turret gets the rotators that turn it and no earlier turret of the group (so no rotator is
        // driven by two turrets of a group); the same rotator can serve turrets of different groups, only one group being aimed at a time.
        private static void FinishCalibration()
        {
            foreach (Gun gun in guns) gun.Drivers.Clear();
            bool any = false;
            foreach (int groupId in groupIds)
            {
                HashSet<Turret> taken = new HashSet<Turret>();
                for (int g = 0; g < guns.Count; g++)
                {
                    Gun gun = guns[g];
                    if (gun.GroupId != groupId || gun.Cannon == null) continue;
                    foreach (Turret turret in turrets)
                    {
                        if (!turret.Usable || turret.Signs[g] == 0f || taken.Contains(turret)) continue;
                        // A turret with rotators linked in the designer is turned by those only; one with none uses the rotators with the old mark.
                        if (gun.Linked != null ? !gun.Linked.Contains(turret.Script) : !turret.Marked) continue;
                        gun.Drivers.Add(turret);
                        taken.Add(turret);
                    }
                    string label = gun.Cannon.PartScript != null && gun.Cannon.PartScript.Part != null ? gun.Cannon.PartScript.Part.Name : "?";
                    gun.Lag = 0f;
                    foreach (Turret driver in gun.Drivers) gun.Lag = Mathf.Max(gun.Lag, driver.Lags[g]);
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: turret " + (g + 1) + " ('" + label + "', group " + groupId + ") is turned by " + gun.Drivers.Count +
                                                     " rotator(s), the barrel follows " + (gun.Lag * 1000f).ToString("F0") + " ms late");
                    if (gun.Drivers.Count == 0)
                        ArmorFragmentsPlugin.Log.LogWarning("Turret aim: turret " + (g + 1) + " ('" + label + "') is not turned by any " +
                                                            (gun.Linked != null ? "of its linked rotators (or an earlier turret of its group already uses them)" : "rotator marked Turret that an earlier turret of its group doesn't already use") +
                                                            ": it only follows that turret");
                    else any = true;
                }
            }
            if (!any)
            {
                // Nothing moved the guns: the vehicle is likely still settling (a heavy one tipping over on spawn throws the measurement out).
                // The aim stays on and measures again 2 seconds later, as long as it takes.
                calibrationRetries++;
                foreach (Turret turret in turrets)
                {
                    turret.Done = turret.Solo = turret.Usable = false;
                    turret.Expect = null;
                    if (turret.Signs != null) Array.Clear(turret.Signs, 0, turret.Signs.Length);
                    if (turret.Scales != null) Array.Clear(turret.Scales, 0, turret.Scales.Length);
                    if (turret.Lags != null) Array.Clear(turret.Lags, 0, turret.Lags.Length);
                }
                calibration = null;
                calibrated = false;
                graphTried = false;
                edges = null;
                calBase = null;
                recalibrateAt = Time.unscaledTime + 2f;
                Status = "Calibrating again: no rotator moved the guns (the vehicle may still be settling)";
                if (calibrationRetries == 1 || calibrationRetries % 10 == 0)
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: no marked rotator moved the guns (the vehicle may still be settling); measuring again in 2 s (try " + calibrationRetries + ")");
                return;
            }
            calibrationRetries = 0;
            FindHull();
        }

        // The hull: from a gun's body, each rotator that has the body on one side leads to the body on its other side, and so on to the
        // vehicle's base. That body's angular velocity is what the stabilizer works against.
        private static Rigidbody hullBody;   // the lead turret's (the camera pivot is centered on it)

        private static Rigidbody BodyOf(PartScript part)
        {
            return part != null && part.Body != null && part.Body.RigidBody != null ? part.Body.RigidBody.PhysxRigidBody : null;
        }

        private static void FindHull()
        {
            hullBody = null;
            pivotBodies.Clear();
            for (int g = 0; g < guns.Count; g++)
            {
                Gun gun = guns[g];
                gun.Hull = null;
                gun.Chain.Clear();
                Rigidbody current = gun.Cannon != null ? BodyOf(gun.Cannon.PartScript) : null;
                if (current == null) continue;
                List<Turret> left = turrets.FindAll(t => t.Usable && t.Script != null && jointRef(t.Script) != null);
                for (int guard = 0; guard < 8 && left.Count > 0; guard++)
                {
                    Turret found = null;
                    Rigidbody parent = null;
                    foreach (Turret turret in left)
                    {
                        ConfigurableJoint joint = jointRef(turret.Script);
                        Rigidbody own = joint.GetComponent<Rigidbody>(), other = joint.connectedBody;
                        if (own == current) { found = turret; parent = other; break; }
                        if (other == current) { found = turret; parent = own; break; }
                    }
                    if (found == null || parent == null) break;
                    left.Remove(found);
                    gun.Chain.Add(found);
                    current = parent;
                }
                gun.Hull = current;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the hull of turret " + (g + 1) + " is the body '" +
                                                 (current != null ? current.name : "?") + "' (" +
                                                 (left.Count == 0 ? "every rotator is on the way to it" : left.Count + " rotator(s) are not on the way") + ")");
            }

            // Every body joined to the hull (the whole vehicle): the camera pivot follows their centre of mass, which a rocking hull part does not move.
            if (edges != null)
            {
                foreach (Gun gun in guns)
                {
                    if (gun.Hull == null) continue;
                    foreach (Rigidbody body in Reach(edges, gun.Hull, null)) pivotBodies.Add(body);
                    break;
                }
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim: the camera pivot follows the centre of mass of " + pivotBodies.Count + " joined bodies");
            }
        }

        // The rotation (axis x angle, radians) that takes direction a onto direction b.
        private static Vector3 RotationVector(Vector3 a, Vector3 b)
        {
            Vector3 cross = Vector3.Cross(a, b);
            float sin = cross.magnitude;
            if (sin < 1e-7f) return Vector3.zero;
            return cross / sin * Mathf.Atan2(sin, Vector3.Dot(a, b));
        }

        // Least squares: theta minimizing |sum(theta_i * axes_i) - target|, with a little damping so a rotator whose axis is almost
        // along the barrel (it can't do anything useful) is not asked for wild angles.
        private static double[] Solve(Vector3[] axes, Vector3 target)
        {
            int n = axes.Length;
            double[,] m = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) m[i, j] = Vector3.Dot(axes[i], axes[j]) + (i == j ? 0.02 : 0.0);
                m[i, n] = Vector3.Dot(axes[i], target);
            }
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < n; row++) if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
                if (pivot != col)
                    for (int k = 0; k <= n; k++) { double swap = m[col, k]; m[col, k] = m[pivot, k]; m[pivot, k] = swap; }
                double diagonal = m[col, col];
                if (Math.Abs(diagonal) < 1e-9) continue;
                for (int row = 0; row < n; row++)
                {
                    if (row == col) continue;
                    double factor = m[row, col] / diagonal;
                    for (int k = col; k <= n; k++) m[row, k] -= factor * m[col, k];
                }
            }
            double[] result = new double[n];
            for (int i = 0; i < n; i++) result[i] = Math.Abs(m[i, i]) < 1e-9 ? 0.0 : m[i, n] / m[i, i];
            return result;
        }

        // ---- The circle and the marker on the screen ----------------------------------------------------------------

        private static GameObject hudRoot;
        private static RectTransform ringRect, markerRect;
        private static RawImage ringImage, markerImage;
        private static Texture2D ringTexture, dotTexture;

        // A white picture whose alpha is a function of the distance from the middle (0 at the middle, 1 at the edge). Drawn by a RawImage, which
        // takes the texture as it is, so what is drawn is only the alpha and the color the RawImage is given.
        private static Texture2D MakeTexture(int size, Func<float, float> alphaAtRadius)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "ArmorFragments turret hud",
            };
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float alpha = Mathf.Clamp01(alphaAtRadius(Mathf.Sqrt(dx * dx + dy * dy)));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);   // stays readable (the check below reads it)
            return texture;
        }

        private static RawImage MakeImage(Transform parent, string name, Texture2D texture, float size, out RectTransform rect)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            rect = go.AddComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            RawImage image = go.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
        }

        private static void MakeHud()
        {
            DestroyHud();
            if (ringTexture == null)
                ringTexture = MakeTexture(256, r => 1f - Edge(0.006f, 0.022f, Mathf.Abs(r - 0.9f)));
            if (dotTexture == null)
                dotTexture = MakeTexture(48, r => (1f - Edge(0.5f, 0.7f, r)) * 0.9f + (1f - Edge(0.04f, 0.12f, Mathf.Abs(r - 0.86f))) * 0.3f);

            hudRoot = new GameObject("ArmorFragments turret aim");
            Canvas canvas = hudRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            ringImage = MakeImage(hudRoot.transform, "circle", ringTexture, 64f, out ringRect);
            markerImage = MakeImage(hudRoot.transform, "gun marker", dotTexture, 16f, out markerRect);

            // If the circle shows up as a plain square again, this says whether the picture itself is right (alpha 0 in the middle of the ring,
            // 255 on it) and which shader the picture is drawn with.
            try
            {
                Material material = ringImage.materialForRendering;
                ArmorFragmentsPlugin.Log.LogInfo("Turret aim HUD: the ring picture has alpha " + ringTexture.GetPixel(64, 64).a.ToString("F2") + " in the middle and " +
                    ringTexture.GetPixel(64 + 56, 64).a.ToString("F2") + " on the ring; drawn with shader '" + (material != null && material.shader != null ? material.shader.name : "none") + "'");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Turret aim HUD check failed: " + e.Message);
            }
        }

        private static void DestroyHud()
        {
            if (hudRoot != null) UnityEngine.Object.Destroy(hudRoot);
            hudRoot = null;
            ringRect = markerRect = null;
            ringImage = markerImage = null;
        }

        // ---- The reticle ---------------------------------------------------------------------------------------------
        //
        // Two layers, drawn over everything in OnGUI:
        //  - the fixed sight: where you aim (the aim point seen from the camera; the middle of the screen unless the free cursor is on). Through the
        //    sight camera it is a full gunsight (thick outer bars, thin inner lines, a few ticks, a chevron in the middle); in the orbit view only
        //    its middle part.
        //  - a gun circle for each gun being aimed: where that gun's barrel really points (the first thing along the barrel, or a far point). It
        //    trails the fixed sight while the turret turns and sits on it when the gun is on target; it is bigger the further the gun is off.
        //    In it, a small chevron that lights green when the gun is on target, and around it a ring of segments that fills as the gun reloads.
        // Under the right wing of the fixed sight: the lead gun's reload countdown. Under each gun circle: its range. (The rounds left are on
        // the ammo rack bars at the bottom left, AmmoHud.cs.)
        // Every line and letter has a thin dark edge, so it reads on sky and on mud. Perfect aim: the circle shows no spread (there is none yet).

        private sealed class GunMark
        {
            public Vector2 At;        // screen pixels, y up
            public float Radius;
            public bool Lit;          // on target
            public float Reload;      // 0..1 (1 = ready)
            public string Range;      // "850 m" ("" = nothing in reach)
        }

        private static readonly List<GunMark> gunMarks = new List<GunMark>();
        private static bool hudRingShown;
        private static Vector2 hudRingAt;   // the fixed sight, screen pixels, y up
        private static bool hudSightView, hudFreeLook;
        private static string hudReload = "", hudZoom = "";
        private static bool hudReloading;

        private static void UpdateHud(Camera cam, Vector3 muzzle, bool freeLook)
        {
            hudRingShown = false;
            hudSightView = sight;
            hudFreeLook = freeLook;
            gunMarks.Clear();
            float now = Time.time;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);

            if (haveDesired)
            {
                Vector3 ring = cam.WorldToScreenPoint(WotStyle && wotRanging ? wotReticlePoint : muzzle + desired * aimDistance);   // World of Tanks: the reticle is on the line of sight, the gun above it
                hudRingShown = ring.z > 0f;
                hudRingAt = new Vector2(ring.x, ring.y);
            }
            else if (Lead() == null)
            {
                hudRingShown = false;
            }

            // War Thunder style: only the ring and a dot per gun (where its barrel points at the aim's distance).
            if (!WotStyle)
            {
                wtMarkers.Clear();
                if (haveDesired)   // also in the sight: each gun's ring, catching up with the crosshair
                {
                    Vector3 aimPoint = muzzle + desired * aimDistance;
                    foreach (Gun gun in guns)
                    {
                        if (!InActiveGroup(gun) || !Live(gun)) continue;
                        Vector3 tip = gun.Cannon.TipPosition;
                        Vector3 marker = cam.WorldToScreenPoint(tip + gun.Cannon.transform.forward.normalized * Mathf.Max((aimPoint - tip).magnitude, 1f));
                        if (marker.z > 0f) wtMarkers.Add(new Vector2(marker.x, marker.y));
                    }
                }
                UpdateRangeMarks(cam);
                if (hudRoot != null && ringImage != null) { ringImage.enabled = false; markerImage.enabled = false; }
                return;
            }

            int raycasts = 0;
            Gun lead = Lead();
            foreach (Gun gun in guns)
            {
                if (!InActiveGroup(gun) || !Live(gun)) continue;
                Vector3 tip = gun.Cannon.TipPosition;
                Vector3 forward = gun.Cannon.transform.forward.normalized;
                // Where the barrel points: the first thing along it (a few guns a frame), else the aim's distance.
                Vector3 hit;
                if (WotStyle && wotRanging)
                {
                    // Where the shell lands: the barrel turned down by the drop, out to the range in use (on the reticle when the gun is on).
                    hit = tip + Depressed(forward, wotDrop) * Mathf.Max(wotRange, 5f);
                    gun.RangeToHit = -1f;
                }
                else if (raycasts < 6)
                {
                    raycasts++;
                    hit = AimPoint(tip, forward);
                    float distance = (hit - tip).magnitude;
                    gun.RangeToHit = distance < 2999f ? distance : -1f;
                    if (gun.RangeToHit < 0f) hit = tip + forward * Mathf.Max(aimDistance, 50f);
                }
                else hit = tip + forward * Mathf.Max(aimDistance, 50f);
                Vector3 screen = cam.WorldToScreenPoint(hit);
                if (screen.z <= 0f) continue;

                // Through the sight camera, which sits on the gun when it does not follow the mouse, the gun's own circle is the middle of the view.
                float scale = Mathf.Max(Screen.height / 1080f, 0.6f);
                float off = gun.Drivers.Count == 0 ? 0f : Mathf.Clamp(gun.AimError, 0f, 10f);
                float target = (sight ? 26f : 18f) * scale + off * 3.5f * scale;
                gun.CircleRadius = gun.CircleRadius < 0f ? target : Mathf.Lerp(gun.CircleRadius, target, 1f - Mathf.Exp(-dt / 0.12f));

                float reload = gun.ReloadTotal > 0f && gun.NextFire > now ? 1f - (gun.NextFire - now) / gun.ReloadTotal : 1f;
                gunMarks.Add(new GunMark
                {
                    At = new Vector2(screen.x, screen.y),
                    Radius = gun.CircleRadius,
                    Lit = calibrated && (gun.Drivers.Count == 0 || OnTargetStrict(gun)),
                    Reload = Mathf.Clamp01(reload),
                    Range = gun.RangeToHit > 0f ? RangeText(gun.RangeToHit) : "",
                });
            }

            // The lead gun's reload, under the right wing of the fixed sight.
            hudReloading = lead != null && lead.NextFire > now;
            hudReload = lead == null ? "" : hudReloading ? (lead.NextFire - now).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" : "READY";
            CameraManagerScript cm = CameraManagerScript.Instance;
            float zoom = cm != null && cam.fieldOfView > 0.01f ? cm.CurrentCameraFieldOfView / cam.fieldOfView : 1f;
            hudZoom = zoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x";


            if (hudRoot != null && ringImage != null)
            {
                ringImage.enabled = false;
                markerImage.enabled = false;
            }
        }

        // ---- Drawing --------------------------------------------------------------------------------------------------

        private static Material lineMaterial;
        private static bool lineTried;

        // The plain colored shader every Unity player has, blended by its alpha.
        private static bool ReticleReady()
        {
            if (!lineTried)
            {
                lineTried = true;
                try
                {
                    Shader shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader != null)
                    {
                        lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                        lineMaterial.SetInt("_ZWrite", 0);
                        if (lineMaterial.HasProperty("_ZTest")) lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    }
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim HUD: " + (lineMaterial != null ? "the reticle is drawn with Hidden/Internal-Colored" : "Hidden/Internal-Colored is not in this build, no reticle"));
                }
                catch (Exception e)
                {
                    lineMaterial = null;
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim HUD: the reticle could not be made (" + e.Message + ")");
                }
            }
            return lineMaterial != null;
        }

        private static readonly Color Ink = new Color(0.96f, 0.95f, 0.90f, 0.95f);
        private static readonly Color InkDim = new Color(0.96f, 0.95f, 0.90f, 0.25f);
        private static readonly Color Edge2 = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color LitGreen = new Color(0.49f, 0.89f, 0.55f, 1f);
        private static readonly Color Amber = new Color(1f, 0.71f, 0.28f, 1f);
        private static readonly Color Muted = new Color(0.79f, 0.78f, 0.73f, 0.9f);

        // A straight stroke of width w (pixels, y down).
        private static void Stroke(float x0, float y0, float x1, float y1, float w, Color color)
        {
            float dx = x1 - x0, dy = y1 - y0;
            float length = Mathf.Sqrt(dx * dx + dy * dy);
            if (length < 1e-4f) return;
            float nx = -dy / length * w * 0.5f, ny = dx / length * w * 0.5f;
            // Round-ish ends: the stroke runs half a width past each end.
            float ex = dx / length * w * 0.5f, ey = dy / length * w * 0.5f;
            GL.Color(color);
            GL.Vertex3(x0 - ex + nx, y0 - ey + ny, 0f);
            GL.Vertex3(x1 + ex + nx, y1 + ey + ny, 0f);
            GL.Vertex3(x1 + ex - nx, y1 + ey - ny, 0f);
            GL.Vertex3(x0 - ex + nx, y0 - ey + ny, 0f);
            GL.Vertex3(x1 + ex - nx, y1 + ey - ny, 0f);
            GL.Vertex3(x0 - ex - nx, y0 - ey - ny, 0f);
        }

        // An arc of a circle (angles in radians, 0 = up, clockwise on screen).
        private static void ArcStroke(float cx, float cy, float r, float a0, float a1, float w, Color color)
        {
            int segments = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(a1 - a0) / (Mathf.PI * 2f) * 64f));
            float inner = r - w * 0.5f, outer = r + w * 0.5f;
            GL.Color(color);
            for (int i = 0; i < segments; i++)
            {
                float t0 = a0 + (a1 - a0) * i / segments, t1 = a0 + (a1 - a0) * (i + 1) / segments;
                float s0 = Mathf.Sin(t0), c0 = -Mathf.Cos(t0), s1 = Mathf.Sin(t1), c1 = -Mathf.Cos(t1);
                GL.Vertex3(cx + s0 * inner, cy + c0 * inner, 0f);
                GL.Vertex3(cx + s1 * inner, cy + c1 * inner, 0f);
                GL.Vertex3(cx + s1 * outer, cy + c1 * outer, 0f);
                GL.Vertex3(cx + s0 * inner, cy + c0 * inner, 0f);
                GL.Vertex3(cx + s1 * outer, cy + c1 * outer, 0f);
                GL.Vertex3(cx + s0 * outer, cy + c0 * outer, 0f);
            }
        }

        // The edge pass draws everything wider in dark, the ink pass on top.
        private static bool edgePass;

        private static void Line(float x0, float y0, float x1, float y1, float w, Color color)
        {
            if (edgePass) Stroke(x0, y0, x1, y1, w + 2.4f, Edge2);
            else Stroke(x0, y0, x1, y1, w, color);
        }

        private static void Arc(float cx, float cy, float r, float a0, float a1, float w, Color color)
        {
            if (edgePass) ArcStroke(cx, cy, r, a0, a1, w + 2.4f, Edge2);
            else ArcStroke(cx, cy, r, a0, a1, w, color);
        }

        private static void DrawFixedSight(float cx, float cy, float s, bool full)
        {
            // Middle: the chevron (its tip on the aim point) and the thin inner lines with a gap around it.
            Line(cx - 9f * s, cy + 9f * s, cx, cy + 1f * s, 1.8f * s, Ink);
            Line(cx, cy + 1f * s, cx + 9f * s, cy + 9f * s, 1.8f * s, Ink);
            float inner = full ? 90f : 46f;
            Line(cx - inner * s, cy, cx - 16f * s, cy, 1.4f * s, Ink);
            Line(cx + 16f * s, cy, cx + inner * s, cy, 1.4f * s, Ink);
            Line(cx, cy + 16f * s, cx, cy + inner * s, 1.4f * s, Ink);
            if (!full) return;
            // The gunsight: thick outer bars, ticks on the inner lines, and the drop ladder below.
            Line(cx - 300f * s, cy, cx - 90f * s, cy, 4f * s, Ink);
            Line(cx + 90f * s, cy, cx + 300f * s, cy, 4f * s, Ink);
            Line(cx, cy + 90f * s, cx, cy + 260f * s, 4f * s, Ink);
            foreach (float d in new[] { 40f, 65f })
            {
                Line(cx - d * s, cy - 5f * s, cx - d * s, cy + 5f * s, 1.4f * s, Ink);
                Line(cx + d * s, cy - 5f * s, cx + d * s, cy + 5f * s, 1.4f * s, Ink);
            }
            foreach (float d in new[] { 40f, 65f })
                Line(cx - 7f * s, cy + d * s, cx + 7f * s, cy + d * s, 1.4f * s, Ink);
        }

        private static void DrawGunMark(GunMark mark, float x, float y, float s)
        {
            float r = mark.Radius;
            Arc(x, y, r, 0f, Mathf.PI * 2f, 1.6f * s, Ink);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f, len = (i % 2 == 0 ? 5f : 3f) * s;
                float sx = Mathf.Sin(a), sy = -Mathf.Cos(a);
                Line(x + sx * (r + 3f * s), y + sy * (r + 3f * s), x + sx * (r + 3f * s + len), y + sy * (r + 3f * s + len), 1.4f * s, Ink);
            }
            // The reload ring: 24 segments, filled clockwise from the top.
            const int Segments = 24;
            float ringR = r + 13f * s, gap = 0.06f, step = Mathf.PI * 2f / Segments;
            int filled = Mathf.FloorToInt(mark.Reload * Segments + 1e-4f);
            for (int i = 0; i < Segments; i++)
            {
                Color color = mark.Reload >= 1f ? new Color(LitGreen.r, LitGreen.g, LitGreen.b, 0.75f) : i < filled ? Amber : InkDim;
                Arc(x, y, ringR, i * step + gap, (i + 1) * step - gap, 2.2f * s, color);
            }
            // The small chevron: lit when the gun is on target.
            Color chevron = mark.Lit ? LitGreen : Muted;
            Line(x - 7f * s, y + 5f * s, x, y - 2f * s, 2.4f * s, chevron);
            Line(x, y - 2f * s, x + 7f * s, y + 5f * s, 2.4f * s, chevron);
        }

        private static GUIStyle textStyle;

        private static void Label(float x, float y, string text, int size, Color color, TextAnchor anchor)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (textStyle == null) textStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Overflow };
            textStyle.fontSize = size;
            textStyle.alignment = anchor;
            Rect rect = new Rect(x - 300f, y - size, 600f, size * 2f);
            if (anchor == TextAnchor.MiddleLeft) rect = new Rect(x, y - size, 600f, size * 2f);
            else if (anchor == TextAnchor.MiddleRight) rect = new Rect(x - 600f, y - size, 600f, size * 2f);
            textStyle.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            foreach (Vector2 o in new[] { new Vector2(1f, 1f), new Vector2(-1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, -1f) })
                GUI.Label(new Rect(rect.x + o.x, rect.y + o.y, rect.width, rect.height), text, textStyle);
            textStyle.normal.textColor = color;
            GUI.Label(rect, text, textStyle);
        }

        // ---- War Thunder style: the gunsight's range marks -----------------------------------------------------------------
        //
        // Through a sight, a crosshair on the line of the ranging gun's barrel, with a mark below it for each range: put the mark of the target's range
        // on the target and the shell lands there. The marks come from the gun's own shell (its muzzle velocity, gravity and the shell's drag, flown
        // like the game's physics does it, see AutoAim.Solve), worked out for each gun every time the aim starts (each spawn). On screen each mark is
        // where that range really is from the sight: the barrel line turned down by the drop, at that distance, seen from the camera, so a sight that
        // sits away from the gun (a ship's director) shows them in the right place too.
        // The ranging gun: the sight's own gun; for a Gun Sight shared by the guns of a group, the one marked Ranging gun, else the biggest.

        private static readonly float[] RangeSteps = BuildRangeSteps();

        private static float[] BuildRangeSteps()
        {
            List<float> steps = new List<float>();
            for (float r = 100f; r <= 2000f; r += 100f) steps.Add(r);
            for (float r = 2500f; r <= 6000f; r += 500f) steps.Add(r);
            return steps.ToArray();
        }

        private static void BuildRangeTables()
        {
            foreach (Gun gun in guns)
            {
                gun.RangeDrop = null;
                gun.Caliber = 0f;
                try
                {
                    AmmoRound round;
                    if (gun.Cannon.PartScript != null && AmmoRound.TryFromPart(gun.Cannon.PartScript, out round)) gun.Caliber = round.CaliberMm;
                    ShellModel shell = AutoAim.ShellOf(gun.Cannon, Vector3.zero);
                    shell.Muzzle = Vector3.zero;
                    if (shell.Speed < 1f) continue;
                    float[] drop = new float[RangeSteps.Length];
                    StringBuilder log = new StringBuilder();
                    for (int i = 0; i < RangeSteps.Length; i++)
                    {
                        AimSolution solution = AutoAim.Solve(shell, Vector3.forward * RangeSteps[i], Vector3.zero, false, true);
                        drop[i] = solution.Ok ? solution.DropDegrees : -1f;
                        if (solution.Ok && (i == 4 || i == 9 || i == 19)) log.Append(RangeSteps[i].ToString("F0")).Append(" m ").Append(solution.DropDegrees.ToString("F2")).Append(" deg, ");
                    }
                    gun.RangeDrop = drop;
                    ArmorFragmentsPlugin.Log.LogInfo("Turret aim: range marks for a " + gun.Caliber.ToString("F0") + " mm gun at " + shell.Speed.ToString("F0") + " m/s: " + log);
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("Turret aim: no range marks for a gun (" + e.Message + ")");
                }
            }
        }

        // The gun the sight in use is ranged for.
        private static Gun RangingGunOf(Gun sightOwner)
        {
            if (sightOwner == null || sightOwner.Sight == null) return sightOwner;
            Gun marked = null, biggest = null;
            foreach (Gun gun in guns)
            {
                if (gun.Sight != sightOwner.Sight || !Live(gun)) continue;
                if (gun.RangingGun && marked == null) marked = gun;
                if (biggest == null || gun.Caliber > biggest.Caliber) biggest = gun;
            }
            return marked ?? biggest ?? sightOwner;
        }

        private struct RangeMark
        {
            public Vector2 At;      // screen pixels, y up
            public float Meters;
        }

        private static readonly List<RangeMark> rangeMarks = new List<RangeMark>();
        private static Vector2 rangeCenter;   // the sight's line of view, far out (screen pixels, y up)
        private static bool rangeShown;

        private static void UpdateRangeMarks(Camera cam)
        {
            rangeMarks.Clear();
            rangeShown = false;
            if (!sight || hudFreeLook) return;
            Gun gun = RangingGunOf(sightGun);
            if (!Live(gun) || gun.RangeDrop == null) return;
            // On the sight's own line of view (the middle of the scope): the guns aim at what it meets, so a mark put on the target, the shell lands there.
            Vector3 tip = cam.transform.position;
            Vector3 bore = cam.transform.forward.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, bore);
            if (right.sqrMagnitude < 1e-4f) return;   // pointing straight up or down
            right.Normalize();
            Vector3 center = cam.WorldToScreenPoint(tip + bore * 3000f);
            if (center.z <= 0f) return;
            rangeCenter = new Vector2(center.x, center.y);
            rangeShown = true;
            for (int i = 0; i < RangeSteps.Length; i++)
            {
                float drop = gun.RangeDrop[i];
                if (drop < 0f) break;
                Vector3 line = Quaternion.AngleAxis(drop, right) * bore;
                Vector3 at = cam.WorldToScreenPoint(tip + line * RangeSteps[i]);
                if (at.z <= 0f) continue;
                rangeMarks.Add(new RangeMark { At = new Vector2(at.x, at.y), Meters = RangeSteps[i] });
            }
        }

        // The sight's crosshair and range marks (drawn with the plain alpha lines and a dark edge, like the World of Tanks reticle).
        private static void DrawRangeSight(float height, bool flip)
        {
            if (!rangeShown || !ReticleReady()) return;
            float s = Mathf.Max(height / 1080f, 0.6f);
            float cx = rangeCenter.x, cy = flip ? rangeCenter.y : height - rangeCenter.y;
            float bottom = cy + 40f * s;
            foreach (RangeMark mark in rangeMarks) bottom = Mathf.Max(bottom, (flip ? mark.At.y : height - mark.At.y) + 12f * s);

            GL.PushMatrix();
            try
            {
                lineMaterial.SetPass(0);
                GL.LoadPixelMatrix(0f, Screen.width, height, 0f);
                GL.Begin(GL.TRIANGLES);
                for (int pass = 0; pass < 2; pass++)
                {
                    edgePass = pass == 0;
                    // The crosshair: a horizontal line with a gap round the middle, and the vertical line down through the marks.
                    Line(cx - 220f * s, cy, cx - 12f * s, cy, 1.4f * s, Ink);
                    Line(cx + 12f * s, cy, cx + 220f * s, cy, 1.4f * s, Ink);
                    Line(cx, cy - 60f * s, cx, cy - 12f * s, 1.4f * s, Ink);
                    Line(cx, cy + 12f * s, cx, bottom, 1.4f * s, InkDim);
                    float lastY = float.NegativeInfinity;
                    foreach (RangeMark mark in rangeMarks)
                    {
                        float x = mark.At.x, y = flip ? mark.At.y : height - mark.At.y;
                        bool major = Mathf.Approximately(mark.Meters % 400f, 0f) || mark.Meters >= 2000f;
                        if (y - lastY < (major ? 3f : 6f) * s) continue;   // marks too close together are left out
                        lastY = y;
                        float half = (major ? 14f : 7f) * s;
                        Line(x - half, y, x + half, y, (major ? 1.6f : 1.1f) * s, Ink);
                    }
                }
                GL.End();
            }
            finally
            {
                GL.PopMatrix();
                edgePass = false;
            }

            int size = Mathf.RoundToInt(13f * s);
            float labelY = float.NegativeInfinity;
            foreach (RangeMark mark in rangeMarks)
            {
                bool major = Mathf.Approximately(mark.Meters % 400f, 0f) || mark.Meters >= 2000f;
                if (!major) continue;
                float y = flip ? mark.At.y : height - mark.At.y;
                if (y - labelY < 14f * s) continue;
                labelY = y;
                Label(mark.At.x + 20f * s, y, (mark.Meters / 100f).ToString("F0"), size, Ink, TextAnchor.MiddleLeft);
            }
        }

        // ---- War Thunder style: Luciano's crosshair ---------------------------------------------------------------
        // A thin ring where you aim and a small dot where each gun's barrel points (at the aim's distance; not through the sight, where the
        // camera is on the gun). Not in a color of their own: they invert what is behind them, like the crosshair of Minecraft.

        private static Material invertMaterial;
        private static bool invertTried;

        private static bool InvertingReady()
        {
            if (!invertTried)
            {
                invertTried = true;
                try
                {
                    Shader shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader != null)
                    {
                        invertMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        invertMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusDstColor);
                        invertMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcColor);
                        invertMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                        invertMaterial.SetInt("_ZWrite", 0);
                        if (invertMaterial.HasProperty("_ZTest")) invertMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    }
                }
                catch (Exception)
                {
                    invertMaterial = null;
                }
            }
            return invertMaterial != null;
        }

        private static void InvertVertex(float x, float y, float brightness)
        {
            GL.Color(new Color(brightness, brightness, brightness, 1f));
            GL.Vertex3(x, y, 0f);
        }

        private static void InvertQuad(float x0, float y0, float b0, float x1, float y1, float b1, float x2, float y2, float b2, float x3, float y3, float b3)
        {
            InvertVertex(x0, y0, b0); InvertVertex(x1, y1, b1); InvertVertex(x2, y2, b2);
            InvertVertex(x0, y0, b0); InvertVertex(x2, y2, b2); InvertVertex(x3, y3, b3);
        }

        private static void InvertRing(float cx, float cy, float radius, float halfWidth)
        {
            const int Segments = 72;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                float inner = radius - halfWidth, outer = radius + halfWidth;
                InvertQuad(cx + c0 * inner, cy + s0 * inner, 0f, cx + c1 * inner, cy + s1 * inner, 0f,
                           cx + c1 * radius, cy + s1 * radius, 1f, cx + c0 * radius, cy + s0 * radius, 1f);
                InvertQuad(cx + c0 * radius, cy + s0 * radius, 1f, cx + c1 * radius, cy + s1 * radius, 1f,
                           cx + c1 * outer, cy + s1 * outer, 0f, cx + c0 * outer, cy + s0 * outer, 0f);
            }
        }

        private static void InvertDot(float cx, float cy, float radius)
        {
            const int Segments = 20;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                InvertVertex(cx, cy, 1f); InvertVertex(cx + c0 * radius, cy + s0 * radius, 1f); InvertVertex(cx + c1 * radius, cy + s1 * radius, 1f);
                InvertQuad(cx + c0 * radius, cy + s0 * radius, 1f, cx + c1 * radius, cy + s1 * radius, 1f,
                           cx + c1 * (radius + 1f), cy + s1 * (radius + 1f), 0f, cx + c0 * (radius + 1f), cy + s0 * (radius + 1f), 0f);
            }
        }

        private static readonly List<Vector2> wtMarkers = new List<Vector2>();   // War Thunder style: where each gun points, at the aim's distance

        private static void DrawWarThunder(float height, bool flip)
        {
            if (!InvertingReady()) return;
            GL.PushMatrix();
            try
            {
                invertMaterial.SetPass(0);
                GL.LoadPixelMatrix(0f, Screen.width, height, 0f);
                GL.Begin(GL.TRIANGLES);
                if (hudSightView)
                {
                    // Through the sight: a ring for each gun where its barrel points, catching up with the scope's crosshair (a gun that can't turn
                    // that far, or is still turning, stays off it).
                    foreach (Vector2 marker in wtMarkers) InvertRing(marker.x, flip ? marker.y : height - marker.y, 22f, 0.9f);
                }
                else
                {
                    if (hudRingShown) InvertRing(hudRingAt.x, flip ? hudRingAt.y : height - hudRingAt.y, 26f, 0.9f);
                    foreach (Vector2 marker in wtMarkers) InvertDot(marker.x, flip ? marker.y : height - marker.y, 2.6f);
                }
                GL.End();
            }
            finally
            {
                GL.PopMatrix();
            }
        }

        // Through the sight: the screen blacked out round a circle in the middle (the scope), its edge softened.
        private static readonly Color ScopeBlack = new Color(0f, 0f, 0f, 1f);
        private static readonly Color ScopeClear = new Color(0f, 0f, 0f, 0f);

        private static void DrawScopeMask(float width, float height)
        {
            if (!ReticleReady()) return;
            float cx = width * 0.5f, cy = height * 0.5f;
            float radius = height * 0.47f;
            float soft = radius * 0.9f;
            float far = Mathf.Sqrt(width * width + height * height);   // past every corner
            const int Segments = 96;
            GL.PushMatrix();
            try
            {
                lineMaterial.SetPass(0);
                GL.LoadPixelMatrix(0f, width, height, 0f);
                GL.Begin(GL.TRIANGLES);
                for (int i = 0; i < Segments; i++)
                {
                    float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                    float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                    // The soft edge: clear inside, black at the scope's rim.
                    MaskQuad(cx + c0 * soft, cy + s0 * soft, cx + c1 * soft, cy + s1 * soft, ScopeClear,
                             cx + c1 * radius, cy + s1 * radius, cx + c0 * radius, cy + s0 * radius, ScopeBlack);
                    // Black from the rim out past the screen.
                    MaskQuad(cx + c0 * radius, cy + s0 * radius, cx + c1 * radius, cy + s1 * radius, ScopeBlack,
                             cx + c1 * far, cy + s1 * far, cx + c0 * far, cy + s0 * far, ScopeBlack);
                }
                GL.End();
            }
            finally
            {
                GL.PopMatrix();
            }
        }

        // A quad of two triangles: the first two corners in one color, the last two in another.
        private static void MaskQuad(float x0, float y0, float x1, float y1, Color inner, float x2, float y2, float x3, float y3, Color outer)
        {
            GL.Color(inner); GL.Vertex3(x0, y0, 0f);
            GL.Color(inner); GL.Vertex3(x1, y1, 0f);
            GL.Color(outer); GL.Vertex3(x2, y2, 0f);
            GL.Color(inner); GL.Vertex3(x0, y0, 0f);
            GL.Color(outer); GL.Vertex3(x2, y2, 0f);
            GL.Color(outer); GL.Vertex3(x3, y3, 0f);
        }

        // Called by the plugin's OnGUI.
        internal static void OnGui()
        {
            if (!Active) return;
            Event current = Event.current;
            if (current == null || current.type != EventType.Repaint) return;
            // The game's pause menu or another menu or dialog open: nothing is drawn over it (the scope's black would hide it).
            if (PauseManager.Paused || MenuWantsPointer()) return;
            if (!WotStyle)
            {
                if (hudSightView && !hudFreeLook)
                {
                    DrawScopeMask(Screen.width, Screen.height);
                    // The magnification, under the right side of the crosshair.
                    float ms = Mathf.Max(Screen.height / 1080f, 0.6f);
                    Label(Screen.width * 0.5f + 70f * ms, Screen.height * 0.5f + 24f * ms, SightMagnifications[Mathf.Clamp(sightZoom, 0, SightMagnifications.Length - 1)].ToString("0") + "x", Mathf.RoundToInt(18f * ms), Ink, TextAnchor.MiddleCenter);
                }
                DrawRangeSight(Screen.height, ArmorFragmentsPlugin.TurretCrosshairFlipY.Value);
                DrawWarThunder(Screen.height, ArmorFragmentsPlugin.TurretCrosshairFlipY.Value);
                return;
            }
            if (!ReticleReady()) return;

            float width = Screen.width, height = Screen.height;
            bool flip = ArmorFragmentsPlugin.TurretCrosshairFlipY.Value;
            float s = Mathf.Max(height / 1080f, 0.6f);
            float ringX = hudRingAt.x, ringY = flip ? hudRingAt.y : height - hudRingAt.y;
            bool full = hudSightView;

            GL.PushMatrix();
            try
            {
                lineMaterial.SetPass(0);
                GL.LoadPixelMatrix(0f, width, height, 0f);   // pixels, the origin at the top left
                GL.Begin(GL.TRIANGLES);
                for (int pass = 0; pass < 2; pass++)
                {
                    edgePass = pass == 0;
                    if (hudRingShown && !hudFreeLook) DrawFixedSight(ringX, ringY, s, full);
                    foreach (GunMark mark in gunMarks)
                        DrawGunMark(mark, mark.At.x, flip ? mark.At.y : height - mark.At.y, s);
                }
                GL.End();
            }
            finally
            {
                GL.PopMatrix();
                edgePass = false;
            }

            // The texts.
            int small = Mathf.RoundToInt(15f * s), big = Mathf.RoundToInt(26f * s);
            foreach (GunMark mark in gunMarks)
                Label(mark.At.x, (flip ? mark.At.y : height - mark.At.y) + mark.Radius + 30f * s, mark.Range, small, Ink, TextAnchor.MiddleCenter);
            if (hudRingShown && !hudFreeLook)
            {
                float wing = full ? 195f : 90f;
                Label(ringX + wing * s, ringY + 24f * s, hudReload, full ? big : small, hudReloading ? Amber : LitGreen, TextAnchor.MiddleCenter);
                if (full) Label(ringX - wing * s, ringY + 24f * s, hudZoom, big, Ink, TextAnchor.MiddleCenter);
                // The range the gun is raised for: amber while locked, dim while it is held from an earlier reading.
                if (wotRanging && wotRange > 0f)
                    Label(ringX, ringY + (full ? 300f : 64f) * s, RangeText(wotRange) + (wotLocked ? "  LOCK" : ""), small, wotLocked ? Amber : wotHeld ? Muted : Ink, TextAnchor.MiddleCenter);
            }
        }
    }

    // In mouse aim the turret group fires its own cannons (TurretAim.FireGuns); the game's weapon systems fire no cannon of the vehicle.
    [HarmonyPatch(typeof(CannonWeaponSystem), "Fire")]
    internal static class TurretGameFirePatch
    {
        private static bool Prefix(CannonWeaponSystem __instance, ref WeaponPart __result)
        {
            if (!TurretAim.BlocksGameCannons(__instance)) return true;
            __result = null;
            return false;
        }
    }

    // The sight view turns the game's mouse look off, and its controller would then free the cursor every frame.
    [HarmonyPatch(typeof(InteractiveCameraController), "UpdateCursor")]
    internal static class TurretCursorPatch
    {
        private static bool Prefix(InteractiveCameraController __instance)
        {
            return !TurretAim.OwnsCursor(__instance);
        }
    }

    // While the aim is on, the game's keys that switch the camera view (C by default) and the right button's mouse-joystick toggle do nothing.
    [HarmonyPatch(typeof(GameInput), "GetButtonDownIfEnabled")]
    internal static class TurretViewKeyPatch
    {
        private static void Postfix(GameInput __instance, ref bool __result)
        {
            TurretAim.BlockViewKeys(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetButtonDown")]
    internal static class TurretViewKeyDownPatch
    {
        private static void Postfix(GameInput __instance, ref bool __result)
        {
            TurretAim.BlockViewKeys(__instance, ref __result);
        }
    }

    // The orbit camera turns around the aim's own pivot while the aim is on. The swap lasts one call of the camera's Update and is undone at its
    // end (also if it throws), so nothing stays changed in the game's camera when the aim is switched off or the plugin is reloaded.
    [HarmonyPatch(typeof(OrbitCameraController), "Update")]
    internal static class TurretPivotPatch
    {
        private static void Prefix(OrbitCameraController __instance, out TurretAim.PivotSwap __state)
        {
            __state = TurretAim.SwapPivotIn(__instance);
        }

        private static Exception Finalizer(Exception __exception, OrbitCameraController __instance, TurretAim.PivotSwap __state)
        {
            TurretAim.SwapPivotOut(__instance, __state);
            return __exception;
        }
    }

    // Each marked rotator takes the angle the aim works out, in the physics step that moves it.
    [HarmonyPatch(typeof(JointRotatorScript), "OnFixedUpdate")]
    internal static class TurretRotatorPatch
    {
        private static void Prefix(JointRotatorScript __instance)
        {
            TurretAim.OnRotatorFixedUpdate(__instance);
        }
    }
}

namespace ArmorFragments
{
    // World of Tanks style: the game's own wheel zoom of the aim's cameras is left out; the mod steps the zoom itself.
    [HarmonyPatch(typeof(Assets.Scripts.Flight.Cameras.InteractiveCameraController), "Zoom")]
    internal static class TurretZoomPatch
    {
        private static bool Prefix(Assets.Scripts.Flight.Cameras.InteractiveCameraController __instance)
        {
            return !TurretAim.BlocksGameZoom(__instance);
        }
    }
}
