using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoWaypoints
{
    // Lokacje (lochy, obozy, wioski, ruiny...) rozpoznawane po znaczniku LocationProxy, a nie po
    // tym, w jaki obiekt trafi skan: budynki czesci lokacji (np. chaty obozu Fulingow) to osobne
    // obiekty sieciowe, ktore nie wisza ani pod LocationProxy, ani pod generatorem pomieszczen.
    // Kazdy wczytany znacznik jest zapamietywany (Awake/OnDestroy) i sprawdzany po odleglosci
    // od srodka lokacji.
    public partial class AutoWaypointsPlugin
    {
        // Srodek duzej lokacji bywa daleko od jej krawedzi - promien co najmniej taki.
        private const float LocationDiscoverRadius = 40f;
        private static readonly HashSet<LocationProxy> LoadedLocationProxies = new HashSet<LocationProxy>();

        [HarmonyPatch(typeof(LocationProxy), "Awake")]
        private static class LocationProxy_Awake_Patch
        {
            private static void Postfix(LocationProxy __instance) => LoadedLocationProxies.Add(__instance);
        }

        [HarmonyPatch(typeof(LocationProxy), "OnDestroy")]
        private static class LocationProxy_OnDestroy_Patch
        {
            private static void Postfix(LocationProxy __instance) => LoadedLocationProxies.Remove(__instance);
        }

        // Czy pozycja lezy w obrebie wczytanej lokacji, ktora mod rozpoznaje (loch albo struktura).
        private bool IsNearKnownLocation(Vector3 pos)
        {
            foreach (var proxy in LoadedLocationProxies)
            {
                if (proxy == null || Utils.DistanceXZ(proxy.transform.position, pos) > LocationDiscoverRadius)
                    continue;
                var zdo = proxy.GetComponent<ZNetView>()?.GetZDO();
                int hash = zdo != null ? zdo.GetInt(ZDOVars.s_location) : 0;
                if (DungeonLocationByHash.ContainsKey(hash) || _structureByLocationHash.ContainsKey(hash))
                    return true;
            }
            return false;
        }

        // Piny "luznych ruin" (Ruins / Stone Tower Ruins / Wood House ze skupisk elementow) w obrebie
        // lokacji, ktora ma juz wlasny pin - powstaly z jej murow (przed rozpoznawaniem lokacji albo
        // jako kilka skupisk jednej budowli) i nakladaja sie na jej pin. Takze te schowane.
        private const float LooseRuinCleanupRadius = 30f;

        private void RemoveLooseRuinPinsNear(Vector3 pos, Minimap.PinData keep)
        {
            if (Minimap.instance == null || !(PinsField.GetValue(Minimap.instance) is List<Minimap.PinData> pins))
                return;
            var stale = pins.Where(p => p != keep && Utils.DistanceXZ(p.m_pos, pos) < LooseRuinCleanupRadius &&
                                        IsRuinClusterCategory(CategoryForPin(p))).ToList();
            foreach (var pin in stale)
            {
                Minimap.instance.RemovePin(pin);
                _ruinClusters.RemoveAll(cl => cl.Pin == pin);
                foreach (var key in _tracked.Where(kv => kv.Value.Pin == pin).Select(kv => kv.Key).ToList())
                    _tracked.Remove(key);
            }
            foreach (var category in new[] { _looseRuinsCategory, _looseTowerCategory, _looseWoodCategory })
                if (category != null)
                    RemoveHiddenRecordsNear(category, pos, LooseRuinCleanupRadius, keep);
            if (stale.Count > 0)
                Log.LogInfo($"Usunieto {stale.Count} pinow luznych ruin przy lokacji @ {pos} (to jej mury, ma wlasny pin).");
        }

        private void ScanNearbyLocations(Vector3 playerPos)
        {
            float radius = Mathf.Max(_scanRadius.Value, LocationDiscoverRadius);
            foreach (var proxy in LoadedLocationProxies.ToList())
            {
                if (proxy == null)
                {
                    LoadedLocationProxies.Remove(proxy);
                    continue;
                }
                GameObject locObj = proxy.gameObject;
                if (_tracked.ContainsKey(locObj) || Utils.DistanceXZ(locObj.transform.position, playerPos) > radius)
                    continue;
                var zdo = locObj.GetComponent<ZNetView>()?.GetZDO();
                int locationHash = zdo != null ? zdo.GetInt(ZDOVars.s_location) : 0;
                if (locationHash != 0)
                    TrackLocation(locObj, locationHash, playerPos, MinPinSpacing);
            }
        }
    }
}
