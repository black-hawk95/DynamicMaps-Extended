using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMapsExtended
{
    internal sealed class ConfigurationManagerAttributes
    {
        public int Order;
    }

    internal static class FactionColours
    {
        internal static ConfigEntry<bool> Enabled;
        internal static readonly Dictionary<string, ConfigEntry<Color>> Colours = new();
        private static readonly Dictionary<int, string> Roles = new();
        private static readonly Dictionary<int, ConfigEntry<string>> GroupOverrides = new();
        private static ConfigFile _config;
        internal const string AutomaticGroup = "Automatic";
        internal const string NativeGroup = "DynamicMaps default";
        private static readonly HashSet<int> UntarBossRoles = new();
        private static readonly Dictionary<string, MemberInfo> Members = new();
        private static readonly HashSet<string> Warnings = new();
        private static readonly HashSet<string> Traces = new();
        private static bool _registryRead;
        private static bool _patched;
        private static int _registryCount;
        private static PropertyInfo _imageProperty, _labelProperty;

        internal static void Initialize(ConfigFile config, Harmony harmony)
        {
            _config = config;
            Enabled = config.Bind("Faction Colours", "Enable faction colours", true,
                new ConfigDescription("Colour recognised addon factions. Native tracking and visibility remain controlled by DynamicMaps.", null, new ConfigurationManagerAttributes { Order = 100 }));
            Bind(config, "Black Division", new Color32(119, 119, 119, 255), 90);
            Bind(config, "UNTAR", new Color32(0, 191, 255, 255), 70);
            Bind(config, "RUAF", new Color32(137, 155, 69, 255), 60);
            Bind(config, "Remnant", new Color32(137, 155, 69, 255), 50);
            Bind(config, "ISB", new Color32(26, 188, 156, 255), 40);
            Bind(config, "White Tusks", new Color32(232, 232, 240, 255), 30);
            var migrated = config.Bind("Faction Colours", "Palette version", 0,
                new ConfigDescription("Internal palette migration version. Existing custom colours are preserved.", null, new System.ComponentModel.BrowsableAttribute(false)));
            if (migrated.Value < 1)
            {
                MigrateColour("Black Division", new Color32(24, 24, 24, 255), new Color32(85, 85, 85, 255));
                MigrateColour("UNTAR", new Color32(56, 189, 248, 255), new Color32(0, 191, 255, 255));
                MigrateColour("Remnant", new Color32(224, 68, 68, 255), new Color32(137, 155, 69, 255));
                migrated.Value = 1;
            }
            if (migrated.Value < 2)
            {
                MigrateColour("Black Division", new Color32(24, 24, 24, 255), new Color32(119, 119, 119, 255));
                MigrateColour("Black Division", new Color32(85, 85, 85, 255), new Color32(119, 119, 119, 255));
                migrated.Value = 2;
            }
            Enabled.SettingChanged += SettingsChanged;
            // MoreBotsAPI registrations are prepatch metadata, already available before
            // plugin Awake. Create all F12 groups before Plugin binds the Debug section.
            ReadRegistry();
            try
            {
                var view = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
                var marker = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
                var add = AccessTools.Method(view, "AddPlayerMarker");
                var setter = AccessTools.PropertySetter(marker, "Color");
                _imageProperty = AccessTools.Property(marker, "Image");
                _labelProperty = AccessTools.Property(marker, "Label");
                if (add == null || setter == null || _imageProperty == null || _labelProperty == null ||
                    add.GetParameters().Length != 4 || add.GetParameters()[2].ParameterType != typeof(Color))
                    throw new MissingMemberException("Unsupported DynamicMaps marker API");
                harmony.Patch(add, postfix: new HarmonyMethod(typeof(FactionColours), nameof(MarkerCreated)));
                harmony.Patch(setter, postfix: new HarmonyMethod(typeof(FactionColours), nameof(NativeColourChanged)));
                var boss = AccessTools.Method(AccessTools.TypeByName("DynamicMaps.Utils.GameUtils"), "IsTrackedBoss");
                if (boss == null || boss.ReturnType != typeof(bool) || boss.GetParameters().Length != 1)
                    throw new MissingMemberException("Unsupported DynamicMaps boss classification API");
                harmony.Patch(boss, postfix: new HarmonyMethod(typeof(FactionColours), nameof(TrackedBoss)));
                _patched = true;
                Plugin.Log?.LogInfo("[DMExt:Factions] Marker colour hooks enabled. UNTAR leaders/officers use native boss shapes and visibility.");
            }
            catch (Exception ex) { Warn("initialization", ex); }
        }

        private static void MigrateColour(string faction, Color previous, Color current)
        {
            if (Colours[faction].Value == previous) Colours[faction].Value = current;
        }

        private static bool IsBot(object player)
        {
            if (Read(player, "IsAI") is bool local && local) return true;
            // Fika remote bots have no local AI controller. The inherited public
            // IsObservedAI field carries their host-provided bot identity.
            bool observed = Read(player, "IsObservedAI") is bool remote && remote;
            Trace("identity:" + player.GetType().FullName + ":" + observed,
                $"Marker identity type={player.GetType().FullName}, localAI=False, observedAI={observed}.");
            return observed;
        }

        private static void TrackedBoss(object __0, ref bool __result)
        {
            if (__result || __0 == null) return;
            try
            {
                if (!IsBot(__0)) return;
                var profile = Read(__0, "Profile");
                if (Convert.ToInt32(Read(profile, "Side")) != 4) return;
                ReadRegistry();
                var role = Read(Read(Read(profile, "Info"), "Settings"), "Role");
                if (role == null) return;
                if (UntarBossRoles.Contains(Convert.ToInt32(role)) || FactionRolePolicy.IsUntarBoss(role.ToString()))
                {
                    __result = true;
                    Trace("boss:" + role, $"UNTAR boss role={role}; native boss icon and visibility enabled.");
                }
            }
            catch (Exception ex) { Warn("boss-classification", ex); }
        }

        private static void Bind(ConfigFile config, string name, Color colour, int order)
        {
            var entry = config.Bind("Faction Colours", name, colour,
                new ConfigDescription("One colour for this faction's soldiers, bosses and followers. RGB is used; DynamicMaps controls opacity by floor.", null, new ConfigurationManagerAttributes { Order = order }));
            Colours.Add(name, entry);
            entry.SettingChanged += SettingsChanged;
        }

        private static void SettingsChanged(object sender, EventArgs args)
        {
            FactionMarkerTint.RefreshAll();
            Plugin.Debug("[Factions] Settings changed; refreshed existing faction markers. " + Describe());
        }

        // Cache reflection members by concrete runtime type; Fika implementations may differ.
        private static object Read(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var key = type.AssemblyQualifiedName + "|" + name;
            if (!Members.TryGetValue(key, out var member))
            {
                member = (MemberInfo)type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Members[key] = member;
            }
            return member is PropertyInfo p ? p.GetValue(target) : (member as FieldInfo)?.GetValue(target);
        }

        private static void ReadRegistry()
        {
            if (_registryRead) return;
            _registryRead = true;
            // Prepatch metadata exists on both Fika hosts and joining clients, unlike the
            // server-populated FactionManager. Read only: never register/modify bot roles.
            var manager = AccessTools.TypeByName("MoreBotsAPI.CustomWildSpawnTypeManager");
            if (manager == null)
            {
                Plugin.Debug("[Factions] MoreBotsAPI absent; exact registered role-name fallback only.");
                return;
            }
            try
            {
                var getter = AccessTools.Method(manager, "GetCustomWildSpawnTypes");
                if (getter == null) throw new MissingMethodException(manager.FullName, "GetCustomWildSpawnTypes");
                if (!(getter.Invoke(null, null) is IEnumerable entries))
                    throw new InvalidOperationException("MoreBotsAPI registry did not return a collection");
                var discovered = new List<(int id, string role, string metadata)>();
                foreach (var entry in entries)
                {
                    try
                    {
                        var role = Read(entry, "WildSpawnTypeName") as string;
                        if (string.IsNullOrWhiteSpace(role)) continue;
                        var id = Convert.ToInt32(Read(entry, "WildSpawnTypeValue"));
                        var group = Read(entry, "ScavRole") as string;
                        discovered.Add((id, role, group));
                    }
                    catch (Exception ex) { Warn("registry-entry", ex); }
                }
                foreach (var bot in discovered.OrderBy(x => x.role, StringComparer.Ordinal))
                {
                    if (Roles.ContainsKey(bot.id)) continue;
                    var faction = FactionRolePolicy.Resolve(bot.role, bot.metadata);
                    // Unknown custom roles receive their own stable colour group. Generic
                    // metadata such as Boss/Scav must never merge unrelated mods.
                    if (faction == null) faction = "Detected - " + ConfigName(bot.role);
                    if (!Colours.ContainsKey(faction)) Bind(_config, faction, GeneratedColour(bot.role), 0);
                    Roles[bot.id] = faction;
                    if (FactionRolePolicy.IsUntarBoss(bot.role)) UntarBossRoles.Add(bot.id);
                    Trace("registry:" + bot.id, $"Registered role={bot.role}, id={bot.id}, metadata={bot.metadata}, colour group={faction}.");
                }
                var choices = new[] { AutomaticGroup, NativeGroup }
                    .Concat(Colours.Keys.OrderBy(x => x, StringComparer.Ordinal)).ToArray();
                foreach (var bot in discovered.OrderBy(x => x.role, StringComparer.Ordinal))
                {
                    if (GroupOverrides.ContainsKey(bot.id)) continue;
                    var entry = _config.Bind("Faction Grouping (Optional)", "Group with - " + ConfigName(bot.role), AutomaticGroup,
                        new ConfigDescription($"Automatic uses {Roles[bot.id]}. Select another colour group to share its colour, or DynamicMaps default to leave this role unchanged. Colour only: does not change teams, AI or boss icons.",
                            new AcceptableValueList<string>(choices)));
                    GroupOverrides[bot.id] = entry;
                    entry.SettingChanged += SettingsChanged;
                }
                _registryCount = discovered.Count;
                Plugin.Log?.LogInfo($"[DMExt:Factions] Discovered {_registryCount} custom roles in {Colours.Count} colour groups. Optional grouping available in F12.");
            }
            catch (Exception ex) { Warn("registry", ex); }
        }

        // Escape forbidden config-key characters without collisions or unstable IDs.
        private static string ConfigName(string value)
        {
            var result = new StringBuilder();
            foreach (var c in value)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') result.Append(c);
                else result.Append('~').Append(((int)c).ToString("X4"));
            return result.ToString();
        }

        private static Color GeneratedColour(string role)
        {
            uint hash = 2166136261;
            foreach (var c in role) hash = unchecked((hash ^ c) * 16777619);
            // Stable bright colours: no per-session randomisation.
            return new Color32((byte)(80 + hash % 160), (byte)(80 + (hash >> 8) % 160), (byte)(80 + (hash >> 16) % 160), 255);
        }

        internal static string ResolveGroup(int id, string fallback)
        {
            if (GroupOverrides.TryGetValue(id, out var entry))
            {
                if (entry.Value == NativeGroup) return null;
                if (entry.Value != AutomaticGroup && Colours.ContainsKey(entry.Value)) return entry.Value;
            }
            return Roles.TryGetValue(id, out var group) ? group : fallback;
        }

        private static void MarkerCreated(object __0, string __1, Color __2, object __result)
        {
            try
            {
                if (!(__result is Component marker) || marker == null || __0 == null) return;
                // Never recolour the local player or a native friendly/team marker.
                if (__1 == "Friendly Player" || !IsBot(__0)) return;
                ReadRegistry();
                var profile = Read(__0, "Profile");
                var role = Read(Read(Read(profile, "Info"), "Settings"), "Role");
                if (role == null) { Warn("missing-role", new MissingMemberException("AI Profile.Info.Settings.Role unavailable")); return; }
                var id = Convert.ToInt32(role);
                if (!Roles.TryGetValue(id, out var faction)) faction = FactionRolePolicy.Resolve(role.ToString(), null);
                if (faction == null)
                {
                    Trace("unmapped:" + id, $"Unmapped role={role}, id={id}; kept native category={__1} and colour.");
                    return;
                }
                var image = _imageProperty.GetValue(marker) as Image;
                var label = _labelProperty.GetValue(marker) as Graphic;
                if (image == null) throw new MissingMemberException("DynamicMaps marker Image unavailable");
                var tint = marker.GetComponent<FactionMarkerTint>() ?? marker.gameObject.AddComponent<FactionMarkerTint>();
                tint.Initialize(image, label, faction, __2, id);
                Trace("marker:" + id, $"Marker role={role}, id={id}, faction={faction}, category={__1}, sprite={image.sprite?.name}.");
            }
            catch (Exception ex) { Warn("marker-create", ex); }
        }

        private static void NativeColourChanged(object __instance, Color __0)
        {
            try
            {
                if (__instance is Component marker && marker != null)
                    marker.GetComponent<FactionMarkerTint>()?.NativeColourChanged(__0);
            }
            catch (Exception ex) { Warn("native-colour", ex); }
        }

        internal static void Trace(string key, string message)
        {
            if (Plugin.DebugEnabled && Traces.Add(key)) Plugin.Debug("[Factions] " + message);
        }

        internal static void Warn(string key, Exception ex)
        {
            if (Warnings.Add(key)) Plugin.Log?.LogWarning($"[DMExt:Factions] {key}: {ex}. Native map functionality remains available. Further identical errors suppressed.");
        }

        internal static string Describe() => $"hooks={_patched}, enabled={Enabled?.Value}, registryRead={_registryRead}, registered={_registryCount}, mapped={Roles.Count}, markers={FactionMarkerTint.Count}, errors={Warnings.Count}";

        internal static void LogSnapshot()
        {
            Plugin.Debug("[Factions] " + Describe());
            foreach (var pair in Colours) Plugin.Debug($"[Factions] {pair.Key} RGB={pair.Value.Value}");
            foreach (var pair in Roles) Plugin.Debug($"[Factions] role {pair.Key} => {pair.Value}");
        }

        internal static void Shutdown() => FactionMarkerTint.RestoreAll();
    }

    // Event-driven: no Update/LateUpdate and no additional marker objects or textures.
    internal sealed class FactionMarkerTint : MonoBehaviour
    {
        private static readonly HashSet<FactionMarkerTint> Active = new();
        internal static int Count => Active.Count;
        private Image _image;
        private Graphic _label;
        private string _faction;
        private Color _native;
        private int _roleId;

        internal void Initialize(Image image, Graphic label, string faction, Color native, int roleId)
        {
            _image = image; _label = label; _faction = faction; _native = native; _roleId = roleId;
            Active.Add(this);
            Apply();
        }

        internal void NativeColourChanged(Color colour) { _native = colour; Apply(); }

        internal void Apply(bool restore = false)
        {
            if (_image == null) return;
            var group = FactionColours.ResolveGroup(_roleId, _faction);
            bool enabled = !restore && FactionColours.Enabled.Value && group != null;
            var colour = enabled ? FactionColours.Colours[group].Value : _native;
            SetRgb(_image, colour);
            if (_label != null) SetRgb(_label, colour);
        }

        private static void SetRgb(Graphic graphic, Color colour)
        {
            // Native layer visibility is authoritative, including zero alpha.
            colour.a = graphic.color.a;
            if (graphic.color != colour) graphic.color = colour;
        }

        internal static void RefreshAll()
        {
            foreach (var tint in Active)
                if (tint != null)
                    try { tint.Apply(); } catch (Exception ex) { FactionColours.Warn("refresh", ex); }
        }

        internal static void RestoreAll()
        {
            foreach (var tint in Active)
                if (tint != null)
                    try { tint.Apply(true); } catch (Exception ex) { FactionColours.Warn("restore", ex); }
        }

        private void OnDestroy() { Active.Remove(this); }
    }
}
