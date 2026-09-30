# Valheim Auto Waypoints

BepInEx mod for [Valheim](https://www.valheimgame.com/) that automatically
puts pins on your map for useful things you walk past - ore veins, berry
bushes, mushrooms, crops, herbs, beehives, dungeon entrances, world
structures and ruins, portals and ships - each with a fitting icon and name.
Everything is controlled from an in-game settings window on the map.

> **Unofficial mod.** This is a fan-made mod, not affiliated with or endorsed by
> Iron Gate. It marks your game as modded (the game shows this in the main menu),
> as Iron Gate asks mod authors to do.

## Screenshots

![Pins added automatically while exploring](docs/map.jpg)

![Settings window on the large map](docs/settings.png)

## What gets pinned

Anything in the scan radius around you (25 m by default). Places - dungeons,
camps, villages, ruins and the like - are pinned when you are within 40 m of
their centre, since large ones are much wider than the scan radius:

| Group | Pins (icon as shown on the map) | Notes |
|---|---|---|
| **Ores** | ![](docs/icons/Copper.png) Copper Ore · ![](docs/icons/Silver.png) Silver Ore · ![](docs/icons/Tin.png) Tin Ore · ![](docs/icons/Iron.png) Iron Deposit · ![](docs/icons/Obsidian.png) Obsidian · ![](docs/icons/Meteorite.png) Meteorite · ![](docs/icons/FlametalDeposit.png) Flametal Deposit | Also untouched deposits you haven't hit with a pickaxe yet; the pin goes when the vein is mined out. Meteorites and the flametal deposits in the Ashlands lava drop the same ore, so they share its icon |
| **Berries** | ![](docs/icons/Blueberry.png) Blueberries · ![](docs/icons/Cloudberry.png) Cloudberries · ![](docs/icons/Raspberry.png) Raspberries · ![](docs/icons/Lingonberry.png) Lingonberries · ![](docs/icons/Vineberry.png) Vineberries | The pin stays after picking - bushes and vines regrow |
| **Mushrooms** | ![](docs/icons/MushroomCommon.png) Mushroom · ![](docs/icons/MushroomBlue.png) Blue Mushroom · ![](docs/icons/MushroomYellow.png) Yellow Mushroom · ![](docs/icons/MushroomJotunPuffs.png) Jotun Puffs · ![](docs/icons/MushroomMagecap.png) Magecap · ![](docs/icons/MushroomSmokePuff.png) Smoke Puff |  |
| **Herbs** | ![](docs/icons/Thistle.png) Thistle · ![](docs/icons/Dandelion.png) Dandelion · ![](docs/icons/Fiddlehead.png) Fiddlehead |  |
| **Crops** | ![](docs/icons/CropCarrot.png) Carrot · ![](docs/icons/CropTurnip.png) Turnip · ![](docs/icons/CropOnion.png) Onion · ![](docs/icons/CropKale.png) Kale · ![](docs/icons/CropPoteitr.png) Poteitr · ![](docs/icons/CropBarley.png) Wild Barley · ![](docs/icons/CropFlax.png) Wild Flax | Anything grown and ready to pick, wild or planted |
| **Seeds** | ![](docs/icons/SeedCarrot.png) Carrot Seeds · ![](docs/icons/SeedTurnip.png) Turnip Seeds · ![](docs/icons/SeedOnion.png) Onion Seeds · ![](docs/icons/SeedKale.png) Kale Seeds | Wild seed plants - the only source before you start farming |
| **Beehives** | ![](docs/icons/Beehive.png) Beehive (built) · ![](docs/icons/BeehiveWild.png) Wild beehive | Your own hives and wild ones (the tree hives that drop the queen bee) |
| **Dungeons** | ![](docs/icons/DungeonBearCave.png) Bear Cave · ![](docs/icons/DungeonTrollCave.png) Troll Cave · ![](docs/icons/DungeonCrypt.png) Burial Chambers · ![](docs/icons/DungeonSunkenCrypt.png) Sunken Crypt · ![](docs/icons/DungeonFrostCave.png) Frost Cave · ![](docs/icons/DungeonFulingCamp.png) Fuling Camp · ![](docs/icons/DungeonSurtling.png) Surtling · ![](docs/icons/DungeonInfestedMine.png) Infested Mine · ![](docs/icons/DungeonWindingTunnels.png) Winding Tunnels · ![](docs/icons/DungeonMorkhalla.png) Morkhalla · ![](docs/icons/DungeonSmoulderingTomb.png) Smouldering Tomb · ![](docs/icons/DungeonHowlingCavern.png) Howling Cavern · ![](docs/icons/DungeonSealedTower.png) Sealed Tower | Each kind has its own switch |
| **Boss altars** | ![](docs/icons/DungeonEikthyrsAltar.png) Eikthyr's Altar · ![](docs/icons/DungeonEldersAltar.png) Elder's Altar · ![](docs/icons/DungeonBonemassAltar.png) Bonemass' Altar · ![](docs/icons/DungeonModersAltar.png) Moder's Altar · ![](docs/icons/DungeonYagluthsAltar.png) Yagluth's Altar · ![](docs/icons/DungeonInfestedCitadel.png) Infested Citadel · ![](docs/icons/DungeonFadersAltar.png) Fader's Altar · ![](docs/icons/DungeonDeepNorthBoss.png) Deep North Boss | Trophy icons or renders with **Replace boss icons** on; otherwise the game's boss icon ![](docs/icons/BossNative.png) |
| **Structures** | ![](docs/icons/StructRuins.png) Ruins · ![](docs/icons/StructLogCabin.png) Log Cabin · ![](docs/icons/StructWoodHouse.png) Wood House · ![](docs/icons/StructDraugrVillage.png) Draugr Village · ![](docs/icons/StructAbandonedFarm.png) Abandoned Farm · ![](docs/icons/StructSwampHut.png) Swamp Hut · ![](docs/icons/StructSwampTower.png) Stone Tower Ruins · ![](docs/icons/StructShipwreck.png) Shipwreck · ![](docs/icons/StructStoneCircle.png) Stone Circle · ![](docs/icons/StructWell.png) Well · ![](docs/icons/StructDolmen.png) Dolmen · ![](docs/icons/StructRunestone.png) Runestone · ![](docs/icons/StructInfestedTree.png) Infested Tree · ![](docs/icons/StructSacrificialStones.png) Sacrificial Stones · ![](docs/icons/StructCombatRuin.png) Combat Ruin · ![](docs/icons/StructStoneShip.png) Stone Ship · ![](docs/icons/StructGreydwarfCamp.png) Greydwarf Camp · ![](docs/icons/StructBigRockClearing.png) Big Rock Clearing · ![](docs/icons/StructDraugrGrave.png) Draugr Grave · ![](docs/icons/StructSwampRuin.png) Swamp Ruin · ![](docs/icons/StructSwampWell.png) Swamp Well · ![](docs/icons/StructDragonNest.png) Dragon Nest · ![](docs/icons/StructMountainGrave.png) Mountain Grave · ![](docs/icons/StructWaymarker.png) Waymarker · ![](docs/icons/StructAncientUpgradeStation.png) Ancient Upgrade Station · ![](docs/icons/StructFulingTower.png) Fuling Tower · ![](docs/icons/StructFulingHut.png) Fuling Hut · ![](docs/icons/StructTarPit.png) Tar Pit | Loose ruins made of generic building pieces count too; anything a player built is never tagged |
| **Mistlands** | ![](docs/icons/StructHarbour.png) Harbour · ![](docs/icons/StructViaduct.png) Viaduct · ![](docs/icons/StructStatues.png) Statues · ![](docs/icons/StructGiantRemains.png) Giant Remains · ![](docs/icons/StructGiantArmor.png) Giant Armor · ![](docs/icons/StructDvergrTower.png) Dvergr Tower · ![](docs/icons/StructDvergrExcavation.png) Dvergr Excavation · ![](docs/icons/StructRoadPost.png) Road Post |  |
| **Ashlands** | ![](docs/icons/StructCharredFortress.png) Charred Fortress · ![](docs/icons/StructFortressRuins.png) Fortress Ruins · ![](docs/icons/StructAshlandRuins.png) Ashland Ruins · ![](docs/icons/StructCharredRuins.png) Charred Ruins · ![](docs/icons/StructCharredTowerRuins.png)![](docs/icons/StructCharredTowerRuins__CharredTowerRuins2.png)![](docs/icons/StructCharredTowerRuins__CharredTowerRuins3.png) Charred Tower Ruins · ![](docs/icons/StructDvergrTowerRuins.png) Dvergr Tower Ruins · ![](docs/icons/StructPlaceOfMystery.png) Place of Mystery · ![](docs/icons/StructCharredStones.png) Charred Stones · ![](docs/icons/StructSulfurArch.png) Sulfur Arch · ![](docs/icons/StructVoltureNest.png) Volture Nest | Charred Tower Ruins come in three shapes, each with its own icon |
| **Deep North** | ![](docs/icons/StructNorthVillage.png) North Village · ![](docs/icons/StructFrozenShip.png) Frozen Ship · ![](docs/icons/StructLumberCamp.png) Lumber Camp · ![](docs/icons/StructNorthernHut.png) Northern Hut · ![](docs/icons/StructPetrifiedGammeltroll.png) Petrified Gammeltroll · ![](docs/icons/StructIcePond.png) Ice Pond · ![](docs/icons/StructNorthMemorial.png) North Memorial |  |
| **Portals** | ![](docs/icons/Portal.png) Portal | Labelled with just the portal's name |
| **Ships** | ![](docs/icons/Ship.png) Ship / Ship (docked) | A live pin that follows a ship while someone sails it, and a docked pin where it was left |
| **Other** | ![](docs/icons/DungeonTrader.png) Trader · ![](docs/icons/DungeonHildirsCamp.png) Hildir's Camp · ![](docs/icons/DungeonBogWitchCamp.png) Bog Witch Camp · ![](docs/icons/DragonEgg.png) Dragon Egg | Traders use this mod's icons with **Replace location icons** on. The dragon egg pin goes when someone picks the egg up |

Pins use the matching item or building icon, the trophy of whoever lives there
for caves, camps and boss altars (coins for the trader), or a custom icon
rendered from the game's own 3D model for structures, crypts and wild beehives. A pin is removed when its resource is gone (vein mined out, crop
picked); berry bushes keep their pin since they regrow. A pin you delete by
hand comes back the next time you pass by - to get rid of a whole kind of
pin, switch it off in the settings instead.

