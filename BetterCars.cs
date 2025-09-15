using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Rust;
using Network;

namespace Oxide.Plugins
{
    [Info("BetterCars", "S0F1st1Kt3dB3ar + Dan", "0.9.0")]
    [Description("Improves modular car suspension, grip, and roll control with presets and per-axle tuning.")]
    public class BetterCars : CovalencePlugin
    {
        private const string PERM_USE = "BetterCars.use";

        #region Config
        private ConfigData config;

        private class Preset
        {
            public float FrontSpring = 38000f;
            public float RearSpring  = 42000f;
            public float FrontDamper = 2800f;
            public float RearDamper  = 3000f;
            public float SuspDistance = 0.20f;     // meters of travel
            public float TargetPos    = 0.5f;      // 0..1 spring preload position

            // Tire friction stiffness (multiplies the curve)
            public float ForwardStiffness  = 1.8f;
            public float SidewaysStiffness = 2.0f;

            // Anti-roll bar strength (per axle)
            public float AntiRollFront = 16000f;
            public float AntiRollRear  = 18000f;

            // Optional helpers
            public bool  SpeedSteerAssist = true;
            public float SteerAssistMinSpeed = 12f; // m/s (~27mph)
            public float SteerAssistFactor  = 0.35f;

            // Downforce (N) at highway speed, scaled by v^2 (small but helps stability)
            public float DownforceN = 900f;

            public Preset Clone() => (Preset)MemberwiseClone();
        }

        private class ConfigData
        {
            public string DefaultPreset = "sport";
            public Dictionary<string, Preset> Presets = new Dictionary<string, Preset>
            {
                ["sport"] = new Preset
                {
                    FrontSpring=42000, RearSpring=46000,
                    FrontDamper=3200, RearDamper=3400,
                    SuspDistance=0.18f, TargetPos=0.55f,
                    ForwardStiffness=2.0f, SidewaysStiffness=2.2f,
                    AntiRollFront=20000, AntiRollRear=22000,
                    DownforceN=1100
                },
                ["offroad"] = new Preset
                {
                    FrontSpring=32000, RearSpring=36000,
                    FrontDamper=2400, RearDamper=2600,
                    SuspDistance=0.26f, TargetPos=0.45f,
                    ForwardStiffness=1.6f, SidewaysStiffness=1.7f,
                    AntiRollFront=9000, AntiRollRear=10000,
                    DownforceN=700
                },
                ["drift"] = new Preset
                {
                    FrontSpring=38000, RearSpring=42000,
                    FrontDamper=2800, RearDamper=3000,
                    SuspDistance=0.20f, TargetPos=0.55f,
                    ForwardStiffness=1.5f, SidewaysStiffness=1.1f, // looser lateral grip
                    AntiRollFront=12000, AntiRollRear=14000,
                    DownforceN=600,
                    SteerAssistFactor=0.2f
                },
                ["tow"] = new Preset
                {
                    FrontSpring=42000, RearSpring=52000, // stiffer rear for tongue weight
                    FrontDamper=3200, RearDamper=3800,
                    SuspDistance=0.19f, TargetPos=0.6f,
                    ForwardStiffness=2.0f, SidewaysStiffness=2.2f,
                    AntiRollFront=20000, AntiRollRear=26000,
                    DownforceN=1400
                },
                ["stock"] = new Preset
                {
                    FrontSpring=32000, RearSpring=32000,
                    FrontDamper=2200, RearDamper=2200,
                    SuspDistance=0.22f, TargetPos=0.5f,
                    ForwardStiffness=1.3f, SidewaysStiffness=1.3f,
                    AntiRollFront=8000, AntiRollRear=8000,
                    DownforceN=0, SteerAssistFactor=0.0f
                }
            };

            // If true and TowCars link detected, apply "tow" base with mild extra rear bias
            public bool TowAware = true;
            public float TowExtraRearSpring = 4000f;     // added to preset rear spring when towing
            public float TowExtraRearDamper = 400f;
        }
        #endregion

