using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoWaypoints
{
    // Ikony lokacji, ktore gra sama pokazuje na mapie (handlarze: Haldor, Hildir, Bog Witch;
    // kamienie ofiarne na starcie) - odpowiednik "Replace boss icons" dla oltarzy bossow:
    //  - opcja "Replace location icons": wlaczona = ikona moda, wylaczona = ikona gry, na pinie
    //    gry i na pinie moda tej samej kategorii,
    //  - pin gry chowa sie, gdy w tym samym miejscu stoi pin moda (z nazwa) - bez podwojnej ikony,
    //  - pin gry slucha przelacznika kategorii i "Show all pins" (tylko niewidoczny, nie usuwany).
    // Piny gry to PinType.None bez nazwy, dodawane co 5 s z ZoneSystem.GetLocationIcons do
    // Minimap.m_locationPins (pozycja -> pin); nazwa lokacji pochodzi z tej samej listy.
    public partial class AutoWaypointsPlugin
    {
        private ConfigEntry<bool> _replaceLocationIcons;

        private const float LocationPinMatchRadius = 30f;
        private static readonly FieldInfo LocationPinsField = AccessTools.Field(typeof(Minimap), "m_locationPins");
        private readonly Dictionary<Vector3, string> _locationIconNames = new Dictionary<Vector3, string>();
        private readonly Dictionary<ResourceCategory, Sprite> _modLocationIcons = new Dictionary<ResourceCategory, Sprite>();
        private readonly Dictionary<ResourceCategory, Sprite> _gameLocationIcons = new Dictionary<ResourceCategory, Sprite>();
        private Minimap _gameLocationIconsSource;

        private void BindLocationIconsConfig()
        {
            _replaceLocationIcons = Config.Bind("General", "ReplaceLocationIcons", false,
                "On: the game's own map icons for traders (Haldor, Hildir, Bog Witch) and the sacrificial stones at the start " +
                "use this mod's icons. Off: they keep the game's icons, and this mod's pins for those places use the game's icons too. " +
                "Either way the game's icon steps aside where this mod already has a named pin.");
            _replaceLocationIcons.SettingChanged += (_, _) => RequestPinUpdate();
        }

        [HarmonyPatch(typeof(Minimap), "UpdateLocationPins")]
        private static class Minimap_UpdateLocationPins_Patch
        {
            private static void Postfix() => Instance?.RefreshLocationIconNames();
        }

        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static class Minimap_UpdatePins_LocationPatch
        {
            private static void Postfix(List<Minimap.PinData> ___m_pins) => Instance?.ApplyLocationIcons(___m_pins);
        }

        private void RefreshLocationIconNames()
        {
            if (ZoneSystem.instance == null)
                return;
            _locationIconNames.Clear();
            ZoneSystem.instance.GetLocationIcons(_locationIconNames);
        }

        // Kategoria moda dla lokacji z ikona gry. Oltarze bossow obsluguje "Replace boss icons".
        private ResourceCategory LocationIconCategory(string locationName)
        {
            int hash = locationName.GetStableHashCode();
            ResourceCategory category = null;
            if (DungeonLocationByHash.TryGetValue(hash, out var info))
                _dungeonCategoriesByName.TryGetValue(info.DisplayName, out category);
            else
                _structureByLocationHash.TryGetValue(hash, out category);
            return category != null && category.MenuGroup != BossAltarsGroup ? category : null;
        }

        private Sprite ModLocationIcon(ResourceCategory category)
        {
            if (_modLocationIcons.TryGetValue(category, out var icon) && icon != null)
                return icon;
            icon = category.CustomIcon;
            if (icon == null)
            {
                var def = DungeonLocationDefs.FirstOrDefault(d => d.DisplayName == category.DisplayName);
                if (def.DisplayName != null)
                    icon = ResolveDungeonIcon(def.IconKey, def.TrophyItemName);
            }
            _modLocationIcons[category] = icon;
            return icon;
        }

        // Ikona, ktora gra pokazuje dla lokacji tej kategorii (null = gra jej nie pokazuje).
        private Sprite GameLocationIcon(ResourceCategory category)
        {
            if (_gameLocationIconsSource != Minimap.instance)
            {
                _gameLocationIconsSource = Minimap.instance;
                _gameLocationIcons.Clear();
            }
            if (_gameLocationIcons.TryGetValue(category, out var icon))
                return icon;
            icon = null;
            if (category.LocationPrefabs != null && category.MenuGroup != BossAltarsGroup && Minimap.instance != null)
                icon = Minimap.instance.m_locationIcons.Where(l => category.LocationPrefabs.Contains(l.m_name)).Select(l => l.m_icon).FirstOrDefault();
            _gameLocationIcons[category] = icon;
            return icon;
        }

        private Sprite LocationIconFor(ResourceCategory category)
        {
            var game = GameLocationIcon(category);
            return _replaceLocationIcons.Value || game == null ? ModLocationIcon(category) : game;
        }

        private void ApplyLocationIcons(List<Minimap.PinData> pins)
        {
            if (Minimap.instance == null || _replaceLocationIcons == null)
                return;

            // Piny moda tych kategorii - ikona wedlug opcji.
            foreach (var pin in pins)
            {
                var category = CategoryForPin(pin);
                if (category != null && GameLocationIcon(category) != null)
                    SetPinIcon(pin, LocationIconFor(category));
            }

            if (!(LocationPinsField?.GetValue(Minimap.instance) is Dictionary<Vector3, Minimap.PinData> nativePins))
                return;
            foreach (var kv in nativePins)
            {
                if (!_locationIconNames.TryGetValue(kv.Key, out var locationName))
                    continue;
                var category = LocationIconCategory(locationName);
                if (category == null)
                    continue;
                var native = kv.Value;
                bool modPinHere = pins.Any(p => p != native && CategoryForPin(p) == category &&
                                                Utils.DistanceXZ(p.m_pos, native.m_pos) < LocationPinMatchRadius);
                SetHiddenByAlpha(native.m_uiElement != null ? native.m_uiElement.gameObject : null,
                    modPinHere || !IsCategoryVisible(category));
                SetPinIcon(native, LocationIconFor(category));
            }
        }

        private static void SetPinIcon(Minimap.PinData pin, Sprite icon)
        {
            if (icon == null || pin.m_icon == icon)
                return;
            pin.m_icon = icon;
            if (pin.m_iconElement != null)
                pin.m_iconElement.sprite = icon;
        }
    }
}