The mod keeps collecting even while a kind of pin (or everything) is hidden:
what you walk past goes straight into the hidden set and shows up as soon as
you switch it back on.

## Settings window

Open the large map and click **Waypoint Settings** (bottom-right, under the
map). Press Esc once to close the window, twice to also close the map.

- **Show all pins** - hide or show every pin this mod added
- **Show pin labels** - hide or show the names under this mod's pins
- **Replace boss icons** (off by default) - boss pins, including the ones the
  game adds when you read a Vegvisir runestone, get the boss trophy icon
  instead of the game's standard boss icon
- **Replace location icons** (off by default) - the icons the game itself
  shows for traders (Haldor, Hildir, the Bog Witch) and the sacrificial stones
  at the start use this mod's icons instead of the game's. Where the mod already
  has a named pin for such a place, the game's icon steps aside
- **Scan radius** - 5 to 100 m
- A list of categories in collapsible groups (Ores, Berries, Structures...),
  with two columns: **Pin** hides/shows the pins, **Label** hides/shows just
  their names. Each group header toggles the whole group.

Boss pins the game adds from Vegvisir runestones follow the switches of their
boss altar too - hiding them only makes them invisible, it never deletes them
from your map. Where the game already has a boss pin, the mod doesn't add a
second one.

Hiding never deletes anything: hidden pins are remembered per world and
character, and come back when you turn them on again, even after restarting
the game.

