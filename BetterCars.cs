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
    [Info("BetterCars", "S0F1st1Kt3dB3ar", "0.9.0")]
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

            // Clone helper for tweaks without mutating original preset data in config values
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

            // Engine awareness
            public bool  UseEngineAwareness = true;   // watch engine state and swap presets
            public string EngineOffPresetKey = "stock";
            public float EnginePollSeconds   = 0.25f; // 4x/sec is plenty
        }
        #endregion

        #region Data & State
        private readonly Dictionary<ulong, string> playerPresetChoice = new Dictionary<ulong, string>();
        private readonly Dictionary<ModularCar, Preset> carPreset = new Dictionary<ModularCar, Preset>();
        // Last-known engine state for cars we've touched
        private readonly Dictionary<ModularCar, bool> _engineOn = new Dictionary<ModularCar, bool>();
        private Timer _engineWatchTimer;

        // Cached probe delegate for “is engine on”
        private static System.Func<ModularCar, bool> _engineProbe;
        #endregion

        #region Hooks

        private readonly Dictionary<ulong, float> _lastSwitchAt = new Dictionary<ulong, float>();
        private static readonly string[] _modeOrder = { "sport", "offroad", "drift", "tow", "stock" };
        private const float _switchCooldown = 0.35f; // seconds

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || !player.IsConnected) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;

            var car = player.GetMountedVehicle() as ModularCar ?? player.GetParentEntity() as ModularCar;
            if (car == null) return;

            if (!input.WasJustPressed(BUTTON.FIRE_THIRD)) return;

            float now = Time.realtimeSinceStartup;
            if (_lastSwitchAt.TryGetValue(player.userID, out float last) && (now - last) < _switchCooldown) return;
            _lastSwitchAt[player.userID] = now;

            string currentKey = config.DefaultPreset;
            if (playerPresetChoice.TryGetValue(player.userID, out var saved) && config.Presets.ContainsKey(saved))
                currentKey = saved;

            string nextKey = NextModeKey(currentKey);
            if (!config.Presets.TryGetValue(nextKey, out var nextPreset)) return;

            // 🚫 Do not apply while engine is OFF or engine state is unknown
            if (config.UseEngineAwareness && (!TryIsEngineOn(car, out var on) || !on))
            {
                playerPresetChoice[player.userID] = nextKey; // remember choice
                player.ChatMessage($"<color=#9cf>BetterCars</color>: <b>{Title(nextKey)}</b> queued — start the engine to apply.");
                return;
            }

            // Engine is ON → apply immediately
            TryApplyToCar(car, PrepareTowAwarePreset(car, nextPreset));
            playerPresetChoice[player.userID] = nextKey;
            player.ChatMessage($"<color=#9cf>BetterCars</color>: Drive mode set to <b>{Title(nextKey)}</b>");
        }


        // Helpers for cycling & label
        private string NextModeKey(string current)
        {
            int idx = Array.FindIndex(_modeOrder, k => string.Equals(k, current, StringComparison.OrdinalIgnoreCase));
            for (int i = 1; i <= _modeOrder.Length; i++)
            {
                string candidate = _modeOrder[(idx + i + _modeOrder.Length) % _modeOrder.Length];
                if (config.Presets.ContainsKey(candidate)) return candidate;
            }
            return "sport";
        }

        private string Title(string key) => string.IsNullOrEmpty(key) ? "" : char.ToUpperInvariant(key[0]) + key.Substring(1);

        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);

            // Safe read (ReadObject can return null on first run or malformed file)
            try
            {
                config = Config.ReadObject<ConfigData>() ?? new ConfigData();
            }
            catch
            {
                config = new ConfigData();
            }

            // If you really want to reset to fresh defaults when presets are missing, do it *before*
            // setting per-field defaults so we don't wipe them afterwards.
            if (config.Presets == null || config.Presets.Count == 0)
                config = new ConfigData();

            // Now apply/normalize engine-aware defaults
            if (string.IsNullOrEmpty(config.EngineOffPresetKey))
                config.EngineOffPresetKey = "stock";

            // Clamp poll interval (protect against 0/negative)
            config.EnginePollSeconds = Mathf.Clamp(
                config.EnginePollSeconds <= 0f ? 0.25f : config.EnginePollSeconds,
                0.1f, 1.0f
            );

            // Write back so any new fields appear in the JSON
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

            if (config.UseEngineAwareness && config.EnginePollSeconds > 0f)
            {
                _engineWatchTimer?.Destroy();
                _engineWatchTimer = timer.Every(config.EnginePollSeconds, EngineWatchTick);
            }
        }


        private void Unload()
        {
            _engineWatchTimer?.Destroy();
            _engineWatchTimer = null;
            _engineOn.Clear();

            foreach (var car in carPreset.Keys.ToList())
            {
                if (!car || car.IsDestroyed) { carPreset.Remove(car); continue; }

                CHP_AntiRollHelper comp = null;
                try { comp = car.GetComponent<CHP_AntiRollHelper>(); }
                catch { /* car destroyed mid-check */ }

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

        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            var car = mountable?.GetComponentInParent<ModularCar>();
            if (car == null) return;

            // Always go to safe physics immediately when someone mounts.
            ApplyPresetKey(car, config.EngineOffPresetKey);

            // Reset our cached engine state for this car; we'll promote via EngineWatchTick on OFF→ON.
            _engineOn[car] = false;

            // If engine is already ON and we can see it, promote immediately to the driver's choice
            if (config.UseEngineAwareness && TryIsEngineOn(car, out var onNow) && onNow)
            {
                var wanted = ResolveActivePresetForCar(car);
                ApplyPresetKey(car, wanted);
                _engineOn[car] = true;
                player.ChatMessage($"<color=#9cf>BetterCars</color>: Engine detected ON — applied <b>{Title(wanted)}</b>.");
                return;
            }


            // If they have a saved choice, just tell them we’ll apply it once the engine is started.
            if (playerPresetChoice.TryGetValue(player.userID, out var key) && config.Presets.ContainsKey(key))
            {
                if (!config.UseEngineAwareness)
                {
                    // If engine awareness is disabled, apply instantly (old behavior).
                    TryApplyToCar(car, PrepareTowAwarePreset(car, config.Presets[key]));
                    player.ChatMessage($"<color=#9cf>BetterCars</color>: Applied <b>{key}</b>.");
                }
                else
                {
                    player.ChatMessage($"<color=#9cf>BetterCars</color>: Engine is off — using <b>{Title(config.EngineOffPresetKey)}</b> until you start it.");
                }
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

        private void EngineWatchTick()
        {
            if (!config.UseEngineAwareness || carPreset.Count == 0) return;

            foreach (var kv in carPreset.ToList())
            {
                var car = kv.Key;
                if (!car || car.IsDestroyed)
                {
                    _engineOn.Remove(car);
                    carPreset.Remove(car);
                    continue;
                }

                // Treat "unknown" as OFF to avoid reapplying the aggressive preset.
                bool isOn = false;
                TryIsEngineOn(car, out isOn);

                bool prev;
                _engineOn.TryGetValue(car, out prev);
                if (prev == isOn) continue;

                _engineOn[car] = isOn;
                ApplyPresetKey(car, isOn ? ResolveActivePresetForCar(car) : config.EngineOffPresetKey);
            }
        }


        private bool TryIsEngineOn(ModularCar car, out bool isOn)
        {
            isOn = false;
            if (!car || car.IsDestroyed) return false;

            // If we learned a working probe, use it
            if (_engineProbe != null)
            {
                try { isOn = _engineProbe(car); return true; }
                catch { /* fall through to rebuild */ }
            }

            // Try to (re)build a probe
            var probe = BuildEngineProbe(car);
            if (probe != null)
            {
                _engineProbe = probe;
                try { isOn = _engineProbe(car); return true; }
                catch { /* ignore */ }
            }

            return false;
        }

        private System.Func<ModularCar, bool> BuildEngineProbe(ModularCar sample)
        {
            const System.Reflection.BindingFlags BF =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            // --- (A) direct members on ModularCar -----------------------------------
            var carType = sample.GetType();
            string[] names = { "IsEngineOn", "IsOn", "EngineOn", "isEngineOn", "isOn", "IsRunning" };

            // property / field
            foreach (var n in names)
            {
                var p = carType.GetProperty(n, BF);
                if (p != null && p.PropertyType == typeof(bool))
                    return (ModularCar c) => (bool)p.GetValue(c);

                var f = carType.GetField(n, BF);
                if (f != null && f.FieldType == typeof(bool))
                    return (ModularCar c) => (bool)f.GetValue(c);
            }
            // zero-arg method returning bool
            foreach (var n in names)
            {
                var m = carType.GetMethod(n, BF, null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType == typeof(bool))
                    return (ModularCar c) => (bool)m.Invoke(c, null);
            }

            // --- (B) find a controller component and read it -------------------------
            // Prefer *EngineController* types to avoid grabbing cosmetic "Engine..." bits.
            Component engineCompOnSample = null;
            try
            {
                engineCompOnSample = sample.GetComponentsInChildren<Component>(true)
                    .FirstOrDefault(c =>
                    {
                        if (c == null) return false;
                        var n = c.GetType().Name;
                        return n.IndexOf("EngineController", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("VehicleEngineController", StringComparison.OrdinalIgnoreCase) >= 0
                            || (n.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                n.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0);
                    });
            }
            catch { /* ignore */ }

            if (engineCompOnSample != null)
            {
                var et = engineCompOnSample.GetType();

                // property / field
                foreach (var n in names)
                {
                    var p = et.GetProperty(n, BF);
                    if (p != null && p.PropertyType == typeof(bool))
                    {
                        return (ModularCar c) =>
                        {
                            var comp = c ? c.GetComponentsInChildren(et, true).FirstOrDefault() : null;
                            return comp != null && (bool)p.GetValue(comp);
                        };
                    }
                    var f = et.GetField(n, BF);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        return (ModularCar c) =>
                        {
                            var comp = c ? c.GetComponentsInChildren(et, true).FirstOrDefault() : null;
                            return comp != null && (bool)f.GetValue(comp);
                        };
                    }
                }

                // zero-arg method returning bool
                foreach (var n in names)
                {
                    var m = et.GetMethod(n, BF, null, Type.EmptyTypes, null);
                    if (m != null && m.ReturnType == typeof(bool))
                    {
                        return (ModularCar c) =>
                        {
                            var comp = c ? c.GetComponentsInChildren(et, true).FirstOrDefault() : null;
                            return comp != null && (bool)m.Invoke(comp, null);
                        };
                    }
                }
            }

            // --- (C) last chance: look for a car field/property that *is* a controller,
            // then query IsOn/IsRunning on that object.
            var engineHolder =
                carType.GetProperties(BF).FirstOrDefault(pi => pi.PropertyType.Name.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0
                                                            && pi.PropertyType.Name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? carType.GetProperties(BF).FirstOrDefault(pi => pi.PropertyType.Name.IndexOf("VehicleEngineController", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? null;

            if (engineHolder != null)
            {
                var ct = engineHolder.PropertyType;
                var pBool = new[] { "IsOn", "IsRunning", "EngineOn" }
                    .Select(n => ct.GetProperty(n, BF)).FirstOrDefault(pi => pi != null && pi.PropertyType == typeof(bool));
                var fBool = pBool == null ? new[] { "isOn", "running" }
                    .Select(n => ct.GetField(n, BF)).FirstOrDefault(fi => fi != null && fi.FieldType == typeof(bool)) : null;
                var mBool = (pBool == null && fBool == null)
                    ? new[] { "IsOn", "IsRunning", "EngineOn" }
                        .Select(n => ct.GetMethod(n, BF, null, Type.EmptyTypes, null))
                        .FirstOrDefault(mi => mi != null && mi.ReturnType == typeof(bool))
                    : null;

                if (pBool != null)
                    return (ModularCar c) => { var ctrl = engineHolder.GetValue(c); return ctrl != null && (bool)pBool.GetValue(ctrl); };
                if (fBool != null)
                    return (ModularCar c) => { var ctrl = engineHolder.GetValue(c); return ctrl != null && (bool)fBool.GetValue(ctrl); };
                if (mBool != null)
                    return (ModularCar c) => { var ctrl = engineHolder.GetValue(c); return ctrl != null && (bool)mBool.Invoke(ctrl, null); };
            }

            // Give up – caller will treat "unknown" as OFF for safety.
            return null;
        }



        private void OnEntityDismounted(BaseMountable mountable, BasePlayer player)
        {
            if (!config.UseEngineAwareness) return;
            if (mountable == null || player == null || !player.IsConnected) return;

            var car = mountable.GetComponentInParent<ModularCar>();
            if (car == null) return;

            // If driver seat is now empty, go safe next tick
            if (car.GetDriver() != null) return;

            NextTick(() =>
            {
                if (!car || car.IsDestroyed) return;
                ApplyPresetKey(car, config.EngineOffPresetKey);
            });
        }

        #endregion

        #region Chat/Console
        [Command("carhandling")]
        private void CmdCarHandling(IPlayer iplayer, string cmd, string[] args)
        {
            var bp = iplayer.Object as BasePlayer;
            if (bp == null || !bp.isActiveAndEnabled || !bp.IsConnected) return;
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
            var car = bp.GetMountedVehicle() as ModularCar
                    ?? bp.GetParentEntity() as ModularCar
                    ?? bp.GetComponentInParent<ModularCar>();

            // >>> REPLACE your old 'mode' block with this one <<<
            if (sub == "mode" && args.Length >= 2)
            {
                var key = args[1].ToLowerInvariant();
                if (!config.Presets.TryGetValue(key, out var preset))
                {
                    iplayer.Reply($"Unknown preset '{key}'.");
                    return;
                }
                playerPresetChoice[bp.userID] = key;

                if (car != null)
                {
                    // Only apply if engine is ON; otherwise just queue it and keep safe physics
                    if (!config.UseEngineAwareness || (TryIsEngineOn(car, out var on) && on))
                    {
                        TryApplyToCar(car, PrepareTowAwarePreset(car, preset));
                        iplayer.Reply($"Applied <b>{key}</b> to current car.");
                    }
                    else
                    {
                        ApplyPresetKey(car, config.EngineOffPresetKey);
                        iplayer.Reply($"Queued <b>{key}</b>. Engine is off — using <b>{config.EngineOffPresetKey}</b> until you start it.");
                    }
                }
                else
                {
                    iplayer.Reply($"Selected <b>{key}</b>. It’ll apply when you enter a car and start the engine.");
                }
                return;
            }

            if (sub == "set" && args.Length >= 3)
            {
                if (car == null)
                {
                    iplayer.Reply("You must be driving a modular car to use /carhandling set.");
                    return;
                }
                if (!carPreset.TryGetValue(car, out var p))
                    p = GetDefaultPresetForCar(car).Clone();

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
                    default:
                        iplayer.Reply("Unknown param. Use: frontSpring, rearSpring, frontDamper, rearDamper, susp, target, fwdFric, sideFric");
                        return;
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

            // Apply preset by key (respects TowAware)
            private void ApplyPresetKey(ModularCar car, string key)
            {
                if (!car || car.IsDestroyed) return;
                if (!config.Presets.TryGetValue(key, out var p)) p = new Preset();
                TryApplyToCar(car, PrepareTowAwarePreset(car, p));
            }

            // Resolve which preset to use when engine turns ON.
            // Uses the current driver's saved choice if available; else default.
            private string ResolveActivePresetForCar(ModularCar car)
            {
                try
                {
                    var driver = car?.GetDriver();
                    if (driver != null &&
                        playerPresetChoice.TryGetValue(driver.userID, out var key) &&
                        config.Presets.ContainsKey(key))
                        return key;
                }
                catch { /* ignore */ }

                return string.IsNullOrEmpty(config.DefaultPreset) ? "sport" : config.DefaultPreset;
            }

        #endregion
    }
}
