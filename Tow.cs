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

        class TowLink
        {
            public BaseEntity A; // Towing car
            public BaseEntity B; // Towed car
            public ConfigurableJoint Joint;
        }

        private readonly Dictionary<ulong, TowLink> active = new Dictionary<ulong, TowLink>(); // key = towing car netID

        void Init()
        {
            permission.RegisterPermission(PERM, this);
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

            // Detach if already towing
            if (active.TryGetValue(carA.net.ID, out var link))
            {
                Detach(link, notify:true);
                return;
            }

            var carB = FindCarInFront(bp, 8f);
            if (carB == null || carB == carA) { p.Reply("No target car found in front of you."); return; }

            if (!TryAttach(carA, carB, out var newLink, out var reason))
            {
                p.Reply($"Cannot tow: {reason}");
                return;
            }

            active[carA.net.ID] = newLink;
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

            RaycastHit hit;
            if (Physics.Raycast(eyes, dir, out hit, maxDist, Layers.Mask.Server.Effects | Layers.Mask.Deployed))
            {
                var ent = hit.GetEntity();
                if (IsTowableCar(ent)) return ent;
            }

            // fallback: sphere search
            var hits = Physics.OverlapSphere(bp.transform.position + dir * 4f, 4f, Layers.Mask.Deployed | Layers.Mask.Server.Effects);
            foreach (var col in hits)
            {
                var ent = col.ToBaseEntity();
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

        bool TryAttach(BaseEntity carA, BaseEntity carB, out TowLink link, out string reason)
        {
            link = null; reason = null;

            var rbA = carA?.rigidbody;
            var rbB = carB?.rigidbody;
            if (rbA == null || rbB == null) { reason = "Missing rigidbody."; return false; }

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
            cj.breakForce = 30000f;
            cj.breakTorque = 30000f;

            link = new TowLink { A = carA, B = carB, Joint = cj };
            return true;
        }

        void Detach(TowLink link, bool notify = false)
        {
            if (link == null) return;
            if (link.Joint != null) GameObject.Destroy(link.Joint);
            active.Remove(link.A.net.ID);
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
                if (kv.Value.Joint == joint) Detach(kv.Value, notify:true);
        }
        #endregion
    }
}
