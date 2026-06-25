using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Barotrauma;

namespace ItemOptimizerMod
{
    public enum ItemRuleAction
    {
        Skip,
        Throttle
    }

    public class ItemRule
    {
        public string Identifier = "";
        public ItemRuleAction Action = ItemRuleAction.Skip;
        public int SkipFrames = 3;
        public string Condition = "always"; // "always", "coldStorage", "notInActiveUse"
    }

    /// <summary>
    /// Per-mod optimization profile: tier base skip frames + intensity slider.
    /// Intensity 0→use tier bases as-is, 1→all tiers converge to MaxSkip.
    /// </summary>
    public class ModOptProfile
    {
        public int[] TierBases = { 1, 3, 5, 8 };
        public float Intensity = 0f;  // 0~1

        public const int MaxSkip = 15;

        public int GetEffectiveSkip(int tier)
        {
            int baseVal = TierBases[tier];
            return baseVal + (int)Math.Round((MaxSkip - baseVal) * Intensity);
        }
    }

    static class OptimizerConfig
    {
        public static bool EnableColdStorageSkip = true;
        public static bool EnableGroundItemThrottle = true;
        public static int GroundItemSkipFrames = 3;
        public static bool EnableMotionSensorRewrite = true;
        public static bool EnableWaterDetectorRewrite = true;
        public static bool EnableRelayRewrite = false;
        public static bool EnablePowerTransferRewrite = false;
        public static bool EnablePowerContainerRewrite = false;
        public static bool EnableWireSkip = false;
        public static bool EnableHasStatusTagCache = false;
        public static bool EnableHullSpatialIndex = true;   // Hull-based spatial pre-filtering for MotionSensor

        // ── Character optimization ──
        public static bool EnableAnimLOD = false;
        public static bool EnableCharacterStagger = false;
        public static int CharacterStaggerGroups = 4;
        public static bool EnableLadderFix = true;          // fix ladder climbing desync (client-only)
        public static bool EnablePlatformFix = true;        // fix IgnorePlatforms desync (client-only)

        public static int MotionSensorSkipFrames = 9;
        public static int WaterDetectorSkipFrames = 9;

        // Proxy item system (batch compute + sync architecture)
        public static bool EnableProxySystem = true;

        // ── Client optimization ──
        public static bool EnableInteractionLabelOpt = true;
        public static int InteractionLabelMaxCount = 50; // 10-200
        public static bool EnableButtonTerminalOpt = false;
        public static bool EnablePumpOpt = true;

        // Misc entity parallelism (Hull/Structure/Gap/Power — safe, no side effects)
        public static bool EnableMiscParallel = true;

        // Signal graph accelerator (0=Off, 1=Accelerate, 2=Aggressive)
        public static int SignalGraphMode = 1;

        // NativeComponent runtime (experimental — default off)
        public static bool EnableNativeRuntime = true;
        public static bool EnableZoneSkip = true;  // Skip Item.Update for items in Dormant/Unloaded zones (requires NativeRuntime)

        // Spike detector (off by default — adds ~1-2ms overhead when enabled)
        public static bool EnableSpikeDetector = false;
        public static float SpikeThresholdMs = 30f;

        // ── Server-side optimizations ──
        public static bool EnableServerHashSetDedup = true;
        public static float MetricSendInterval = 0.5f;
        public static bool AllowClientSync = false;

        // Per-item rules (manual, user-defined)
        public static List<ItemRule> ItemRules = new();

        // Pre-compiled lookup tables for fast per-frame checks
        public static readonly Dictionary<string, ItemRule> RuleLookup = new();

        // ── Whitelist (items that should never be throttled by ModOpt) ──
        public static List<string> Whitelist = new();
        public static HashSet<string> WhitelistLookup = new(StringComparer.Ordinal);

        public static void RebuildWhitelistLookup()
        {
            WhitelistLookup = new HashSet<string>(Whitelist, StringComparer.Ordinal);
        }

