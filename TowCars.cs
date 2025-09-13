using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Rust;
using Network;
using System.Globalization;

namespace Oxide.Plugins
{
    [Info("TowCars", "chatgpt", "1.1.0")]
    [Description("Use the Hammer's middle-click to link two modular cars with a tow strap")]
    public class TowCars : CovalencePlugin
    {
        private const string PERM_USE  = "towcars.use";
        private Timer assistTimer;
        private Timer ropeTimer;
        private Color ropeColor;


        #region Config
        private ConfigData config;

        private class ConfigData
        {
            // SFX on release/break
            public bool   PlaySoundOnRelease  = true;
            public string SoundPathOnRelease  = "assets/bundled/prefabs/fx/notice/item.deselect.fx.prefab";
            public bool   PlaySoundOnBreak    = true;
            public string SoundPathOnBreak    = "assets/bundled/prefabs/fx/impacts/metal/metal_sheet_impact.prefab";

            // SFX on successful link
            public bool  PlaySoundOnLink   = true;
            public string SoundPathOnLink  = "assets/bundled/prefabs/fx/notice/item.select.fx.prefab";
            public string SoundAudience    = "drivers"; // "drivers" or "nearby"
            public float SoundRange        = 25f;       // meters for "nearby"

            // Yaw-align assist
            public bool  AlignYawWhenTaut   = true;
            public float AlignYawKp         = 2.8f;
            public float AlignYawKd         = 0.7f;
            public float AlignYawMaxTorque  = 8000f;
            public float AlignDownforceN    = 4000f;
            public float AlignMinSpeed      = 0.5f;

            // Rope visuals / audience
            public int    RopeMaxViewers        = 6;
            public string RopeAudience          = "nearby";
            public bool   ShowRope              = true;
            public string RopeColor             = "0.35,0.22,0.10,1";
            public float  RopeDrawInterval      = 0.3f;
            public int    RopeSegments          = 3;
            public float  RopeSag               = 0.35f;
            public float  RopeVisibleDistance   = 30f;

            // Towed brake easing
            public bool  EaseTowedBrakes        = true;
            public float TowedBrakeTorque       = 30f;
            public bool  PersistBrakeEase       = true;

            // Assist (planar PD) – damped for short ropes
            public bool  AssistWhenTaut         = true;
            public float AssistThreshold        = 0.70f;
            public float AssistAccelB           = 3.0f;
            public float AssistBackforceA       = 0.8f;
            public float AssistMaxBVel          = 18f;
            public float AssistTick             = 0.03f;
            public float AssistKp               = 5.0f;  // was 6
            public float AssistKd               = 3.0f;  // was 2
            public float AssistMaxAccelB        = 14.0f;
            public float AssistMaxBackA         = 6.0f;

            // Auto-extend (short cap, smoother)
            public bool  AutoExtendWhenTaut     = true;
            public float MaxRopeLength          = 6.5f;   // tiny headroom over 6.0
            public float AutoExtendRate         = 1.0f;   // was 1.5
            public float ExtendAtFraction       = 0.90f;  // was 0.85

            // Rope length & damping
            public float RopeLengthMin          = 2.5f;
            public float RopeDamper             = 90f;    // was 60
            public float TowedDragDeltaB        = 0.05f;  // subtle whip reduction
            public float TowedAngularDragDeltaB = 0.05f;

            // Solver projection (more stable)
            public float ProjectionDistance     = 0.08f;  // was 0.05
            public float ProjectionAngle        = 3.0f;   // was 5.0

            // Tool & ray
            public string ToolShortname         = "hammer";
            public ulong  ToolSkinId            = 0;
            public string ToolDisplayName       = "Tow Hook";
            public float  RayDistance           = 12f;

            // Anchors
            public float FrontAnchorForward     = 1.8f;
            public float RearAnchorBack         = 1.8f;
            public float AnchorHeight           = 0.5f;

            // Legacy spring/drive (kept for completeness – joint uses limit+damper)
            public float Slack                  = 1.5f;
            public float Spring                 = 2500f;
            public float Damper                 = 120f;
            public float MaxDriveForce          = 20000f;

            // Break thresholds (soft break handled by limit/damper; keep high)
            public float BreakForce             = 3000000f;
            public float BreakTorque            = 3000000f;

