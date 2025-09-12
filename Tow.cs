using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using System.Collections.Generic;
using UnityEngine;
namespace Oxide.Plugins
{
    [Info("TowCars", "yourname", "0.1.0")]
    [Description("Tow one car with another using a physics joint")]
    public class TowCars : CovalencePlugin
    {
        private const string PERM = "towcars.use";
        
        private Configuration config;
        class Configuration
        {
            public float DragMultiplier { get; set; } = 1.25f;
            public float MaxDistance { get; set; } = 40f;
            public float Cooldown { get; set; } = 30f;
            public float JointBreakForce { get; set; } = 30000f;
            public float JointBreakTorque { get; set; } = 30000f;
            public bool RequireVehicleOwnership { get; set; } = true;
            public bool EnableDebugLogging { get; set; } = false;
            public VersionNumber Version { get; set; } = new VersionNumber(0, 1, 0);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try 
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
                
                // Check for version mismatch and handle upgrades
                var currentVersion = new VersionNumber(0, 1, 0);
                if (config.Version < currentVersion)
                {
                    LogDebug($"Upgrading config from version {config.Version} to {currentVersion}");
                    config.Version = currentVersion;
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error loading config: {ex.Message}");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }
        class TowLink
        {
            public BaseEntity A; // Towing car
            public BaseEntity B; // Towed car
            public ConfigurableJoint Joint;
            public float Drag;
        }
        private readonly Dictionary<ulong, TowLink> active = new Dictionary<ulong, TowLink>(); // key = towing car netID
        private readonly Dictionary<ulong, float> cooldowns = new Dictionary<ulong, float>();
        private bool initialized;

        void Init()
        {
            permission.RegisterPermission(PERM, this);
            timer.Every(5f, CheckLinks);
            initialized = true;
            LogDebug("TowCars plugin initialized successfully");
        }

        void LogDebug(string message)
        {
            if (config?.EnableDebugLogging ?? false)
            {
                Puts($"[TowCars] {message}");
            }
        }
        #region Commands
        [Command("tow")]
        private void CmdTow(IPlayer p, string cmd, string[] args)
        {
            if (!p.HasPermission(PERM)) { p.Reply("You don't have permission."); return; }
            var bp = p.Object as BasePlayer;
            if (bp == null || !bp.IsConnected) return;
            var carA = GetMountedCar(bp);
            if (carA == null) { p.Reply("You must be driving a car."); return; }
            var veh = carA as BaseVehicle;
            if (veh?.GetDriver() != bp) { p.Reply("Only the driver can tow."); return; }
            if (cooldowns.TryGetValue(bp.userID, out var next) && Time.realtimeSinceStartup < next)
            {
                p.Reply($"You must wait {(int)(next - Time.realtimeSinceStartup)}s before towing again.");
                return;
            }
            // Detach if already towing
            if (active.TryGetValue(carA.net.ID, out var link))
            {
                Detach(link, notify:true);
                return;
            }
            var carB = FindCarInFront(bp, 8f);
            if (carB == null || carB == carA) { p.Reply("No target car found in front of you."); return; }
            
            // Check vehicle ownership/authorization if enabled
            if (config.RequireVehicleOwnership)
            {
                var vehicleB = carB as BaseVehicle;
                if (vehicleB != null && !vehicleB.CanBeLooted(bp))
                {
                    p.Reply("You don't have permission to tow this vehicle. (Vehicle ownership check is enabled)");
                    return;
                }
            }
            
            if (IsInNoTowZone(carA) || IsInNoTowZone(carB))
            {
                p.Reply("You cannot tow in safe zones or monuments.");
                return;
            }
            if (!TryAttach(carA, carB, out var newLink, out var reason))
            {
                p.Reply($"Cannot tow: {reason}");
                return;
            }
            active[carA.net.ID] = newLink;
            cooldowns[bp.userID] = Time.realtimeSinceStartup + config.Cooldown;
            LogDebug($"Tow attached: {carA.net.ID} -> {carB.net.ID} by {bp.UserIDString}");
            p.Reply("Tow attached. Use /tow again to release.");
        }
        #endregion
        #region Core logic
        BaseEntity GetMountedCar(BasePlayer bp)
        {
            var seat = bp.GetMountedVehicle();
            return seat?.Vehicle as BaseEntity; // Works for ModularCar; adjust if needed
        }
        BaseEntity FindCarInFront(BasePlayer bp, float maxDist)
        {
            var eyes = bp.eyes.position;
            var dir = bp.eyes.BodyForward();
            
            // Do a boxcast instead - better for finding vehicles
            var halfExtents = new Vector3(1.5f, 1f, 1.5f); // Approximate size of a car
            RaycastHit[] hits = Physics.BoxCastAll(
                eyes, 
                halfExtents,
                dir,
                Quaternion.LookRotation(dir),
                maxDist,
                Layers.Mask.Server.Effects | Layers.Mask.Deployed
            );

            // Sort hits by distance to get the closest valid car
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            
            foreach (var hit in hits)
            {
                var ent = hit.GetEntity();
                if (IsTowableCar(ent)) return ent;
            }
            
            return null;
        }
        bool IsTowableCar(BaseEntity ent)
        {
            if (ent == null || ent.IsDestroyed) return false;
            // Basic filter: ModularCar & variants
            return ent.ShortPrefabName.Contains("modularcar");
        }
        bool IsInNoTowZone(BaseEntity ent)
        {
            if (ent == null) return false;
            return ent.IsInSafeZone() || ent.GetComponentInParent<MonumentInfo>() != null;
        }
        bool TryAttach(BaseEntity carA, BaseEntity carB, out TowLink link, out string reason)
        {
            link = null; reason = null;
            var rbA = carA?.rigidbody;
            var rbB = carB?.rigidbody;
            if (rbA == null || rbB == null) { reason = "Missing rigidbody."; return false; }
            
            // Check vehicle health
            var healthA = carA.GetComponent<BaseCombatEntity>();
            var healthB = carB.GetComponent<BaseCombatEntity>();
            if (healthA != null && healthA.Health() < healthA.MaxHealth() * 0.2f)
            {
                reason = "Your vehicle is too damaged to tow.";
                return false;
            }
            if (healthB != null && healthB.Health() < healthB.MaxHealth() * 0.2f)
            {
                reason = "Target vehicle is too damaged to tow.";
                return false;
            }
            
            // Prevent chains/loops
            if (active.ContainsKey(carB.net.ID)) { reason = "Target is already towing something."; return false; }
            var cj = carA.gameObject.AddComponent<ConfigurableJoint>();
            cj.connectedBody = rbB;
            // Anchor roughly at rear of A to front of B; you can compute bounds for better anchors
            cj.autoConfigureConnectedAnchor = false;
            cj.anchor = new Vector3(0f, 0.5f, -1.8f);
            cj.connectedAnchor = new Vector3(0f, 0.5f, 1.8f);
            // Linear limits like a tow strap
            cj.xMotion = ConfigurableJointMotion.Limited;
            cj.yMotion = ConfigurableJointMotion.Limited;
            cj.zMotion = ConfigurableJointMotion.Limited;
            var limit = new SoftJointLimit { limit = 0.75f }; // slack length
            cj.linearLimit = limit;
            var drive = new JointDrive { positionSpring = 2500f, positionDamper = 120f, maximumForce = 20000f };
            cj.xDrive = cj.yDrive = cj.zDrive = drive;
            // Angular freedom so B can trail behind
            cj.angularXMotion = ConfigurableJointMotion.Limited;
            cj.angularYMotion = ConfigurableJointMotion.Limited;
            cj.angularZMotion = ConfigurableJointMotion.Limited;
            var ang = new SoftJointLimit { limit = 30f };
            cj.lowAngularXLimit = new SoftJointLimit { limit = -15f };
            cj.highAngularXLimit = new SoftJointLimit { limit = 15f };
            cj.angularYLimit = ang;
            cj.angularZLimit = ang;
            // Optional: break force to auto-detach on crazy pulls
            cj.breakForce = config.JointBreakForce;
            cj.breakTorque = config.JointBreakTorque;
            var origDrag = rbA.drag;
            rbA.drag *= config.DragMultiplier;
            link = new TowLink { A = carA, B = carB, Joint = cj, Drag = origDrag };
            return true;
        }
        void Detach(TowLink link, bool notify = false)
        {
            if (link == null) return;
            if (link.Joint != null) GameObject.Destroy(link.Joint);
            if (link.A?.rigidbody != null) link.A.rigidbody.drag = link.Drag;
            active.Remove(link.A.net.ID);
            LogDebug($"Tow detached: {link.A?.net.ID} -> {link.B?.net.ID}");
            if (notify) SendInfo(link.A, "Tow released.");
        }
        void SendInfo(BaseEntity car, string msg)
        {
            foreach (var mount in car.children)
            {
                var seat = mount as BaseMountable;
                var rider = seat?.GetMounted();
                if (rider != null) rider.IPlayer?.Reply(msg);
            }
        }
        #endregion
        #region Hooks & cleanup
        void CheckLinks()
        {
            foreach (var kv in new Dictionary<ulong, TowLink>(active))
            {
                var link = kv.Value;
                if (link.A == null || link.B == null)
                {
                    Detach(link);
                    continue;
                }
                if (Vector3.Distance(link.A.transform.position, link.B.transform.position) > config.MaxDistance)
                    Detach(link, notify: true);
            }
        }
        void OnEntityKill(BaseNetworkable ent)
        {
            var be = ent as BaseEntity;
            if (be == null) return;
            // If towing car dies
            TowLink link;
            if (active.TryGetValue(be.net.ID, out link)) Detach(link);
            // If towed car dies, find any link referencing it
            foreach (var kv in new Dictionary<ulong, TowLink>(active))
                if (kv.Value.B == be) Detach(kv.Value);
        }
        void OnServerSave() { /* nothing persistent needed */ }
        void Unload()
        {
            foreach (var kv in active) if (kv.Value.Joint != null) GameObject.Destroy(kv.Value.Joint);
            active.Clear();
        }
        void OnJointBreak(float breakForce, ConfigurableJoint joint)
        {
            // Find which link this was
            foreach (var kv in new Dictionary<ulong, TowLink>(active))
            {
                if (kv.Value.Joint == joint)
                {
                    Detach(kv.Value, notify: true);
                    LogDebug($"Tow joint broke with force: {breakForce}");
                    break;
                }
            }
        }
    }
            foreach (var kv in new Dictionary<ulong, TowLink>(active))
            {
                if (kv.Value.Joint == joint)
                {
                    Detach(kv.Value, notify: true);
                    SendInfo(kv.Value.A, $"Tow connection broke due to excessive force ({breakForce:F0} N)!");
                    break;
                }
            }