        #region Data & State
        private readonly Dictionary<ulong, string> playerPresetChoice = new Dictionary<ulong, string>();
        private readonly Dictionary<ModularCar, Preset> carPreset = new Dictionary<ModularCar, Preset>();
        #endregion

        #region Hooks
        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
            config = Config.ReadObject<ConfigData>();
            if (config.Presets == null || config.Presets.Count == 0) config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => config = new ConfigData();

        private void OnServerInitialized()
        {
            // Apply to existing cars after startup
            NextTick(() =>
            {
                foreach (var car in BaseNetworkable.serverEntities.OfType<ModularCar>())
                    TryApplyToCar(car, GetDefaultPresetForCar(car));
            });
        }

        private void Unload()
        {
            foreach (var car in carPreset.Keys.ToList())
            {
                var comp = car.GetComponent<CHP_AntiRollHelper>();
                if (comp) UnityEngine.Object.Destroy(comp);
            }
            carPreset.Clear();
        }

        private void OnEntitySpawned(BaseNetworkable ent)
        {
            var car = ent as ModularCar;
            if (car == null) return;
            NextTick(() => TryApplyToCar(car, GetDefaultPresetForCar(car)));
        }

        // When player enters driver seat, apply their chosen preset
        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            var car = mountable?.GetComponentInParent<ModularCar>();
            if (car == null) return;

            if (playerPresetChoice.TryGetValue(player.userID, out var key) && config.Presets.TryGetValue(key, out var preset))
            {
                TryApplyToCar(car, PrepareTowAwarePreset(car, preset));
                player.ChatMessage($"<color=#9cf>CarHandling+</color>: Applied <b>{key}</b>.");
            }
        }
        #endregion

        #region Core Apply
        private Preset GetDefaultPresetForCar(ModularCar car)
        {
            var key = string.IsNullOrEmpty(config.DefaultPreset) ? "sport" : config.DefaultPreset;
            if (!config.Presets.TryGetValue(key, out var p)) p = new Preset();
            return PrepareTowAwarePreset(car, p);
        }

        private Preset PrepareTowAwarePreset(ModularCar car, Preset basePreset)
        {
            var p = basePreset.Clone();
            if (!config.TowAware) return p;

            // Lightweight TowCars link detection: look for a child GameObject named with "tow" rope or a joint we tag.
            // This is intentionally generic to avoid hard dependency.
            bool looksTowing = car.gameObject.GetComponentsInChildren<Joint>(true).Any(j => j.name.IndexOf("tow", StringComparison.OrdinalIgnoreCase) >= 0)
                            || car.gameObject.GetComponentsInChildren<Transform>(true).Any(t => t.name.IndexOf("towrope", StringComparison.OrdinalIgnoreCase) >= 0);

            if (looksTowing)
            {
                p.RearSpring += config.TowExtraRearSpring;
                p.RearDamper += config.TowExtraRearDamper;
            }
            return p;
        }

        private void TryApplyToCar(ModularCar car, Preset preset)
        {
            if (car == null || car.IsDestroyed) return;

            var wheels = car.GetComponentsInChildren<WheelCollider>(true);
            if (wheels == null || wheels.Length == 0) return;

            // Rough axle grouping by local Z (front negative-ish, rear positive-ish); adjust as needed if Rust’s car prefab changes
            var center = car.transform.InverseTransformPoint(car.transform.position);
            var ordered = wheels.OrderBy(w => w.transform.localPosition.z).ToList();
            var midZ = ordered.Average(w => w.transform.localPosition.z);

            foreach (var wc in wheels)
            {
                bool isFront = wc.transform.localPosition.z < midZ;

                wc.suspensionDistance = preset.SuspDistance;

                var spring = wc.suspensionSpring;
                spring.targetPosition = Mathf.Clamp01(preset.TargetPos);
                spring.spring = isFront ? preset.FrontSpring : preset.RearSpring;
                spring.damper = isFront ? preset.FrontDamper : preset.RearDamper;
                wc.suspensionSpring = spring;

                // Build a reasonable friction curve; we preserve existing slip/value geometry, scale stiffness
                var fwd = wc.forwardFriction;
                fwd.stiffness = preset.ForwardStiffness;
                wc.forwardFriction = fwd;

                var side = wc.sidewaysFriction;
                side.stiffness = preset.SidewaysStiffness;
                wc.sidewaysFriction = side;
            }

            // Attach/update helper component
            var helper = car.GetComponent<CHP_AntiRollHelper>() ?? car.gameObject.AddComponent<CHP_AntiRollHelper>();
            helper.Apply(car, preset);

            carPreset[car] = preset;
        }
        #endregion