**Per character:** map pins belong to a character, so everything tied to them
is kept separately for each character - hidden pins and discovered categories
per world, and the settings window (pin and label switches, **Show all pins**,
**Show pin labels**, **Replace boss icons**, **Replace location icons**) per
character. The scan radius is shared. A character that has no settings yet
starts from the values in the config file.

**No spoilers:** a category only shows up in the list once the mod has
actually pinned one of its kind in that world, so a new player doesn't learn
from the settings that silver exists before finding any. If a whole group is
hidden, newly discovered kinds of that group start hidden too.

Your own pins are never touched.

Works together with
[Nav Compass](https://github.com/Ab5oluteZer0/valheim-nav-compass): a tracked
pin's name on the compass follows the label settings here. Other mods can ask
the same thing through the public `AutoWaypointsPlugin.IsPinLabelVisible(pin)`.

**Upgrading from 0.1.0:** the single "Dungeon" switch is replaced by one per
dungeon kind (its old setting carries over), and portal pins named
"Portal: name" are renamed to just "name" the first time you load a world.

**Upgrading from 1.0.3:** hidden pins, discovered categories and settings used
to be shared by all characters. The first character that enters a world after
the update takes over that world's data (the old files are kept as
`*.migrated`); every character starts from the current settings. Some names
changed - "Crypt" is now "Burial Chambers", "Barley"/"Flax" are "Wild
Barley"/"Wild Flax", and "Farm Village" is split into "Draugr Village" and
"Abandoned Farm". Existing pins are renamed when the world loads (or, on a
server, when you pass by).

