using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMapsExtended
{
    internal static class FactionColours
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<Color> OutlineColour;
        internal static readonly Dictionary<string, ConfigEntry<Color>> Colours = new();
        private static readonly Dictionary<int, string> Roles = new();
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
            Enabled = config.Bind("Faction Colours", "Enable faction colours", true,
                "Colour recognised addon factions. Vanilla colours, marker shapes, visibility and tracking remain controlled by DynamicMaps.");
            Bind(config, "Black Division", new Color32(85, 85, 85, 255));
            Bind(config, "UNTAR", new Color32(0, 191, 255, 255));
            Bind(config, "RUAF", new Color32(137, 155, 69, 255));
            Bind(config, "Remnant", new Color32(137, 155, 69, 255));
            var migrated = config.Bind("Faction Colours", "Palette version", 0,
                "Internal palette migration version. Existing custom colours are preserved.");
            if (migrated.Value < 1)
            {
                MigrateColour("Black Division", new Color32(24, 24, 24, 255), new Color32(85, 85, 85, 255));
                MigrateColour("UNTAR", new Color32(56, 189, 248, 255), new Color32(0, 191, 255, 255));
                MigrateColour("Remnant", new Color32(224, 68, 68, 255), new Color32(137, 155, 69, 255));
                migrated.Value = 1;
            }
            OutlineColour = config.Bind("Faction Colours", "Black Division outline", (Color)new Color32(224, 224, 224, 255),
                "Light outline for Black Division arrows and existing boss stars on dark artwork. Follows the marker's floor visibility.");
            Enabled.SettingChanged += SettingsChanged;
            OutlineColour.SettingChanged += SettingsChanged;
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

        private static void TrackedBoss(object __0, ref bool __result)
        {
            if (__result || __0 == null) return;
            try
            {
                if (!(Read(__0, "IsAI") is bool ai) || !ai) return;
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

        private static void Bind(ConfigFile config, string name, Color colour)
        {
            var entry = config.Bind("Faction Colours", name, colour,
                "One colour for this faction's soldiers, bosses and followers. Applies when that faction is installed. RGB is used; DynamicMaps controls opacity by floor.");
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
                foreach (var entry in entries)
                {
                    var role = Read(entry, "WildSpawnTypeName") as string;
                    var group = Read(entry, "ScavRole") as string;
                    var id = Convert.ToInt32(Read(entry, "WildSpawnTypeValue"));
                    var faction = FactionRolePolicy.Resolve(role, group);
                    _registryCount++;
                    if (faction != null) Roles[id] = faction;
                    if (FactionRolePolicy.IsUntarBoss(role)) UntarBossRoles.Add(id);
                    Trace("registry:" + id, $"Registered role={role}, id={id}, metadata={group}, colour faction={faction ?? "native fallback"}.");
                }
                Plugin.Log?.LogInfo($"[DMExt:Factions] Read {_registryCount} custom roles; {Roles.Count} matched the four supported factions.");
            }
            catch (Exception ex) { Warn("registry", ex); }
        }

        private static void MarkerCreated(object __0, string __1, Color __2, object __result)
        {
            try
            {
                if (!(__result is Component marker) || marker == null || __0 == null) return;
                // Never recolour the local player or a native friendly/team marker.
                if (__1 == "Friendly Player" || !(Read(__0, "IsAI") is bool ai) || !ai) return;
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
                tint.Initialize(image, label, faction, __2);
                Trace("marker:" + id, $"Marker role={role}, id={id}, faction={faction}, category={__1}, sprite={image.sprite?.name}, outline={faction == "Black Division"}.");
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
        private Outline _outline;
        private string _faction;
        private Color _native;

        internal void Initialize(Image image, Graphic label, string faction, Color native)
        {
            _image = image; _label = label; _faction = faction; _native = native;
            Active.Add(this);
            Apply();
        }

        internal void NativeColourChanged(Color colour) { _native = colour; Apply(); }

        internal void Apply(bool restore = false)
        {
            if (_image == null) return;
            bool enabled = !restore && FactionColours.Enabled.Value;
            var colour = enabled ? FactionColours.Colours[_faction].Value : _native;
            SetRgb(_image, colour);
            if (_label != null) SetRgb(_label, colour);
            bool outline = enabled && _faction == "Black Division";
            if (outline && _outline == null)
            {
                // Own component only; do not modify any effect supplied by another mod.
                _outline = _image.gameObject.AddComponent<Outline>();
                _outline.effectDistance = new Vector2(1.25f, -1.25f);
                _outline.useGraphicAlpha = true;
            }
            if (_outline != null)
            {
                _outline.effectColor = FactionColours.OutlineColour.Value;
                _outline.enabled = outline;
            }
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
