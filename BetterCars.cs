// ... (file header unchanged)
    public class CarHandlingPlus : CovalencePlugin
    {
        private const string PERM_USE = "carhandlingplus.use";

        #region Config
        private ConfigData config;

        private class Preset
        {
            // (unchanged)
        }

        private class ConfigData
        {
            public string DefaultPreset = "sport";
            public Dictionary<string, Preset> Presets = new Dictionary<string, Preset>
            {
                // (same presets as before)
            };

            // TowCars auto-bias (unchanged)
            public bool TowAware = true;
            public float TowExtraRearSpring = 4000f;
            public float TowExtraRearDamper = 400f;

            // NEW: middle-click switching
            public bool EnableMiddleClickSwitch = true;
            public float MiddleClickCooldown = 0.35f; // seconds
            public string[] ModeOrder = new[] { "sport", "offroad", "drift", "tow", "stock" };

            // UX
            public bool ShowToast = true;
            public bool ShowChat  = true;
        }
        #endregion

        #region Data & State
        private readonly Dictionary<ulong, string> playerPresetChoice = new Dictionary<ulong, string>();
        private readonly Dictionary<ModularCar, Preset> carPreset = new Dictionary<ModularCar, Preset>();

        // NEW: debounce middle-click per player
        private readonly Dictionary<ulong, float> lastSwitchAt = new Dictionary<ulong, float>();
        #endregion

        #region Hooks
        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
            config = Config.ReadObject<ConfigData>();
            if (config.Presets == null || config.Presets.Count == 0) config = new ConfigData();
            if (config.ModeOrder == null || config.ModeOrder.Length == 0)
                config.ModeOrder = new[] { "sport", "offroad", "drift", "tow", "stock" };
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => config = new ConfigData();

        private void OnServerInitialized()
        {
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

        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            var car = mountable?.GetComponentInParent<ModularCar>();
            if (car == null) return;

            if (playerPresetChoice.TryGetValue(player.userID, out var key) && config.Presets.TryGetValue(key, out var preset))
            {
                TryApplyToCar(car, PrepareTowAwarePreset(car, preset));
                Notify(player, $"Applied <b>{key}</b>.");
            }
        }

        // NEW: handle middle-mouse mode switching
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!config.EnableMiddleClickSwitch || player == null || input == null) return;
            if (!player.IsConnected) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;

            // Only when driving a modular car
            var car = player.GetMountedVehicle() as ModularCar ?? player.GetParentEntity() as ModularCar;
            if (car == null) return;

            // Middle mouse is FIRE_THIRD in Rust’s BUTTON enum
            if (!input.WasJustPressed(BUTTON.FIRE_THIRD)) return;

            float now = Time.realtimeSinceStartup;
            if (lastSwitchAt.TryGetValue(player.userID, out var last) && (now - last) < Mathf.Max(0.05f, config.MiddleClickCooldown))
                return;
            lastSwitchAt[player.userID] = now;

            // Determine next mode
            string currentKey = GetPlayerActiveKey(player, fallbackToConfigDefault: true);
            string nextKey = NextModeKey(currentKey);
            if (!config.Presets.TryGetValue(nextKey, out var nextPreset)) return;

            // Persist player choice and apply
            playerPresetChoice[player.userID] = nextKey;
            TryApplyToCar(car, PrepareTowAwarePreset(car, nextPreset));
            Notify(player, $"Drive mode: <b>{Nice(nextKey)}</b>");
        }
        #endregion

        #region Core Apply (unchanged methods trimmed for brevity)
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

            var midZ = wheels.Average(w => w.transform.localPosition.z);

            foreach (var wc in wheels)
            {
                bool isFront = wc.transform.localPosition.z < midZ;

                wc.suspensionDistance = preset.SuspDistance;

                var spring = wc.suspensionSpring;
                spring.targetPosition = Mathf.Clamp01(preset.TargetPos);
                spring.spring = isFront ? preset.FrontSpring : preset.RearSpring;
                spring.damper = isFront ? preset.FrontDamper : preset.RearDamper;
                wc.suspensionSpring = spring;

                var fwd = wc.forwardFriction; fwd.stiffness = preset.ForwardStiffness; wc.forwardFriction = fwd;
                var side = wc.sidewaysFriction; side.stiffness = preset.SidewaysStiffness; wc.sidewaysFriction = side;
            }

            var helper = car.GetComponent<CHP_AntiRollHelper>() ?? car.gameObject.AddComponent<CHP_AntiRollHelper>();
            helper.Apply(car, preset);

            carPreset[car] = preset;
        }
        #endregion

        #region Chat/Console (minor tweaks)
        [Command("carhandling")]
        private void CmdCarHandling(IPlayer iplayer, string cmd, string[] args)
        {
            var bp = iplayer.Object as BasePlayer;
            if (bp == null || !bp.IsConnected) return;
            if (!iplayer.HasPermission(PERM_USE))
            {
                iplayer.Reply("You lack permission (carhandlingplus.use).");
                return;
            }

            if (args.Length == 0)
            {
                iplayer.Reply("CarHandling+ — Usage:\n" +
                    "• Middle-click while driving: cycle modes\n" +
                    "• /carhandling mode <sport|offroad|drift|tow|stock>\n" +
                    "• /carhandling set <param> <value>\n" +
                    "Params: frontSpring, rearSpring, frontDamper, rearDamper, susp, target, fwdFric, sideFric");
                return;
            }

            // (rest unchanged)
            // ...
        }

        [ConsoleCommand("carhandling.globalmode")]
        private void CcmdGlobal(ConsoleSystem.Arg arg)
        {
            // (unchanged)
        }
        #endregion

        #region Helpers
        private string NextModeKey(string current)
        {
            var order = config.ModeOrder;
            if (order == null || order.Length == 0) order = new[] { "sport", "offroad", "drift", "tow", "stock" };
            int idx = Array.FindIndex(order, s => string.Equals(s, current, StringComparison.OrdinalIgnoreCase));
            int next = (idx < 0 ? 0 : (idx + 1) % order.Length);
            // Skip unknown keys
            for (int i = 0; i < order.Length; i++)
            {
                var k = order[(next + i) % order.Length];
                if (config.Presets.ContainsKey(k)) return k;
            }
            return "sport";
        }

        private string GetPlayerActiveKey(BasePlayer bp, bool fallbackToConfigDefault)
        {
            if (playerPresetChoice.TryGetValue(bp.userID, out var k) && config.Presets.ContainsKey(k)) return k;
            return fallbackToConfigDefault ? config.DefaultPreset : "sport";
        }

        private string Nice(string key) => char.ToUpperInvariant(key[0]) + key.Substring(1);

        private void Notify(BasePlayer p, string msg)
        {
            if (config.ShowToast)
                p.Command("gametip.showtoast", "CarHandling+", msg); // harmless if client ignores
            if (config.ShowChat)
                p.ChatMessage($"<color=#9cf>CarHandling+</color>: {msg}");
        }
        #endregion

        #region Anti-roll helper (unchanged)
        private class CHP_AntiRollHelper : FacepunchBehaviour
        {
            // (same as before)
        }
        #endregion
    }
// ... end file