            // Separation (now scaled in code; this is a floor)
            public float MaxSeparationDistance  = 18f;

            // Drag tweak on A
            public bool  ReduceDragOnA          = true;
            public float DragDeltaA             = 0.25f;

            // Housekeeping
            public float HealthTickSeconds      = 0.25f;
            public bool  DebugLog               = false;
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { config = Config.ReadObject<ConfigData>();
                // Write back to add any new fields
                SaveConfig();
                }
            catch { LoadDefaultConfig(); }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion

        #region State
        private class TowLink
        {
            public ModularCar A;
            public ModularCar B;
            public ConfigurableJoint Joint;
            public Rigidbody RbA;
            public Rigidbody RbB;
            public float OrigDragA;
            public float OrigDragB;
            public float OrigAngDragB;
            public float RopeLimit;   // final rope length used by the joint
            public List<WheelCollider> WheelsB;
            public List<float> OrigBrakeTorqueB;
            public List<float> OrigSidewaysStiff;

        }

        private class PlayerSel
        {
            public ModularCar CarA;
            public Vector3 AnchorLocalA;
            public float StartedAt;
        }

        private readonly Dictionary<NetworkableId, TowLink> byA = new Dictionary<NetworkableId, TowLink>();
        private readonly Dictionary<ulong, PlayerSel> selecting = new Dictionary<ulong, PlayerSel>();

        private Timer healthTimer;
        #endregion

        #region Lifecycle
        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
        }

        private void OnServerInitialized()
        {
            healthTimer = timer.Every(config.HealthTickSeconds, HealthSweep);
            assistTimer = timer.Every(Mathf.Max(0.02f, config.AssistTick), PhysicsAssistTick);
            ropeColor  = ParseColor(config.RopeColor, new Color(0.95f, 0.85f, 0.2f, 1f));
            ropeTimer  = timer.Every(Mathf.Max(0.03f, config.RopeDrawInterval), DrawRopesTick);

        }

        private void Unload()
        {
            healthTimer?.Destroy();
            assistTimer?.Destroy();
            ropeTimer?.Destroy();

            foreach (var link in new List<TowLink>(byA.Values))
                SafeRelease(link, notify: false, reason: "unload");

            byA.Clear();
            selecting.Clear();
        }
        #endregion

        #region Input (bind to Hammer middle-click)
        private void OnPlayerInput(BasePlayer bp, InputState input)
        {
            if (bp == null || input == null || !bp.IsConnected) return;
            if (!permission.UserHasPermission(bp.UserIDString, PERM_USE)) return;
            if (!IsHoldingTowTool(bp)) return;

            if (input.WasJustPressed(BUTTON.FIRE_THIRD))
            {
                HandleMiddleClick(bp);
                
            }

            if (input.WasJustPressed(BUTTON.RELOAD))
            {
                selecting.Remove(bp.userID);
                bp.ChatMessage("Tow selection cancelled.");
            }
        }

        private bool IsHoldingTowTool(BasePlayer bp)
        {
            var it = bp.GetActiveItem();
            if (it == null) return false;
            if (it.info?.shortname != config.ToolShortname) return false;
            if (config.ToolSkinId != 0 && it.skin != config.ToolSkinId) return false;
            return true;
        }

        private Rigidbody GetRigidbody(BaseEntity e)
        {
            if (e == null) return null;
            var go = e.gameObject;
            return go.GetComponent<Rigidbody>()
                ?? go.GetComponentInParent<Rigidbody>()
                ?? go.GetComponentInChildren<Rigidbody>();
        }