        #region Chat/Console
        [Command("carhandling")]
        private void CmdCarHandling(IPlayer iplayer, string cmd, string[] args)
        {
            var bp = iplayer.Object as BasePlayer;
            if (bp == null || !bp.IsConnected) return;
            if (!iplayer.HasPermission(PERM_USE))
            {
                iplayer.Reply("You lack permission (BetterCars.use).");
                return;
            }

            if (args.Length == 0)
            {
                iplayer.Reply("CarHandling+ — Usage:\n" +
                    "/carhandling mode <sport|offroad|drift|tow|stock>\n" +
                    "/carhandling set <param> <value>\n" +
                    "Params: frontSpring, rearSpring, frontDamper, rearDamper, susp, target, fwdFric, sideFric");
                return;
            }

            var sub = args[0].ToLowerInvariant();
            var car = bp.GetMountedVehicle() as ModularCar ?? bp.GetParentEntity() as ModularCar ?? bp.GetComponentInParent<ModularCar>();
            if (sub == "mode" && args.Length >= 2)
            {
                var key = args[1].ToLowerInvariant();
                if (!config.Presets.TryGetValue(key, out var preset))
                {
                    iplayer.Reply($"Unknown preset '{key}'.");
                    return;
                }
                playerPresetChoice[bp.userID] = key;

                if (car != null) { TryApplyToCar(car, PrepareTowAwarePreset(car, preset)); iplayer.Reply($"Applied <b>{key}</b> to current car."); }
                else iplayer.Reply($"Selected <b>{key}</b>. It’ll apply when you enter a car.");
                return;
            }

            if (sub == "set" && args.Length >= 3)
            {
                if (car == null) { iplayer.Reply("You must be driving a modular car to use /carhandling set."); return; }
                if (!carPreset.TryGetValue(car, out var p)) p = GetDefaultPresetForCar(car).Clone();

                string param = args[1].ToLowerInvariant();
                if (!float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
                { iplayer.Reply("Value must be a number."); return; }

                switch (param)
                {
                    case "frontspring":  p.FrontSpring = val; break;
                    case "rearspring":   p.RearSpring = val; break;
                    case "frontdamper":  p.FrontDamper = val; break;
                    case "reardamper":   p.RearDamper = val; break;
                    case "susp":         p.SuspDistance = Mathf.Clamp(val, 0.05f, 0.40f); break;
                    case "target":       p.TargetPos    = Mathf.Clamp01(val); break;
                    case "fwdfric":      p.ForwardStiffness  = Mathf.Max(0.2f, val); break;
                    case "sidefric":     p.SidewaysStiffness = Mathf.Max(0.2f, val); break;
                    default: iplayer.Reply("Unknown param. Use: frontSpring, rearSpring, frontDamper, rearDamper, susp, target, fwdFric, sideFric"); return;
                }

                TryApplyToCar(car, p);
                iplayer.Reply($"Updated <b>{param}</b> = {val}. Applied to your current car.");
                return;
            }

            iplayer.Reply("Unknown subcommand. Try /carhandling");
        }

        [ConsoleCommand("carhandling.globalmode")]
        private void CcmdGlobal(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            var key = arg.GetString(0, "sport").ToLowerInvariant();
            if (!config.Presets.ContainsKey(key)) { Puts($"Unknown preset '{key}'."); return; }
            config.DefaultPreset = key; SaveConfig();
            Puts($"Default preset set to {key}. New cars will spawn with it.");
        }
        #endregion

        #region Helpers: Anti-roll + Downforce + Steering Assist
        private class CHP_AntiRollHelper : FacepunchBehaviour
        {
            private ModularCar car;
            private Preset preset;
            private WheelCollider[] wheels;
            private Rigidbody rb;

            public void Apply(ModularCar c, Preset p)
            {
                car = c; preset = p;
                wheels = car.GetComponentsInChildren<WheelCollider>(true);
                rb = car.GetComponent<Rigidbody>();
                enabled = true;
            }

            private void FixedUpdate()
            {
                if (car == null || car.IsDestroyed || wheels == null || wheels.Length == 0) { enabled = false; return; }

                // Group wheels by axle using local Z
                var midZ = wheels.Average(w => w.transform.localPosition.z);
                var front = wheels.Where(w => w.transform.localPosition.z < midZ).ToArray();
                var rear  = wheels.Where(w => w.transform.localPosition.z >= midZ).ToArray();

                ApplyAntiRollAxle(front, preset.AntiRollFront);
                ApplyAntiRollAxle(rear,  preset.AntiRollRear);

                // Downforce (v^2 scaled)
                if (preset.DownforceN > 0 && rb != null)
                {
                    float v = rb.velocity.magnitude; // m/s
                    float scale = Mathf.Clamp01(v * v / (30f * 30f)); // ramps by ~30 m/s (~67 mph)
                    rb.AddForce(-car.transform.up * (preset.DownforceN * scale), ForceMode.Force);
                }

                // Speed-sensitive steering “softening”
                if (preset.SpeedSteerAssist && rb != null && preset.SteerAssistFactor > 0f)
                {
                    float v = rb.velocity.magnitude;
                    if (v > preset.SteerAssistMinSpeed)
                    {
                        // Nudge the car’s yaw towards current velocity vector to stabilize quick oversteer snaps
                        Vector3 vel = rb.velocity;
                        vel.y = 0f;
                        if (vel.sqrMagnitude > 0.01f)
                        {
                            Quaternion velRot = Quaternion.LookRotation(vel.normalized, Vector3.up);
                            Quaternion curRot = Quaternion.Euler(0f, car.transform.eulerAngles.y, 0f);
                            Quaternion tgt = Quaternion.Slerp(curRot, velRot, preset.SteerAssistFactor * Time.fixedDeltaTime);
                            Vector3 e = tgt.eulerAngles;
                            rb.MoveRotation(Quaternion.Euler(car.transform.eulerAngles.x, e.y, car.transform.eulerAngles.z));
                        }
                    }
                }
            }

            private void ApplyAntiRollAxle(WheelCollider[] axle, float strength)
            {
                if (axle == null || axle.Length < 2 || strength <= 0f) return;

                // Pair left/right by local X
                var ordered = axle.OrderBy(w => w.transform.localPosition.x).ToArray();
                var left = ordered.First();
                var right = ordered.Last();

                float travelL = GetSuspensionTravel(left);
                float travelR = GetSuspensionTravel(right);

                float antiRollForce = (travelL - travelR) * strength;

                if (left.GetGroundHit(out WheelHit hitL))
                    left.attachedRigidbody.AddForceAtPosition(left.transform.up * -antiRollForce, hitL.point);

                if (right.GetGroundHit(out WheelHit hitR))
                    right.attachedRigidbody.AddForceAtPosition(right.transform.up * antiRollForce, hitR.point);
            }

            private float GetSuspensionTravel(WheelCollider wc)
            {
                if (wc.GetGroundHit(out WheelHit hit))
                {
                    return (-wc.transform.InverseTransformPoint(hit.point).y - wc.radius) / wc.suspensionDistance;
                }
                // fully extended
                return 1f;
            }
        }
        #endregion
    }
}
