using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("BiTurbo", "chatgpt", "0.1.0")]
    [Description("Hold right-click while driving a modular car to engage a turbo-style acceleration boost")]
    public class BiTurbo : CovalencePlugin
    {
        private const string PERM_USE = "biturbo.use";

        private ConfigData config;
        private Timer tickTimer;

        // Track vehicles that should be boosted and per-vehicle state
        private readonly Dictionary<BaseVehicle, BoostState> boosting = new Dictionary<BaseVehicle, BoostState>();

        private class BoostState
        {
            public float lastBoostTime;     // last time (server time) boost applied
            public float cooldownUntil;     // when boost is next allowed (server time)
            public BasePlayer driver;       // cached driver ref (not strictly needed, handy for SFX/audience)
            public float lastSfxTime;
        }

        #region Config
        private class ConfigData
        {
            public float TickRate              = 0.05f;   // seconds between physics pushes
            public float BoostAcceleration     = 18.0f;   // m/s^2 equivalent force applied (scaled internally)
            public float MaxBoostSpeed         = 35.0f;   // m/s (≈ 126 km/h) hard cap during boost; 0 = no cap
            public float CooldownSeconds       = 0.0f;    // simple GCD after you release; 0 = none
            public bool  RequireEngineRunning  = true;    // only boost when the car engine is on
            public bool  RequireOnGround       = true;    // don’t boost in the air
            public bool  OnlyForward           = true;    // only when the car is moving forward-ish
            public float ForwardDotThreshold   = 0.25f;   // if velocity dot forward < this, skip (0.25 ≈ >~75° off)
            public float FuelPerSecond         = 0.0f;    // optional fuel drain per second while boosting (0 = off)
            public bool  PlaySfx               = true;    // turbo hiss while boosting (throttled)
            public float SfxInterval           = 0.7f;    // seconds between SFX plays while held
            public string SfxPath              = "assets/bundled/prefabs/fx/attack/wood/shovel/shovel_attack2.prefab"; // placeholder hiss
            public float  SfxRange             = 25f;     // who hears it

            public string Audience             = "driver"; // "driver", "nearby"
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
            SaveConfig(config);
        }

        protected override void SaveConfig() => SaveConfig(config);
        private void SaveConfig(ConfigData cfg) { Config.WriteObject(cfg, true); }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null) throw new Exception();
            }
            catch
            {
                PrintError("Invalid config, generating new.");
                LoadDefaultConfig();
            }
        }
        #endregion

        #region Hooks
        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);

            // start physics tick
            tickTimer = timer.Every(Mathf.Max(0.02f, config.TickRate), PhysicsTick);
        }

        private void Unload()
        {
            tickTimer?.Destroy();
            boosting.Clear();
        }

        // Detect right-click while driving
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            if (!player.IsAlive() || !player.IsConnected) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;

            // FIRE_SECONDARY is right-click
            bool holdingSecondary = input.WasJustPressed(BUTTON.FIRE_SECONDARY) || input.IsDown(BUTTON.FIRE_SECONDARY);
            var seat = player.GetMounted() as BaseMountable;
            if (seat == null) { StopBoostingForDriver(player); return; }

            // Find car + ensure it's a ModularCar and this seat is driver seat
            var mseat = seat as ModularCarSeat;
            if (mseat == null || !mseat.IsDriver) { StopBoostingForDriver(player); return; }

            var vehicle = mseat.Vehicle;
            var car = vehicle as ModularCar;
            if (car == null) { StopBoostingForDriver(player); return; }

            // conditions to allow starting/continuing boost
            if (!holdingSecondary) { StopBoostingVehicle(vehicle, player); return; }

            if (config.RequireEngineRunning && !car.IsEngineOn()) { StopBoostingVehicle(vehicle, player); return; }

            // cooldown check
            if (boosting.TryGetValue(vehicle, out var st) && Time.realtimeSinceStartup < st.cooldownUntil)
            {
                return; // still cooling down, ignore input
            }

            // mark as boosting
            if (!boosting.TryGetValue(vehicle, out st))
            {
                st = new BoostState();
                boosting[vehicle] = st;
            }
            st.driver = player;
            st.lastBoostTime = Time.realtimeSinceStartup;
        }

        // Clean up if driver dismounts, dies, or vehicle is destroyed
        private void OnEntityDismounted(BaseMountable mount, BasePlayer player)
        {
            if (player != null) StopBoostingForDriver(player);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var v = entity as BaseVehicle;
            if (v != null) boosting.Remove(v);
        }
        #endregion

        #region Core
        private void PhysicsTick()
        {
            if (boosting.Count == 0) return;

            var toClear = Pool.GetList<BaseVehicle>();

            foreach (var kvp in boosting)
            {
                var vehicle = kvp.Key;
                var st = kvp.Value;

                if (vehicle == null || vehicle.IsDestroyed)
                {
                    toClear.Add(vehicle);
                    continue;
                }

                // Must still have a driver and permission
                BasePlayer driver = st.driver;
                bool validDriver = driver != null
                                   && driver.IsConnected
                                   && driver.IsAlive()
                                   && driver.GetMounted() is ModularCarSeat seat
                                   && seat.IsDriver
                                   && seat.Vehicle == vehicle
                                   && permission.UserHasPermission(driver.UserIDString, PERM_USE);

                if (!validDriver)
                {
                    toClear.Add(vehicle);
                    continue;
                }

                var car = vehicle as ModularCar;
                if (car == null)
                {
                    toClear.Add(vehicle);
                    continue;
                }

                if (config.RequireEngineRunning && !car.IsEngineOn())
                {
                    toClear.Add(vehicle);
                    continue;
                }

                var rb = vehicle.GetComponent<Rigidbody>();
                if (rb == null)
                {
                    toClear.Add(vehicle);
                    continue;
                }

                // Optional ground check (raycast down from chassis)
                if (config.RequireOnGround && !IsVehicleGrounded(vehicle))
                    continue;

                // Compute boost vector & apply
                Vector3 fwd = vehicle.transform.forward;
                Vector3 vel = rb.velocity;

                if (config.OnlyForward && vel.sqrMagnitude > 0.01f)
                {
                    float dot = Vector3.Dot(vel.normalized, fwd);
                    if (dot < config.ForwardDotThreshold) continue;
                }

                // Speed cap (during boost)
                float speed = vel.magnitude;
                if (config.MaxBoostSpeed > 0.0f && speed >= config.MaxBoostSpeed)
                {
                    MaybePlaySfx(vehicle, st);
                    continue;
                }

                // Force magnitude — scale by mass and tick duration to feel consistent
                float dt = Mathf.Max(0.02f, config.TickRate);
                float accel = Mathf.Max(0f, config.BoostAcceleration);
                float force = rb.mass * accel; // F = m*a

                // apply as acceleration over dt
                rb.AddForce(fwd * force * dt, ForceMode.Force);

                // clamp if we’d overshoot the cap
                if (config.MaxBoostSpeed > 0f)
                {
                    Vector3 newVel = rb.velocity;
                    float newSpeed = newVel.magnitude;
                    if (newSpeed > config.MaxBoostSpeed)
                        rb.velocity = newVel.normalized * config.MaxBoostSpeed;
                }

                // Optional fuel drain
                if (config.FuelPerSecond > 0f)
                    BurnFuel(car, config.FuelPerSecond * dt);

                st.lastBoostTime = Time.realtimeSinceStartup;
                MaybePlaySfx(vehicle, st);
            }

            // Clear vehicles no longer eligible
            if (toClear.Count > 0)
            {
                foreach (var v in toClear) boosting.Remove(v);
            }
            Pool.FreeList(ref toClear);
        }

        private bool IsVehicleGrounded(BaseVehicle vehicle)
        {
            // Simple ray from slightly above chassis downwards
            var pos = vehicle.transform.position + Vector3.up * 0.25f;
            return Physics.Raycast(pos, Vector3.down, 0.6f, LayerMask.GetMask("World", "Terrain"));
        }

        private void BurnFuel(ModularCar car, float amount)
        {
            // Very light-touch: draw from the engine’s fuel system if present
            try
            {
                var engine = car.CarLock; // placeholder; real fuel systems are inside engine module(s)
                // If you want real fuel integration later, we can wire into EngineController + SmallRefinery style containers.
            }
            catch { /* intentionally no-op for initial version */ }
        }

        private void MaybePlaySfx(BaseVehicle vehicle, BoostState st)
        {
            if (!config.PlaySfx || string.IsNullOrEmpty(config.SfxPath)) return;

            float now = Time.realtimeSinceStartup;
            if (now - st.lastSfxTime < Mathf.Max(0.15f, config.SfxInterval)) return;

            st.lastSfxTime = now;

            if (config.Audience == "driver" && st.driver != null)
            {
                Effect.server.Run(config.SfxPath, vehicle.transform.position, Vector3.zero, st.driver.net?.connection);
            }
            else
            {
                Effect.server.Run(config.SfxPath, vehicle.transform.position);
            }
        }
        #endregion

        #region Helpers
        private void StopBoostingForDriver(BasePlayer player)
        {
            if (player == null) return;
            foreach (var kvp in boosting)
            {
                var st = kvp.Value;
                if (st.driver == player)
                {
                    ApplyCooldown(st);
                    st.driver = null;
                }
            }
            // prune any entries with no driver on next tick
        }

        private void StopBoostingVehicle(BaseVehicle vehicle, BasePlayer driverIfKnown = null)
        {
            if (vehicle == null) return;
            if (boosting.TryGetValue(vehicle, out var st))
            {
                ApplyCooldown(st);
                if (driverIfKnown != null && st.driver == driverIfKnown) st.driver = null;
            }
        }

        private void ApplyCooldown(BoostState st)
        {
            if (config.CooldownSeconds > 0f)
                st.cooldownUntil = Time.realtimeSinceStartup + config.CooldownSeconds;
        }
        #endregion

        #region Commands
        [Command("biturbo")]
        private void CmdBiTurbo(IPlayer iPlayer, string cmd, string[] args)
        {
            if (!iPlayer.HasPermission(PERM_USE))
            {
                iPlayer.Reply("You don’t have permission to use BiTurbo.");
                return;
            }

            iPlayer.Reply($"BiTurbo: Hold right-click while driving a modular car to boost. (Accel {config.BoostAcceleration} m/s², cap {config.MaxBoostSpeed} m/s, cooldown {config.CooldownSeconds}s)");
        }
        #endregion
    }
}