        private void HandleMiddleClick(BasePlayer bp)
        {
            var hitOpt = RaycastEyes(bp, config.RayDistance);
            if (!hitOpt.HasValue) { bp.ChatMessage("Aim at a modular car."); return; }

            var hit = hitOpt.Value;
            var ent = EntityFromHit(hit);
            var car = ToCar(ent);
            if (car == null) { bp.ChatMessage("That’s not a modular car."); return; }
            if (car.net == null) { bp.ChatMessage("Car network ID not available."); return; }

            if (byA.TryGetValue(car.net.ID, out var linkA))
            {
                    // ADD before SafeRelease so joint anchors exist to play SFX at mid-point
                if (config.PlaySoundOnRelease) PlaySfxAtLinkMid(linkA, config.SoundPathOnRelease);

                SafeRelease(linkA, notify: true, reason: "manual release");
                bp.ChatMessage("Tow released.");
                return;
            }
            foreach (var l in new List<TowLink>(byA.Values))
            {
                if (l?.B == car)
                {
                    // ADD before SafeRelease so joint anchors exist to play SFX at mid-point
                    if (config.PlaySoundOnRelease) PlaySfxAtLinkMid(l, config.SoundPathOnRelease);

                    SafeRelease(l, notify: true, reason: "manual release");
                    bp.ChatMessage("Tow released.");
                    return;
                }
            }

            var anchorWorld = ChooseAnchorWorld(car, hit.point);
            var anchorLocal = car.transform.InverseTransformPoint(anchorWorld);

            if (!selecting.TryGetValue(bp.userID, out var sel))
            {
                selecting[bp.userID] = new PlayerSel
                {
                    CarA = car,
                    AnchorLocalA = anchorLocal,
                    StartedAt = Time.realtimeSinceStartup
                };
                bp.ChatMessage("First hook set. Middle-click the second car to link.");
                return;
            }

            var carA = sel.CarA;
            var anchorLocalA2 = sel.AnchorLocalA;
            selecting.Remove(bp.userID);

            if (carA == null || carA.IsDestroyed) { bp.ChatMessage("First car is no longer valid."); return; }
            if (carA == car) { bp.ChatMessage("Pick a different car for the second hook."); return; }
            if (IsCarInAnyLink(carA)) { bp.ChatMessage("First car is already involved in a tow."); return; }
            if (IsCarInAnyLink(car))  { bp.ChatMessage("Second car is already involved in a tow."); return; }

            if (!TryAttach(carA, anchorLocalA2, car, anchorLocal, out var link, out var reason))
            {
                bp.ChatMessage($"Cannot link: {reason}");
                return;
            }

            byA[carA.net.ID] = link;
            bp.ChatMessage($"Tow secured. Rope length ~{link.RopeLimit:0.0} m. Middle-click a linked car to release.");
            InfoToDrivers(link, "Tow attached. Middle-click a linked car to release.");


                        // --- SFX: link success ---
            if (config.PlaySoundOnLink && !string.IsNullOrEmpty(config.SoundPathOnLink))
            {
                var mid = GetLinkMid(link);
                PlayFx(config.SoundPathOnLink, mid); // broadcast to all players
                if (config.DebugLog) Puts($"[Tow] Played link SFX at {mid} : {config.SoundPathOnLink}");
            }


        }
        #endregion