## Requirements

- Valheim (tested on 1.0.15)
- [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) 5.4.x
- [Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/) (used for the game-styled settings window)

## Installation (players)

1. Install BepInEx and Jotunn (links above, or use
   [r2modman](https://valheim.thunderstore.io/package/ebkr/r2modman/)).
2. Download `AutoWaypoints.dll` from the
   [latest release](../../releases/latest).
3. Drop it into `<Valheim install folder>\BepInEx\plugins\AutoWaypoints\`.
4. Launch the game and walk around - pins appear as you go.

The config file `BepInEx\config\com.michal.valheim.autowaypoints.cfg` holds
the scan radius and the starting settings for new characters. Everything else
is kept in `BepInEx\config\AutoWaypoints\`, so updating the mod with a mod
manager doesn't wipe it:

- `settings_<character>.json` - the settings window, per character
- `hidden_pins_<world>_<character>.json`, `discovered_<world>_<character>.json`
- `portals_<world>.json` - portal positions, per world

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer)
and a local Valheim install with BepInEx and Jotunn installed.

```bash
git clone https://github.com/Ab5oluteZer0/valheim-auto-waypoints.git
cd valheim-auto-waypoints
dotnet build -c Release -p:ValheimPath="C:\Path\To\Valheim"
```

If you don't pass `-p:ValheimPath`, the build looks for a `VALHEIM_PATH`
environment variable, then falls back to the default Steam location
(`C:\Program Files (x86)\Steam\steamapps\common\Valheim`). Jotunn is expected
in `BepInEx\plugins\ValheimModding-Jotunn\` (where r2modman puts it);
override with `-p:JotunnPath=...` if yours is elsewhere.

The build automatically copies the built DLL into
`<Valheim>\BepInEx\plugins\AutoWaypoints\` for quick in-game testing.

## Notes on how it works (and a few gotchas found along the way)

- `Minimap.RemovePin` really deletes a pin, and the game saves the map on
  its own schedule - so "hiding" a category keeps the removed pins in the
  mod's own per-world file and re-adds them when shown again.
- `JsonUtility` silently drops a `List<YourClass>` field in a plugin
  assembly (you get `{}` with no error). The save files only use lists of
  built-in types, and every save is read back and checked before it
  overwrites the previous file.
- The game re-enables a pin's name label from two places: `UpdatePins`
  (only when it decides something changed) and a coroutine one second after
  the label is created - which happens again every time the pin scrolls back
  into view. Fighting that with `SetActive(false)` always loses; labels are
  hidden with a `CanvasGroup` alpha instead, which the game never touches.
- Ore veins (`MineRock`/`MineRock5`) have colliders on oddly named child
  fragments, so they're matched by what they drop, not by object name.
- Scanning a large radius is spread over several frames, so a 100 m radius
  doesn't cause a hitch.
- Every location in the world (dungeon, camp, ruin, trader...) is spawned as a
  `LocationProxy` holding the location's name hash, with the visible model
  parented to it. Places assembled from rooms (Fuling camps, villages,
  fortresses) are different: their buildings are separate network objects, not
  under the proxy - so the mod keeps its own list of loaded proxies and checks
  them by distance instead of relying on what the scan happens to hit.
- Loose ruins (clusters of generic building pieces) are only pinned away from
  known locations: the walls of a Fuling tower or a village used to be picked
  up as extra "Ruins" pins on top of the location's own pin. Such leftover pins
  are removed when you pass by the location.
- Two map pins that look the same can be different places in the game data
  (e.g. three "Charred Tower Ruins" shapes); an icon can be set per location
  variant (`Icons/<category>__<location>.png`).

## Credits

Structure and dungeon icons are renders of Valheim's own 3D models.
Valheim and its assets are the property of Iron Gate AB.

## Support

All my mods are free and will stay free. If you enjoy them and want to say
thanks, you can leave a voluntary tip via [PayPal](https://www.paypal.com/ncp/payment/4JQUSHTJGBAG6) - it doesn't
unlock anything, it just helps me keep making mods.

## License

MIT - see [LICENSE](LICENSE). Applies to this mod's code, not to Valheim's
assets.
