using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("BiTurbo", "S0F1st1Kt3dB3ar", "0.3.0")]
    [Description("Turbo-style acceleration for modular cars: hold right-click; boost spools above a speed gate and spits flames.")]
    public class BiTurbo : CovalencePlugin
    {
        private const string PERM_USE = "biturbo.use";

        private ConfigData config;
        private Timer tickTimer;

        // Vehicle -> boost state
        private readonly Dictionary<BaseVehicle, BoostState> boosting = new Dictionary<BaseVehicle, BoostState>();

        private class BoostState
        {
            public BasePlayer driver;
            public float lastSfxTime;
            public float cooldownUntil;
            public float spool01;              // 0..1 current turbo spool
            public bool  holding;              // current input
            public bool  wasHolding;           // previous tick input
            public float lastHeldTime;
            public float lastAboveGateTime;

            // FX cadence
            public float lastFireFxTime;
        }

        private class ConfigData
        {
            // Core
            public float TickRate                = 0.05f;   // seconds between physics pushes
            public float BoostAccelAtFullSpool  = 48.0f;   // m/s^2 at spool = 1.0 (turned up)
            public float MaxBoostSpeed          = 60.0f;   // m/s cap during boost; 0 = no cap

            // Spool behaviour (RPM-like)
            public float MinSpeedForSpool       = 2.0f;    // m/s threshold to start/keep spooling
            public float SpoolUpPerSecond       = 3.5f;    // 0..1 per second while held & above gate
            public float SpoolDownPerSecond     = 1.2f;    // 0..1 per second when not held / gate fails
            public float GateGraceSeconds       = 0.20f;   // hold spool briefly across tiny dips

            // Safety / realism
            public bool  RequireOnGround        = true;    // no boosting while airborne
            public bool  OnlyForward            = true;    // boost only when moving forward-ish
            public float ForwardDotThreshold    = 0.25f;   // dot(vel, fwd) must be >= this

            // SFX (optional)
            public bool   PlaySfx               = true;
            public float  SfxInterval           = 0.6f;
            public string SfxSpoolHiss          = "assets/bundled/prefabs/fx/notice/item.select.fx.prefab";
            public string SfxBlowOff            = "assets/bundled/prefabs/fx/notice/item.deselect.fx.prefab";
            public string Audience              = "nearby"; // "driver" or "nearby"

            // Fire FX (exhaust flames) while boosting
            public string FireFxPrefab          = "assets/bundled/prefabs/fx/weapons/flamethrower/flamethrower_fireball.prefab";
            public float  FireFxInterval        = 0.10f;   // seconds between spawns while boosting
            public float  FireFxOffsetZ         = -1.6f;   // behind the car (local Z)
            public float  FireFxOffsetY         = 0.45f;   // up from ground (local Y)

            // Fire FX (chain/jet look)
            public bool   FireFxDual          = true;    // spawn left & right exhausts
            public float  FireFxOffsetX       = 0.45f;   // lateral offset per exhaust
            public int    FireFxCountPerPulse = 3;       // how many flames per tick
            public float  FireFxStepZ         = 0.55f;   // spacing backward between flames

            // Misc
            public float CooldownSeconds        = 0.0f;    // simple global cooldown after release; 0 = none
        }

        protected override void LoadDefaultConfig() { config = new ConfigData(); SaveConfig(); }
        protected override void SaveConfig() => Config.WriteObject(config, true);
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { config = Config.ReadObject<ConfigData>() ?? new ConfigData(); }
            catch { LoadDefaultConfig(); }
        }

        #region Lifecycle
        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
            tickTimer = timer.Every(Mathf.Max(0.02f, config.TickRate), PhysicsTick);
        }

        private void Unload()
        {
            tickTimer?.Destroy();
            boosting.Clear();
        }
        #endregion

        #region Input hooks
        // Right-click while driving to engage turbo
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            if (!player.IsAlive() || !player.IsConnected) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;

            var vehicle = player.GetMountedVehicle();
            var car = vehicle as ModularCar;

            // must be driver of a modular car
            if (car == null || vehicle.GetDriver() != player)
            {
                StopBoostingForDriver(player);
                return;
            }

            bool holdingSecondary = input.IsDown(BUTTON.FIRE_SECONDARY);

            if (!boosting.TryGetValue(vehicle, out var st))
            {
                st = new BoostState();
                boosting[vehicle] = st;
            }

            // cooldown gate
            if (Time.realtimeSinceStartup < st.cooldownUntil)
                holdingSecondary = false;

            st.driver = player;
            st.wasHolding = st.holding;
            st.holding = holdingSecondary;
            if (st.holding) st.lastHeldTime = Time.realtimeSinceStartup;
        }

        private void OnEntityDismounted(BaseMountable m, BasePlayer p)
        {
            if (p != null) StopBoostingForDriver(p);
        }

        private void OnEntityKill(BaseNetworkable e)
        {
            if (e is BaseVehicle v) boosting.Remove(v);
        }
        #endregion

        #region Core boost tick
        private void PhysicsTick()
        {
            if (boosting.Count == 0) return;

            var toClear = new List<BaseVehicle>();
            float now = Time.realtimeSinceStartup;
            float dt  = Mathf.Max(0.02f, config.TickRate);

            foreach (var kvp in boosting)
            {
                var vehicle = kvp.Key;
                var st = kvp.Value;

                if (vehicle == null || vehicle.IsDestroyed) { toClear.Add(vehicle); continue; }

                var driver = st.driver;
                bool validDriver = driver != null
                                   && driver.IsConnected
                                   && driver.IsAlive()
                                   && driver.GetMountedVehicle() == vehicle
                                   && vehicle.GetDriver() == driver
                                   && permission.UserHasPermission(driver.UserIDString, PERM_USE);
                if (!validDriver) { toClear.Add(vehicle); continue; }

                var car = vehicle as ModularCar;
                if (car == null) { toClear.Add(vehicle); continue; }

                var rb = vehicle.GetComponent<Rigidbody>();
                if (rb == null) { toClear.Add(vehicle); continue; }

                // Optional ground check
                if (config.RequireOnGround && !IsVehicleGrounded(vehicle))
                {
                    // decay spool mid-air
                    st.spool01 = Mathf.Max(0f, st.spool01 - config.SpoolDownPerSecond * dt);
                    // handle release SFX/cooldown transitions
                    if (!st.holding && st.wasHolding && st.spool01 > 0.3f) PlayBlowOff(vehicle, st);
                    st.wasHolding = st.holding;
                    continue;
                }

                Vector3 fwd = vehicle.transform.forward;
                Vector3 vel = rb.velocity;
                float   speed = vel.magnitude;

                // gate based on speed with a small grace window
                if (speed >= config.MinSpeedForSpool)
                    st.lastAboveGateTime = now;

                bool gateOk = (now - st.lastAboveGateTime) <= Mathf.Max(0f, config.GateGraceSeconds);

                // update spool
                if (st.holding && gateOk)
                    st.spool01 = Mathf.Min(1f, st.spool01 + config.SpoolUpPerSecond * dt);
                else
                    st.spool01 = Mathf.Max(0f, st.spool01 - config.SpoolDownPerSecond * dt);

                // Blow-off if we just released while we had some boost
                if (!st.holding && st.wasHolding && st.spool01 > 0.3f)
                    PlayBlowOff(vehicle, st);

                // Forward gating for actual force application
                if (config.OnlyForward && vel.sqrMagnitude > 0.01f)
                {
                    float dot = Vector3.Dot(vel.normalized, fwd);
                    if (dot < config.ForwardDotThreshold)
                    {
                        st.wasHolding = st.holding;
                        continue;
                    }
                }

                // Speed cap during boost
                if (config.MaxBoostSpeed > 0f && speed >= config.MaxBoostSpeed)
                {
                    MaybePlaySpoolHiss(vehicle, st);
                    MaybePlayFireFx(vehicle, st); // still let the flames show when capped
                    if (!st.holding && st.wasHolding && config.CooldownSeconds > 0f)
                        st.cooldownUntil = now + config.CooldownSeconds;
                    st.wasHolding = st.holding;
                    continue;
                }

                // Apply force proportional to spool if holding and past the gate
                if (st.spool01 > 0f && st.holding && gateOk)
                {
                    // Use acceleration mode so BoostAccelAtFullSpool is literal m/s²
                    float a = config.BoostAccelAtFullSpool * st.spool01;
                    rb.AddForce(fwd * a * dt, ForceMode.Acceleration);

                    // Clamp if we overshoot max speed
                    if (config.MaxBoostSpeed > 0f)
                    {
                        Vector3 newVel = rb.velocity;
                        float newSpeed = newVel.magnitude;
                        if (newSpeed > config.MaxBoostSpeed)
                            rb.velocity = newVel.normalized * config.MaxBoostSpeed;
                    }

                    MaybePlaySpoolHiss(vehicle, st);
                    MaybePlayFireFx(vehicle, st);
                }

                // Apply simple cooldown on release (optional)
                if (!st.holding && st.wasHolding && config.CooldownSeconds > 0f)
                    st.cooldownUntil = now + config.CooldownSeconds;

                st.wasHolding = st.holding;
            }

            // cleanup
            if (toClear.Count > 0)
            {
                foreach (var v in toClear) boosting.Remove(v);
            }
        }
        #endregion

        #region Helpers & FX
        private bool IsVehicleGrounded(BaseVehicle vehicle)
        {
            // Simple downward ray just above chassis; uses all layers
            var pos = vehicle.transform.position + Vector3.up * 0.25f;
            return Physics.Raycast(pos, Vector3.down, 0.6f, Physics.AllLayers, QueryTriggerInteraction.Ignore);
        }

        private void MaybePlaySpoolHiss(BaseVehicle vehicle, BoostState st)
        {
            if (!config.PlaySfx || string.IsNullOrEmpty(config.SfxSpoolHiss)) return;
            float now = Time.realtimeSinceStartup;
            if (now - st.lastSfxTime < Mathf.Max(0.15f, config.SfxInterval)) return;
            st.lastSfxTime = now;

            if (string.Equals(config.Audience, "driver", StringComparison.OrdinalIgnoreCase) && st.driver != null)
                Effect.server.Run(config.SfxSpoolHiss, vehicle.transform.position, Vector3.zero, st.driver.net?.connection);
            else
                Effect.server.Run(config.SfxSpoolHiss, vehicle.transform.position);
        }

        private void PlayBlowOff(BaseVehicle vehicle, BoostState st)
        {
            if (!config.PlaySfx || string.IsNullOrEmpty(config.SfxBlowOff)) return;

            if (string.Equals(config.Audience, "driver", StringComparison.OrdinalIgnoreCase) && st.driver != null)
                Effect.server.Run(config.SfxBlowOff, vehicle.transform.position, Vector3.zero, st.driver.net?.connection);
            else
                Effect.server.Run(config.SfxBlowOff, vehicle.transform.position);
        }

        private void MaybePlayFireFx(BaseVehicle vehicle, BoostState st)
        {
            if (string.IsNullOrEmpty(config.FireFxPrefab)) return;

            float now = Time.realtimeSinceStartup;
            if (now - st.lastFireFxTime < Mathf.Max(0.03f, config.FireFxInterval)) return; // throttle
            st.lastFireFxTime = now;

            int   count = Mathf.Max(1, config.FireFxCountPerPulse);
            float stepZ = Mathf.Max(0.05f, config.FireFxStepZ);

            // single or dual exhaust lateral positions
            List<float> xs = new List<float>();
            if (config.FireFxDual)
            {
                float x = Mathf.Abs(config.FireFxOffsetX) > 0.01f ? config.FireFxOffsetX : 0.45f;
                xs.Add(-x);
                xs.Add(+x);
            }
            else xs.Add(0f);

            for (int i = 0; i < count; i++)
            {
                float z = config.FireFxOffsetZ - (i * stepZ);
                foreach (float x in xs)
                {
                    Vector3 localOffset = new Vector3(x, config.FireFxOffsetY, z);
                    Vector3 pos = vehicle.transform.TransformPoint(localOffset);
                    Vector3 dir = -vehicle.transform.forward;

                    // Use the KNOWN-GOOD prefab you tested earlier:
                    Effect.server.Run(config.FireFxPrefab, pos, dir);
                }
            }
        }

        private void StopBoostingForDriver(BasePlayer player)
        {
            if (player == null) return;

            // Mark any entries with this driver as released.
            foreach (var kvp in boosting)
            {
                var st = kvp.Value;
                if (st.driver == player)
                {
                    if (st.holding && st.spool01 > 0.3f) PlayBlowOff(kvp.Key, st);
                    ApplyCooldown(st);
                    st.holding = false;
                    st.wasHolding = false;
                    st.driver = null;
                }
            }
        }

        private void ApplyCooldown(BoostState st)
        {
            if (config.CooldownSeconds > 0f)
                st.cooldownUntil = Time.realtimeSinceStartup + config.CooldownSeconds;
        }
        #endregion

        #region Command
        [Command("biturbo")]
        private void CmdBiTurbo(IPlayer ip, string cmd, string[] args)
        {
            if (!ip.HasPermission(PERM_USE))
            {
                ip.Reply("You don’t have permission to use BiTurbo.");
                return;
            }

            ip.Reply(
                $"BiTurbo ready. Hold right-click while driving.\n" +
                $"- Gate ≥ {config.MinSpeedForSpool:0.0} m/s | Spool ↑ {config.SpoolUpPerSecond:0.##}/s ↓ {config.SpoolDownPerSecond:0.##}/s\n" +
                $"- Full-spool accel: {config.BoostAccelAtFullSpool:0.##} m/s² | Cap: {(config.MaxBoostSpeed > 0 ? config.MaxBoostSpeed.ToString("0.0") + " m/s" : "none")}\n" +
                $"- Flames: {config.FireFxPrefab} every {config.FireFxInterval:0.00}s"
            );
        }
        #endregion
    }
}