        #region Attach / Release / Health
        private bool TryAttach(ModularCar carA, Vector3 anchorLocalA, ModularCar carB, Vector3 anchorLocalB, out TowLink link, out string reason)
        {
            link = null; reason = null;

            var rbA = GetRigidbody(carA);
            var rbB = GetRigidbody(carB);
            if (rbA == null || rbB == null) { reason = "missing rigidbody"; return false; }

            var cj = carA.gameObject.AddComponent<ConfigurableJoint>();
            cj.connectedBody = rbB;
            cj.autoConfigureConnectedAnchor = false;
            cj.anchor = anchorLocalA;
            cj.connectedAnchor = anchorLocalB;

            // --- Compute rope length (never shorter than current distance + small buffer) ---
            var pA = carA.transform.TransformPoint(anchorLocalA);
            var pB = carB.transform.TransformPoint(anchorLocalB);
            var currentDist = Vector3.Distance(pA, pB);

            var desired = Mathf.Max(config.RopeLengthMin, config.Slack);  // treat Slack as rope length now
            var ropeLen = Mathf.Max(desired, currentDist + 0.25f);        // avoid instant snap on attach

            // --- Rope behavior: limited distance, NO drives pulling together ---
            cj.xMotion = ConfigurableJointMotion.Limited;
            cj.yMotion = ConfigurableJointMotion.Limited;
            cj.zMotion = ConfigurableJointMotion.Limited;

            cj.linearLimit = new SoftJointLimit { limit = ropeLen };
            // No spring (no bungee), just damping near the limit to prevent slap/oscillation
            cj.linearLimitSpring = new SoftJointLimitSpring { spring = 0f, damper = Mathf.Max(0f, config.RopeDamper) };

            var noDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
            cj.xDrive = noDrive; cj.yDrive = noDrive; cj.zDrive = noDrive;

            // Let the towed car freely rotate (collisions remain enabled)
            cj.angularXMotion = ConfigurableJointMotion.Free;
            cj.angularYMotion = ConfigurableJointMotion.Free;
            cj.angularZMotion = ConfigurableJointMotion.Free;

            // Subtle solver stabilization without “teleport yanks”
            cj.projectionMode = JointProjectionMode.PositionAndRotation;
            cj.projectionDistance = Mathf.Max(0.01f, config.ProjectionDistance);
            cj.projectionAngle    = Mathf.Max(1f,    config.ProjectionAngle);

            // Softer break to avoid explosive energy
            cj.breakForce  = Mathf.Max(1f, config.BreakForce);
            cj.breakTorque = Mathf.Max(1f, config.BreakTorque);

            // keep car-to-car collisions while linked
            cj.enableCollision = true;


            var newLink = new TowLink
            {
                A = carA,
                B = carB,
                Joint = cj,
                RbA = rbA,
                RbB = rbB,
                OrigDragA = rbA.drag,
                OrigDragB = rbB.drag,
                OrigAngDragB = rbB.angularDrag,
                RopeLimit = ropeLen
            };

            // Mildly slow both vehicles to reduce whipping while still allowing bumps
            if (config.ReduceDragOnA) rbA.drag = newLink.OrigDragA + Mathf.Max(0f, config.DragDeltaA);
            if (config.TowedDragDeltaB > 0f)      rbB.drag        = newLink.OrigDragB     + config.TowedDragDeltaB;
            if (config.TowedAngularDragDeltaB > 0f) rbB.angularDrag = newLink.OrigAngDragB + config.TowedAngularDragDeltaB;

            // Make the towed car roll more freely
            // Collect wheels once (used by both features)
            var wheels = newLink.B.GetComponentsInChildren<WheelCollider>(true);
            newLink.WheelsB = new List<WheelCollider>(wheels);

            // (optional) ease brakes
            if (config.EaseTowedBrakes)
            {
                newLink.OrigBrakeTorqueB = new List<float>(wheels.Length);
                var target = Mathf.Max(0f, config.TowedBrakeTorque);
                foreach (var w in wheels)
                {
                    newLink.OrigBrakeTorqueB.Add(w.brakeTorque);
                    if (w != null && w.enabled)
                        w.brakeTorque = Mathf.Min(w.brakeTorque, target);
                }
            }

            // (independent) bump sideways grip
            newLink.OrigSidewaysStiff = new List<float>(wheels.Length);
            foreach (var w in wheels)
            {
                if (w == null) { newLink.OrigSidewaysStiff.Add(0f); continue; }
                var sf = w.sidewaysFriction;
                newLink.OrigSidewaysStiff.Add(sf.stiffness);
                sf.stiffness = Mathf.Min(2.0f, sf.stiffness + 0.15f); // subtle extra lateral grip
                w.sidewaysFriction = sf;
            }

            // wake up to apply physics changes immediately
            newLink.RbB.WakeUp();


            link = newLink;
            if (config.DebugLog) Puts($"[Tow] Rope attach A:{carA.net.ID} -> B:{carB.net.ID}, rope={ropeLen:0.00}m (current={currentDist:0.00}m)");
            return true;
        }