        // ── Mod Optimization (tier-based, separate from manual rules) ──
        // Persistence: packageName → ModOptProfile { tierBases[4], intensity }
        public static readonly Dictionary<string, ModOptProfile> ModOptProfiles = new(StringComparer.Ordinal);
        // Runtime flat lookup: identifier → skipFrames (built from ModOptProfiles + prefab classification)
        public static volatile Dictionary<string, int> ModOptLookup = new(StringComparer.Ordinal);

        /// <summary>
        /// Classify an ItemPrefab into activity tier (0=Critical,1=Active,2=Moderate,3=Static)
        /// using XML pattern detection. Shared between SettingsPanel and BuildModOptLookup.
        /// </summary>
        public static int ClassifyItemPrefab(ItemPrefab prefab)
        {
            var configEl = prefab.ConfigElement;
            if (configEl == null) return 3; // Static

            bool hasStatusHUD = false;
            bool hasAffliction = false;
            bool hasConditional = false;
            int statusEffectCount = 0;

            foreach (var compEl in configEl.Elements())
            {
                if (compEl.Name.ToString().Equals("StatusHUD", StringComparison.OrdinalIgnoreCase))
                    hasStatusHUD = true;

                foreach (var subEl in compEl.Elements())
                {
                    var subName = subEl.Name.ToString();
                    if (subName.Equals("statuseffect", StringComparison.OrdinalIgnoreCase))
                    {
                        statusEffectCount++;
                        var typeAttr = subEl.GetAttributeString("type", "OnActive");
                        if (typeAttr.Equals("OnActive", StringComparison.OrdinalIgnoreCase)
                            || typeAttr.Equals("Always", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var seChild in subEl.Elements())
                            {
                                if (seChild.Name.ToString().Equals("Affliction", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasAffliction = true;
                                    break;
                                }
                            }
                        }
                    }
                    else if (subName.Equals("activeconditional", StringComparison.OrdinalIgnoreCase)
                          || subName.Equals("isactiveconditional", StringComparison.OrdinalIgnoreCase)
                          || subName.Equals("isactive", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConditional = true;
                    }
                }
            }

            bool hasMultiSE = statusEffectCount > 5;

            if (hasStatusHUD) return 0; // Critical
            if (hasMultiSE || (hasAffliction && hasConditional)) return 1; // Active
            if (hasAffliction || hasConditional || statusEffectCount > 2) return 2; // Moderate
            return 3; // Static
        }

        /// <summary>
        /// Rebuild ModOptLookup from ModOptProfiles by scanning all non-vanilla prefabs.
        /// Only items whose package is in ModOptProfiles get an entry.
        /// Items with effective skipFrames &lt;= 1 are excluded (no throttle needed).
        /// </summary>
        public static void BuildModOptLookup()
        {
            var newLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            if (ModOptProfiles.Count == 0)
            {
                ModOptLookup = newLookup;
                return;
            }

            foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
            {
                var pkg = prefab.ContentPackage;
                if (pkg == null || pkg == ContentPackageManager.VanillaCorePackage) continue;

                if (!ModOptProfiles.TryGetValue(pkg.Name, out var profile)) continue;

                int tier = ClassifyItemPrefab(prefab);
                int skip = profile.GetEffectiveSkip(tier);
                if (skip <= 1) continue; // no throttle

                newLookup[prefab.Identifier.Value] = skip;
            }

            ModOptLookup = newLookup; // atomic reference swap
        }

        // ── Profile System (per-mod-set persistence) ──

        private static string _profileHash;

        internal static string GetModSetHash()
        {
            if (_profileHash != null) return _profileHash;
            var names = new List<string>();
            foreach (var pkg in ContentPackageManager.EnabledPackages.All)
            {
                if (pkg == ContentPackageManager.VanillaCorePackage) continue;
                names.Add(pkg.Name);
            }
            names.Sort(StringComparer.Ordinal);
            uint hash = 2166136261;
            foreach (var name in names)
                foreach (char c in name)
                    hash = (hash ^ c) * 16777619;
            _profileHash = hash.ToString("x8");
            return _profileHash;
        }

        private static string GetProfileDir()
        {
            return Path.Combine(ModPaths.UserDataDir, "Optimizerlist");
        }

        internal static string GetProfilePath()
        {
            return Path.Combine(GetProfileDir(), $"profile_{GetModSetHash()}.xml");
        }


        public static void SaveProfile()
        {
            try
            {
                var dir = GetProfileDir();
                Directory.CreateDirectory(dir);

                var modOptElement = SerializeModOpt();
                var rulesElement = SerializeItemRules();
                var whitelistElement = SerializeWhitelist();

                var modsElement = new XElement("EnabledMods");
                foreach (var pkg in ContentPackageManager.EnabledPackages.All)
                {
                    if (pkg == ContentPackageManager.VanillaCorePackage) continue;
                    modsElement.Add(new XElement("Mod", new XAttribute("name", pkg.Name)));
                }

                var doc = new XDocument(
                    new XElement("OptimizerProfile",
                        new XAttribute("hash", GetModSetHash()),
                        modsElement,
                        modOptElement,
                        rulesElement,
                        whitelistElement));
                doc.Save(GetProfilePath());
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to save profile: {e.Message}");
            }
        }

        public static void LoadProfile()
        {
            try
            {
                var path = GetProfilePath();
                if (!File.Exists(path)) return;

                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return;

                DeserializeModOpt(root);
                DeserializeItemRules(root);
                DeserializeWhitelist(root);

                BuildLookupTables();
                BuildModOptLookup();
                RebuildWhitelistLookup();
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to load profile: {e.Message}");
            }
        }

        /// <summary>Auto-save: called after any optimization change.</summary>
        public static void AutoSave()
        {
            Save();
            SaveProfile();
        }

        private static string _configPath;

        private static string GetConfigPath()
        {
            if (_configPath != null) return _configPath;
            _configPath = ModPaths.ResolveUserData("ItemOptimizer_config.xml");
            return _configPath;
        }

        /// <summary>
        /// One-time migration: copy config and profiles from mod directory to user data directory.
        /// This ensures settings survive Steam Workshop mod updates.
        /// </summary>
        private static void MigrateFromModDir()
        {
            try
            {
                var newConfigPath = GetConfigPath();
                if (!File.Exists(newConfigPath))
                {
                    var oldConfigPath = ModPaths.Resolve("ItemOptimizer_config.xml");
                    if (File.Exists(oldConfigPath))
                        File.Copy(oldConfigPath, newConfigPath);
                }

                var newProfileDir = GetProfileDir();
                if (!Directory.Exists(newProfileDir))
                {
                    var oldProfileDir = Path.Combine(ModPaths.ModDir, "Optimizerlist");
                    if (Directory.Exists(oldProfileDir))
                    {
                        Directory.CreateDirectory(newProfileDir);
                        foreach (var f in Directory.GetFiles(oldProfileDir, "*.xml"))
                            File.Copy(f, Path.Combine(newProfileDir, Path.GetFileName(f)), false);
                    }
                }
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Config migration failed: {e.Message}");
            }
        }

        /// <summary>Describes one config field for data-driven Load/Save.</summary>
        private abstract class ConfigField
        {
            public readonly string XmlName;
            protected ConfigField(string xmlName) => XmlName = xmlName;
            public abstract void ReadFrom(XElement root);
            public abstract XElement WriteTo();
        }

        private sealed class BoolField : ConfigField
        {
            private readonly Func<bool> _get; private readonly Action<bool> _set; private readonly bool _def;
            public BoolField(string xml, Func<bool> get, Action<bool> set, bool def) : base(xml) { _get = get; _set = set; _def = def; }
            public override void ReadFrom(XElement root)
            {
                var el = root.Element(XmlName);
                if (el != null) _set(ParseBool(el.Attribute("enabled")?.Value, _def));
            }
            public override XElement WriteTo() => new XElement(XmlName, new XAttribute("enabled", _get()));
        }

        private sealed class IntField : ConfigField
        {
            private readonly Func<int> _get; private readonly Action<int> _set; private readonly int _def, _min, _max; private readonly string _attr;
            public IntField(string xml, Func<int> get, Action<int> set, int def, int min, int max, string attr = "skipFrames") : base(xml) { _get = get; _set = set; _def = def; _min = min; _max = max; _attr = attr; }
            public override void ReadFrom(XElement root)
            {
                var el = root.Element(XmlName);
                if (el != null) _set(ParseInt(el.Attribute(_attr)?.Value, _def, _min, _max));
            }
            public override XElement WriteTo() => new XElement(XmlName, new XAttribute(_attr, _get()));
        }

        /// <summary>Custom compound field with arbitrary read/write logic.</summary>
        private sealed class CompoundField : ConfigField
        {
            private readonly Action<XElement> _read; private readonly Func<XElement> _write;
            public CompoundField(string xml, Action<XElement> read, Func<XElement> write) : base(xml) { _read = read; _write = write; }
            public override void ReadFrom(XElement root)
            {
                var el = root.Element(XmlName);
                if (el != null) _read(el);
            }
            public override XElement WriteTo() => _write();
        }

        private static readonly ConfigField[] _configFields = BuildFieldList();

        private static ConfigField[] BuildFieldList()
        {
            return new ConfigField[]
            {
                new BoolField("ColdStorageSkip",         () => EnableColdStorageSkip,        v => EnableColdStorageSkip = v,        true),
                new CompoundField("GroundItemThrottle",  el => {
                    EnableGroundItemThrottle = ParseBool(el.Attribute("enabled")?.Value, true);
                    GroundItemSkipFrames = ParseInt(el.Attribute("skipFrames")?.Value, 3, 1, 30);
                }, () => new XElement("GroundItemThrottle",
                    new XAttribute("enabled", EnableGroundItemThrottle),
                    new XAttribute("skipFrames", GroundItemSkipFrames))),
                new IntField("MotionSensorThrottle",     () => MotionSensorSkipFrames,        v => MotionSensorSkipFrames = v,        3, 1, 30),
                new IntField("WaterDetectorThrottle",    () => WaterDetectorSkipFrames,       v => WaterDetectorSkipFrames = v,       3, 1, 30),
                new BoolField("HasStatusTagCache",        () => EnableHasStatusTagCache,       v => EnableHasStatusTagCache = v,       true),
                new BoolField("HullSpatialIndex",         () => EnableHullSpatialIndex,        v => EnableHullSpatialIndex = v,        true),
                new BoolField("WireSkip",                 () => EnableWireSkip,                v => EnableWireSkip = v,                false),
                new BoolField("MotionSensorRewrite",      () => EnableMotionSensorRewrite,     v => EnableMotionSensorRewrite = v,     true),
                new BoolField("WaterDetectorRewrite",     () => EnableWaterDetectorRewrite,    v => EnableWaterDetectorRewrite = v,    true),
                new BoolField("RelayRewrite",             () => EnableRelayRewrite,            v => EnableRelayRewrite = v,            true),
                new BoolField("PowerTransferRewrite",     () => EnablePowerTransferRewrite,    v => EnablePowerTransferRewrite = v,    true),
                new BoolField("PowerContainerRewrite",    () => EnablePowerContainerRewrite,   v => EnablePowerContainerRewrite = v,   true),
                new BoolField("AnimLOD",                  () => EnableAnimLOD,                 v => EnableAnimLOD = v,                 true),
                new CompoundField("CharacterStagger",    el => {
                    EnableCharacterStagger = ParseBool(el.Attribute("enabled")?.Value, false);
                    CharacterStaggerGroups = ParseInt(el.Attribute("groups")?.Value, 3, 2, 8);
                }, () => new XElement("CharacterStagger",
                    new XAttribute("enabled", EnableCharacterStagger),
                    new XAttribute("groups", CharacterStaggerGroups))),
                new BoolField("LadderFix",                () => EnableLadderFix,               v => EnableLadderFix = v,               true),
                new BoolField("PlatformFix",              () => EnablePlatformFix,             v => EnablePlatformFix = v,             true),
                new CompoundField("SpikeDetector",       el => {
                    EnableSpikeDetector = ParseBool(el.Attribute("enabled")?.Value, false);
                    SpikeThresholdMs = ParseFloat(el.Attribute("thresholdMs")?.Value, 30f, 5f, 1000f);
                }, () => new XElement("SpikeDetector",
                    new XAttribute("enabled", EnableSpikeDetector),
                    new XAttribute("thresholdMs", SpikeThresholdMs))),
                new CompoundField("SignalGraphAccel",    el => {
                    SignalGraphMode = ParseInt(el.Attribute("mode")?.Value, 0, 0, 2);
                }, () => new XElement("SignalGraphAccel", new XAttribute("mode", SignalGraphMode))),
                new CompoundField("NativeRuntime",       el => {
                    EnableNativeRuntime = ParseBool(el.Attribute("enabled")?.Value, true);
                    EnableZoneSkip = ParseBool(el.Attribute("zoneSkip")?.Value, true);
                }, () => new XElement("NativeRuntime",
                    new XAttribute("enabled", EnableNativeRuntime),
                    new XAttribute("zoneSkip", EnableZoneSkip))),
                new CompoundField("ProxySystem",         el => {
                    EnableProxySystem = bool.TryParse(el.Attribute("enabled")?.Value, out var v) ? v : true;
                }, () => new XElement("ProxySystem", new XAttribute("enabled", EnableProxySystem))),
                new CompoundField("InteractionLabel",    el => {
                    EnableInteractionLabelOpt = ParseBool(el.Attribute("enabled")?.Value, true);
                    InteractionLabelMaxCount = ParseInt(el.Attribute("maxCount")?.Value, 50, 10, 200);
                }, () => new XElement("InteractionLabel",
                    new XAttribute("enabled", EnableInteractionLabelOpt),
                    new XAttribute("maxCount", InteractionLabelMaxCount))),
                new BoolField("ButtonTerminalOpt",        () => EnableButtonTerminalOpt,       v => EnableButtonTerminalOpt = v,       true),
                new BoolField("PumpOpt",                  () => EnablePumpOpt,                 v => EnablePumpOpt = v,                 true),
                new BoolField("MiscParallel",              () => EnableMiscParallel,            v => EnableMiscParallel = v,             true),
                new CompoundField("ServerOptimization",  el => {
                    EnableServerHashSetDedup = ParseBool(el.Attribute("enabled")?.Value, true);
                    MetricSendInterval = ParseFloat(el.Attribute("metricInterval")?.Value, 0.5f, 0.1f, 5f);
                    AllowClientSync = ParseBool(el.Attribute("allowClientSync")?.Value, false);
                }, () => new XElement("ServerOptimization",
                    new XAttribute("enabled", EnableServerHashSetDedup),
                    new XAttribute("metricInterval", MetricSendInterval),
                    new XAttribute("allowClientSync", AllowClientSync))),
            };
        }

        public static void Load()
        {
            MigrateFromModDir();
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path))
                {
                    Save();
                    return;
                }

                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return;

                foreach (var field in _configFields)
                    field.ReadFrom(root);

                DeserializeItemRules(root);
                DeserializeModOpt(root);
                DeserializeWhitelist(root);

                // Load profile (overrides ItemRules + ModOpt + Whitelist if profile exists)
                LoadProfile();
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to load config: {e.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var children = new List<XObject>(_configFields.Length + 3);
                foreach (var field in _configFields)
                    children.Add(field.WriteTo());
                children.Add(SerializeItemRules());
                children.Add(SerializeModOpt());
                children.Add(SerializeWhitelist());

                var doc = new XDocument(new XElement("ItemOptimizerConfig", children.ToArray()));
                doc.Save(GetConfigPath());
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to save config: {e.Message}");
            }
        }

