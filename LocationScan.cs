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