        private void SafeRelease(TowLink link, bool notify, string reason)
        {
            if (link == null) return;

            if (link.Joint != null)
                UnityEngine.Object.Destroy(link.Joint);

            if (config.ReduceDragOnA && link.RbA != null)
                link.RbA.drag = link.OrigDragA;

                // Restore B's wheel brakes
            if (link.WheelsB != null && link.OrigBrakeTorqueB != null && link.WheelsB.Count == link.OrigBrakeTorqueB.Count)
            {
                for (int i = 0; i < link.WheelsB.Count; i++)
                {
                    var w = link.WheelsB[i];
                    if (w != null && w.enabled) w.brakeTorque = link.OrigBrakeTorqueB[i];
                }
            }

            if (link.RbB != null)
            {
                link.RbB.drag = link.OrigDragB;
                link.RbB.angularDrag = link.OrigAngDragB;
            }

            if (link.A != null && link.A.net != null)
                byA.Remove(link.A.net.ID);

            if (notify) InfoToDrivers(link, $"Tow released ({reason}).");
            if (config.DebugLog) Puts($"[Tow] Released A:{link.A?.net?.ID} ({reason})");

            // Restore B's wheel sideways stiffness
            if (link.WheelsB != null && link.OrigSidewaysStiff != null && link.WheelsB.Count == link.OrigSidewaysStiff.Count)
            {
                for (int i = 0; i < link.WheelsB.Count; i++)
                {
                    var w = link.WheelsB[i];
                    if (w == null) continue;
                    var sf = w.sidewaysFriction;
                    sf.stiffness = link.OrigSidewaysStiff[i];
                    w.sidewaysFriction = sf;
                }
            }

        }

        private void HealthSweep()
        {
            if (byA.Count == 0) return;

            var toRelease = new List<TowLink>();
            foreach (var link in byA.Values)
            {
                if (link == null) { toRelease.Add(link); continue; }

                // entity validity
                if (link.A == null || link.B == null || link.A.IsDestroyed || link.B.IsDestroyed)
                {
                    toRelease.Add(link);
                    continue;
                }

                // joint validity
                if (link.Joint == null)
                {
                    // ADD before SafeRelease so joint anchors exist to play SFX at mid-point
                    if (config.PlaySoundOnBreak) PlaySfxAtLinkMid(link, config.SoundPathOnBreak);

                    // extra info in case joint broke
                    InfoToDrivers(link, "Tow strap broke.");
                    toRelease.Add(link);
                    continue;
                }

                // Keep towed brakes eased while linked (optional but recommended)
                if (config.PersistBrakeEase && link.WheelsB != null)
                {
                    var target = Mathf.Max(0f, config.TowedBrakeTorque);
                    foreach (var w in link.WheelsB)
                        if (w != null && w.enabled && w.brakeTorque > target)
                            w.brakeTorque = target;
                }

                // --- anchor-to-anchor distance (more accurate than car pivots) ---
                var pA = link.A.transform.TransformPoint(link.Joint.anchor);
                var pB = link.B.transform.TransformPoint(link.Joint.connectedAnchor);
                var dist = Vector3.Distance(pA, pB);

                // --- auto-extend when rope is nearly taut, up to a cap (optional) ---
                if (config.AutoExtendWhenTaut && link.RopeLimit < config.MaxRopeLength)
                {
                    var thresh = link.RopeLimit * Mathf.Clamp01(config.ExtendAtFraction);
                    if (dist > thresh)
                    {
                        var delta  = config.AutoExtendRate * config.HealthTickSeconds;
                        var newLen = Mathf.Min(config.MaxRopeLength, link.RopeLimit + delta);

                        var lim = link.Joint.linearLimit;
                        lim.limit = newLen;
                        link.Joint.linearLimit = lim;

                        link.RopeLimit = newLen;
                    }
                }

                // --- auto-release only if well beyond the current rope limit ---
                float ropeLimit = link.RopeLimit > 0f ? link.RopeLimit : link.Joint.linearLimit.limit;
                float buffer = Mathf.Max(1.5f, ropeLimit * 0.20f); // at least 1.5m or 20% of rope
                float maxSep = Mathf.Max(config.MaxSeparationDistance, ropeLimit + buffer);
                if (dist > maxSep)
                {
                    // ADD before SafeRelease so joint anchors exist to play SFX at mid-point
                    if (config.PlaySoundOnRelease) PlaySfxAtLinkMid(link, config.SoundPathOnRelease);
                    
                    // extra info in case MaxSeparationDistance is large
                    InfoToDrivers(link, "Tow auto-released (too far apart).");
                    toRelease.Add(link);
                    continue;
                }
            }

            foreach (var l in toRelease)
                if (l != null) SafeRelease(l, notify: false, reason: "cleanup");
        }
        #endregion

        #region Rope Visuals
        // helper: draw up to (budget) segments to one player, then stop
        private void DrawToPlayer(BasePlayer ply, List<Vector3> pts, float dur, ref int budget)
        {
            if (ply == null || !ply.IsConnected) return;
            for (int i = 0; i < pts.Count - 1 && budget > 0; i++)
            {
                ply.SendConsoleCommand("ddraw.line", dur, ropeColor, pts[i], pts[i + 1]);
                budget--;
            }
        }