        public static void BuildLookupTables()
        {
            RuleLookup.Clear();
            foreach (var rule in ItemRules)
            {
                if (!string.IsNullOrWhiteSpace(rule.Identifier))
                    RuleLookup[rule.Identifier] = rule;
            }
        }

        // ── Serialization Helpers ──

        private static XElement SerializeItemRules()
        {
            var el = new XElement("ItemRules");
            foreach (var rule in ItemRules)
            {
                if (string.IsNullOrWhiteSpace(rule.Identifier)) continue;
                el.Add(new XElement("Rule",
                    new XAttribute("identifier", rule.Identifier),
                    new XAttribute("action", rule.Action.ToString()),
                    new XAttribute("skipFrames", rule.SkipFrames),
                    new XAttribute("condition", rule.Condition)));
            }
            return el;
        }

        private static XElement SerializeModOpt()
        {
            var el = new XElement("ModOptimization");
            foreach (var kv in ModOptProfiles)
            {
                var profile = kv.Value;
                el.Add(new XElement("Mod",
                    new XAttribute("name", kv.Key),
                    new XAttribute("critical", profile.TierBases[0]),
                    new XAttribute("active", profile.TierBases[1]),
                    new XAttribute("moderate", profile.TierBases[2]),
                    new XAttribute("static", profile.TierBases[3]),
                    new XAttribute("intensity", profile.Intensity.ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture))));
            }
            return el;
        }

