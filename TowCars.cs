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
    [Info("TowCars", "S0F1st1Kt3dB3ar", "2.0.0")]
    [Description("Simplified rope-based towing system with manual tow mode activation")]
    public class TowCars : CovalencePlugin
    {
        private const string PERM_USE = "towcars.use";
        private Timer ropeDrawTimer;
        private Color ropeColor;


        #region Config
        private ConfigData config;

        private class ConfigData
        {
            // Tool settings
            public string ToolShortname = "hammer";
            public ulong ToolSkinId = 0;
            public float RayDistance = 12f;

            // Rope physics
            public float RopeLengthMin = 3.5f;
            public float RopeLengthMax = 5.0f;
            public float RopeBreakForce = 5000000f;
            public float RopeSpringForce = 50000f;
            public float RopeDamper = 2000f;

            // Towed car wheel control (free-roll)
            public bool FreeRollTowedWheels = true;
            public float TowedBrakeTorque = 0f;
            public float TowedForwardStiffness = 0.01f;
            public float TowedSidewaysStiffness = 0.1f;

            // Yaw alignment (helps orient towed car)
            public bool AlignTowedCarYaw = true;
            public float AlignYawStrength = 5000f;
            public float AlignYawDamping = 1000f;

            // Winch force (keeps towed car close)
            public bool EnableWinchForce = true;
            public float WinchForceStrength = 15000f;
            public float WinchActivationDistance = 0.5f; // Start pulling when rope is 50% stretched

            // Anchor points
            public float FrontAnchorForward = 1.8f;
            public float RearAnchorBack = 1.8f;
            public float AnchorHeight = 0.5f;

            // Rope visuals - client-side drawing
            public bool ShowRope = true;
            public string RopeColor = "0.6,0.4,0.2,1";  // Brown/tan rope color
            public float RopeWidth = 0.08f;  // Rope thickness
            public float RopeDrawInterval = 0.4f;  // Draw every 0.4 seconds (balance between smooth updates and network load)
            public float RopeVisibleDistance = 150f;  // Max distance players can see rope

            // Sounds (disabled by default - invalid effect paths)
            public bool PlaySoundOnLink = false;
            public string SoundPathOnLink = "";
            public bool PlaySoundOnRelease = false;
            public string SoundPathOnRelease = "";
            public bool PlaySoundOnBreak = false;
            public string SoundPathOnBreak = "";

            // Separation
            public float MaxSeparationMultiplier = 2.5f;

            // Update intervals
            public float HealthCheckInterval = 0.5f;

            public bool DebugLog = false;
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                SaveConfig();
            }
            catch
            {
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion

        #region State
        private class TowLink
        {
            public ModularCar TowingCar;
            public ModularCar TowedCar;
            public NetworkableId TowingId;
            public NetworkableId TowedId;
            public Joint RopeJoint;
            public float RopeLength;
            public float CreatedAt;
            public List<WheelCollider> TowedWheels;
            public List<float> OrigBrakeTorques;
            public List<float> OrigForwardStiffness;
            public List<float> OrigSidewaysStiffness;
            public float OrigDrag;
            public float OrigAngularDrag;
        }

        private class PlayerSelection
        {
            public ModularCar FirstCar;
            public Vector3 FirstAnchor;
            public float SelectionTime;
        }

        private readonly Dictionary<NetworkableId, TowLink> activeLinks = new Dictionary<NetworkableId, TowLink>();
        private readonly Dictionary<ulong, PlayerSelection> playerSelections = new Dictionary<ulong, PlayerSelection>();
        private Timer healthTimer;
        private Timer alignTimer;
        #endregion

        #region Lifecycle
        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
        }

        private void OnServerInitialized()
        {
            healthTimer = timer.Every(config.HealthCheckInterval, CheckAllLinks);
            ropeColor = ParseColor(config.RopeColor, new Color(0.6f, 0.4f, 0.2f, 1f));
            
            if (config.ShowRope)
            {
                ropeDrawTimer = timer.Every(config.RopeDrawInterval, DrawAllRopes);
            }

            if (config.AlignTowedCarYaw)
            {
                alignTimer = timer.Every(0.1f, AlignTowedCars);
            }
        }

        private void Unload()
        {
            healthTimer?.Destroy();
            ropeDrawTimer?.Destroy();
            alignTimer?.Destroy();

            foreach (var link in new List<TowLink>(activeLinks.Values))
            {
                ReleaseLink(link, false, "plugin unload");
            }

            activeLinks.Clear();
            playerSelections.Clear();
        }
        #endregion

        #region Input Handling
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || !player.IsConnected) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;
            if (!IsHoldingTowTool(player)) return;

            if (input.WasJustPressed(BUTTON.FIRE_THIRD))
            {
                HandleTowToolClick(player);
            }

            if (input.WasJustPressed(BUTTON.RELOAD))
            {
                if (playerSelections.Remove(player.userID))
                {
                    player.ChatMessage("Tow selection cancelled.");
                }
            }
        }

        private bool IsHoldingTowTool(BasePlayer player)
        {
            var item = player.GetActiveItem();
            if (item == null) return false;
            if (item.info?.shortname != config.ToolShortname) return false;
            if (config.ToolSkinId != 0 && item.skin != config.ToolSkinId) return false;
            return true;
        }

        private void HandleTowToolClick(BasePlayer player)
        {
            var hit = RaycastFromEyes(player, config.RayDistance);
            if (!hit.HasValue)
            {
                player.ChatMessage("Aim at a modular car.");
                return;
            }

            var car = GetCarFromHit(hit.Value);
            if (car == null)
            {
                player.ChatMessage("That's not a modular car.");
                return;
            }

            if (car.net == null)
            {
                player.ChatMessage("Car network unavailable.");
                return;
            }

            // Check if clicking an already linked car to release it
            if (activeLinks.TryGetValue(car.net.ID, out var existingLink))
            {
                if (config.PlaySoundOnRelease)
                {
                    PlaySoundAtMidpoint(existingLink, config.SoundPathOnRelease);
                }
                ReleaseLink(existingLink, true, "manual release");
                player.ChatMessage("Tow released.");
                return;
            }

            // First selection
            if (!playerSelections.TryGetValue(player.userID, out var selection))
            {
                var anchorWorld = ChooseAnchorPoint(car, hit.Value.point);
                var anchorLocal = car.transform.InverseTransformPoint(anchorWorld);

                playerSelections[player.userID] = new PlayerSelection
                {
                    FirstCar = car,
                    FirstAnchor = anchorLocal,
                    SelectionTime = Time.realtimeSinceStartup
                };

                player.ChatMessage("First car selected. Click the second car to create tow link.");
                return;
            }

            // Second selection - create link
            var firstCar = selection.FirstCar;
            var firstAnchorLocal = selection.FirstAnchor;
            playerSelections.Remove(player.userID);

            if (firstCar == null || firstCar.IsDestroyed)
            {
                player.ChatMessage("First car is no longer valid.");
                return;
            }

            if (firstCar == car)
            {
                player.ChatMessage("Cannot tow a car to itself.");
                return;
            }

            if (IsCarLinked(firstCar) || IsCarLinked(car))
            {
                player.ChatMessage("One of the cars is already in a tow link.");
                return;
            }

            // IMPORTANT: Towed car (second car) MUST use front anchor, first car uses rear
            // This ensures proper towing orientation
            
            // Get FRONT anchor for TOWED car (second/car) - using transform-relative position
            var towedAnchorLocal = new Vector3(0f, config.AnchorHeight, config.FrontAnchorForward);
            
            // Get REAR anchor for TOWING car (first car) - using transform-relative position
            var firstAnchorLocalRear = new Vector3(0f, config.AnchorHeight, -config.RearAnchorBack);
            
            if (config.DebugLog)
            {
                player.ChatMessage($"Towing (first) car - using REAR anchor (local): {firstAnchorLocalRear}");
                player.ChatMessage($"Towed (second) car - using FRONT anchor (local): {towedAnchorLocal}");
            }

            if (CreateTowLink(firstCar, firstAnchorLocalRear, car, towedAnchorLocal, out var newLink, out var errorMsg))
            {
                player.ChatMessage($"Tow link created. Rope length: {newLink.RopeLength:F1}m");
                NotifyOccupants(newLink.TowingCar, "Tow attached. You are towing.");
                NotifyOccupants(newLink.TowedCar, "Tow attached. You are being towed.");

                if (config.PlaySoundOnLink)
                {
                    PlaySoundAtMidpoint(newLink, config.SoundPathOnLink);
                }
            }
            else
            {
                player.ChatMessage($"Failed to create tow: {errorMsg}");
            }
        }
        #endregion

        #region Tow Link Management
        private bool CreateTowLink(ModularCar towingCar, Vector3 towingAnchor, ModularCar towedCar, Vector3 towedAnchor, out TowLink link, out string error)
        {
            link = null;
            error = null;

            var rbTowing = GetRigidbody(towingCar);
            var rbTowed = GetRigidbody(towedCar);

            if (rbTowing == null || rbTowed == null)
            {
                error = "Missing rigidbody";
                return false;
            }

            // Calculate rope length from current distance
            var worldPosTowing = towingCar.transform.TransformPoint(towingAnchor);
            var worldPosTowed = towedCar.transform.TransformPoint(towedAnchor);
            var currentDistance = Vector3.Distance(worldPosTowing, worldPosTowed);

            // Always use minimum rope length or current distance + buffer, whichever is larger
            // This ensures enough slack for proper towing
            var ropeLength = Mathf.Max(config.RopeLengthMin, currentDistance + 0.5f);
            ropeLength = Mathf.Min(ropeLength, config.RopeLengthMax);

            // Create configurable joint (acts like rope with max distance)
            var joint = towingCar.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = rbTowed;
            joint.anchor = towingAnchor;
            joint.connectedAnchor = towedAnchor;
            joint.autoConfigureConnectedAnchor = false;

            // Lock all motion within rope length limit
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Limited;
            joint.zMotion = ConfigurableJointMotion.Limited;

            // Set rope length as limit with higher constraint force
            joint.linearLimit = new SoftJointLimit { limit = ropeLength, bounciness = 0f };
            
            // Strong spring to pull towed car - much higher force
            joint.linearLimitSpring = new SoftJointLimitSpring 
            { 
                spring = config.RopeSpringForce,
                damper = config.RopeDamper 
            };

            // Free rotation
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;

            // No drives - let physics handle it
            var noDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
            joint.xDrive = noDrive;
            joint.yDrive = noDrive;
            joint.zDrive = noDrive;

            // Enable projection to enforce constraint aggressively
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = 0.05f;  // Tighter
            joint.projectionAngle = 2f;

            joint.breakForce = config.RopeBreakForce;
            joint.breakTorque = config.RopeBreakForce;
            joint.enableCollision = true;
            joint.enablePreprocessing = false;

            // Increase mass scale for better constraint solving
            joint.massScale = 1.0f;
            joint.connectedMassScale = 1.0f;

            // Wake up both rigidbodies
            rbTowing.WakeUp();
            rbTowed.WakeUp();

            // Reduce rigidbody drag to make unmanned car easier to move
            float origDrag = rbTowed.drag;
            float origAngularDrag = rbTowed.angularDrag;
            rbTowed.drag = 0.05f;  // Very low drag
            rbTowed.angularDrag = 0.05f;  // Very low angular drag

            // Free-roll the towed car's wheels
            var wheels = towedCar.GetComponentsInChildren<WheelCollider>();
            var towedWheels = new List<WheelCollider>();
            var origBrakes = new List<float>();
            var origForward = new List<float>();
            var origSideways = new List<float>();

            foreach (var wheel in wheels)
            {
                if (wheel == null) continue;
                towedWheels.Add(wheel);
                origBrakes.Add(wheel.brakeTorque);
                origForward.Add(wheel.forwardFriction.stiffness);
                origSideways.Add(wheel.sidewaysFriction.stiffness);

                if (config.FreeRollTowedWheels)
                {
                    // Release brakes completely
                    wheel.brakeTorque = config.TowedBrakeTorque;
                    wheel.motorTorque = 0f;

                    // Reduce friction so wheels roll freely
                    var ff = wheel.forwardFriction;
                    ff.stiffness = config.TowedForwardStiffness;
                    wheel.forwardFriction = ff;

                    var sf = wheel.sidewaysFriction;
                    sf.stiffness = config.TowedSidewaysStiffness;
                    wheel.sidewaysFriction = sf;
                }
            }

            // Create link record
            link = new TowLink
            {
                TowingCar = towingCar,
                TowedCar = towedCar,
                TowingId = towingCar.net.ID,
                TowedId = towedCar.net.ID,
                RopeJoint = joint,
                RopeLength = ropeLength,
                CreatedAt = Time.realtimeSinceStartup,
                TowedWheels = towedWheels,
                OrigBrakeTorques = origBrakes,
                OrigForwardStiffness = origForward,
                OrigSidewaysStiffness = origSideways,
                OrigDrag = origDrag,
                OrigAngularDrag = origAngularDrag
            };
            
            // Store in both lookups
            activeLinks[towingCar.net.ID] = link;
            activeLinks[towedCar.net.ID] = link;

            if (config.DebugLog)
            {
                var worldPosA = towingCar.transform.TransformPoint(towingAnchor);
                var worldPosB = towedCar.transform.TransformPoint(towedAnchor);
                Puts($"[Tow] Link created: TowingAnchor={worldPosA}, TowedAnchor={worldPosB}, TowedAnchorLocal={towedAnchor}");
            }

            return true;
        }

        private void ReleaseLink(TowLink link, bool notify, string reason)
        {
            if (link == null) return;

            // Restore rigidbody drag
            if (link.TowedCar != null && !link.TowedCar.IsDestroyed)
            {
                var rb = GetRigidbody(link.TowedCar);
                if (rb != null)
                {
                    rb.drag = link.OrigDrag;
                    rb.angularDrag = link.OrigAngularDrag;
                }
            }

            // Restore wheel settings
            if (link.TowedWheels != null && link.TowedWheels.Count > 0)
            {
                for (int i = 0; i < link.TowedWheels.Count; i++)
                {
                    var wheel = link.TowedWheels[i];
                    if (wheel == null) continue;

                    if (link.OrigBrakeTorques != null && i < link.OrigBrakeTorques.Count)
                        wheel.brakeTorque = link.OrigBrakeTorques[i];

                    if (link.OrigForwardStiffness != null && i < link.OrigForwardStiffness.Count)
                    {
                        var ff = wheel.forwardFriction;
                        ff.stiffness = link.OrigForwardStiffness[i];
                        wheel.forwardFriction = ff;
                    }

                    if (link.OrigSidewaysStiffness != null && i < link.OrigSidewaysStiffness.Count)
                    {
                        var sf = wheel.sidewaysFriction;
                        sf.stiffness = link.OrigSidewaysStiffness[i];
                        wheel.sidewaysFriction = sf;
                    }
                }
            }

            if (link.RopeJoint != null)
            {
                UnityEngine.Object.Destroy(link.RopeJoint);
            }

            activeLinks.Remove(link.TowingId);
            activeLinks.Remove(link.TowedId);

            if (notify)
            {
                NotifyOccupants(link.TowingCar, $"Tow released ({reason})");
                NotifyOccupants(link.TowedCar, $"Tow released ({reason})");
            }

            if (config.DebugLog)
            {
                Puts($"[Tow] Released link: {reason}");
            }
        }

        private void CheckAllLinks()
        {
            if (activeLinks.Count == 0) return;

            var linksToRelease = new List<TowLink>();

            foreach (var link in activeLinks.Values)
            {
                if (link == null)
                {
                    linksToRelease.Add(link);
                    continue;
                }

                // Check entity validity
                if (link.TowingCar == null || link.TowedCar == null || 
                    link.TowingCar.IsDestroyed || link.TowedCar.IsDestroyed)
                {
                    linksToRelease.Add(link);
                    continue;
                }

                // Check joint validity
                if (link.RopeJoint == null)
                {
                    if (config.PlaySoundOnBreak)
                    {
                        PlaySoundAtMidpoint(link, config.SoundPathOnBreak);
                    }
                    NotifyOccupants(link.TowingCar, "Tow rope broke!");
                    NotifyOccupants(link.TowedCar, "Tow rope broke!");
                    linksToRelease.Add(link);
                    continue;
                }

                // Check separation distance
                var worldPosTowing = link.TowingCar.transform.TransformPoint(link.RopeJoint.anchor);
                var worldPosTowed = link.TowedCar.transform.TransformPoint(link.RopeJoint.connectedAnchor);
                var distance = Vector3.Distance(worldPosTowing, worldPosTowed);

                var maxAllowed = link.RopeLength * config.MaxSeparationMultiplier;
                if (distance > maxAllowed)
                {
                    if (config.PlaySoundOnRelease)
                    {
                        PlaySoundAtMidpoint(link, config.SoundPathOnRelease);
                    }
                    linksToRelease.Add(link);
                }
            }

            // Clean up broken links (avoid duplicates)
            var released = new HashSet<TowLink>();
            foreach (var link in linksToRelease)
            {
                if (link != null && released.Add(link))
                {
                    ReleaseLink(link, true, "separation/destroyed");
                }
            }
        }
        #endregion

        #region Yaw Alignment
        private void AlignTowedCars()
        {
            if (activeLinks.Count == 0) return;

            var processedLinks = new HashSet<TowLink>();

            foreach (var link in activeLinks.Values)
            {
                if (link == null || !processedLinks.Add(link)) continue;
                if (link.RopeJoint == null || link.TowingCar == null || link.TowedCar == null) continue;
                if (link.TowingCar.IsDestroyed || link.TowedCar.IsDestroyed) continue;

                var rbTowing = GetRigidbody(link.TowingCar);
                var rbTowed = GetRigidbody(link.TowedCar);
                if (rbTowed == null || rbTowing == null) continue;

                // Get rope direction (from towed car to towing car)
                var towedPos = link.TowedCar.transform.TransformPoint(link.RopeJoint.connectedAnchor);
                var towingPos = link.TowingCar.transform.TransformPoint(link.RopeJoint.anchor);
                var ropeVec = towingPos - towedPos;
                var ropeDistance = ropeVec.magnitude;
                
                if (ropeDistance < 0.01f) continue;
                
                var ropeDir = ropeVec / ropeDistance;

                // Yaw alignment
                if (config.AlignTowedCarYaw)
                {
                    // Project to horizontal plane
                    var ropeDirFlat = Vector3.ProjectOnPlane(ropeDir, Vector3.up).normalized;
                    if (ropeDirFlat.sqrMagnitude > 0.01f)
                    {
                        // Get towed car's forward direction (flat)
                        var carForward = Vector3.ProjectOnPlane(link.TowedCar.transform.forward, Vector3.up).normalized;

                        // Calculate yaw error (angle between car forward and rope direction)
                        float yawError = Vector3.SignedAngle(carForward, ropeDirFlat, Vector3.up) * Mathf.Deg2Rad;

                        // Current yaw velocity
                        float yawVelocity = rbTowed.angularVelocity.y;

                        // Apply corrective torque (PD controller)
                        float torque = config.AlignYawStrength * yawError - config.AlignYawDamping * yawVelocity;
                        rbTowed.AddTorque(Vector3.up * torque, ForceMode.Force);
                    }
                }

                // Direct pulling force - overcomes static friction when rope is stretched
                if (config.EnableWinchForce)
                {
                    float stretchRatio = ropeDistance / link.RopeLength;
                    
                    // Apply force when rope is stretched beyond activation threshold
                    if (stretchRatio > config.WinchActivationDistance)
                    {
                        // Check if towing car is moving (has velocity)
                        bool towingCarMoving = rbTowing.velocity.sqrMagnitude > 0.05f;
                        
                        if (towingCarMoving)
                        {
                            // Calculate stretch amount over threshold
                            float overStretch = Mathf.Max(0f, stretchRatio - config.WinchActivationDistance);
                            
                            // Apply very strong pulling force - base + bonus for stretch
                            // This force is strong enough to overcome static friction
                            float pullForce = config.WinchForceStrength * (1f + overStretch * 5f);
                            
                            // Project rope direction to horizontal plane to prevent upward forces
                            var ropeDirHorizontal = Vector3.ProjectOnPlane(ropeDir, Vector3.up).normalized;
                            
                            // Only apply force if there's a valid horizontal component
                            if (ropeDirHorizontal.sqrMagnitude > 0.01f)
                            {
                                // Apply force
                                rbTowed.AddForce(ropeDirHorizontal * pullForce, ForceMode.Force);
                                
                                // Direct velocity manipulation for unmanned vehicles
                                // If towed car is nearly stationary but towing car is moving, give it a velocity boost
                                if (rbTowed.velocity.sqrMagnitude < 0.5f)
                                {
                                    // Gradually blend in some of the towing car's velocity
                                    var targetVelocity = rbTowing.velocity * 0.3f;
                                    rbTowed.velocity = Vector3.Lerp(rbTowed.velocity, targetVelocity, 0.1f);
                                }
                                
                                // Also apply matching force to towing car (Newton's 3rd law simulation)
                                // This helps prevent the towing car from being pulled backward
                                rbTowing.AddForce(-ropeDirHorizontal * (pullForce * 0.3f), ForceMode.Force);
                            }
                        }
                    }
                }
            }
        }
        #endregion

        #region Rope Visuals
        private void DrawAllRopes()
        {
            if (activeLinks.Count == 0) return;

            var processedLinks = new HashSet<TowLink>();
            float duration = config.RopeDrawInterval * 1.2f;
            float visDistSqr = config.RopeVisibleDistance * config.RopeVisibleDistance;

            foreach (var link in activeLinks.Values)
            {
                if (link == null || !processedLinks.Add(link)) continue;
                if (link.RopeJoint == null || link.TowingCar == null || link.TowedCar == null) continue;
                if (link.TowingCar.IsDestroyed || link.TowedCar.IsDestroyed) continue;

                // Calculate rope endpoints directly from car positions (not from joint anchors)
                // REAR of towing car
                var posA = link.TowingCar.transform.position 
                         - link.TowingCar.transform.forward * config.RearAnchorBack 
                         + link.TowingCar.transform.up * config.AnchorHeight;
                
                // FRONT of towed car  
                var posB = link.TowedCar.transform.position 
                         + link.TowedCar.transform.forward * config.FrontAnchorForward 
                         + link.TowedCar.transform.up * config.AnchorHeight;
                
                var midpoint = (posA + posB) * 0.5f;
                var distance = Vector3.Distance(posA, posB);

                if (config.DebugLog)
                {
                    Puts($"[Rope] Drawing rope from {posA} to {posB}, distance={distance:F2}m");
                }

                // Draw for nearby players
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected) continue;
                    if ((player.transform.position - midpoint).sqrMagnitude > visDistSqr) continue;

                    // Draw thick rope line
                    player.SendConsoleCommand("ddraw.line", duration, ropeColor, posA, posB);
                    
                    // Add spheres at endpoints for visibility
                    player.SendConsoleCommand("ddraw.sphere", duration, ropeColor, posA, 0.15f);
                    player.SendConsoleCommand("ddraw.sphere", duration, ropeColor, posB, 0.15f);
                }
            }
        }
        #endregion

        #region Hooks
        private void OnEntityKill(BaseNetworkable entity)
        {
            var baseEntity = entity as BaseEntity;
            if (baseEntity?.net == null) return;

            if (activeLinks.TryGetValue(baseEntity.net.ID, out var link))
            {
                ReleaseLink(link, false, "entity killed");
            }
        }

        private void OnJointBreak(float breakForce, Joint joint)
        {
            foreach (var link in new List<TowLink>(activeLinks.Values))
            {
                if (link?.RopeJoint == joint)
                {
                    if (config.PlaySoundOnBreak)
                    {
                        PlaySoundAtMidpoint(link, config.SoundPathOnBreak);
                    }
                    NotifyOccupants(link.TowingCar, "Tow rope broke!");
                    NotifyOccupants(link.TowedCar, "Tow rope broke!");
                    ReleaseLink(link, false, "joint break");
                    break;
                }
            }
        }
        #endregion

        #region Helper Methods
        private RaycastHit? RaycastFromEyes(BasePlayer player, float distance)
        {
            var ray = new Ray(player.eyes.position, player.eyes.BodyForward());
            if (Physics.Raycast(ray, out var hit, distance, Physics.AllLayers, QueryTriggerInteraction.Ignore))
            {
                return hit;
            }
            return null;
        }

        private ModularCar GetCarFromHit(RaycastHit hit)
        {
            var entity = hit.GetEntity() ?? hit.collider?.ToBaseEntity() ?? hit.collider?.GetComponentInParent<BaseEntity>();
            if (entity == null) return null;

            var car = entity as ModularCar ?? entity.GetComponentInParent<ModularCar>();
            if (car != null) return car;

            // Check parent chain
            for (int i = 0; i < 5 && entity != null; i++)
            {
                car = entity as ModularCar ?? entity.GetComponentInParent<ModularCar>();
                if (car != null) return car;
                entity = entity.GetParentEntity() as BaseEntity;
            }

            return null;
        }

        private Rigidbody GetRigidbody(BaseEntity entity)
        {
            if (entity == null) return null;
            var gameObj = entity.gameObject;
            return gameObj.GetComponent<Rigidbody>() ?? 
                   gameObj.GetComponentInParent<Rigidbody>() ?? 
                   gameObj.GetComponentInChildren<Rigidbody>();
        }

        private Vector3 ChooseAnchorPoint(ModularCar car, Vector3 hitPoint)
        {
            var frontAnchor = car.transform.position + car.transform.forward * config.FrontAnchorForward + Vector3.up * config.AnchorHeight;
            var rearAnchor = car.transform.position - car.transform.forward * config.RearAnchorBack + Vector3.up * config.AnchorHeight;

            float distToFront = (hitPoint - frontAnchor).sqrMagnitude;
            float distToRear = (hitPoint - rearAnchor).sqrMagnitude;

            return distToFront <= distToRear ? frontAnchor : rearAnchor;
        }

        private bool IsCarLinked(ModularCar car)
        {
            return car?.net != null && activeLinks.ContainsKey(car.net.ID);
        }

        private Bounds GetCarBounds(ModularCar car)
        {
            var renderers = car.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                // Fallback to rigidbody-based bounds
                var rb = GetRigidbody(car);
                if (rb != null)
                {
                    var center = car.transform.InverseTransformPoint(rb.worldCenterOfMass);
                    return new Bounds(center, new Vector3(4f, 2f, 6f)); // Default car size
                }
                return new Bounds(Vector3.zero, new Vector3(4f, 2f, 6f));
            }

            // Calculate combined bounds in local space
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            // Convert to local space
            var localCenter = car.transform.InverseTransformPoint(bounds.center);
            var localSize = car.transform.InverseTransformVector(bounds.size);
            
            return new Bounds(localCenter, new Vector3(
                Mathf.Abs(localSize.x),
                Mathf.Abs(localSize.y),
                Mathf.Abs(localSize.z)
            ));
        }

        private void NotifyOccupants(ModularCar car, string message)
        {
            if (car == null) return;

            foreach (var child in car.children)
            {
                var mountable = child as BaseMountable;
                var rider = mountable?.GetMounted();
                if (rider != null && rider.IsConnected)
                {
                    rider.ChatMessage(message);
                }
            }
        }

        private void PlaySoundAtMidpoint(TowLink link, string soundPath)
        {
            if (link == null || string.IsNullOrEmpty(soundPath)) return;

            Vector3 midpoint;
            if (link.RopeJoint != null && link.TowingCar != null && link.TowedCar != null)
            {
                var posA = link.TowingCar.transform.TransformPoint(link.RopeJoint.anchor);
                var posB = link.TowedCar.transform.TransformPoint(link.RopeJoint.connectedAnchor);
                midpoint = (posA + posB) * 0.5f;
            }
            else if (link.TowingCar != null && link.TowedCar != null)
            {
                midpoint = (link.TowingCar.transform.position + link.TowedCar.transform.position) * 0.5f;
            }
            else
            {
                return;
            }

            Effect.server.Run(soundPath, midpoint, Vector3.up);
        }

        private Color ParseColor(string colorString, Color fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(colorString)) return fallback;
                
                var parts = colorString.Split(',');
                if (parts.Length < 3) return fallback;

                float r = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                float g = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                float b = float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                float a = parts.Length >= 4 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f;

                return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), Mathf.Clamp01(a));
            }
            catch
            {
                return fallback;
            }
        }
        #endregion
    }
}