        private void DrawRopesTick()
        {
            if (!config.ShowRope || byA.Count == 0) return;

            float visSqr = config.RopeVisibleDistance * config.RopeVisibleDistance;
            float dur    = Mathf.Max(config.RopeDrawInterval * 1.5f, 0.15f);
            int   segs   = Mathf.Clamp(config.RopeSegments, 2, 4);

            // global per-tick safety cap
            int budget = Mathf.Max(24, config.RopeMaxViewers * segs * 2);

            foreach (var link in byA.Values)
            {
                if (budget <= 0) break;
                if (link?.Joint == null || link.A == null || link.B == null) continue;
                if (link.A.IsDestroyed || link.B.IsDestroyed) continue;

                var a = link.A.transform.TransformPoint(link.Joint.anchor);
                var b = link.B.transform.TransformPoint(link.Joint.connectedAnchor);
                var center = (a + b) * 0.5f;

                var pts = MakeSagCurve(a, b, segs, Mathf.Max(0f, config.RopeSag));

                if (string.Equals(config.RopeAudience, "drivers", System.StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var ply in Occupants(link.A))
                    {
                        DrawToPlayer(ply, pts, dur, ref budget);
                        if (budget <= 0) break;
                    }
                    if (budget > 0)
                    {
                        foreach (var ply in Occupants(link.B))
                        {
                            DrawToPlayer(ply, pts, dur, ref budget);
                            if (budget <= 0) break;
                        }
                    }
                }
                else
                {
                    var viewers = BasePlayer.activePlayerList;
                    if (viewers == null || viewers.Count == 0) continue;

                    var nearby = new List<BasePlayer>(viewers.Count);
                    foreach (var ply in viewers)
                    {
                        if (ply == null || !ply.IsConnected) continue;
                        if ((ply.transform.position - center).sqrMagnitude > visSqr) continue;
                        nearby.Add(ply);
                    }
                    if (nearby.Count == 0) continue;

                    nearby.Sort((x, y) =>
                    {
                        var dx = (x.transform.position - center).sqrMagnitude;
                        var dy = (y.transform.position - center).sqrMagnitude;
                        return dx.CompareTo(dy);
                    });

                    int limit = Mathf.Max(1, config.RopeMaxViewers);
                    int count = Mathf.Min(limit, nearby.Count);
                    for (int vi = 0; vi < count && budget > 0; vi++)
                        DrawToPlayer(nearby[vi], pts, dur, ref budget);
                }
            }
        }
        #endregion