        private static void DeserializeItemRules(XElement root)
        {
            ItemRules.Clear();
            var rulesElement = root.Element("ItemRules");
            if (rulesElement == null) return;
            foreach (var ruleEl in rulesElement.Elements("Rule"))
            {
                var rule = new ItemRule
                {
                    Identifier = ruleEl.Attribute("identifier")?.Value ?? "",
                    SkipFrames = ParseInt(ruleEl.Attribute("skipFrames")?.Value, 3, 1, 30),
                    Condition = ruleEl.Attribute("condition")?.Value ?? "always"
                };
                var actionStr = ruleEl.Attribute("action")?.Value ?? "Skip";
                rule.Action = actionStr.Equals("Throttle", StringComparison.OrdinalIgnoreCase)
                    ? ItemRuleAction.Throttle
                    : ItemRuleAction.Skip;
                if (!string.IsNullOrWhiteSpace(rule.Identifier))
                    ItemRules.Add(rule);
            }
        }

        private static void DeserializeModOpt(XElement root)
        {
            ModOptProfiles.Clear();
            var modOptElement = root.Element("ModOptimization");
            if (modOptElement == null) return;
            foreach (var modEl in modOptElement.Elements("Mod"))
            {
                var name = modEl.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profile = new ModOptProfile
                {
                    TierBases = new int[]
                    {
                        ParseInt(modEl.Attribute("critical")?.Value, 1, 1, 15),
                        ParseInt(modEl.Attribute("active")?.Value, 3, 1, 15),
                        ParseInt(modEl.Attribute("moderate")?.Value, 5, 1, 15),
                        ParseInt(modEl.Attribute("static")?.Value, 8, 1, 15)
                    },
                    // Backward compat: old configs have no intensity attribute → defaults to 0
                    Intensity = ParseFloat(modEl.Attribute("intensity")?.Value, 0f, 0f, 1f)
                };
                ModOptProfiles[name] = profile;
            }
        }

        private static XElement SerializeWhitelist()
        {
            var el = new XElement("Whitelist");
            foreach (var id in Whitelist)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                el.Add(new XElement("Item", new XAttribute("identifier", id)));
            }
            return el;
        }

        private static void DeserializeWhitelist(XElement root)
        {
            Whitelist.Clear();
            var whitelistElement = root.Element("Whitelist");
            if (whitelistElement == null) return;
            foreach (var itemEl in whitelistElement.Elements("Item"))
            {
                var id = itemEl.Attribute("identifier")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                    Whitelist.Add(id);
            }
            RebuildWhitelistLookup();
        }

        private static bool ParseBool(string val, bool def)
        {
            if (string.IsNullOrEmpty(val)) return def;
            return bool.TryParse(val, out var result) ? result : def;
        }

        private static int ParseInt(string val, int def, int min, int max)
        {
            if (string.IsNullOrEmpty(val)) return def;
            if (!int.TryParse(val, out var result)) return def;
            return Math.Clamp(result, min, max);
        }

        private static float ParseFloat(string val, float def, float min, float max)
        {
            if (string.IsNullOrEmpty(val)) return def;
            if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var result)) return def;
            return Math.Clamp(result, min, max);
        }
    }
}