        #region Physics Assist
        private void PhysicsAssistTick()
        {
            if (!config.AssistWhenTaut || byA.Count == 0) return;

            foreach (var link in byA.Values)
            {
                if (link?.Joint == null || link.A == null || link.B == null) continue;
                if (link.A.IsDestroyed || link.B.IsDestroyed) continue;

                // Anchor positions and separation
                var pA = link.A.transform.TransformPoint(link.Joint.anchor);
                var pB = link.B.transform.TransformPoint(link.Joint.connectedAnchor);
                var delta = pA - pB;
                var dist = delta.magnitude;
                if (dist <= 0.001f) continue;

                // Rope limit in effect
                var limit = link.RopeLimit > 0f ? link.RopeLimit : link.Joint.linearLimit.limit;
                if (limit <= 0f) continue;

                var frac = dist / limit;
                if (frac < config.AssistThreshold) continue; // not taut enough

                // --- Along-rope assist (PLANAR to avoid nose-up launches) ---
                var up = Vector3.up;
                var dirPlanar = Vector3.ProjectOnPlane(delta, up);
                if (dirPlanar.sqrMagnitude < 1e-6f) continue; // rope is basically vertical; skip
                var dirP = dirPlanar.normalized;

                // PD along the rope (planar)
                var relVelAlong = Vector3.Dot(link.RbA.velocity - link.RbB.velocity, dirP);
                var stretch = Mathf.Max(0f, dist - limit);
                var aDes = config.AssistKp * stretch + config.AssistKd * relVelAlong;

                if (aDes > 0f)
                {
                    var bAlong = Vector3.Dot(link.RbB.velocity, dirP);
                    if (bAlong < config.AssistMaxBVel)
                    {
                        var aB = Mathf.Min(aDes, config.AssistMaxAccelB);
                        link.RbB.AddForce(dirP * aB, ForceMode.Acceleration);
                    }

                    var aA = Mathf.Min(aDes * 0.5f, config.AssistMaxBackA); // half on A by default
                    if (aA > 0f) link.RbA.AddForce(-dirP * aA, ForceMode.Acceleration);
                }

                // --- Yaw-align & front-axle downforce (also gated by tautness + min speed) ---
                if (config.AlignYawWhenTaut)
                {
                    float minSpeed2 = config.AlignMinSpeed * config.AlignMinSpeed;
                    if (link.RbA.velocity.sqrMagnitude + link.RbB.velocity.sqrMagnitude > minSpeed2)
                    {
                        var dirPlanarN = dirP; // already normalized planar rope direction
                        var fwdB = Vector3.ProjectOnPlane(link.B.transform.forward, up).normalized;

                        // Signed yaw error (left/right via cross.y)
                        float sinAng = Mathf.Clamp(Vector3.Cross(fwdB, dirPlanarN).y, -1f, 1f);
                        float cosAng = Mathf.Clamp(Vector3.Dot(fwdB, dirPlanarN), -1f, 1f);
                        float angErr = Mathf.Atan2(sinAng, cosAng); // radians

                        float yawRate = link.RbB.angularVelocity.y;

                        float torqueY = config.AlignYawKp * angErr - config.AlignYawKd * yawRate;
                        torqueY = Mathf.Clamp(torqueY, -config.AlignYawMaxTorque, config.AlignYawMaxTorque);
                        link.RbB.AddTorque(up * torqueY, ForceMode.Acceleration);

                        // Correct DOWNWARD force at the hook to give the front tires bite
                        var anchorWorldB = link.B.transform.TransformPoint(link.Joint.connectedAnchor);
                        var down = Physics.gravity.normalized; // points downward
                        link.RbB.AddForceAtPosition(
                            down * Mathf.Max(0f, config.AlignDownforceN),
                            anchorWorldB,
                            ForceMode.Force
                        );
                    }
                }

                // Ensure bodies stay active after impulses
                link.RbA.WakeUp();
                link.RbB.WakeUp();
            }
        }
        #endregion

        #region Hooks
        private void OnEntityKill(BaseNetworkable ent)
        {
            var be = ent as BaseEntity;
            if (be == null) return;

            if (be.net != null && byA.TryGetValue(be.net.ID, out var link))
            {
                SafeRelease(link, notify: false, reason: "entity kill A");
                return;
            }

            foreach (var l in new List<TowLink>(byA.Values))
            {
                if (l?.B == be)
                {
                    SafeRelease(l, notify: false, reason: "entity kill B");
                    break;
                }
            }
        }

        private void OnJointBreak(float force, Joint joint)
        {
            foreach (var l in new List<TowLink>(byA.Values))
            {
                if (l?.Joint == joint)
                {
                    // ADD before SafeRelease so joint anchors exist to play SFX at mid-point
                    if (config.PlaySoundOnBreak) PlaySfxAtLinkMid(l, config.SoundPathOnBreak);

                    InfoToDrivers(l, "Tow strap broke.");
                    SafeRelease(l, notify: false, reason: "joint break");
                    break;
                }
            }
        }
        #endregion

        #region Helpers

        private Vector3 GetLinkMid(TowLink link)
        {
            if (link == null) return Vector3.zero;

            if (link.Joint != null)
            {
                var pA = link.A.transform.TransformPoint(link.Joint.anchor);
                var pB = link.B.transform.TransformPoint(link.Joint.connectedAnchor);
                return (pA + pB) * 0.5f;
            }

            // Fallback if joint is already gone
            return (link.A.transform.position + link.B.transform.position) * 0.5f;
        }

        private void PlayFx(string path, Vector3 pos, BasePlayer to = null)
        {
            if (string.IsNullOrEmpty(path)) return;

            if (to != null)
            {
                // Play only for a single player
                Effect.server.Run(path, pos, Vector3.up, to.net.connection);
            }
            else
            {
                // Broadcast as a world sound (like building or gunshots)
                Effect.server.Run(path, pos, Vector3.up);
            }
        }


        private void PlaySfxAtLinkMid(TowLink link, string path)
        {
            if (link == null || string.IsNullOrEmpty(path)) return;

            var mid = GetLinkMid(link);
            PlayFx(path, mid); // broadcast world sound
        }


        private IEnumerable<BasePlayer> Occupants(ModularCar car)
        {
            if (car == null) yield break;
            foreach (var child in car.children)
            {
                var m = child as BaseMountable;
                var ply = m?.GetMounted();
                if (ply != null && ply.IsConnected) yield return ply;
            }
        }

        private Color ParseColor(string s, Color fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return fallback;
                var p = s.Split(',');
                if (p.Length < 3) return fallback;
                float r = float.Parse(p[0].Trim(), CultureInfo.InvariantCulture);
                float g = float.Parse(p[1].Trim(), CultureInfo.InvariantCulture);
                float b = float.Parse(p[2].Trim(), CultureInfo.InvariantCulture);
                float a = (p.Length >= 4) ? float.Parse(p[3].Trim(), CultureInfo.InvariantCulture) : 1f;
                return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), Mathf.Clamp01(a));
            }
            catch { return fallback; }
        }

        private List<Vector3> MakeSagCurve(Vector3 a, Vector3 b, int segments, float sagMeters)
        {
            // Quadratic Bezier from A to B with a sagging middle control point
            segments = Mathf.Max(1, segments);
            var pts = new List<Vector3>(segments + 1);
            var mid = (a + b) * 0.5f; mid.y -= Mathf.Max(0f, sagMeters);

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float u = 1f - t;
                // p = u^2 * A + 2u t * Mid + t^2 * B
                var p = (u * u) * a + (2f * u * t) * mid + (t * t) * b;
                pts.Add(p);
            }
            return pts;
        }

        private RaycastHit? RaycastEyes(BasePlayer bp, float dist)
        {
            var eyes = bp.eyes.position;
            var dir  = bp.eyes.BodyForward();
            if (Physics.Raycast(eyes, dir, out var hit, dist, Physics.AllLayers, QueryTriggerInteraction.Ignore))
                return hit;
            return null;
        }

        private BaseEntity EntityFromHit(RaycastHit hit)
        {
            var ent = hit.GetEntity();
            if (ent != null) return ent;
            ent = hit.collider?.ToBaseEntity();
            if (ent != null) return ent;
            return hit.collider?.GetComponentInParent<BaseEntity>();
        }

        private ModularCar ToCar(BaseEntity ent)
        {
            if (ent == null) return null;

            var car = ent as ModularCar ?? ent.GetComponentInParent<ModularCar>();
            if (car != null) return car;

            var be = ent;
            for (int i = 0; i < 8 && be != null; i++)
            {
                car = be as ModularCar ?? be.GetComponentInParent<ModularCar>();
                if (car != null) return car;
                be = be.GetParentEntity() as BaseEntity;
            }
            return null;
        }

        private Vector3 ChooseAnchorWorld(ModularCar car, Vector3 hitPoint)
        {
            var front = car.transform.position + car.transform.forward * config.FrontAnchorForward + Vector3.up * config.AnchorHeight;
            var rear  = car.transform.position - car.transform.forward * config.RearAnchorBack     + Vector3.up * config.AnchorHeight;

            var dF = (hitPoint - front).sqrMagnitude;
            var dR = (hitPoint - rear).sqrMagnitude;
            return dF <= dR ? front : rear;
        }

        private bool IsCarInAnyLink(ModularCar car)
        {
            if (car == null || car.net == null) return false;
            if (byA.ContainsKey(car.net.ID)) return true;
            foreach (var l in byA.Values) if (l?.B == car) return true;
            return false;
        }

        private void InfoToDrivers(TowLink link, string msg)
        {
            SendToOccupants(link.A, msg);
            SendToOccupants(link.B, msg);
        }

        private void SendToOccupants(ModularCar car, string msg)
        {
            if (car == null) return;
            foreach (var child in car.children)
            {
                var m = child as BaseMountable;
                var rider = m?.GetMounted();
                if (rider != null) rider.ChatMessage(msg);
            }
        }
        #endregion
    }
}
