using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AutoWaypoints
{
    // Automatyczne piny na mapie dla zasobow w pobliżu gracza (rudy, jagody, grzyby,
    // wejscia do lochow). Skanuje okolice co ~1.5s (NIE co klatke), zawezone pionowo
    // do wysokosci postaci (zeby nie wykrywac zloz kilka pieter nizej pod ziemia),
    // uzywa prawdziwych ikon przedmiotow z gry, i usuwa pin gdy zasob zniknie
    // (wydobyty/zebrany). Kompatybilne z Nav Compass (zwykle Minimap.AddPin,
    // save=true - dziala tez ze stolem kartograficznym, ktory dzieli piny save'owalne
    // miedzy graczami).
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class AutoWaypointsPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.michal.valheim.autowaypoints";
        public const string PluginName = "Auto Waypoints";
        public const string PluginVersion = "0.2.0";

        private const float ScanInterval = 1.5f;
        // "od stop do glowy postaci" - waskie okno pionowe, zeby nie wylapywac zloz
        // znajdujacych sie kilka poziomow jaskini nizej/wyzej przez skale.
        private const float MaxVerticalDelta = 3f;
        // Skupiska (grzadka warzyw, klaster uli/krzakow) maja wiele osobnych obiektow
        // tuz obok siebie - bez tego kazda roslinka dostaje wlasny pin i mapa robi sie
        // nieczytelna. Jeden pin na skupisko: pomijamy nowy obiekt jesli w promieniu
        // ponizej juz jest sledzony pin tej samej kategorii.
        private const float MinPinSpacing = 8f;

        private static readonly FieldInfo PinsField = AccessTools.Field(typeof(Minimap), "m_pins");
        private static readonly FieldInfo LocationsByHashField = AccessTools.Field(typeof(ZoneSystem), "m_locationsByHash");
        // Ustawiane przez sama gre DOPIERO PO synchronicznym wczytaniu zapisanych pinow gracza
        // (Minimap.LoadMapData, wywolywane raz w Minimap.Update). Player.m_localPlayer potrafi
        // istniec kilka klatek WCZESNIEJ niz to sie skonczy - jesli nasz jednorazowy przebieg
        // (globalna naprawa ikonek / wczytanie+wymuszenie widocznosci) odpali sie przed tym, to
        // widzi liste pinow ktora jeszcze nie ma prawdziwych nazw/pozycji - obserwowane w logu
        // jako "0 pasujacych" dla KAZDEJ kategorii mimo dziesiatkow pinow juz na mapie.
        private static readonly FieldInfo MapHasGeneratedField = AccessTools.Field(typeof(Minimap), "m_hasGenerated");

        // LocationProxy trzyma typ lokacji jako hash stringa w ZDO (ZDOVars.s_location), nie
        // jako czytelna nazwe na obiekcie - porownujemy wiec hashe znanych nazw lochow.
        // Uwaga: prawdziwe nazwy to zwykle, krotkie identyfikatory typu "BearCave"/"TrollCave02"
        // (zweryfikowane w logu przez odczyt ZoneSystem.m_locationsByHash), NIE "DG_..." -
        // to zle zalozenie z poczatku. "DG_" to inne, wewnetrzne prefaby (generacja pokoi).
        // Kazdy typ dostaje wlasna, czytelna nazwe pinu (nie ogolne "Dungeon" dla wszystkich)
        // i osobna ikone per grupa tematyczna.
        // IconKey: klucz do wlasnej ikony (LoadEmbeddedIcon) renderowanej z pliku gry. Lochy w
        // Valheim to w wiekszosci proceduralna kompozycja z wielu malych pokoi (Assets/world/Rooms)
        // - nie istnieje jeden, calosciowy model "tak wyglada Bear Cave" do wyeksportowania (proba
        // wyrenderowania "DG_Cave"/"DG_ForestCrypt"/"DG_SunkenCrypt"/"DG_GoblinCamp" dala puste
        // pliki - to same generatory kompozycji, bez wlasnej geometrii). Oltarze przywolania
        // bossow to za to zwykle, samodzielne struktury - dla nich renderowanie zadzialalo.
        // TrophyItemName: alternatywa dla IconKey - ikona trofeum przeciwnika zamieszkujacego
        // loch, pobrana z ObjectDB tak jak dla rudy/jagod (gra i tak ma ta ikone gotowa, nie
        // trzeba jej renderowac). "Bear" w BearCave to w tej grze Bjorn (TrophyBjorn), nie
        // TrophyBear - taki plik nie istnieje (zweryfikowane w plikach gry).
        // Oba pola null = brak dopasowania, kategoria zostaje przy wbudowanej ikonie PinType.
        private static readonly (string LocationName, string DisplayName, Minimap.PinType PinType, string IconKey, string TrophyItemName)[] DungeonLocationDefs =
        {
            ("BearCave", "Bear Cave", Minimap.PinType.Icon2, null, "TrophyBjorn"),
            // Potwierdzone przez uzytkownika w grze: to lodowy troll, nie lesny.
            ("TrollCave", "Troll Cave", Minimap.PinType.Icon2, null, "TrophyFrostTroll"),
            ("TrollCave01", "Troll Cave", Minimap.PinType.Icon2, null, "TrophyFrostTroll"),
            ("TrollCave02", "Troll Cave", Minimap.PinType.Icon2, null, "TrophyFrostTroll"),
            // "halfBurried_forestcrypt_entrance_large" - jedyny znaleziony model wejscia do
            // krypty (zwykly "DG_ForestCrypt" to pusty generator ukladu pokoi, bez geometrii).
            // Wizualnie potwierdzone przez uzytkownika jako pasujace do tego co widac w grze.
            ("Crypt1", "Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("Crypt2", "Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("Crypt3", "Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("Crypt4", "Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            // Brak dedykowanego modelu wejscia (tylko sama metalowa krata bez kamienia,
            // reszta to generyczne kawalki wnetrza) - na prosbe uzytkownika reuzywamy
            // ikony zwyklej krypty (podobny motyw: kamien + wejscie).
            ("SunkenCrypt1", "Sunken Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("SunkenCrypt2", "Sunken Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("SunkenCrypt3", "Sunken Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("SunkenCrypt4", "Sunken Crypt", Minimap.PinType.Icon4, "DungeonCrypt", null),
            ("MountainCave01", "Frost Cave", Minimap.PinType.Icon2, null, null),
            ("MountainCave02", "Frost Cave", Minimap.PinType.Icon2, null, null),
            ("MountainCave03", "Frost Cave", Minimap.PinType.Icon2, null, null),
            ("MountainCave04", "Frost Cave", Minimap.PinType.Icon2, null, null),
            ("GoblinCamp1", "Fuling Camp", Minimap.PinType.Icon3, null, null),
            ("GoblinCamp2", "Fuling Camp", Minimap.PinType.Icon3, null, null),
            ("GoblinCamp3", "Fuling Camp", Minimap.PinType.Icon3, null, null),
            ("GoblinCamp4", "Fuling Camp", Minimap.PinType.Icon3, null, null),
            ("FireHole", "Surtling", Minimap.PinType.Icon3, null, null),
            // Oltarze przywolania bossow - nazwa wewnetrzna to zwykle skrot ("GDKing" = Elder).
            // Wszystkie oltarze bossow maja ikone trofeum bossa (jak Bear Cave trofeum Bjorna) -
            // jeden, spojny styl (wczesniej Bonemass/Moder mialy rendery oltarzy).
            ("Eikthyrnir", "Eikthyr's Altar", Minimap.PinType.Boss, null, "TrophyEikthyr"),
            ("GDKing", "Elder's Altar", Minimap.PinType.Boss, null, "TrophyTheElder"),
            ("Bonemass", "Bonemass' Altar", Minimap.PinType.Boss, null, "TrophyBonemass"),
            ("Dragonqueen", "Moder's Altar", Minimap.PinType.Boss, null, "TrophyDragonQueen"),
            ("GoblinKing", "Yagluth's Altar", Minimap.PinType.Boss, null, "TrophyGoblinKing"),
            ("Vendor_BlackForest", "Trader", Minimap.PinType.Icon3, null, null),
        };
        private static readonly Dictionary<int, (string DisplayName, Minimap.PinType PinType, string IconKey, string TrophyItemName)> DungeonLocationByHash =
            DungeonLocationDefs.ToDictionary(d => d.LocationName.GetStableHashCode(), d => (d.DisplayName, d.PinType, d.IconKey, d.TrophyItemName));

        // Wlasne ikony lochow (klucz -> gotowy Sprite), wczytane raz w BuildConfig. Osobny
        // slownik od ResourceCategory.CustomIcon, bo DungeonLocationDefs nie jest kategoria -
        // kazdy typ lochu dzieli jedna z kilku wspolnych ikon (albo zadnej).
        private readonly Dictionary<string, Sprite> _dungeonIcons = new Dictionary<string, Sprite>();

        internal static ManualLogSource Log;

        private ConfigEntry<bool> _showAutoPins;
        private ConfigEntry<bool> _showPinLabels;
        private ConfigEntry<bool> _replaceBossAltars;
        // Nazwa pinu -> kategoria moda. Piny spoza tej mapy (wlasne piny gracza) sa nietykalne.
        private readonly Dictionary<string, ResourceCategory> _categoryByPinName = new Dictionary<string, ResourceCategory>();
        private ConfigEntry<bool> _debugLogNearby;
        private ConfigEntry<float> _scanRadius;
        private ConfigEntry<bool> _shipsEnabled;
        private ConfigEntry<bool> _shipsDockedEnabled;
        private readonly ResourceCategory _shipLiveCategory = new ResourceCategory { Key = "ShipLive", DisplayName = "Live pin" };
        private readonly ResourceCategory _shipDockedCategory = new ResourceCategory { Key = "ShipDocked", DisplayName = "Docked" };
        private ConfigEntry<bool> _portalsEnabled;
        private readonly ResourceCategory _portalCategory = new ResourceCategory { Key = "Portal", DisplayName = "Portal" };
        // Nazwa pinu lochu ("Bear Cave", "Crypt"...) -> jego wlasna kategoria.
        private readonly Dictionary<string, ResourceCategory> _dungeonCategoriesByName = new Dictionary<string, ResourceCategory>();
        private ResourceCategory _looseRuinsCategory;
        private ResourceCategory _looseTowerCategory;
        private ResourceCategory _looseWoodCategory;
        private readonly List<ResourceCategory> _categories = new List<ResourceCategory>();

        // Ruiny zbudowane z osobnych, generycznych elementow konstrukcyjnych (te same prefaby
        // co gracz moze postawic - stone_wall_2x1, wood_floor itd.) zamiast jednego, nazwanego
        // obiektu. Nie maja LocationProxy (potwierdzone w logu - brak [DIAG-DUNGEON] dla takiej
        // ruiny), wiec nie da sie ich zlapac hashem lokacji ani dopasowaniem po nazwie jednego
        // obiektu jak pozostale struktury. Wykrywamy je jako SKUPISKO kilku takich elementow w
        // niewielkiej odleglosci od siebie i stawiamy JEDEN pin w ich srodku ciezkosci.
        private static readonly HashSet<string> RuinPieceNames = new HashSet<string>
        {
            "stone_wall_2x1", "stone_wall_1x1", "stone_floor_2x2", "stone_stair",
            "wood_floor", "wood_floor_1x1", "wood_stepladder"
        };
        private const float RuinClusterRadius = 20f;
        private const int RuinClusterMinPieces = 3;
        // Wysoka, waska wieza (duza roznica wysokosci miedzy elementami skupiska w niewielkim
        // promieniu) wyglada zupelnie inaczej niz plaska ruina domu - potwierdzone screenem
        // uzytkownika (kamienna wieza z drabinami w srodku). Taki ksztalt dostaje ikone
        // "Stone Tower Ruins" zamiast domyslnej "Ruins" (StoneHouse4).
        private const float TowerHeightSpan = 6f;

        private class RuinCluster
        {
            public Vector3 Center;
            public float MinY = float.PositiveInfinity;
            public float MaxY = float.NegativeInfinity;
            public int StoneCount;
            public int WoodCount;
            public readonly HashSet<GameObject> Pieces = new HashSet<GameObject>();
            public Minimap.PinData Pin;
            public ResourceCategory Category;
        }
        private readonly List<RuinCluster> _ruinClusters = new List<RuinCluster>();

        private float _scanTimer;
        private bool _globalIconPassDone;

        private const float ShipUpdateInterval = 0.3f;
        private float _shipUpdateTimer;
        private readonly Dictionary<Ship, Minimap.PinData> _shipPins = new Dictionary<Ship, Minimap.PinData>();

        private class ResourceCategory
        {
            public string Key;
            public string DisplayName;
            public HashSet<string> ExactNames;
            public string Prefix;
            public HashSet<string> OreDropItemNames;
            public HashSet<string> OreNameTokens;
            public Minimap.PinType PinType;
            public ConfigEntry<bool> Enabled;
            public ConfigEntry<bool> ShowName;
            // Grupa w menu, gdy nie wynika z klucza (GetGroupName) - np. lochy vs oltarze bossow.
            public string MenuGroup;
            // null = dowolny obiekt; true/false = tylko postawiony przez gracza / tylko nie
            // (Piece.GetCreator) - ten sam prefab bywa i ulem gracza, i wygenerowanym ze swiatem.
            public bool? PlayerBuilt;
            // Ikona budowli z menu mlota (Piece.m_icon prefabu) - pobierana po wczytaniu swiata.
            public string IconPiecePrefab;
            // Dawne nazwy pinow tej kategorii (sprzed podzialu) - przejmowane i przemianowywane.
            public string[] LegacyPinNames;
            public string IconItemNameOverride;
            public string[] IconItemNameCandidates;
            public bool RemoveOnPicked = true;
            public float? MaxVerticalDeltaOverride;
            public Sprite CustomIcon;

            public bool Matches(string name)
            {
                if (ExactNames != null && ExactNames.Contains(name)) return true;
                if (Prefix != null && name.StartsWith(Prefix, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        private class TrackedResource
        {
            public GameObject Obj;
            public Minimap.PinData Pin;
            public Pickable PickableComp;
            public ResourceCategory Category;
            // Nienaruszone zloze (Destructible), ktore gra przy pierwszym uderzeniu podmienia na
            // wersje z kawalkami - znikniecie Obj nie oznacza wtedy, ze rudy juz nie ma.
            public bool ReplacedWhenDamaged;
        }

        private readonly Dictionary<GameObject, TrackedResource> _tracked = new Dictionary<GameObject, TrackedResource>();

        private GameObject _settingsPanel;
        internal static AutoWaypointsPlugin Instance;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            BuildConfig();
            GUIManager.OnCustomGUIAvailable += BuildSettingsMenu;
            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(AutoWaypointsPlugin).Assembly);
        }

        // Zamiast klawisza (M jest juz zajete przez wlasna mape gry!) - guzik "Waypoint Settings"
        // wstawiony wprost do ekranu duzej mapy, kolo przelacznika "Visible to other players".
        [HarmonyPatch(typeof(Minimap), "Awake")]
        private static class Minimap_Awake_Patch
        {
            private static void Postfix(Minimap __instance)
            {
                Instance?.CreateSettingsButton(__instance);
            }
        }

        // Bez tego pierwszy Escape zamykal od razu MAPE (Minimap.Update ma wlasna obsluge
        // Escape do zamkniecia duzej mapy), zostawiajac nasze menu osierocone nad juz zamknieta
        // mapa - trzeba by je zamykac osobno klikiem w guzik. Prefix pomija CALY oryginalny
        // Update() na TEN JEDEN klatke gdy nasze menu jest otwarte i wlasnie nacisnieto Escape -
        // zamyka tylko nasze menu, mapa zostaje otwarta. Drugi Escape (menu juz zamkniete)
        // dziala normalnie i zamyka mape, tak jak oczekiwal uzytkownik.
        [HarmonyPatch(typeof(Minimap), "Update")]
        private static class Minimap_Update_EscapePatch
        {
            private static bool Prefix()
            {
                if (Instance == null || Instance._settingsPanel == null || !Instance._settingsPanel.activeSelf)
                    return true;
                if (!Input.GetKeyDown(KeyCode.Escape))
                    return true;

                Instance.ToggleSettingsMenu();
                return false;
            }
        }

        private void CreateSettingsButton(Minimap mm)
        {
            if (GUIManager.Instance == null || mm.m_largeRoot == null)
                return;
            if (mm.m_largeRoot.transform.Find("AutoWaypointsSettingsButton") != null)
                return;

            var btnGO = GUIManager.Instance.CreateButton("Waypoint Settings", mm.m_largeRoot.transform,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-150f, -30f), 180f, 30f);
            btnGO.name = "AutoWaypointsSettingsButton";
            btnGO.GetComponent<Button>().onClick.AddListener(ToggleSettingsMenu);
        }

        private void BuildConfig()
        {
            _showAutoPins = Config.Bind("General", "ShowAutoPins", true,
                "Master switch. When off, all pins this mod added are removed from the map and no new ones are added - only the game's own pins remain.");
            _debugLogNearby = Config.Bind("General", "DebugLogNearby", false,
                "Temporary debug aid: logs every distinct object name found in scan range each scan tick. Noisy - turn off once category prefab names are confirmed working.");
            _scanRadius = Config.Bind("General", "ScanRadius", 25f,
                "How far (meters) to scan around the player. WARNING: large values (100+) can hit FPS since Physics.OverlapSphere gets expensive - only raise this temporarily for diagnostics, then set back down.");
            _showAutoPins.SettingChanged += (_, _) => RefreshAllVisibility();
            _showPinLabels = Config.Bind("Labels", "ShowPinLabels", true,
                "Master switch for the name labels under this mod's pins on the large map. Pins themselves stay visible.");
            _showPinLabels.SettingChanged += (_, _) => ApplyLabelVisibilityToAllPins();
            _replaceBossAltars = Config.Bind("General", "ReplaceBossAltars", false,
                "On: boss pins - both this mod's altar pins and the game's own boss pins from Vegvisir runestones - use this mod's icons (trophies/altar renders). Off: all of them use the game's standard boss icon. Either way the mod's altar pin steps aside where the game already has a pin for that boss.");
            _replaceBossAltars.SettingChanged += (_, _) =>
            {
                // Poprzednia wersja tej opcji chowala oltarze z moda - przywraca je, jesli leza w schowanych.
                foreach (var category in _categories.Where(c => c.MenuGroup == BossAltarsGroup))
                    SetCategoryVisibility(category, IsCategoryVisible(category));
                ReapplyAllKnownPinIcons();
                RequestPinUpdate();
            };

            // Ruda: MineRock/MineRock5 maja kolidery na oderwanych, technicznie nazwanych
            // fragmentach (np. "MineRock5 m_meshFilter") - dopasowanie po nazwie obiektu w
            // swiecie nie dziala. Dopasowujemy wiec po nazwie przedmiotu ktory FAKTYCZNIE
            // wypada z zyly (odczytane z DropTable), nie po nazwie/prefabie samej zyly.
            // m_name "$piece_deposit_copper" potwierdzone w logu; reszta wg tego samego wzorca
            // nazewnictwa gry - jesli ktoras zle zgadnieta, [DIAG-ORE] to pokaze przy okazji.
            AddOreCategory("Copper", "Copper Ore", "CopperOre", "$piece_deposit_copper");
            AddOreCategory("Silver", "Silver Ore", "SilverOre", "$piece_deposit_silver");
            AddOreCategory("Tin", "Tin Ore", "TinOre", "$piece_deposit_tin", worldObjectName: "MineRock_Tin");
            AddOreCategory("Iron", "Iron Deposit", "IronOre", "$piece_deposit_iron");
            AddOreCategory("Obsidian", "Obsidian", "Obsidian", "$piece_deposit_obsidian");
            AddOreCategory("Meteorite", "Meteorite", "Meteorite", "$piece_deposit_meteorite");

            // Krzaki jagod: NIE usuwamy pinu przy samym zebraniu jagod (odrastaja) - tylko
            // jesli krzak faktycznie zniknie (np. zniszczony). Jedyny taki wyjatek.
            // IconItemNameOverride ustawiony tez tutaj (nie tylko dla rudy), zeby globalny
            // przebieg ponizej (ReapplyAllKnownPinIcons) mogl naprawic ikony BEZ potrzeby
            // podchodzenia - dziala dla kategorii 1:1 z jednym przedmiotem.
            AddCategory("Blueberry", "Blueberries", new[] { "BlueberryBush" }, null, Minimap.PinType.Icon1, removeOnPicked: false,
                iconItemNameCandidates: new[] { "Blueberry", "Blueberries" });
            AddCategory("Cloudberry", "Cloudberries", new[] { "CloudberryBush" }, null, Minimap.PinType.Icon1, "Cloudberry", removeOnPicked: false);
            AddCategory("Raspberry", "Raspberries", new[] { "RaspberryBush" }, null, Minimap.PinType.Icon1, "Raspberry", removeOnPicked: false);
            AddCategory("Lingonberry", "Lingonberries", new[] { "LingonberryBush" }, null, Minimap.PinType.Icon1, "Lingonberry", removeOnPicked: false);

            // Grzyby: rozbite na osobne kategorie (jak jagody/warzywa) zeby kazda odmiana miala
            // wlasna, precyzyjna ikone od razu przy starcie (globalny przebieg), nie tylko po
            // podejsciu. Nazwy przedmiotow niepewne - podane po kilka kandydatow na wszelki wypadek.
            AddCategory("MushroomCommon", "Mushroom", new[] { "Pickable_Mushroom" }, null, Minimap.PinType.Icon1,
                iconItemNameCandidates: new[] { "Mushroom" });
            AddCategory("MushroomBlue", "Blue Mushroom", new[] { "Pickable_Mushroom_blue" }, null, Minimap.PinType.Icon1,
                iconItemNameCandidates: new[] { "MushroomBlue", "Mushroom_blue", "BlueMushroom" });
            AddCategory("MushroomYellow", "Yellow Mushroom", new[] { "Pickable_Mushroom_yellow" }, null, Minimap.PinType.Icon1,
                iconItemNameCandidates: new[] { "MushroomYellow", "Mushroom_yellow", "YellowMushroom" });
            AddCategory("MushroomJotunPuffs", "Jotun Puffs", new[] { "Pickable_Mushroom_JotunPuffs" }, null, Minimap.PinType.Icon1,
                iconItemNameCandidates: new[] { "JotunPuffs", "MushroomJotunPuffs" });
            AddCategory("MushroomMagecap", "Magecap", new[] { "Pickable_Mushroom_Magecap" }, null, Minimap.PinType.Icon1,
                iconItemNameCandidates: new[] { "Magecap", "MushroomMagecap" });
            // Lochy: obiekt w swiecie nazywa sie "LocationProxy" (nie "DG_..." - to tylko
            // nazwy wewnetrznych prefabow lokacji, zapisanych jako hash w ZDO, nie w nazwie
            // obiektu na zewnatrz) - dopasowanie odbywa sie w osobnej galezi w ScanNearby.
            // Kazdy rodzaj (Bear Cave, Crypt, oltarz bossa...) ma wlasny przelacznik. Do v0.1.0
            // byl jeden wspolny "Dungeon" - jego stan staje sie domyslnym dla nowych przelacznikow,
            // a stary wpis znika z pliku konfiguracji.
            bool legacyDungeonEnabled = TakeLegacySetting("Categories", "Dungeon");
            bool legacyDungeonLabel = TakeLegacySetting("Labels", "Dungeon");
            foreach (var displayName in DungeonLocationDefs.Select(d => d.DisplayName).Distinct())
            {
                var def = DungeonLocationDefs.First(d => d.DisplayName == displayName);
                string key = "Dungeon" + new string(displayName.Where(char.IsLetterOrDigit).ToArray());
                var category = AddCategory(key, displayName, null, null, def.PinType, maxVerticalDeltaOverride: 60f,
                    defaultEnabled: legacyDungeonEnabled, defaultShowName: legacyDungeonLabel);
                category.MenuGroup = def.PinType == Minimap.PinType.Boss ? BossAltarsGroup
                    : def.LocationName.StartsWith("Vendor", StringComparison.Ordinal) ? "Other"
                    : "Dungeons";
                _dungeonCategoriesByName[displayName] = category;
            }
            AddCategory("CropCarrot", "Carrot", new[] { "Pickable_Carrot" }, null, Minimap.PinType.Icon1, "Carrot");
            AddCategory("CropTurnip", "Turnip", new[] { "Pickable_Turnip" }, null, Minimap.PinType.Icon1, "Turnip");
            AddCategory("CropOnion", "Onion", new[] { "Pickable_Onion" }, null, Minimap.PinType.Icon1, "Onion");
            AddCategory("CropKale", "Kale", new[] { "Pickable_Kale" }, null, Minimap.PinType.Icon1, "Kale");
            AddCategory("CropPoteitr", "Poteitr", new[] { "Pickable_Poteitr" }, null, Minimap.PinType.Icon1, "Poteitr");
            // Ule: postawione przez gracza osobno od dzikich. Dziki ul z drzew to inny obiekt
            // ("Beehive", daje krolowa pszczol), a ule wygenerowane ze swiatem (np. w opuszczonych
            // domach) to ten sam prefab co ul gracza, tylko bez tworcy - tez licza sie jako dzikie.
            var playerHive = AddCategory("Beehive", "Beehive", new[] { "piece_beehive" }, null, Minimap.PinType.Icon1);
            playerHive.PlayerBuilt = true;
            playerHive.IconPiecePrefab = "piece_beehive";
            // Dziki ul wisi na drzewie, kilka metrow nad graczem - stad wieksza tolerancja pionowa.
            var wildHive = AddCategory("BeehiveWild", "Wild Beehive", new[] { "Beehive", "piece_beehive" }, null, Minimap.PinType.Icon1,
                maxVerticalDeltaOverride: 12f);
            wildHive.PlayerBuilt = false;
            wildHive.CustomIcon = LoadEmbeddedIcon("BeehiveWild");
            wildHive.LegacyPinNames = new[] { "Beehive" };
            AddCategory("Thistle", "Thistle", new[] { "Pickable_Thistle" }, null, Minimap.PinType.Icon1, "Thistle");
            AddCategory("Dandelion", "Dandelion", new[] { "Pickable_Dandelion" }, null, Minimap.PinType.Icon1, "Dandelion");
            // Dzikie nasiona (Czarny Las / Bagna / Gory) i dzikie zboza z Rownin - jedyne zrodlo
            // tych roslin, zanim gracz zacznie je uprawiac. Nazwy prefabow potwierdzone w plikach gry.
            AddCategory("SeedCarrot", "Carrot Seeds", new[] { "Pickable_SeedCarrot" }, null, Minimap.PinType.Icon1, "CarrotSeeds");
            AddCategory("SeedTurnip", "Turnip Seeds", new[] { "Pickable_SeedTurnip" }, null, Minimap.PinType.Icon1, "TurnipSeeds");
            AddCategory("SeedOnion", "Onion Seeds", new[] { "Pickable_SeedOnion" }, null, Minimap.PinType.Icon1, "OnionSeeds");
            AddCategory("CropBarley", "Barley", new[] { "Pickable_Barley_Wild" }, null, Minimap.PinType.Icon1, "Barley");
            AddCategory("CropFlax", "Flax", new[] { "Pickable_Flax_Wild" }, null, Minimap.PinType.Icon1, "Flax");

            _shipsEnabled = Config.Bind("Categories", "Ships", true,
                "Show a live pin for ships currently being sailed (by anyone). The pin follows the ship while sailed.");
            _shipLiveCategory.Enabled = _shipsEnabled;
            _shipLiveCategory.ShowName = BindLabel("Ships", "live ship");
            _shipsDockedEnabled = Config.Bind("Categories", "ShipsDocked", true,
                "When a ship's live pin loses its crew, keep a saved pin marking where it was left instead of removing it.");
            _shipDockedCategory.Enabled = _shipsDockedEnabled;
            _shipDockedCategory.ShowName = BindLabel("ShipsDocked", "docked ship");
            _shipsDockedEnabled.SettingChanged += (_, _) =>
                SetCategoryVisibility(_shipDockedCategory, IsCategoryVisible(_shipDockedCategory));

            _portalsEnabled = Config.Bind("Categories", "Portals", true,
                "Auto-pin portals, labelled with their connection tag (the name you set on the portal).");
            _portalCategory.Enabled = _portalsEnabled; // potrzebne dla IsGone/SetCategoryVisibility
            _portalCategory.ShowName = BindLabel("Portals", "portal");
            _portalsEnabled.SettingChanged += (_, _) =>
                SetCategoryVisibility(_portalCategory, IsCategoryVisible(_portalCategory));

            // Struktury/POI: statyczne obiekty dekoracyjne generowane przy tworzeniu swiata
            // (ruiny, wioski Dvergr, kamienne kregi, wraki itd.) - w przeciwienstwie do lochow
            // to NIE sa LocationProxy z ukrytym hashem, tylko wprost nazwane prefaby w swiecie,
            // wiec dopasowanie po nazwie dziala od razu. Lista nazw zweryfikowana na podstawie
            // open-source moda "AMPED - Auto Map Pins Enhanced" (github.com/raziell74) -
            // nazwy moga byc nieaktualne dla tej wersji gry, [DIAG] w logu to zweryfikuje.
            // Hojna tolerancja pionowa jak dla Dungeon - to statyczne, widoczne struktury,
            // wiec nie ma ryzyka "oszukiwania" przez wykrywanie czegos niedostepnego.
            foreach (var (key, displayName, names, pinType) in StructureDefinitions)
            {
                var category = AddCategory(key, displayName, names, null, pinType, maxVerticalDeltaOverride: 60f);
                category.CustomIcon = LoadEmbeddedIcon(key);
                if (key == "StructRuins")
                    _looseRuinsCategory = category;
                if (key == "StructSwampTower")
                    _looseTowerCategory = category;
                if (key == "StructWoodHouse")
                    _looseWoodCategory = category;
            }

            foreach (var iconKey in DungeonLocationDefs.Select(d => d.IconKey).Where(k => k != null).Distinct())
                _dungeonIcons[iconKey] = LoadEmbeddedIcon(iconKey);

            // Uwaga: MineRock_Stone (zwykly kamien) celowo pominiety - to nie zasob.

            foreach (var c in _categories)
                _categoryByPinName[c.DisplayName] = c;
            _categoryByPinName["Ship"] = _shipLiveCategory;
            _categoryByPinName["Ship (docked)"] = _shipDockedCategory;
        }

        // Odczytuje stary wpis konfiguracji (zastapiony nowymi) i usuwa go z pliku.
        private bool TakeLegacySetting(string section, string key)
        {
            var legacy = Config.Bind(section, key, true, "Legacy setting, replaced by per-type entries.");
            bool value = legacy.Value;
            Config.Remove(legacy.Definition);
            return value;
        }

        private ConfigEntry<bool> BindLabel(string key, string displayName, bool defaultValue = true)
        {
            var entry = Config.Bind("Labels", key, defaultValue, $"Show the name label of {displayName} pins on the large map.");
            entry.SettingChanged += (_, _) => ApplyLabelVisibilityToAllPins();
            return entry;
        }

        // Pin portalu nosi sama nazwe portalu (tag gracza) - po niej nie da sie poznac, ze to
        // portal, wiec najpierw rejestr pozycji portali, potem dokladna nazwa kategorii.
        private ResourceCategory CategoryForPin(Minimap.PinData pin)
        {
            if (pin.m_type == Minimap.PinType.Icon0 && IsRegisteredPortal(pin.m_pos))
                return _portalCategory;
            return CategoryForName(pin.m_name);
        }

        private ResourceCategory CategoryForName(string pinName)
        {
            if (pinName == null)
                return null;
            if (_categoryByPinName.TryGetValue(pinName, out var category))
                return category;
            if (IsLegacyPortalName(pinName))
                return _portalCategory;
            return null;
        }

        // Rejestr pinow portali: pozycje zapisane w pliku swiata (portal stoi w miejscu, wiec to
        // pewny identyfikator). Dawne piny "Portal"/"Portal: <tag>" sa rozpoznawane po nazwie,
        // przemianowywane na sam tag i dopisywane do rejestru przy wczytaniu swiata.
        [Serializable]
        private class PortalRegistryFile
        {
            public List<Vector3> Positions = new List<Vector3>();
        }

        private const string UnnamedPortalPinName = "Portal";
        private const string LegacyPortalPrefix = "Portal: ";
        private const float PortalPinMatchRadius = 1f;
        private readonly List<Vector3> _portalPinPositions = new List<Vector3>();
        private string _portalRegistryWorld;

        private static string PortalPinName(string tag) => string.IsNullOrEmpty(tag) ? UnnamedPortalPinName : tag;

        private static bool IsLegacyPortalName(string name) =>
            name == UnnamedPortalPinName || (name != null && name.StartsWith(LegacyPortalPrefix, StringComparison.Ordinal));

        private bool EnsurePortalRegistryLoaded()
        {
            string worldName = ZNet.instance?.GetWorldName();
            if (string.IsNullOrEmpty(worldName))
                return false;
            if (worldName == _portalRegistryWorld)
                return true;

            _portalRegistryWorld = worldName;
            _portalPinPositions.Clear();
            string path = WorldFilePath("portals", worldName);
            if (!System.IO.File.Exists(path))
                return true;
            try
            {
                var data = JsonUtility.FromJson<PortalRegistryFile>(System.IO.File.ReadAllText(path));
                if (data?.Positions != null)
                    _portalPinPositions.AddRange(data.Positions);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie wczytac rejestru portali ({path}): {e}");
            }
            return true;
        }

        private void SavePortalRegistry()
        {
            try
            {
                var data = new PortalRegistryFile { Positions = _portalPinPositions.ToList() };
                string json = JsonUtility.ToJson(data);
                var check = JsonUtility.FromJson<PortalRegistryFile>(json);
                if (check?.Positions == null || check.Positions.Count != data.Positions.Count)
                {
                    Log.LogError($"Zapis rejestru portali: serializacja zgubila dane ('{json}') - NIE nadpisuje pliku.");
                    return;
                }
                WriteFileAtomically(WorldFilePath("portals", _portalRegistryWorld), json);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie zapisac rejestru portali: {e}");
            }
        }

        private bool IsRegisteredPortal(Vector3 pos) =>
            EnsurePortalRegistryLoaded() && _portalPinPositions.Any(p => Utils.DistanceXZ(p, pos) < PortalPinMatchRadius);

        private void RegisterPortalPin(Vector3 pos)
        {
            if (!EnsurePortalRegistryLoaded() || IsRegisteredPortal(pos))
                return;
            _portalPinPositions.Add(pos);
            SavePortalRegistry();
        }

        private void UnregisterPortalPin(Vector3 pos)
        {
            if (EnsurePortalRegistryLoaded() && _portalPinPositions.RemoveAll(p => Utils.DistanceXZ(p, pos) < PortalPinMatchRadius) > 0)
                SavePortalRegistry();
        }

        // Podpis pinu wlacza gra sama z dwoch miejsc: UpdatePins (tylko gdy uzna, ze cos sie
        // zmienilo) i korutyna DelayActivation sekunde po utworzeniu podpisu (podpis jest
        // tworzony od nowa za kazdym razem, gdy pin wraca w widoczny obszar mapy - np. przy
        // zoomie). Walka przez SetActive(false) zawsze przegrywa z ktoryms z nich, wiec chowamy
        // podpis przezroczystoscia CanvasGroup - tego gra w ogole nie dotyka. Nazwa pinu zostaje
        // nietknieta - po niej rozpoznajemy kategorie przy ukrywaniu/ikonach.
        [HarmonyPatch(typeof(Minimap), "CreateMapNamePin")]
        private static class Minimap_CreateMapNamePin_LabelPatch
        {
            private static void Postfix(Minimap.PinData namePin)
            {
                Instance?.ApplyLabelVisibility(namePin);
            }
        }

        private void ApplyLabelVisibility(Minimap.PinData pin)
        {
            var label = pin.m_NamePinData?.PinNameGameObject;
            if (label == null)
                return;
            var category = CategoryForPin(pin);
            // Pin bossa dodany przez gre (Vegvisir) slucha przelacznika podpisu swojego oltarza.
            if (category == null && pin.m_type == Minimap.PinType.Boss)
                category = NativeBossCategory(pin);
            bool hide = category != null && (!_showPinLabels.Value || !category.ShowName.Value);
            if (!hide && pin.m_type == Minimap.PinType.Boss && Minimap.instance != null &&
                PinsField.GetValue(Minimap.instance) is List<Minimap.PinData> pins)
                hide = IsBossPinYielding(pin, pins) || IsNativeBossPinHiddenByCategory(pin);
            SetHiddenByAlpha(label, hide);
        }

        private static void SetHiddenByAlpha(GameObject go, bool hide)
        {
            if (go == null)
                return;
            var group = go.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (!hide)
                    return;
                group = go.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
            }
            group.alpha = hide ? 0f : 1f;
        }

        private void ApplyLabelVisibilityToAllPins()
        {
            if (Minimap.instance == null || !(PinsField.GetValue(Minimap.instance) is List<Minimap.PinData> pins))
                return;
            foreach (var pin in pins)
                ApplyLabelVisibility(pin);
        }

        // Struktury nie maja odpowiednika w ObjectDB (nie sa przedmiotami), wiec nie mozna im
        // przypisac ikony przedmiotu jak rudzie/jagodom. Zamiast tego czesc kategorii ma teraz
        // WLASNA ikone (CustomIcon, ustawiana w LoadEmbeddedIcon) - wyrenderowana z modelu 3D
        // danej struktury wprost z plikow gry (legalnie, bez korzystania z cudzego moda).
        // Kategorie bez pasujacego renderu zostaja przy wbudowanej ikonie danego PinType.
        private static readonly (string Key, string DisplayName, string[] Names, Minimap.PinType PinType)[] StructureDefinitions =
        {
            ("StructRuins", "Ruins", new[] { "StoneHouse3", "StoneHouse4", "Ruin1", "Ruin2" }, Minimap.PinType.Icon2),
            ("StructLogCabin", "Log Cabin", new[] { "AbandonedLogCabin02", "AbandonedLogCabin03", "AbandonedLogCabin04" }, Minimap.PinType.Icon2),
            ("StructWoodHouse", "Wood House", new[]
            {
                "WoodHouse1", "WoodHouse2", "WoodHouse3", "WoodHouse4", "WoodHouse5", "WoodHouse6",
                "WoodHouse7", "WoodHouse8", "WoodHouse9", "WoodHouse10", "WoodHouse11", "WoodHouse12", "WoodHouse13"
            }, Minimap.PinType.Icon2),
            ("StructFarmVillage", "Farm Village", new[] { "WoodFarm1", "WoodVillage1" }, Minimap.PinType.Icon2),
            ("StructSwampHut", "Swamp Hut", new[] { "SwampHut1", "SwampHut2", "SwampHut3", "SwampHut4", "SwampHut5" }, Minimap.PinType.Icon2),
            // "SwampRuin1/2" nie istnieje w tej wersji gry (potwierdzone: brak takiego pliku
            // wsrod modeli lokacji wyciagnietych z wlasnych plikow gry). Ta sama, ciemna kamienna
            // wieza-ruina wystepuje pod nazwa "StoneTowerRuins0X" w biomach BlackForest i Mountains
            // (nie w Swamp) - potwierdzone wizualnie przez zrzut ekranu uzytkownika.
            ("StructSwampTower", "Stone Tower Ruins", new[]
            {
                "StoneTowerRuins05_leet", "StoneTowerRuins07", "StoneTowerRuins08_sunk", "StoneTowerRuins10_sunk"
            }, Minimap.PinType.Icon2),
            ("StructHarbour", "Harbour", new[] { "Mistlands_Harbour1" }, Minimap.PinType.Icon2),
            ("StructViaduct", "Viaduct", new[] { "Mistlands_Viaduct1", "Mistlands_Viaduct2" }, Minimap.PinType.Icon2),
            ("StructShipwreck", "Shipwreck", new[] { "ShipWreck01", "ShipWreck02", "ShipWreck03", "ShipWreck04" }, Minimap.PinType.Icon2),

            // "StoneCircle" nie istnieje w tej wersji gry (potwierdzone: brak takiego pliku
            // wsrod modeli lokacji wyciagnietych z wlasnych plikow gry) - realna nazwa to
            // StoneHenge3/5 (znalezione w biomie Heath). Moze byc wiecej wariantow (1,2,4) -
            // [DIAG] w logu to pokaze przy nastepnym podejsciu.
            ("StructStoneCircle", "Stone Circle", new[] { "StoneHenge3", "StoneHenge5" }, Minimap.PinType.Icon4),
            ("StructWell", "Well", new[] { "MountainWell1" }, Minimap.PinType.Icon4),
            ("StructDolmen", "Dolmen", new[] { "Dolmen01", "Dolmen02", "Dolmen03" }, Minimap.PinType.Icon4),
            ("StructRunestone", "Runestone", new[]
            {
                "Runestone_Greydwarfs", "Runestone_Draugr", "DrakeLorestone", "Runestone_Boars",
                "Runestone_BlackForest", "Runestone_Mistlands", "Runestone_Meadows", "Runestone_Swamps",
                "Runestone_Mountains", "Runestone_Plains"
            }, Minimap.PinType.Icon4),
            ("StructStatues", "Statues", new[] { "Mistlands_Statue1", "Mistlands_Statue2", "Mistlands_StatueGroup1", "Mistlands_RockSpire1" }, Minimap.PinType.Icon4),

            ("StructGiantRemains", "Giant Remains", new[] { "Mistlands_Giant1", "Mistlands_Giant2" }, Minimap.PinType.Icon3),
            ("StructGiantArmor", "Giant Armor", new[]
            {
                "Mistlands_Swords1", "Mistlands_Swords2", "Mistlands_Swords3",
                "giant_helmet1", "giant_helmet2", "giant_sword1", "giant_sword2"
            }, Minimap.PinType.Icon3),

            ("StructDvergrTower", "Dvergr Tower", new[]
            {
                "Mistlands_GuardTower1_ruined_new2", "Mistlands_GuardTower3_new", "Mistlands_GuardTower3_ruined_new",
                "Mistlands_GuardTower1_new", "Mistlands_GuardTower2_new", "Mistlands_GuardTower1_ruined_new", "Mistlands_Lighthouse1_new"
            }, Minimap.PinType.Icon0),
            ("StructInfestedMine", "Infested Mine", new[] { "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2" }, Minimap.PinType.Icon0),
            ("StructDvergrExcavation", "Dvergr Excavation", new[] { "Mistlands_Excavation1", "Mistlands_Excavation2", "Mistlands_Excavation3" }, Minimap.PinType.Icon0),
            ("StructRoadPost", "Road Post", new[] { "Mistlands_RoadPost1" }, Minimap.PinType.Icon0),

            ("StructInfestedTree", "Infested Tree", new[] { "InfestedTree01" }, Minimap.PinType.Icon1),
        };

        private ResourceCategory AddCategory(string key, string displayName, string[] exactNames, string prefix, Minimap.PinType pinType,
            string iconItemNameOverride = null, bool removeOnPicked = true, float? maxVerticalDeltaOverride = null,
            string[] iconItemNameCandidates = null, bool defaultEnabled = true, bool defaultShowName = true)
        {
            var enabled = Config.Bind("Categories", key, defaultEnabled, $"Auto-pin {displayName}.");
            var category = new ResourceCategory
            {
                Key = key,
                DisplayName = displayName,
                ExactNames = exactNames != null ? new HashSet<string>(exactNames) : null,
                Prefix = prefix,
                PinType = pinType,
                Enabled = enabled,
                ShowName = BindLabel(key, displayName, defaultShowName),
                IconItemNameOverride = iconItemNameOverride,
                IconItemNameCandidates = iconItemNameCandidates,
                RemoveOnPicked = removeOnPicked,
                MaxVerticalDeltaOverride = maxVerticalDeltaOverride
            };
            enabled.SettingChanged += (_, _) =>
                SetCategoryVisibility(category, IsCategoryVisible(category));
            _categories.Add(category);
            return category;
        }

        private void AddOreCategory(string key, string displayName, string dropItemName, string nameToken, string worldObjectName = null)
        {
            var enabled = Config.Bind("Categories", key, true, $"Auto-pin {displayName}.");
            var category = new ResourceCategory
            {
                Key = key,
                DisplayName = displayName,
                OreDropItemNames = new HashSet<string> { dropItemName },
                OreNameTokens = new HashSet<string> { nameToken },
                // Niektore zyly (potwierdzone: Tin) to NIE MineRock/MineRock5, tylko zwykly,
                // plasko nazwany obiekt - dedykowana galaz rudy w ScanNearby ich nie zlapie
                // (brak komponentu), wiec dopasowujemy je dodatkowo po nazwie w ogolnej galezi
                // (ta sama sciezka co struktury/uprawy).
                ExactNames = worldObjectName != null ? new HashSet<string> { worldObjectName } : null,
                IconItemNameOverride = dropItemName,
                PinType = Minimap.PinType.Icon3,
                Enabled = enabled,
                ShowName = BindLabel(key, displayName)
            };
            enabled.SettingChanged += (_, _) =>
                SetCategoryVisibility(category, IsCategoryVisible(category));
            _categories.Add(category);
        }

        // Ikony renderowane z wlasnych, legalnie posiadanych plikow gry (model 3D danej
        // struktury z Assets/world/Locations, zrzutowany do PNG z przezroczystym tlem) -
        // NIE z zewnetrznego moda (brak licencji na jego ikony). Zaszyte jako zasob w DLL,
        // wiec dzialaja od razu przy tworzeniu pinu I przetrwaja restart (w przeciwienstwie
        // do ikon-z-przedmiotu w ReapplyAllKnownPinIcons, ktore trzeba odtwarzac co wczytanie
        // swiata - to gotowy Sprite, nie trzeba go szukac w ObjectDB). Brak pliku dla danego
        // klucza = kategoria zostaje przy wbudowanej ikonie PinType (zwraca null, nikt tego
        // nie sprawdza pod katem bledu).
        private static Sprite LoadEmbeddedIcon(string key)
        {
            // Nazwa logiczna zasobu zalezy od RootNamespace projektu (inny w roboczym folderze,
            // inny w repozytorium) - szukamy wiec po sufiksie zamiast zgadywac pelna nazwe,
            // zeby nie bylo to kruche na zmiany konfiguracji.
            string suffix = $".Icons.{key}.png";
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            if (resourceName == null)
                return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, ms.ToArray()))
                return null;

            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        // Wlasna ikona lochu: albo gotowy, zaszyty w DLL Sprite (IconKey - renderowany z modelu
        // samodzielnej struktury typu oltarz bossa), albo ikona trofeum przeciwnika ktory tam
        // zamieszkuje (TrophyItemName - gra ma ja juz gotowa w ObjectDB, tak jak dla rudy/jagod).
        // Trofeum trzeba szukac za kazdym razem na nowo (ObjectDB nie jest gotowe w Awake).
        private Sprite ResolveDungeonIcon(string iconKey, string trophyItemName)
        {
            if (iconKey != null && _dungeonIcons.TryGetValue(iconKey, out var embedded) && embedded != null)
                return embedded;

            if (trophyItemName != null && ObjectDB.instance != null)
            {
                var itemPrefab = ObjectDB.instance.GetItemPrefab(trophyItemName);
                var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                return itemDrop?.m_itemData?.GetIcon();
            }

            return null;
        }

        // Portal to Piece (budowla), nie przedmiot - nie ma ItemDrop/wpisu w ObjectDB jak reszta
        // ikon-z-gry. Wlasna ikona budowli siedzi wprost na komponencie Piece (ta sama, ktora
        // widac w menu mlota), pobierana z prefaba przez ZNetScene.
        private static Sprite ResolvePortalIcon()
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("portal_wood") : null;
            return prefab != null ? prefab.GetComponent<Piece>()?.m_icon : null;
        }

        // Pin statku nie zapamietuje, jaki to byl statek - przy wczytaniu swiata dostaje ikone
        // Karve (albo pierwszego znalezionego statku), a dokladna ikone swojego statku, gdy ten
        // jest w poblizu (UpdateShipPins). Bez tego po restarcie znacznik "Ship (docked)" mial
        // domyslna ikone PinType (ognisko) i przy wylaczonych podpisach nie dalo sie go poznac.
        private static Sprite _genericShipIcon;

        private static Sprite ResolveGenericShipIcon()
        {
            if (_genericShipIcon != null || ZNetScene.instance == null)
                return _genericShipIcon;
            var shipPrefabs = ZNetScene.instance.m_prefabs.Where(p => p != null && p.GetComponent<Ship>() != null).ToList();
            var prefab = shipPrefabs.FirstOrDefault(p => p.name == "Karve") ?? shipPrefabs.FirstOrDefault();
            _genericShipIcon = prefab != null ? prefab.GetComponent<Piece>()?.m_icon : null;
            return _genericShipIcon;
        }

        private static bool IsShipPinName(string name) => name == "Ship" || name == "Ship (docked)";

        private void Update()
        {
            try
            {
                RunUpdate();
            }
            catch (Exception e)
            {
                Log.LogError($"Auto Waypoints error (will retry next tick): {e}");
            }
        }

        private void RunUpdate()
        {
            if (Player.m_localPlayer == null || Minimap.instance == null)
                return;

            // ObjectDB moze jeszcze nie byc gotowe (puste/null) na SAMYM pierwszym ticku po
            // pojawieniu sie gracza - ReapplyAllKnownPinIcons po cichu nic wtedy nie robi
            // (sam sprawdza ObjectDB.instance == null i wraca). Dla pinow napotkanych normalnie
            // to sie samo-naprawia przy kolejnym podejsciu (ReapplyIcon w ScanNearby), ale piny
            // PRZYWROCONE z ukrycia (RestoreHiddenPinsForCategory) nie dostawaly drugiej szansy -
            // stad zglaszany brak ikonek. Czekamy wiec z obiema jednorazowymi fazami az ObjectDB
            // bedzie NAPRAWDE gotowe, zamiast probowac raz i olewac wynik.
            if (ObjectDB.instance == null)
                return;

            // Patrz komentarz przy MapHasGeneratedField - bez tego jednorazowe fazy nizej widzialy
            // liste pinow ktora jeszcze nie zdazyla sie zapelnic prawdziwymi danymi z zapisu gracza.
            if (MapHasGeneratedField.GetValue(Minimap.instance) is bool hasGenerated && !hasGenerated)
                return;

            if (!_globalIconPassDone)
            {
                ReapplyAllKnownPinIcons();
                _globalIconPassDone = true;
            }

            if (!_hiddenPinsLoaded)
            {
                TryLoadHiddenPins();
                // Wymusza zgodnosc ze stanem configu od razu przy starcie swiata, niezaleznie
                // od tego co akurat zapisala sama gra (wlasny zapis swiata Valheim moze byc
                // zrobiony w innym momencie niz nasze wywolanie RemovePin, wiec poleganie
                // wylacznie na naszym pliku hidden_pins bywalo niewystarczajace - piny czasem
                // wracaly z powrotem mimo wylaczonej kategorii). RefreshAllVisibility() chowa
                // WSZYSTKO co powinno byc schowane i przywraca WSZYSTKO co powinno byc widoczne,
                // wiec samo-naprawia sie niezaleznie od tego w jakim stanie zastalo mape.
                RefreshAllVisibility();
            }

            SaveDiscoveredIfDirty();
            SaveHiddenPinsIfDirty();
            ApplyPendingGroupInheritance();
            ApplyPendingHides();

            // Ship ma wlasny, szybszy timer (nie ten sam co reszta skanu) - to pojedynczy,
            // zywy pin ktory PORUSZA sie wraz ze statkiem, a nie kolejny wpis co skan.
            _shipUpdateTimer += Time.deltaTime;
            if (_shipUpdateTimer >= ShipUpdateInterval)
            {
                _shipUpdateTimer = 0f;
                UpdateShipPins();
            }

            // Jesli poprzedni skan jeszcze nie zostal w pelni przetworzony (patrz komentarz przy
            // HitsPerFrame), doprzetwarzamy kolejna porcje i NIE zaczynamy nowego skanu w tej
            // samej klatce - to jest sedno rozkladania kosztu na wiecej klatek.
            if (_pendingHitCount > 0 && _pendingHitIndex < _pendingHitCount)
            {
                ProcessPendingScanBatch();
                return;
            }

            _scanTimer += Time.deltaTime;
            if (_scanTimer < ScanInterval)
                return;
            _scanTimer = 0f;

            PruneTracked();

            // Skanujemy ZAWSZE, takze przy schowanych pinach - dane maja byc zbierane caly
            // czas, a nowe znaleziska z ukrytych kategorii od razu trafiaja do schowanych
            // (patrz _pendingHide), zeby po wlaczeniu gracz widzial wszystko co minal.
            ScanNearby();
        }

        // Wielokrotnie uzywany bufor dla OverlapSphereNonAlloc - Physics.OverlapSphere (zwykle,
        // alokujace) tworzy NOWA tablice przy KAZDYM skanie (co 1.5s), a jej rozmiar rosnie
        // szescienne z zasiegiem (objetosc kuli) - przy 35m+ to potrafi byc kilkaset obiektow
        // na tick, co regularnie zapycha GC i daje zauwazalne ziecia FPS. NonAlloc + jeden,
        // trzymany bufor eliminuje te alokacje calkowicie.
        private Collider[] _scanBuffer = new Collider[256];

        // Skan dzieli sie na dwie fazy zamiast robic wszystko naraz: (1) tanie zapytanie fizyczne
        // (ScanNearby, wywolywane raz na ScanInterval), (2) stopniowe przetworzenie trafien po
        // HitsPerFrame na klatke (ProcessPendingScanBatch, wywolywane co klatke dopoki trwa).
        // Samo NonAlloc usuwa alokacje, ale nie zmniejsza kosztu PRZETWORZENIA (GetComponentInParent
        // x kilka na kazde trafienie) - przy duzym zasiegu i kilkuset trafieniach to nadal bylo
        // jedno duze "zdarcie" w pojedynczej klatce. Rozbicie na porcje rozklada ten sam koszt
        // na wiecej klatek, wiec pojedyncza klatka nigdy nie robi zbyt duzo naraz.
        private const int HitsPerFrame = 40;
        private int _pendingHitCount;
        private int _pendingHitIndex;
        private Vector3 _pendingPlayerPos;
        private HashSet<GameObject> _pendingSeenRoots;
        private List<string> _pendingDiagNames;

        private void ScanNearby()
        {
            Vector3 playerPos = Player.m_localPlayer.transform.position;
            Vector3 center = playerPos + Vector3.up;

            int hitCount = Physics.OverlapSphereNonAlloc(center, _scanRadius.Value, _scanBuffer);
            if (hitCount >= _scanBuffer.Length)
            {
                // Bufor sie zapelnil - mogly zostac pominiete obiekty. Powiekszamy i powtarzamy
                // zapytanie w tym samym ticku, zamiast czekac do nastepnego (przy duzym zasiegu
                // pierwszy tick po zmianie ustawienia bylby wtedy niepelny).
                Array.Resize(ref _scanBuffer, _scanBuffer.Length * 2);
                hitCount = Physics.OverlapSphereNonAlloc(center, _scanRadius.Value, _scanBuffer);
            }

            _pendingPlayerPos = playerPos;
            _pendingHitCount = hitCount;
            _pendingHitIndex = 0;
            _pendingSeenRoots = new HashSet<GameObject>();
            _pendingDiagNames = _debugLogNearby.Value ? new List<string>() : null;
        }

        // Przetwarza kolejna porcje (max HitsPerFrame) trafien z ostatniego ScanNearby(). Wolane
        // co klatke z RunUpdate dopoki cala partia nie zostanie skonsumowana.
        private void ProcessPendingScanBatch()
        {
            int end = Mathf.Min(_pendingHitIndex + HitsPerFrame, _pendingHitCount);
            for (int i = _pendingHitIndex; i < end; i++)
                ProcessScanHit(_scanBuffer[i], _pendingPlayerPos, _pendingSeenRoots, _pendingDiagNames);
            _pendingHitIndex = end;

            if (_pendingHitIndex < _pendingHitCount)
                return;

            if (_debugLogNearby.Value && _pendingDiagNames != null)
                Log.LogInfo($"[DIAG] obiekty w zasiegu skanu ({_pendingDiagNames.Count}): {string.Join(", ", _pendingDiagNames.Distinct())}");

            _pendingHitCount = 0;
            _pendingSeenRoots = null;
            _pendingDiagNames = null;
        }

        // To co kiedys bylo cialem petli w ScanNearby - kazdy dawny "continue" jest teraz po
        // prostu "return" (koniec obslugi TEGO JEDNEGO trafienia), logika wewnatrz w ogole sie
        // nie zmienila.
        private void ProcessScanHit(Collider hit, Vector3 playerPos, HashSet<GameObject> seenRoots, List<string> diagNames)
        {
            // Kolider zebrany w ScanNearby moze zdazyc zniknac (wydobyty, wyladowany bo gracz
            // odbiegl, ucieta roslina) zanim dojdzie do niego kolej w rozlozonym na klatki
            // przetwarzaniu (patrz HitsPerFrame) - odwolanie do juz zniszczonego obiektu Unity
            // rzuca NullReferenceException mimo ze C# widzi go jako "nie-null" (przeciazone ==
            // w UnityEngine.Object). To byla przyczyna powtarzajacego sie bledu co tick, ktory
            // blokowal caly skan (zlapany wyzej w Update, ale i tak nic nie zdazylo sie dodac).
            if (hit == null)
                return;

            // --- Ruda: MineRock/MineRock5 maja kolidery na oderwanych, technicznie
            // nazwanych fragmentach skaly - identyfikujemy przez komponent w gorze
            // hierarchii, nie przez transform.root.name (ktore dla nich jest bezuzyteczne,
            // np. "MineRock5 m_meshFilter").
            var mineRock = hit.GetComponentInParent<MineRock>();
            var mineRock5 = hit.GetComponentInParent<MineRock5>();
            // Nienaruszone zloze (np. rock4_copper) to lekki Destructible bez MineRock5 - gra
            // podmienia je na wersje z kawalkami dopiero przy pierwszym uderzeniu. Rodzaj rudy
            // bierzemy z tego, czym zloze sie stanie (prefab m_spawnWhenDamaged/Destroyed), wiec
            // dziala dla kazdej rudy, bez listy nazw prefabow.
            Destructible intactDeposit = null;
            if (mineRock == null && mineRock5 == null)
            {
                var destructible = hit.GetComponentInParent<Destructible>();
                if (destructible != null && TryGetOreTemplate(destructible, out mineRock, out mineRock5))
                    intactDeposit = destructible;
            }
            if (mineRock != null || mineRock5 != null)
            {
                GameObject oreObj = intactDeposit != null ? intactDeposit.gameObject
                    : mineRock != null ? mineRock.gameObject : mineRock5.gameObject;
                if (_tracked.ContainsKey(oreObj))
                {
                    if (_debugLogNearby.Value && seenRoots.Add(oreObj))
                        Log.LogInfo($"[DIAG-ORE] '{oreObj.name}' juz sledzona, pin '{_tracked[oreObj].Pin?.m_name}' @ {_tracked[oreObj].Pin?.m_pos}, na mapie={(PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>)?.Contains(_tracked[oreObj].Pin)}");
                    return;
                }

                // Uwaga: NIE porownujemy do oreObj.transform.position.y - to pivot calej
                // zyly (MineRock5), ktory bywa kilka metrow nad/pod miejscem gdzie gracz
                // faktycznie stoi. Liczymy odleglosc w pionie od ZAKRESU wysokosci trafionego
                // kawalka skaly (0, gdy gracz jest miedzy jego dolem a gora) - stojac na
                // szczycie duzej zyly ma sie ja "pod stopami", a nie kilka metrow nizej jak
                // wychodzilo ze srodka kolidera. Nadal odrzuca zyle kilka pieter nizej w jaskini.
                var b = hit.bounds;
                float verticalGap = Mathf.Max(0f, b.min.y - playerPos.y, playerPos.y - b.max.y);
                if (verticalGap > MaxVerticalDelta)
                {
                    if (_debugLogNearby.Value)
                        Log.LogInfo($"[DIAG-ORE] '{oreObj.name}' kawalek '{hit.name}' odrzucony w pionie: gracz y={playerPos.y:0.0}, kawalek y={b.min.y:0.0}..{b.max.y:0.0}, roznica={verticalGap:0.0}m");
                    return;
                }

                // Dopiero PO filtrze wysokosci: zyla MineRock5 ma dziesiatki kawalkow. Gdy
                // oznaczalo sie ja jako obsluzona przy pierwszym trafionym kawalku (np.
                // zakopanym gleboko), odpadala z filtra, a kawalki obok gracza byly juz pomijane.
                if (!seenRoots.Add(oreObj))
                    return;

                DropTable dropTable = mineRock != null ? mineRock.m_dropItems : mineRock5.m_dropItems;
                string nameToken = mineRock != null ? mineRock.m_name : mineRock5.m_name;
                string dropItemName = ResolveDropItemName(dropTable);

                if (_debugLogNearby.Value)
                    Log.LogInfo($"[DIAG-ORE] {(mineRock != null ? "MineRock" : "MineRock5")} m_name='{nameToken}' dropItem='{dropItemName}' pos={oreObj.transform.position}");

                // Dopasowanie po m_name (pewny token typu "$piece_deposit_copper") ALBO,
                // jesli nieznany, po nazwie przedmiotu z drop table (pomijajac "Stone" -
                // to wspolny "smiec" dla kazdego typu zyly, nie sam wlasciwy surowiec).
                var oreCategory = _categories.FirstOrDefault(c =>
                    ((c.OreNameTokens != null && nameToken != null && c.OreNameTokens.Contains(nameToken)) ||
                     (c.OreDropItemNames != null && dropItemName != null && dropItemName != "Stone" && c.OreDropItemNames.Contains(dropItemName))));
                if (oreCategory == null)
                {
                    if (_debugLogNearby.Value)
                        Log.LogInfo($"[DIAG-ORE] '{oreObj.name}' nie pasuje do zadnej kategorii (m_name='{nameToken}', drop='{dropItemName}')");
                    return;
                }

                var existingOrePin = FindNearbyPin(oreCategory, oreObj.transform.position);
                if (existingOrePin != null)
                {
                    if (_debugLogNearby.Value)
                        Log.LogInfo($"[DIAG-ORE] '{oreObj.name}' przejmuje istniejacy pin '{existingOrePin.m_name}' @ {existingOrePin.m_pos} (zyla @ {oreObj.transform.position})");
                    if (!_tracked.Values.Any(t => t.Pin == existingOrePin))
                        _tracked[oreObj] = new TrackedResource { Obj = oreObj, Pin = existingOrePin, PickableComp = null, Category = oreCategory, ReplacedWhenDamaged = intactDeposit != null };
                    ReapplyIcon(existingOrePin, oreCategory, oreObj, null, dropItemName);
                    return;
                }

                AddResourcePin(oreObj, oreCategory, dropItemName, null);
                if (intactDeposit != null && _tracked.TryGetValue(oreObj, out var added))
                    added.ReplacedWhenDamaged = true;
                return;
            }

            // --- Portal: nazwa pinu to tag polaczenia ustawiony przez gracza, nie stala
            // nazwa kategorii - dlatego osobna sciezka zamiast zwyklego dopasowania po nazwie.
            var teleport = hit.GetComponentInParent<TeleportWorld>();
            if (teleport != null)
            {
                GameObject portalObj = teleport.gameObject;
                if (!seenRoots.Add(portalObj))
                    return;
                if (_tracked.ContainsKey(portalObj))
                    return;
                if (Mathf.Abs(portalObj.transform.position.y - playerPos.y) > MaxVerticalDelta)
                    return;

                string pinName = PortalPinName(teleport.GetText());
                Sprite portalIcon = ResolvePortalIcon();

                var existingPortalPin = FindNearbyPortalPin(portalObj.transform.position);
                if (existingPortalPin != null)
                {
                    RegisterPortalPin(existingPortalPin.m_pos);
                    RenamePin(existingPortalPin, pinName); // tag mogl sie zmienic od czasu dodania
                    // Gra nie zapisuje niestandardowej ikony pinu - tak samo jak przy strukturach/
                    // lochach trzeba ja odtworzyc przy kazdym ponownym napotkaniu.
                    if (portalIcon != null)
                    {
                        existingPortalPin.m_icon = portalIcon;
                        if (existingPortalPin.m_iconElement != null)
                            existingPortalPin.m_iconElement.sprite = portalIcon;
                    }
                    if (!_tracked.Values.Any(t => t.Pin == existingPortalPin))
                        _tracked[portalObj] = new TrackedResource { Obj = portalObj, Pin = existingPortalPin, Category = _portalCategory };
                    return;
                }

                // Rejestracja PRZED AddPin - postfix AddPin (odkrywanie, chowanie) rozpoznaje juz portal.
                RegisterPortalPin(portalObj.transform.position);
                var portalPin = CreatePinForFind(_portalCategory, portalObj.transform.position, Minimap.PinType.Icon0, pinName, portalIcon);
                _tracked[portalObj] = new TrackedResource { Obj = portalObj, Pin = portalPin, Category = _portalCategory };
                Log.LogInfo($"Auto-pin dodany: {pinName} @ {portalObj.transform.position} (ikona: {(portalIcon != null ? "OK" : "brak")})");
                return;
            }

            // --- Dungeon: obiekt w swiecie to "LocationProxy" - typ lokacji jest zapisany
            // jako hash stringa w ZDO (ZDOVars.s_location), NIE w nazwie obiektu ("DG_..."
            // to tylko wewnetrzne nazwy prefabow lokacji, uzywane do liczenia hasha).
            var locationProxy = hit.GetComponentInParent<LocationProxy>();
            if (locationProxy != null)
            {
                GameObject locObj = locationProxy.gameObject;
                if (!seenRoots.Add(locObj))
                    return;
                if (_tracked.ContainsKey(locObj))
                    return;

                var znetView = locObj.GetComponent<ZNetView>();
                var zdo = znetView != null ? znetView.GetZDO() : null;
                int locationHash = zdo != null ? zdo.GetInt(ZDOVars.s_location) : 0;

                bool isDungeon = DungeonLocationByHash.TryGetValue(locationHash, out var dungeonInfo);

                if (_debugLogNearby.Value)
                {
                    string resolvedName = "?";
                    var locationsByHash = ZoneSystem.instance != null
                        ? LocationsByHashField.GetValue(ZoneSystem.instance) as Dictionary<int, ZoneSystem.ZoneLocation>
                        : null;
                    if (locationsByHash != null && locationsByHash.TryGetValue(locationHash, out var zoneLoc))
                        resolvedName = $"m_name='{zoneLoc.m_name}' m_prefabName='{zoneLoc.m_prefabName}'";
                    Log.LogInfo($"[DIAG-DUNGEON] LocationProxy locationHash={locationHash} resolved=[{resolvedName}] isDungeon={isDungeon} pos={locObj.transform.position}");
                }

                if (!isDungeon)
                    return;
                var dungeonCategory = _dungeonCategoriesByName[dungeonInfo.DisplayName];
                if (Mathf.Abs(locObj.transform.position.y - playerPos.y) > (dungeonCategory.MaxVerticalDeltaOverride ?? MaxVerticalDelta))
                    return;

                // Adopcja po pozycji (nie po nazwie - kazdy typ dungeonu ma inna nazwe pinu).
                Sprite dungeonIcon = ResolveDungeonPinIcon(dungeonInfo.PinType, dungeonInfo.IconKey, dungeonInfo.TrophyItemName);

                var existingDungeonPin = FindNearbyDungeonPin(locObj.transform.position);
                if (existingDungeonPin != null)
                {
                    existingDungeonPin.m_name = dungeonInfo.DisplayName;
                    // Gra nie zapisuje niestandardowej ikony pinu (tak samo jak dla struktur) -
                    // bez tego istniejacy pin nigdy by nie dostal wlasnej ikony, tylko sama
                    // nazwa by sie odswiezala (to byl dokladnie zgloszony problem z Bear Cave).
                    if (dungeonIcon != null)
                    {
                        existingDungeonPin.m_icon = dungeonIcon;
                        if (existingDungeonPin.m_iconElement != null)
                            existingDungeonPin.m_iconElement.sprite = dungeonIcon;
                    }
                    if (!_tracked.Values.Any(t => t.Pin == existingDungeonPin))
                        _tracked[locObj] = new TrackedResource { Obj = locObj, Pin = existingDungeonPin, Category = dungeonCategory };
                    return;
                }

                var dungeonPin = CreatePinForFind(dungeonCategory, locObj.transform.position, dungeonInfo.PinType, dungeonInfo.DisplayName, dungeonIcon);
                _tracked[locObj] = new TrackedResource { Obj = locObj, Pin = dungeonPin, Category = dungeonCategory };
                Log.LogInfo($"Auto-pin dodany: {dungeonInfo.DisplayName} @ {locObj.transform.position}");
                return;
            }

            GameObject root = hit.transform.root.gameObject;
            if (!seenRoots.Add(root))
                return;

            string name = root.name.Replace("(Clone)", string.Empty);
            if (_debugLogNearby.Value && diagNames != null)
            {
                float dy = Mathf.Abs(root.transform.position.y - playerPos.y);
                diagNames.Add(dy <= MaxVerticalDelta ? name : $"{name} [dy={dy:0.0}, poza zasiegiem pionowym]");
            }

            if (_tracked.ContainsKey(root))
                return;

            if (RuinPieceNames.Contains(name))
            {
                HandleRuinPieceSighting(root, playerPos);
                return;
            }

            var category = _categories.FirstOrDefault(c => c.Matches(name) && MatchesBuilder(c, root));
            if (category == null)
                return;

            // Dungeon: punkt zakotwiczenia generatora lochu bywa daleko ponizej wejscia
            // (glebia interioru), wiec dla tej kategorii uzywamy duzo wiekszej tolerancji
            // pionowej - bez ryzyka "oszukiwania", bo samo wejscie i tak widac na powierzchni.
            float verticalTolerance = category.MaxVerticalDeltaOverride ?? MaxVerticalDelta;
            if (Mathf.Abs(root.transform.position.y - playerPos.y) > verticalTolerance)
                return;

            var pickable = root.GetComponentInChildren<Pickable>();

            // Szukamy pinu tej samej kategorii w poblizu - wsrod WSZYSTKICH pinow na mapie,
            // nie tylko tych ktore mod pamieta z biezacej sesji. Dzieki temu restart gry nie
            // powoduje ponownego duplikowania pinow dodanych w poprzedniej sesji (mod i tak
            // traci o nich pamiec po restarcie, bo _tracked zaczyna sie od zera) - zamiast
            // tego "przejmujemy" istniejacy pin z powrotem pod nadzor.
            var existingPin = FindNearbyPin(category, root.transform.position);
            if (existingPin == null && category.LegacyPinNames != null)
            {
                existingPin = FindNearbyPinNamed(category.LegacyPinNames, root.transform.position);
                if (existingPin != null)
                {
                    existingPin.m_name = category.DisplayName;
                    OnPinRenamed(existingPin);
                }
            }
            if (existingPin != null)
            {
                if (!_tracked.Values.Any(t => t.Pin == existingPin))
                    _tracked[root] = new TrackedResource { Obj = root, Pin = existingPin, PickableComp = pickable, Category = category };
                // Gra NIE zapisuje niestandardowej ikony pinu - po kazdym wczytaniu swiata
                // wraca do domyslnej dla danego PinType. Odtwarzamy ja wiec przy kazdym
                // ponownym napotkaniu pinu, nie tylko raz przy tworzeniu.
                ReapplyIcon(existingPin, category, root, pickable, name);
                return;
            }

            AddResourcePin(root, category, name, pickable);
        }

        // Zywy pin dla kazdego statku ktory AKTUALNIE jest zaogowany (przez kogokolwiek, nie
        // tylko lokalnego gracza) - pin PORUSZA sie wraz ze statkiem (ustawiamy m_pos co tick),
        // zamiast zostawiac nowy pin za kazdym razem. Gdy nikt juz nie plynie, pin NIE znika -
        // zamienia sie w trwaly znacznik "tu zostawilem statek" (m_save=true), zeby gracz mogl
        // go potem odnalezc. Jesli ten sam statek zostanie ponownie wsiadniety w tym samym
        // miejscu, "zaparkowany" pin jest przejmowany z powrotem do zywego trybu (bez duplikatu).
        private void UpdateShipPins()
        {
            if (!_showAutoPins.Value || !_shipsEnabled.Value)
            {
                if (_shipPins.Count > 0)
                {
                    foreach (var pin in _shipPins.Values)
                        Minimap.instance.RemovePin(pin);
                    _shipPins.Clear();
                }
                return;
            }

            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            var active = new HashSet<Ship>();
            foreach (var updater in Ship.Instances)
            {
                if (!(updater is Ship ship) || !ship.HasPlayerOnboard())
                    continue;

                active.Add(ship);
                if (_shipPins.TryGetValue(ship, out var pin))
                {
                    pin.m_pos = ship.transform.position;
                    continue;
                }

                // Statek to Piece (budowla ze stoczni), nie przedmiot - wlasna ikona (ta sama co
                // w menu mlota) siedzi wprost na komponencie Piece, tak samo jak przy portalu.
                var icon = ship.GetComponent<Piece>()?.m_icon;

                var docked = pins?.FirstOrDefault(p =>
                    p.m_name != null && p.m_name.StartsWith("Ship", StringComparison.Ordinal) &&
                    Vector3.Distance(p.m_pos, ship.transform.position) < MinPinSpacing);
                if (docked != null)
                {
                    docked.m_name = "Ship";
                    docked.m_save = false;
                    OnPinRenamed(docked);
                    docked.m_pos = ship.transform.position;
                    if (icon != null)
                    {
                        docked.m_icon = icon;
                        if (docked.m_iconElement != null)
                            docked.m_iconElement.sprite = icon;
                    }
                    _shipPins[ship] = docked;
                }
                else
                {
                    // Schowany znacznik "tu zostawilem statek" przestaje byc aktualny, gdy ktos
                    // znow na nim plynie - inaczej kazdy rejs zostawialby kolejny martwy wpis.
                    RemoveHiddenRecordsNear(_shipDockedCategory, ship.transform.position, MinPinSpacing);
                    var newPin = Minimap.instance.AddPin(ship.transform.position, Minimap.PinType.Icon0, "Ship", false, false);
                    if (icon != null)
                        newPin.m_icon = icon;
                    _shipPins[ship] = newPin;
                }
            }

            foreach (var stale in _shipPins.Keys.Where(s => s == null || !active.Contains(s)).ToList())
            {
                var pin = _shipPins[stale];
                _shipPins.Remove(stale);
                if (pin == null)
                    continue;

                // Nikt juz nie plynie - zamiast usuwac, zostawiamy trwaly znacznik ostatniej
                // pozycji (zapisywany, zeby przetrwal tez restart gry, nie tylko zejscie z pokladu).
                // Przy ukrytych "Docked" znacznik od razu trafia do schowanych (OnPinRenamed).
                pin.m_name = "Ship (docked)";
                pin.m_save = true;
                OnPinRenamed(pin);
            }

            // Wczytany (w poblizu) statek, na ktorym nikt nie plynie - jego znacznik dostaje
            // dokladna ikone tego statku zamiast ogolnej z przebiegu przy wczytaniu swiata.
            if (pins == null)
                return;
            foreach (var updater in Ship.Instances)
            {
                if (!(updater is Ship ship) || active.Contains(ship))
                    continue;
                var shipIcon = ship.GetComponent<Piece>()?.m_icon;
                if (shipIcon == null)
                    continue;
                var dockedPin = pins.FirstOrDefault(p => p.m_name == "Ship (docked)" &&
                    Vector3.Distance(p.m_pos, ship.transform.position) < MinPinSpacing);
                if (dockedPin == null || dockedPin.m_icon == shipIcon)
                    continue;
                dockedPin.m_icon = shipIcon;
                if (dockedPin.m_iconElement != null)
                    dockedPin.m_iconElement.sprite = shipIcon;
            }
        }

        // Naprawia ikonki od razu przy wejsciu do swiata, dla WSZYSTKICH juz zapisanych
        // pinow (nie tylko tych obok gracza) - gra nie zapisuje niestandardowej ikony pinu,
        // wiec po kazdym wczytaniu wraca ona do domyslnej dla danego PinType. Dziala dla
        // kategorii z jednym, pewnym przedmiotem (IconItemNameOverride/IconItemNameCandidates).
        private void ReapplyAllKnownPinIcons()
        {
            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            if (pins == null || ObjectDB.instance == null)
                return;
            ResolvePieceIcons();

            int fixedCount = 0;
            var failuresByCategory = new Dictionary<string, int>();
            foreach (var pin in pins)
            {
                // Lochy (np. oltarze bossow) tez maja wlasna ikone, ale nie sa ResourceCategory -
                // sprawdzamy je osobno, zeby juz zapisane piny z poprzednich sesji tez sie naprawily
                // od razu przy wczytaniu swiata, a nie dopiero gdy gracz znow do nich podejdzie.
                if (CategoryForPin(pin) == _portalCategory)
                {
                    // Jednorazowa migracja dawnych pinow "Portal: <tag>" na sama nazwe portalu.
                    if (pin.m_name.StartsWith(LegacyPortalPrefix, StringComparison.Ordinal))
                        RenamePin(pin, pin.m_name.Substring(LegacyPortalPrefix.Length));
                    RegisterPortalPin(pin.m_pos);

                    var pIcon = ResolvePortalIcon();
                    if (pIcon != null)
                    {
                        pin.m_icon = pIcon;
                        if (pin.m_iconElement != null)
                            pin.m_iconElement.sprite = pIcon;
                        fixedCount++;
                    }
                    continue;
                }

                if (IsNativeBossPin(pin, CategoryForPin(pin)))
                {
                    if (ApplyNativeBossIcon(pin))
                        fixedCount++;
                    continue;
                }

                if (IsShipPinName(pin.m_name))
                {
                    var sIcon = ResolveGenericShipIcon();
                    if (sIcon != null)
                    {
                        pin.m_icon = sIcon;
                        if (pin.m_iconElement != null)
                            pin.m_iconElement.sprite = sIcon;
                        fixedCount++;
                    }
                    continue;
                }

                var dungeonMatch = DungeonLocationDefs.FirstOrDefault(d =>
                    d.DisplayName == pin.m_name && (d.IconKey != null || d.TrophyItemName != null));
                if (dungeonMatch.DisplayName != null)
                {
                    var dIcon = ResolveDungeonPinIcon(dungeonMatch.PinType, dungeonMatch.IconKey, dungeonMatch.TrophyItemName);
                    if (dIcon != null)
                    {
                        pin.m_icon = dIcon;
                        if (pin.m_iconElement != null)
                            pin.m_iconElement.sprite = dIcon;
                        fixedCount++;
                    }
                    continue;
                }

                var category = _categories.FirstOrDefault(c => c.DisplayName == pin.m_name &&
                    (c.IconItemNameOverride != null || c.IconItemNameCandidates != null || c.CustomIcon != null));
                if (category == null)
                    continue;

                // Wlasna ikona (struktury) to gotowy Sprite zaszyty w DLL - nie trzeba go szukac
                // w ObjectDB jak dla przedmiotow, wiec nie moze sie tu "nie udac".
                if (category.CustomIcon != null)
                {
                    pin.m_icon = category.CustomIcon;
                    if (pin.m_iconElement != null)
                        pin.m_iconElement.sprite = category.CustomIcon;
                    fixedCount++;
                    continue;
                }

                var candidates = category.IconItemNameCandidates ?? new[] { category.IconItemNameOverride };
                Sprite icon = null;
                foreach (var candidate in candidates)
                {
                    var itemPrefab = ObjectDB.instance.GetItemPrefab(candidate);
                    var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                    icon = itemDrop?.m_itemData?.GetIcon();
                    if (icon != null)
                        break;
                }

                if (icon == null)
                {
                    failuresByCategory.TryGetValue(category.DisplayName, out int n);
                    failuresByCategory[category.DisplayName] = n + 1;
                    continue;
                }

                pin.m_icon = icon;
                if (pin.m_iconElement != null)
                    pin.m_iconElement.sprite = icon;
                fixedCount++;
            }

            Log.LogInfo($"Globalny przebieg ikonek pinow: naprawiono {fixedCount} z {pins.Count}.");
            foreach (var kv in failuresByCategory)
                Log.LogWarning($"Globalny przebieg ikonek: '{kv.Key}' nie naprawiono ({kv.Value}x) - ObjectDB.GetItemPrefab nie znalazl przedmiotu.");
        }

        // Postawiony przez gracza = Piece z niezerowym tworca; dziki ul z drzew nie ma Piece w ogole.
        private static bool MatchesBuilder(ResourceCategory category, GameObject root)
        {
            if (category.PlayerBuilt == null)
                return true;
            var piece = root.GetComponentInParent<Piece>() ?? root.GetComponentInChildren<Piece>();
            bool builtByPlayer = piece != null && piece.GetCreator() != 0L;
            return builtByPlayer == category.PlayerBuilt.Value;
        }

        private static Minimap.PinData FindNearbyPinNamed(string[] names, Vector3 pos)
        {
            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            return pins?.FirstOrDefault(p => names.Contains(p.m_name) && Vector3.Distance(p.m_pos, pos) < MinPinSpacing);
        }

        // Prefaby budowli sa dostepne dopiero w swiecie (ZNetScene), nie w Awake - stad ikona
        // z menu mlota ustawiana przed pierwszym przebiegiem ikon, a nie w BuildConfig.
        private void ResolvePieceIcons()
        {
            if (ZNetScene.instance == null)
                return;
            foreach (var category in _categories.Where(c => c.IconPiecePrefab != null && c.CustomIcon == null))
                category.CustomIcon = ZNetScene.instance.GetPrefab(category.IconPiecePrefab)?.GetComponent<Piece>()?.m_icon;
        }

        private static Minimap.PinData FindNearbyPin(ResourceCategory category, Vector3 pos)
        {
            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            return pins?.FirstOrDefault(p => p.m_name == category.DisplayName && Vector3.Distance(p.m_pos, pos) < MinPinSpacing);
        }

        // Pin portalu w poblizu - z rejestru pozycji albo (dawne piny) po nazwie "Portal: ...",
        // nie po dokladnej nazwie, bo tag polaczenia moze sie zmienic od czasu dodania pinu.
        private Minimap.PinData FindNearbyPortalPin(Vector3 pos)
        {
            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            return pins?.FirstOrDefault(p => CategoryForPin(p) == _portalCategory && Vector3.Distance(p.m_pos, pos) < MinPinSpacing);
        }

        // Jak FindNearbyPortalPin, ale dla dungeonow: dopasowuje po ktorejkolwiek ze znanych nazw dungeonow
        // (nie po prefiksie, bo kazdy typ ma inna nazwe) - chroni przed przejeciem przypadkowego,
        // niepowiazanego pinu stojacego obok wejscia do lochu.
        private static readonly HashSet<string> DungeonDisplayNames =
            new HashSet<string>(DungeonLocationDefs.Select(d => d.DisplayName));

        private static Minimap.PinData FindNearbyDungeonPin(Vector3 pos)
        {
            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            return pins?.FirstOrDefault(p =>
                p.m_name != null && DungeonDisplayNames.Contains(p.m_name) &&
                Vector3.Distance(p.m_pos, pos) < MinPinSpacing);
        }

        private static string ResolveDropItemName(DropTable table)
        {
            // "Stone" jest wspolnym "smieciowym" dropem prawie kazdej zyly rudy - pomijamy go,
            // zeby nie wziac go pomylkowo za "ten jeden" przedmiot z tabeli.
            var drop = table?.m_drops?.FirstOrDefault(d => d.m_item != null && d.m_item.name != "Stone");
            return drop?.m_item?.name?.Replace("(Clone)", string.Empty);
        }

        private void ReapplyIcon(Minimap.PinData pin, ResourceCategory category, GameObject root, Pickable pickable, string matchedName)
        {
            if (category.CustomIcon != null)
            {
                pin.m_icon = category.CustomIcon;
                if (pin.m_iconElement != null)
                    pin.m_iconElement.sprite = category.CustomIcon;
                return;
            }

            var icon = ResolveIcon(root, pickable, category.IconItemNameOverride ?? matchedName, out string diag);

            if (_debugLogNearby.Value)
                Log.LogInfo($"[DIAG-ICON] ReapplyIcon '{category.DisplayName}': icon={(icon != null ? "OK" : "NULL")} ({diag}), m_iconElement={(pin.m_iconElement != null ? "istnieje" : "NULL")}, pin.m_icon przed={(pin.m_icon != null ? pin.m_icon.name : "null")}");

            if (icon == null)
                return;

            pin.m_icon = icon;
            // Ustawienie samego m_icon nie wystarczy, jesli element UI pinu na mapie zostal
            // juz utworzony (np. zaraz po wczytaniu swiata) - gra ustawia sprite ikony TYLKO
            // raz, w momencie tworzenia tego elementu. Trzeba wiec podmienic tez zywy Image.
            if (pin.m_iconElement != null)
                pin.m_iconElement.sprite = icon;
        }

        // Prefab "pod uderzenie" -> jego MineRock/MineRock5 (albo brak). Wynik zapamietany, bo
        // Destructible w zasiegu skanu sa setki (glazy, pniaki), a prefab rozbitej zyly ma
        // dziesiatki kawalkow - przeszukiwanie go przy kazdym skanie byloby zbednym kosztem.
        private static readonly Dictionary<GameObject, (MineRock Rock, MineRock5 Rock5)> OreTemplateCache =
            new Dictionary<GameObject, (MineRock, MineRock5)>();

        private static bool TryGetOreTemplate(Destructible destructible, out MineRock mineRock, out MineRock5 mineRock5)
        {
            mineRock = null;
            mineRock5 = null;
            foreach (var prefab in new[] { destructible.m_spawnWhenDamaged, destructible.m_spawnWhenDestroyed })
            {
                if (prefab == null)
                    continue;
                if (!OreTemplateCache.TryGetValue(prefab, out var template))
                {
                    template = (prefab.GetComponentInChildren<MineRock>(true), prefab.GetComponentInChildren<MineRock5>(true));
                    OreTemplateCache[prefab] = template;
                }
                if (template.Rock != null || template.Rock5 != null)
                {
                    mineRock = template.Rock;
                    mineRock5 = template.Rock5;
                    return true;
                }
            }
            return false;
        }

        private void AddResourcePin(GameObject root, ResourceCategory category, string matchedName, Pickable pickable)
        {
            Sprite icon = category.CustomIcon;
            string diag = "wlasna ikona (renderowana z pliku gry)";
            if (icon == null)
                icon = ResolveIcon(root, pickable, category.IconItemNameOverride ?? matchedName, out diag);

            var pin = CreatePinForFind(category, root.transform.position, category.PinType, category.DisplayName, icon);

            _tracked[root] = new TrackedResource
            {
                Obj = root,
                Pin = pin,
                PickableComp = pickable,
                Category = category
            };

            Log.LogInfo($"Auto-pin dodany: {category.DisplayName} @ {root.transform.position} (ikona: {diag})");
        }

        private static Sprite ResolveIcon(GameObject root, Pickable pickable, string matchedName, out string diag)
        {
            if (pickable != null)
            {
                if (pickable.m_itemPrefab != null)
                {
                    var drop = pickable.m_itemPrefab.GetComponent<ItemDrop>();
                    if (drop != null)
                    {
                        var icon = drop.m_itemData.GetIcon();
                        diag = icon != null ? $"Pickable.m_itemPrefab={pickable.m_itemPrefab.name} OK" : $"Pickable.m_itemPrefab={pickable.m_itemPrefab.name} ale GetIcon()==null";
                        if (icon != null) return icon;
                    }
                    else
                    {
                        diag = $"Pickable.m_itemPrefab={pickable.m_itemPrefab.name} bez ItemDrop";
                    }
                }
                else
                {
                    diag = "Pickable znaleziony, ale m_itemPrefab==null";
                }
            }
            else
            {
                diag = "brak komponentu Pickable";
            }

            var mineRock = root.GetComponentInChildren<MineRock>();
            if (mineRock != null && mineRock.m_dropItems != null)
            {
                var icon = IconFromDropTable(mineRock.m_dropItems);
                if (icon != null) { diag = "MineRock.m_dropItems OK"; return icon; }
                diag += "; MineRock znaleziony ale brak ikony w m_dropItems";
            }

            var mineRock5 = root.GetComponentInChildren<MineRock5>();
            if (mineRock5 != null && mineRock5.m_dropItems != null)
            {
                var icon = IconFromDropTable(mineRock5.m_dropItems);
                if (icon != null) { diag = "MineRock5.m_dropItems OK"; return icon; }
                diag += "; MineRock5 znaleziony ale brak ikony w m_dropItems";
            }

            // fallback - niektore obiekty swiata (np. Beehive) sa jednoczesnie swoim wlasnym
            // itemem w ObjectDB pod ta sama nazwa prefabu.
            var itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(matchedName) : null;
            var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
            if (itemDrop != null)
            {
                var icon = itemDrop.m_itemData.GetIcon();
                if (icon != null) { diag = $"ObjectDB.GetItemPrefab({matchedName}) OK"; return icon; }
                diag += $"; ObjectDB.GetItemPrefab({matchedName}) znaleziony ale GetIcon()==null";
            }
            else
            {
                diag += $"; ObjectDB.GetItemPrefab({matchedName})==null";
            }

            return null;
        }

        private static Sprite IconFromDropTable(DropTable table)
        {
            var drop = table.m_drops?.FirstOrDefault(d => d.m_item != null && d.m_item.name != "Stone");
            var item = drop?.m_item?.GetComponent<ItemDrop>();
            return item?.m_itemData?.GetIcon();
        }

        // Waga: Valheim usuwa lokalny GameObject zasobu gdy gracz odejdzie za daleko (culling
        // wydajnosci) i tworzy go od nowa gdy gracz wroci - to NIE znaczy ze zasob zostal
        // zebrany/wydobyty. Dlatego "obiekt == null" traktujemy jako dowod zniknięcia TYLKO
        // gdy gracz jest w zasiegu skanu (gdzie obiekt powinien byc zaladowany, jesli nadal
        // istnieje) - a nie gdy jest daleko. Jesli to byl falszywy alarm (culling), ScanNearby
        // zaraz potem i tak znajdzie swiezo zaladowany obiekt i doda pin ponownie.
        private void PruneTracked()
        {
            Vector3 playerPos = Player.m_localPlayer.transform.position;
            var gone = new List<GameObject>();
            var forgotten = new List<GameObject>();

            // Pobrane raz na przebieg - do wykrycia pinow usunietych z mapy "z zewnatrz" (np.
            // reczne usuniecie przez gracza kliknieciem na mapie). Bez tego _tracked nigdy sie
            // nie dowiaduje ze pin zniknal (Obj w swiecie dalej istnieje, Pickable nie dotyczy
            // struktur/lochow/portali) i wpis blokuje ponowne dodanie pinu do konca sesji.
            var alivePins = new HashSet<Minimap.PinData>(
                PinsField.GetValue(Minimap.instance) as List<Minimap.PinData> ?? new List<Minimap.PinData>());

            foreach (var kv in _tracked)
            {
                var entry = kv.Value;
                float dist = Vector3.Distance(playerPos, entry.Pin.m_pos);
                if (dist > _scanRadius.Value)
                    continue; // za daleko zeby cokolwiek stwierdzic - zostawiamy pin w spokoju

                var reason = IsGone(entry, alivePins);
                if (reason == GoneReason.PinMissing)
                    forgotten.Add(kv.Key);
                else if (reason == GoneReason.ResourceGone)
                    gone.Add(kv.Key);
            }

            foreach (var key in forgotten)
                _tracked.Remove(key);

            foreach (var key in gone)
            {
                var entry = _tracked[key];
                _tracked.Remove(key);

                // Zloze zostalo uderzone i podmienione na wersje z kawalkami - pin zostaje,
                // najblizszy skan przejmie go dla nowego obiektu (FindNearbyPin).
                if (entry.ReplacedWhenDamaged && entry.Obj == null)
                    continue;

                if (entry.Category == _looseRuinsCategory)
                {
                    // Zniszczenie JEDNEGO elementu skupiska nie kasuje calego pinu ruiny -
                    // dopiero gdy WSZYSTKIE jej elementy zostana potwierdzone jako zniszczone/
                    // zniknięte, usuwamy pin. Do tego czasu tylko wypisujemy ten element ze
                    // skupiska.
                    var cluster = _ruinClusters.FirstOrDefault(cl => cl.Pin == entry.Pin);
                    if (cluster != null)
                    {
                        cluster.Pieces.Remove(key);
                        if (cluster.Pieces.Count == 0)
                        {
                            RemovePinFor(entry);
                            _ruinClusters.Remove(cluster);
                        }
                    }
                    continue;
                }

                RemovePinFor(entry);
            }

            // Jak wyzej, ale dla skupisk ruin - jesli pin zniknal z mapy (np. gracz usunal go
            // recznie), zapominamy o calym skupisku (nie tylko pojedynczym elemencie), zeby
            // dalo sie je wykryc od nowa.
            // Pin schowany przez nas (kategoria ukryta) tez nie jest na mapie - to NIE jest
            // usuniecie; bez tego warunku skupisko byloby co skan zapominane, wykrywane od nowa
            // i chowane ponownie (zapis pliku co ~1.5 s obok ukrytej ruiny).
            _ruinClusters.RemoveAll(cl => cl.Pin != null && cl.Category != null &&
                                          IsCategoryVisible(cl.Category) && !alivePins.Contains(cl.Pin));
        }

        // Pojedynczy element (np. jeden stone_wall_2x1) sam w sobie nic nie znaczy - dopiero
        // kilka takich elementow blisko siebie sugeruje ruine. Zbieramy je wiec do skupiska i
        // stawiamy pin dopiero gdy uzbiera sie ich odpowiednio duzo (RuinClusterMinPieces).
        private void HandleRuinPieceSighting(GameObject root, Vector3 playerPos)
        {
            if (_looseRuinsCategory == null || _looseTowerCategory == null || _looseWoodCategory == null)
                return;

            float verticalTolerance = _looseRuinsCategory.MaxVerticalDeltaOverride ?? MaxVerticalDelta;
            if (Mathf.Abs(root.transform.position.y - playerPos.y) > verticalTolerance)
                return;

            // Nigdy nie dotykamy wlasnej (ani cudzej) budowli gracza - te same prefaby sluza do
            // budowania, wiec odrozniamy po tym ze element swiata generowanego jako ruina nie ma
            // wpisanego "creator" (0), a kazdy postawiony przez gracza ma niezerowy identyfikator.
            // FAIL-CLOSED: jesli z jakiegokolwiek powodu nie da sie znalezc komponentu Piece albo
            // odczytac jego tworcy, NIE zgadujemy "to pewnie ruina" - wolimy przegapic prawdziwa
            // ruine niz oznaczyc czyjs dom (bylo potwierdzone w logu: piece_workbench/forge/
            // fermenter/bed/piece_chest_wood/piece_cartographytable obok wykrytych elementow).
            // GetComponentInParent (nie GetComponent) na wypadek gdyby kolider siedzial na
            // dziecku obiektu, tak jak przy MineRock/MineRock5.
            var piece = root.GetComponentInParent<Piece>();
            if (piece == null || piece.GetCreator() != 0L)
            {
                if (_debugLogNearby.Value)
                    Log.LogInfo($"[DIAG-RUIN] pominieto '{root.name}' - Piece={(piece == null ? "brak" : "OK")}, creator={(piece != null ? piece.GetCreator().ToString() : "-")}");
                return;
            }

            Vector3 pos = root.transform.position;

            var cluster = _ruinClusters.FirstOrDefault(cl => Vector3.Distance(cl.Center, pos) < RuinClusterRadius);
            if (cluster == null)
            {
                // Kategoria (a wiec i ewentualna adopcja istniejacego pinu) nie jest jeszcze
                // znana - zalezy od ksztaltu, ktory poznamy dopiero po zebraniu progu elementow.
                cluster = new RuinCluster { Center = pos };
                _ruinClusters.Add(cluster);
            }

            if (!cluster.Pieces.Add(root))
                return; // juz policzony

            // Recentrowanie srodka ciezkosci o nowo znaleziony element + poszerzenie zakresu
            // wysokosci (do rozpoznania ksztaltu: wieza vs plaska ruina) + zliczenie materialu
            // (do rozpoznania czy to raczej kamienna, czy drewniana budowla).
            cluster.Center = ((cluster.Center * (cluster.Pieces.Count - 1)) + pos) / cluster.Pieces.Count;
            cluster.MinY = Mathf.Min(cluster.MinY, pos.y);
            cluster.MaxY = Mathf.Max(cluster.MaxY, pos.y);
            if (root.name.StartsWith("stone_", StringComparison.Ordinal)) cluster.StoneCount++;
            else if (root.name.StartsWith("wood_", StringComparison.Ordinal)) cluster.WoodCount++;

            if (_debugLogNearby.Value)
                Log.LogInfo($"[DIAG-RUIN] nowy element skupiska ({cluster.Pieces.Count}/{RuinClusterMinPieces}) @ {pos}, srodek={cluster.Center}, wysokosc={cluster.MaxY - cluster.MinY:0.0}m, kamien={cluster.StoneCount}/drewno={cluster.WoodCount}, ma pin={cluster.Pin != null}");

            // Uwaga: dopoki pin nie istnieje, CELOWO nie dodajemy elementu do _tracked (ktore
            // wymaga zywego Pin - PruneTracked odwoluje sie do entry.Pin.m_pos). Ponowne
            // napotkanie tego samego elementu w kolejnym skanie zanim uzbiera sie prog jest
            // nieszkodliwe (Pieces.Add powyzej po prostu zwroci false).
            if (cluster.Pin != null)
            {
                cluster.Pin.m_pos = cluster.Center;
                _tracked[root] = new TrackedResource { Obj = root, Pin = cluster.Pin, Category = cluster.Category };

                // Ksztalt/material przy progu (RuinClusterMinPieces) bywaja mylace - prawdziwy
                // charakter skupiska ujawnia sie dopiero po zebraniu wiecej elementow. Jesli po
                // fakcie wychodzi inna kategoria niz ta ktora dostal pin na starcie, migrujemy go
                // zamiast zostawiac raz na zawsze zle podpisanym.
                var better = DecideRuinCategory(cluster);
                if (better != null && better != cluster.Category)
                    MigrateRuinCluster(cluster, better);

                return;
            }

            if (cluster.Pieces.Count < RuinClusterMinPieces)
                return;

            cluster.Category = DecideRuinCategory(cluster);

            // Dopiero teraz, znajac kategorie, sprawdzamy czy w poblizu nie ma juz zapisanego
            // pinu tej samej kategorii z poprzedniej sesji (mod traci pamiec _ruinClusters po
            // restarcie) - zamiast tworzyc duplikat, przejmujemy go z powrotem pod nadzor.
            var existingPin = FindNearbyPin(cluster.Category, cluster.Center);
            if (existingPin != null)
            {
                cluster.Pin = existingPin;
                cluster.Pin.m_pos = cluster.Center;
            }
            else
            {
                cluster.Pin = CreatePinForFind(cluster.Category, cluster.Center, cluster.Category.PinType, cluster.Category.DisplayName, cluster.Category.CustomIcon);
            }

            foreach (var p in cluster.Pieces)
                _tracked[p] = new TrackedResource { Obj = p, Pin = cluster.Pin, Category = cluster.Category };

            Log.LogInfo($"Auto-pin dodany: {cluster.Category.DisplayName} (skupisko {cluster.Pieces.Count} elementow, wysokosc {cluster.MaxY - cluster.MinY:0.0}m) @ {cluster.Center}");
        }

        // Ksztalt/material skupiska decyduje o ikonie: wysoka, waska sylwetka (duza roznica
        // wysokosci miedzy elementami w niewielkim promieniu) wyglada jak wieza, nie jak dom;
        // przewaga elementow "wood_*" nad "stone_*" wyglada jak drewniana chata, nie kamienny
        // dom. Kategoria zalezy tylko od tego, czym skupisko JEST - czy ma byc widoczna,
        // rozstrzyga wspolny mechanizm chowania (HideIfCategoryHidden).
        private ResourceCategory DecideRuinCategory(RuinCluster cluster)
        {
            if ((cluster.MaxY - cluster.MinY) >= TowerHeightSpan)
                return _looseTowerCategory;
            if (cluster.WoodCount > cluster.StoneCount)
                return _looseWoodCategory;
            return _looseRuinsCategory;
        }

        // Przenosi juz istniejacy pin skupiska na inna kategorie (np. z "Ruins" na "Stone Tower
        // Ruins", gdy dopiero po fakcie okaze sie ze skupisko jest wysokie) - usuwa stary pin,
        // stawia nowy (lub przejmuje juz istniejacy pin tej kategorii w poblizu) i przepina
        // wszystkie sledzone elementy skupiska na niego.
        private void MigrateRuinCluster(RuinCluster cluster, ResourceCategory newCategory)
        {
            var oldPin = cluster.Pin;

            var existingPin = FindNearbyPin(newCategory, cluster.Center);
            if (existingPin != null && existingPin != oldPin)
            {
                cluster.Pin = existingPin;
                cluster.Pin.m_pos = cluster.Center;
            }
            else
            {
                cluster.Pin = CreatePinForFind(newCategory, cluster.Center, newCategory.PinType, newCategory.DisplayName, newCategory.CustomIcon);
            }

            if (oldPin != null && oldPin != cluster.Pin)
            {
                Minimap.instance.RemovePin(oldPin);
                // Stary pin mogl juz siedziec w schowanych (kategoria ukryta) - bez tego po
                // wlaczeniu obu kategorii w tym samym miejscu stalyby dwa piny.
                RemoveHiddenRecordsNear(cluster.Category, cluster.Center, RuinClusterRadius);
            }

            cluster.Category = newCategory;
            foreach (var p in cluster.Pieces)
                if (p != null)
                    _tracked[p] = new TrackedResource { Obj = p, Pin = cluster.Pin, Category = newCategory };

            Log.LogInfo($"Auto-pin zmigrowany: {newCategory.DisplayName} (skupisko {cluster.Pieces.Count} elementow, wysokosc {cluster.MaxY - cluster.MinY:0.0}m) @ {cluster.Center}");
        }

        private enum GoneReason { None, ResourceGone, PinMissing }

        private GoneReason IsGone(TrackedResource entry, HashSet<Minimap.PinData> alivePins)
        {
            if (entry.Obj == null)
                return GoneReason.ResourceGone;
            // Krzaki jagod: RemoveOnPicked==false - pin zostaje nawet po zebraniu (jagody
            // odrastaja), znika tylko jesli krzak faktycznie zniknie (obsluzone wyzej).
            if (entry.Category.RemoveOnPicked && entry.PickableComp != null && entry.PickableComp.GetPicked())
                return GoneReason.ResourceGone;
            // Pinu nie ma na mapie, choc kategoria jest widoczna: gracz usunal go recznie albo
            // kategoria zostala przed chwila pokazana i schowany pin zastapil NOWY (przywrocony).
            // Tylko zapominamy o obiekcie - skan go przejmie albo przypnie od nowa. Zasob nadal
            // istnieje, wiec NIE ruszamy schowanych wpisow ani rejestru portali.
            // Brak pinu przy ukrytej kategorii to zwykle schowanie - nic nie znaczy.
            if (IsCategoryVisible(entry.Category) && !alivePins.Contains(entry.Pin))
                return GoneReason.PinMissing;
            return GoneReason.None;
        }

        // Dane o odkrytych lokalizacjach (_tracked) sa zbierane ZAWSZE, niezaleznie od tego czy
        // kategoria jest aktualnie widoczna - przelacznik w menu wplywa TYLKO na to, czy dany
        // pin jest wpisany do listy pinow Minimap (a wiec widoczny na mapie), nigdy nie kasuje
        // samego wpisu z _tracked. Dzieki temu wylaczenie i ponowne wlaczenie kategorii jest
        // natychmiastowe - nie trzeba na nowo podchodzic do zadnej lokalizacji, zeby ja odzyskac.
        // Piny schowane przez wylaczenie kategorii - zapisywane NA DYSKU (nie tylko w RAM), zeby
        // przetrwaly pelny restart gry. Bez tego: gracz wylacza kategorie -> pin usuwany przez
        // RemovePin (niszczy go NAPRAWDE, nie tylko chowa) -> gra zapisuje swiat w tym stanie
        // przy wyjsciu -> po restarcie pamiec w RAM jest pusta I pin juz nie istnieje w zapisie
        // gry - "zawsze zbierane dane" byloby zlamane. Ten sam wzorzec co tracked_<swiat>.json
        // w Nav Compass (JsonUtility, plik per-swiat kolo DLL).
        private class HiddenPinRecord
        {
            public string CategoryKey;
            public float X, Y, Z;
            public string DisplayName;
            public int PinType;
            public bool Save;
        }
        // JsonUtility po cichu pomija pole List<wlasna klasa> w tym assembly (netstandard2.1) -
        // plik wychodzil "{}" mimo zapisanych rekordow. Dlatego na dysku same listy typow
        // wbudowanych (List<Vector3> sprawdzone w Nav Compass), rownolegle po indeksie.
        [Serializable]
        private class HiddenPinsFile
        {
            public List<string> CategoryKeys = new List<string>();
            public List<Vector3> Positions = new List<Vector3>();
            public List<string> DisplayNames = new List<string>();
            public List<int> PinTypes = new List<int>();
            public List<bool> Saves = new List<bool>();

            public bool IsConsistent =>
                CategoryKeys != null && Positions != null && DisplayNames != null && PinTypes != null && Saves != null &&
                Positions.Count == CategoryKeys.Count && DisplayNames.Count == CategoryKeys.Count &&
                PinTypes.Count == CategoryKeys.Count && Saves.Count == CategoryKeys.Count;
        }
        private readonly Dictionary<string, List<HiddenPinRecord>> _hiddenPins = new Dictionary<string, List<HiddenPinRecord>>();
        private bool _hiddenPinsLoaded;

        private static string HiddenPinsSaveDir => System.IO.Path.GetDirectoryName(typeof(AutoWaypointsPlugin).Assembly.Location);

        private static string WorldFilePath(string prefix, string worldName)
        {
            string safe = string.Join("_", worldName.Split(System.IO.Path.GetInvalidFileNameChars()));
            return System.IO.Path.Combine(HiddenPinsSaveDir, $"{prefix}_{safe}.json");
        }

        private static string HiddenPinsFilePath(string worldName) => WorldFilePath("hidden_pins", worldName);

        // Zapis przez plik tymczasowy - przerwany zapis (crash, zamkniecie gry) nie zostawi
        // polowicznego JSON-a w miejscu dobrego.
        private static void WriteFileAtomically(string path, string contents)
        {
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, contents);
            if (System.IO.File.Exists(path))
                System.IO.File.Replace(tmp, path, null);
            else
                System.IO.File.Move(tmp, path);
        }

        private void TryLoadHiddenPins()
        {
            string worldName = ZNet.instance?.GetWorldName();
            if (string.IsNullOrEmpty(worldName))
                return;
            _hiddenPinsLoaded = true;
            _hiddenPins.Clear();

            string path = HiddenPinsFilePath(worldName);
            if (!System.IO.File.Exists(path))
                return;
            try
            {
                string raw = System.IO.File.ReadAllText(path);
                var data = JsonUtility.FromJson<HiddenPinsFile>(raw);
                if (data == null || !data.IsConsistent)
                {
                    Log.LogWarning($"Plik schowanych pinow {path} jest uszkodzony (listy roznej dlugosci) - pomijam.");
                    return;
                }
                for (int i = 0; i < data.CategoryKeys.Count; i++)
                {
                    // Kategoria z nazwy pinu, gdy da sie ja ustalic - wpisy zapisane pod dawnym,
                    // wspolnym kluczem (np. "Dungeon" sprzed podzialu lochow) trafiaja pod wlasciwa
                    // kategorie, zamiast zostac w pliku bez mozliwosci przywrocenia.
                    var r = new HiddenPinRecord
                    {
                        // Portal nosi dowolna nazwe gracza - jego wpis zostaje pod kluczem portalu.
                        CategoryKey = data.CategoryKeys[i] == _portalCategory.Key
                            ? data.CategoryKeys[i]
                            : CategoryForName(data.DisplayNames[i])?.Key ?? data.CategoryKeys[i],
                        X = data.Positions[i].x,
                        Y = data.Positions[i].y,
                        Z = data.Positions[i].z,
                        DisplayName = data.DisplayNames[i],
                        PinType = data.PinTypes[i],
                        Save = data.Saves[i]
                    };
                    if (!_hiddenPins.TryGetValue(r.CategoryKey, out var list))
                        _hiddenPins[r.CategoryKey] = list = new List<HiddenPinRecord>();
                    list.Add(r);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie wczytac schowanych pinow: {e}");
            }

            // Schowane piny nie przechodza przez AddPin przy wczytaniu mapy, a ich kategorie
            // tez sa juz odkryte - inaczej zniknelyby z menu na czas ukrycia.
            foreach (var key in _hiddenPins.Keys)
                MarkDiscovered(MenuCategories.FirstOrDefault(c => c.Key == key));
        }

        private void SaveHiddenPins()
        {
            try
            {
                string worldName = ZNet.instance?.GetWorldName();
                if (string.IsNullOrEmpty(worldName))
                {
                    Log.LogWarning("Zapis schowanych pinow: brak nazwy swiata (ZNet.instance.GetWorldName()) - NIE zapisano.");
                    return;
                }
                var data = new HiddenPinsFile();
                foreach (var r in _hiddenPins.Values.SelectMany(l => l))
                {
                    data.CategoryKeys.Add(r.CategoryKey);
                    data.Positions.Add(new Vector3(r.X, r.Y, r.Z));
                    data.DisplayNames.Add(r.DisplayName);
                    data.PinTypes.Add(r.PinType);
                    data.Saves.Add(r.Save);
                }
                string json = JsonUtility.ToJson(data);

                // Ten sam blad (serializer gubiacy dane bez wyjatku) nie moze juz przejsc po cichu.
                var check = JsonUtility.FromJson<HiddenPinsFile>(json);
                if (check == null || !check.IsConsistent || check.CategoryKeys.Count != data.CategoryKeys.Count)
                {
                    Log.LogError($"SaveHiddenPins: serializacja zgubila dane ({data.CategoryKeys.Count} rekordow -> '{json}') - NIE nadpisuje pliku.");
                    return;
                }

                string path = HiddenPinsFilePath(worldName);
                WriteFileAtomically(path, json);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie zapisac schowanych pinow: {e}");
            }
        }

        private void RestoreHiddenPinsForCategory(ResourceCategory category)
        {
            if (!_hiddenPins.TryGetValue(category.Key, out var list) || list.Count == 0)
                return;

            foreach (var r in list)
            {
                var pos = new Vector3(r.X, r.Y, r.Z);
                var fresh = Minimap.instance.AddPin(pos, (Minimap.PinType)r.PinType, r.DisplayName, r.Save, false);
                if (category.CustomIcon != null)
                    fresh.m_icon = category.CustomIcon;
            }
            _hiddenPins.Remove(category.Key);
            SaveHiddenPins();

            // Ikony ktore nie sa CustomIcon (przedmiot z ObjectDB, trofeum, portal, wlasna
            // renderowana ikona lochu) - prosciej dociagnac przez juz istniejacy globalny
            // przebieg niz powtarzac cala logike resolwowania osobno dla kazdego typu.
            ReapplyAllKnownPinIcons();
        }

        private void RemoveHiddenRecordsNear(ResourceCategory category, Vector3 pos, float radius)
        {
            if (category == null || !_hiddenPins.TryGetValue(category.Key, out var list))
                return;
            if (list.RemoveAll(r => Vector3.Distance(new Vector3(r.X, r.Y, r.Z), pos) < radius) == 0)
                return;
            if (list.Count == 0)
                _hiddenPins.Remove(category.Key);
            SaveHiddenPins();
        }

        private bool IsRuinClusterCategory(ResourceCategory category) =>
            category == _looseRuinsCategory || category == _looseTowerCategory || category == _looseWoodCategory;

        private bool IsCategoryVisible(ResourceCategory category) =>
            _showAutoPins.Value && category.Enabled.Value;

        private const string BossAltarsGroup = "Boss Altars";

        // ReplaceBossAltars decyduje tylko o IKONACH bossow - zarowno pinow z moda, jak i pinow
        // dodanych przez gre (Vegvisir: PinType.Boss) - ON: ikony z moda (render/trofeum), OFF:
        // podstawowa ikona bossa z gry. Pin z gry zawsze zostaje widoczny (to zapis gracza), a
        // pin oltarza z moda chowa sie tam, gdzie gra ma juz swoj pin tego bossa (bez dubli).
        private const float NativeBossPinMatchRadius = 30f;

        // Nazwe pinu z Vegvisira bierze gra z danych prefabu, nie z kodu - dlatego boss jest
        // rozpoznawany po POZYCJI: Vegvisir wskazuje konkretna lokacje, a ZoneSystem zna
        // polozenie wszystkich lokacji swiata (gra jednoosobowa / gospodarz). Nazwy ponizej to
        // zapas dla klienta na cudzym serwerze, ktory tej listy lokacji nie ma.
        private static readonly Dictionary<string, string> NativeBossPinNames = new Dictionary<string, string>
        {
            { "$enemy_eikthyr", "Eikthyrnir" },
            { "$enemy_gdking", "GDKing" },
            { "$enemy_bonemass", "Bonemass" },
            { "$enemy_dragon", "Dragonqueen" },
            { "$enemy_goblinking", "GoblinKing" },
        };
        private const float NativeBossLocationRadius = 50f;
        private static readonly FieldInfo LocationInstancesField = AccessTools.Field(typeof(ZoneSystem), "m_locationInstances");
        private List<(Vector3 Pos, string LocationName)> _bossLocations;
        private readonly HashSet<string> _reportedUnknownBossPins = new HashSet<string>();

        private void EnsureBossLocations()
        {
            if (_bossLocations != null || ZoneSystem.instance == null)
                return;
            if (!(LocationInstancesField?.GetValue(ZoneSystem.instance) is System.Collections.IDictionary instances) || instances.Count == 0)
                return;
            var bossNames = new HashSet<string>(DungeonLocationDefs.Where(d => d.PinType == Minimap.PinType.Boss).Select(d => d.LocationName));
            _bossLocations = new List<(Vector3, string)>();
            foreach (var value in instances.Values)
            {
                var instance = (ZoneSystem.LocationInstance)value;
                string prefabName = instance.m_location?.m_prefabName;
                if (prefabName != null && bossNames.Contains(prefabName))
                    _bossLocations.Add((instance.m_position, prefabName));
            }
        }

        private string ResolveNativeBossLocation(Minimap.PinData pin)
        {
            if (pin.m_name != null && NativeBossPinNames.TryGetValue(pin.m_name, out var byName))
                return byName;
            EnsureBossLocations();
            if (_bossLocations != null)
            {
                var nearest = _bossLocations
                    .Select(l => (l.LocationName, Distance: Utils.DistanceXZ(l.Pos, pin.m_pos)))
                    .Where(l => l.Distance < NativeBossLocationRadius)
                    .OrderBy(l => l.Distance)
                    .FirstOrDefault();
                if (nearest.LocationName != null)
                    return nearest.LocationName;
            }
            if (_reportedUnknownBossPins.Add(pin.m_name ?? ""))
                Log.LogInfo($"Pin bossa z gry nierozpoznany (zostaje ikona z gry): '{pin.m_name}' @ {pin.m_pos}");
            return null;
        }

        private ResourceCategory NativeBossCategory(Minimap.PinData pin)
        {
            string locationName = ResolveNativeBossLocation(pin);
            if (locationName == null)
                return null;
            var def = DungeonLocationDefs.First(d => d.LocationName == locationName);
            return _dungeonCategoriesByName.TryGetValue(def.DisplayName, out var category) ? category : null;
        }

        private bool ApplyNativeBossIcon(Minimap.PinData pin)
        {
            string locationName = ResolveNativeBossLocation(pin);
            if (locationName == null)
                return false;
            // Pin z Vegvisira to tez odkrycie oltarza - inaczej jego przelaczniki (m.in. podpisu)
            // pojawilyby sie w menu dopiero po podejsciu do samego oltarza.
            MarkDiscovered(NativeBossCategory(pin));
            var def = DungeonLocationDefs.First(d => d.LocationName == locationName);
            var icon = ResolveDungeonPinIcon(def.PinType, def.IconKey, def.TrophyItemName);
            if (icon == null)
                return false;
            pin.m_icon = icon;
            if (pin.m_iconElement != null)
                pin.m_iconElement.sprite = icon;
            return true;
        }

        private static bool IsNativeBossPin(Minimap.PinData pin, ResourceCategory category) =>
            category == null && pin.m_type == Minimap.PinType.Boss;

        private bool IsModBossAltarPin(ResourceCategory category) =>
            category != null && category.MenuGroup == BossAltarsGroup;

        // Pin oltarza z moda jest niewidoczny, gdy w tym samym miejscu gra ma juz swoj pin bossa.
        private bool IsBossPinYielding(Minimap.PinData pin, List<Minimap.PinData> pins)
        {
            if (!IsModBossAltarPin(CategoryForPin(pin)))
                return false;
            return pins.Any(p => p != pin && IsNativeBossPin(p, CategoryForPin(p)) &&
                                 Utils.DistanceXZ(p.m_pos, pin.m_pos) < NativeBossPinMatchRadius);
        }

        private static readonly MethodInfo MinimapGetSpriteMethod = AccessTools.Method(typeof(Minimap), "GetSprite");

        // Ikona pinu oltarza: z moda albo podstawowa ikona bossa z gry (ReplaceBossAltars OFF).
        private Sprite ResolveDungeonPinIcon(Minimap.PinType pinType, string iconKey, string trophyItemName)
        {
            if (pinType == Minimap.PinType.Boss && !_replaceBossAltars.Value && Minimap.instance != null)
                return MinimapGetSpriteMethod?.Invoke(Minimap.instance, new object[] { pinType }) as Sprite;
            return ResolveDungeonIcon(iconKey, trophyItemName);
        }

        // Pin gry nie jest usuwany (to zapis gracza), tylko niewidoczny - element ikony tworzy
        // gra w UpdatePins (takze od nowa, gdy pin wraca w widok), wiec postfix obejmuje kazdy.
        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static class Minimap_UpdatePins_NativeBossPatch
        {
            private static void Postfix(List<Minimap.PinData> ___m_pins)
            {
                Instance?.ApplyNativeBossPinVisibility(___m_pins);
            }
        }

        private void ApplyNativeBossPinVisibility(List<Minimap.PinData> pins)
        {
            foreach (var pin in pins)
            {
                if (pin.m_type != Minimap.PinType.Boss)
                    continue;
                bool hide = IsBossPinYielding(pin, pins) || IsNativeBossPinHiddenByCategory(pin);
                SetHiddenByAlpha(pin.m_uiElement != null ? pin.m_uiElement.gameObject : null, hide);
                ApplyLabelVisibility(pin);
            }
        }

        // Pin bossa z gry (Vegvisir) slucha kolumny "Pin" swojego oltarza i "Show all pins" -
        // jest tylko niewidoczny, nie usuwany (to zapis mapy gracza), wiec po wlaczeniu wraca.
        private bool IsNativeBossPinHiddenByCategory(Minimap.PinData pin)
        {
            if (!IsNativeBossPin(pin, CategoryForPin(pin)))
                return false;
            var category = NativeBossCategory(pin);
            return category != null && !IsCategoryVisible(category);
        }

        private static readonly FieldInfo PinUpdateRequiredField = AccessTools.Field(typeof(Minimap), "m_pinUpdateRequired");

        private static void RequestPinUpdate()
        {
            if (Minimap.instance != null)
                PinUpdateRequiredField.SetValue(Minimap.instance, true);
        }

        // Nowe znalezisko z ukrytej kategorii (albo przy wylaczonym "Show all pins") laduje
        // najpierw na mapie jak kazdy pin, a klatke pozniej trafia do schowanych - jedno
        // miejsce zamiast osobnej obslugi w kazdej sciezce tworzenia pinu (rudy, lochy,
        // portale, ruiny, statki).
        private readonly HashSet<ResourceCategory> _pendingHide = new HashSet<ResourceCategory>();

        private void HideIfCategoryHidden(ResourceCategory category)
        {
            if (category != null && !IsCategoryVisible(category))
                _pendingHide.Add(category);
        }

        private void ApplyPendingHides()
        {
            if (_pendingHide.Count == 0)
                return;
            var pending = _pendingHide.ToList();
            _pendingHide.Clear();
            foreach (var category in pending)
                if (!IsCategoryVisible(category))
                    SetCategoryVisibility(category, false);
        }

        private bool PinBelongsToCategory(Minimap.PinData pin, ResourceCategory category) =>
            CategoryForPin(pin) == category;

        // Po restarcie mod nie pamieta, ktore obiekty juz widzial - ten sam obiekt z ukrytej
        // kategorii trafia tu wtedy drugi raz. Wpis w poblizu jest tylko odswiezany (np. nowy
        // tag portalu), zamiast dublowany. Zapis pliku - po stronie wywolujacego.
        private void StoreHiddenPin(ResourceCategory category, Minimap.PinData pin)
        {
            if (!_hiddenPins.TryGetValue(category.Key, out var list))
                _hiddenPins[category.Key] = list = new List<HiddenPinRecord>();

            float mergeRadius = IsRuinClusterCategory(category) ? RuinClusterRadius : MinPinSpacing;
            var record = list.FirstOrDefault(r => Vector3.Distance(new Vector3(r.X, r.Y, r.Z), pin.m_pos) < mergeRadius);
            if (record == null)
            {
                record = new HiddenPinRecord { CategoryKey = category.Key, X = pin.m_pos.x, Y = pin.m_pos.y, Z = pin.m_pos.z };
                list.Add(record);
            }
            record.DisplayName = pin.m_name;
            record.PinType = (int)pin.m_type;
            record.Save = pin.m_save;
        }

        // Pin dla nowego znaleziska. Przy ukrytej kategorii (albo wylaczonym "Show all pins")
        // NIE trafia na mape nawet na klatke - inaczej kazde znalezisko migaloby na mapie (duze
        // pole marchwi = ciagle miganie) - tylko prosto do schowanych. Zwrocony pin jest wtedy
        // "odlaczony" (poza lista pinow mapy), tak jak pin schowany przez SetCategoryVisibility,
        // wiec _tracked/PruneTracked traktuja go tak samo.
        private Minimap.PinData CreatePinForFind(ResourceCategory category, Vector3 pos, Minimap.PinType type, string name, Sprite icon)
        {
            if (IsCategoryVisible(category))
            {
                var pin = Minimap.instance.AddPin(pos, type, name, true, false);
                if (icon != null)
                    pin.m_icon = icon;
                return pin;
            }

            var detached = new Minimap.PinData { m_pos = pos, m_type = type, m_name = name, m_save = true, m_icon = icon };
            StoreHiddenPin(category, detached);
            _hiddenPinsSaveDirty = true; // zapis raz na klatke - pole marchwi to kilkadziesiat znalezisk naraz
            MarkDiscovered(category, newFind: true);
            return detached;
        }

        private bool _hiddenPinsSaveDirty;

        private void SaveHiddenPinsIfDirty()
        {
            if (!_hiddenPinsSaveDirty)
                return;
            _hiddenPinsSaveDirty = false;
            SaveHiddenPins();
        }

        private void SetCategoryVisibility(ResourceCategory category, bool visible)
        {
            // Widocznosc ikon bossow z gry zalezy od tego, czy stoi przy nich pin oltarza z moda.
            if (category.MenuGroup == BossAltarsGroup)
                RequestPinUpdate();

            if (!visible)
            {
                var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
                if (pins == null)
                    return;

                // Chowamy WSZYSTKIE piny na mapie tej kategorii, nie tylko te w _tracked - stad
                // dopasowanie po nazwie, tak samo jak w ReapplyAllKnownPinIcons.
                var matching = pins.Where(p => PinBelongsToCategory(p, category)).ToList();
                if (matching.Count == 0)
                    return;

                foreach (var p in matching)
                {
                    StoreHiddenPin(category, p);
                    Minimap.instance.RemovePin(p); // niszczy tez element graficzny, nie tylko wpis w danych
                }
                SaveHiddenPins();
            }
            else
            {
                RestoreHiddenPinsForCategory(category);
            }
        }

        // Master switch: kazda kategoria widoczna tylko jesli WLASNY przelacznik I master sa
        // oba wlaczone naraz.
        private void RefreshAllVisibility()
        {
            foreach (var category in _categories)
                SetCategoryVisibility(category, IsCategoryVisible(category));
            SetCategoryVisibility(_portalCategory, IsCategoryVisible(_portalCategory));
            SetCategoryVisibility(_shipDockedCategory, IsCategoryVisible(_shipDockedCategory));
        }

        private void RemovePinFor(TrackedResource entry)
        {
            if (entry.Pin == null || Minimap.instance == null)
                return;
            Minimap.instance.RemovePin(entry.Pin);
            if (entry.Category == _portalCategory)
                UnregisterPortalPin(entry.Pin.m_pos);
            // Pin z ukrytej kategorii zyje juz tylko wsrod schowanych - zasob zniknal (wydobyty,
            // zebrany), wiec wpis tez, inaczej po wlaczeniu kategorii pin wrocilby w puste miejsce.
            float radius = IsRuinClusterCategory(entry.Category) ? RuinClusterRadius : 1f;
            RemoveHiddenRecordsNear(entry.Category, entry.Pin.m_pos, radius);
        }

        // =====================================================================================
        // Progresywne odkrywanie: kategoria pojawia sie w menu dopiero, gdy na mapie tego swiata
        // pojawi sie jej pin - zeby lista w menu nie zdradzala nowemu graczowi zawartosci gry.
        // Odkrycie lapiemy w jednym miejscu (postfix na Minimap.AddPin), wiec obejmuje tez piny
        // wczytane z zapisu mapy - w istniejacym swiecie wszystko co juz jest na mapie od razu
        // jest odkryte, bez osobnej migracji.
        // =====================================================================================

        [Serializable]
        private class DiscoveredFile
        {
            public List<string> Keys = new List<string>();
        }

        private readonly HashSet<string> _discoveredKeys = new HashSet<string>();
        private string _discoveryWorld;
        private bool _discoverySaveDirty;
        private bool _categoryListDirty = true;

        private IEnumerable<ResourceCategory> MenuCategories =>
            new[] { _portalCategory, _shipLiveCategory, _shipDockedCategory }.Concat(_categories);

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddPin))]
        private static class Minimap_AddPin_DiscoveryPatch
        {
            private static void Postfix(Minimap __instance, Minimap.PinData __result)
            {
                if (Instance == null || __result == null)
                    return;
                // Przed m_hasGenerated to wczytywanie mapy z zapisu, nie nowe znalezisko.
                bool newFind = MapHasGeneratedField.GetValue(__instance) is bool generated && generated;
                var category = Instance.CategoryForPin(__result);
                Instance.MarkDiscovered(category, newFind);
                if (newFind)
                    Instance.HideIfCategoryHidden(category);
                // Pin bossa z Vegvisira przeczytanego w trakcie gry (przy wczytaniu mapy robi to
                // przebieg ikon, gdy ObjectDB/ZoneSystem sa juz gotowe).
                if (newFind && IsNativeBossPin(__result, category))
                    Instance.ApplyNativeBossIcon(__result);
            }
        }

        // Swiat moze sie zmienic bez restartu gry (wyjscie do menu, wejscie do innego) - zbior
        // odkryc zawsze odpowiada swiatu, w ktorym gracz akurat jest.
        private bool EnsureDiscoveryLoaded()
        {
            string worldName = ZNet.instance?.GetWorldName();
            if (string.IsNullOrEmpty(worldName))
                return false;
            if (worldName == _discoveryWorld)
                return true;

            _discoveryWorld = worldName;
            _discoveredKeys.Clear();
            _discoverySaveDirty = false;
            _categoryListDirty = true;

            string path = WorldFilePath("discovered", worldName);
            if (!System.IO.File.Exists(path))
                return true;
            try
            {
                var data = JsonUtility.FromJson<DiscoveredFile>(System.IO.File.ReadAllText(path));
                if (data?.Keys != null)
                    _discoveredKeys.UnionWith(data.Keys);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie wczytac odkrytych kategorii ({path}): {e}");
            }
            return true;
        }

        private void MarkDiscovered(ResourceCategory category, bool newFind = false)
        {
            if (category == null || !EnsureDiscoveryLoaded() || !_discoveredKeys.Add(category.Key))
                return;

            Log.LogInfo($"Odkryto kategorie: {category.DisplayName} ({category.Key})");
            if (newFind)
                _pendingGroupInheritance.Add(category);
            _discoverySaveDirty = true;
            _categoryListDirty = true;
            if (_settingsPanel != null && _settingsPanel.activeSelf)
                RebuildCategoryList();
        }

        // Zapis raz na klatke, a nie przy kazdym odkryciu - przy wczytaniu mapy istniejacego
        // swiata odkrywa sie naraz kilkadziesiat kategorii.
        private void SaveDiscoveredIfDirty()
        {
            if (!_discoverySaveDirty || _discoveryWorld == null)
                return;
            _discoverySaveDirty = false;
            try
            {
                var data = new DiscoveredFile { Keys = _discoveredKeys.OrderBy(k => k).ToList() };
                string json = JsonUtility.ToJson(data);
                var check = JsonUtility.FromJson<DiscoveredFile>(json);
                if (check?.Keys == null || check.Keys.Count != data.Keys.Count)
                {
                    Log.LogError($"Zapis odkrytych kategorii: serializacja zgubila dane ('{json}') - NIE nadpisuje pliku.");
                    return;
                }
                WriteFileAtomically(WorldFilePath("discovered", _discoveryWorld), json);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie zapisac odkrytych kategorii: {e}");
            }
        }

        // Wylaczona, a jeszcze nieodkryta kategoria (np. wylaczona w innym swiecie - przelaczniki
        // sa wspolne dla wszystkich swiatow) tez musi byc w menu: wylaczonej mod nie oznacza, wiec
        // nigdy by jej nie odkryl i gracz nie mialby jak jej z powrotem wlaczyc.
        private bool IsShownInMenu(ResourceCategory category) =>
            _discoveredKeys.Contains(category.Key) || !category.Enabled.Value;

        // Gracz, ktory schowal cala grupe (np. Berries), nie chce, zeby nowo odkryty jej rodzaj
        // sam pojawil sie na mapie - nowa pozycja przejmuje wylaczenie, gdy WSZYSTKIE pozostale
        // pozycje grupy w menu sa wylaczone (osobno dla pinow i dla podpisow). Wykonywane
        // klatke pozniej, a nie w srodku AddPin - wylaczenie chowa (usuwa) swiezo dodany pin.
        private readonly HashSet<ResourceCategory> _pendingGroupInheritance = new HashSet<ResourceCategory>();

        private string MenuGroupOf(ResourceCategory category)
        {
            if (category == _portalCategory)
                return null;
            if (category == _shipLiveCategory || category == _shipDockedCategory)
                return "Ships";
            return GetGroupName(category);
        }

        private void ApplyPendingGroupInheritance()
        {
            if (_pendingGroupInheritance.Count == 0)
                return;
            var pending = _pendingGroupInheritance.ToList();
            _pendingGroupInheritance.Clear();

            foreach (var category in pending)
            {
                string group = MenuGroupOf(category);
                if (group == null)
                    continue;
                var siblings = MenuCategories
                    .Where(c => c != category && MenuGroupOf(c) == group && IsShownInMenu(c))
                    .ToList();
                if (siblings.Count == 0)
                    continue;

                if (category.Enabled.Value && siblings.All(c => !c.Enabled.Value))
                {
                    Log.LogInfo($"{category.DisplayName}: cala grupa '{group}' jest schowana - nowa pozycja tez.");
                    category.Enabled.Value = false;
                }
                if (category.ShowName.Value && siblings.All(c => !c.ShowName.Value))
                    category.ShowName.Value = false;
            }

            _categoryListDirty = true;
            if (_settingsPanel != null && _settingsPanel.activeSelf)
                RebuildCategoryList();
        }

        // Gra ustawia tekst podpisu tylko przy jego tworzeniu - po zmianie nazwy niszczymy stary
        // podpis, a UpdatePins tworzy nowy z aktualna nazwa (m_pinUpdateRequired).
        private static void RefreshPinLabel(Minimap.PinData pin)
        {
            if (pin.m_NamePinData?.PinNameGameObject != null)
                Destroy(pin.m_NamePinData.PinNameGameObject);
            pin.m_NamePinData = string.IsNullOrEmpty(pin.m_name) ? null : new Minimap.PinNameData(pin);
            RequestPinUpdate();
        }

        private void RenamePin(Minimap.PinData pin, string newName)
        {
            if (pin.m_name == newName)
                return;
            pin.m_name = newName;
            RefreshPinLabel(pin);
        }

        private void OnPinRenamed(Minimap.PinData pin)
        {
            RefreshPinLabel(pin);
            ApplyLabelVisibility(pin);
            var category = CategoryForPin(pin);
            MarkDiscovered(category, newFind: true);
            HideIfCategoryHidden(category);
        }

        // =====================================================================================
        // Menu ustawien (guzik na duzej mapie): master switch pinow i podpisow, suwak zasiegu
        // skanu, lista kategorii w grupach - kolumna "Pin" chowa/pokazuje piny, kolumna "Label"
        // same podpisy pod nimi. Zbudowane z pomoca Jotunn.GUIManager (CreateWoodpanel/CreateToggle/
        // CreateText) zeby wygladalo jak natywne UI gry - ten sam font (AveriaSerifBold), ten sam
        // kolor akcentu (ValheimOrange), ta sama "drewniana" ramka co reszta interfejsu Valheim.
        // =====================================================================================

        private const float MenuPanelWidth = 460f;
        private const float MenuPanelHeight = 560f;
        private const float MenuRowHeight = 26f;

        private void ToggleSettingsMenu()
        {
            if (_settingsPanel == null)
                return; // GUIManager jeszcze nie gotowy (np. sam moment wejscia do gry)

            bool nowActive = !_settingsPanel.activeSelf;
            if (nowActive && EnsureDiscoveryLoaded() && _categoryListDirty)
                RebuildCategoryList();
            _settingsPanel.SetActive(nowActive);
            GUIManager.BlockInput(nowActive);
        }

        private void BuildSettingsMenu()
        {
            if (GUIManager.Instance == null || GUIManager.CustomGUIFront == null)
            {
                Log.LogWarning("Menu ustawien: GUIManager niedostepny, pomijam budowe.");
                return;
            }

            var gui = GUIManager.Instance;
            _settingsPanel = gui.CreateWoodpanel(
                GUIManager.CustomGUIFront.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                MenuPanelWidth, MenuPanelHeight);
            _settingsPanel.SetActive(false);

            // WAZNE: caly panel jest zakotwiczony/pivotowany do GORY (anchor/pivot y=1), wiec
            // wspolrzedna Y=0 to GORNA krawedz, a kazdy kolejny wiersz w DOL wymaga coraz
            // BARDZIEJ UJEMNEJ wartosci Y - nie dodatniej (dodatnia wypycha element NAD panel,
            // poza widoczny obszar - to byl dokladnie ten blad).
            float y = -24f;

            var title = gui.CreateText("Auto Waypoints - Settings", _settingsPanel.transform,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y),
                gui.AveriaSerifBold, 20, gui.ValheimOrange, true, Color.black, MenuPanelWidth - 40f, 30f, false);
            title.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 1f);
            title.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

            y -= 40f;
            y = BuildToggleRow(_settingsPanel.transform, y, "Show all pins", _showAutoPins);
            y -= 4f;
            y = BuildToggleRow(_settingsPanel.transform, y, "Show pin labels", _showPinLabels);
            y -= 4f;
            y = BuildToggleRow(_settingsPanel.transform, y, "Replace boss icons", _replaceBossAltars);

            y -= 10f;
            y = BuildScanRadiusRow(_settingsPanel.transform, y);

            y -= 10f;
            y = BuildListColumnHeaders(_settingsPanel.transform, y);

            // Przewijana lista wszystkich kategorii - GUIManager.CreateScrollView buduje caly
            // mechanizm (ScrollRect/Viewport/Mask/Scrollbar) I wlasny kontener "Content" z
            // VerticalLayoutGroup + ContentSizeFitter - wiersze wystarczy dodac jako dzieci w
            // kolejnosci, bez recznego liczenia pozycji Y (to byla przyczyna poprzedniego bugu).
            // Dzieci "_settingsPanel" pozycjonujemy wzgledem GORNEJ krawedzi (anchor Y=1, patrz
            // komentarz wyzej), wiec DOLNA krawedz panelu w tym samym ukladzie to Y=-MenuPanelHeight
            // (CALA wysokosc, nie polowa - to byl blad przez pomylenie z lokalnym ukladem pivota
            // samego panelu, ktory jest inny niz uklad, w ktorym poruszaja sie jego dzieci).
            float bottomMargin = 20f;
            float listHeight = y + MenuPanelHeight - bottomMargin;
            var handleColors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(1f, 0.9f, 0.7f, 1f),
                pressedColor = gui.ValheimOrange,
                disabledColor = Color.gray,
                colorMultiplier = 1f,
                fadeDuration = 0.1f
            };
            var scrollViewGO = gui.CreateScrollView(_settingsPanel.transform, false, true, ListHandleSize, ListHandleDistance,
                handleColors, new Color(0f, 0f, 0f, 0.4f), ListWidth, listHeight);
            var scrollViewRect = scrollViewGO.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = new Vector2(0.5f, 1f);
            scrollViewRect.anchorMax = new Vector2(0.5f, 1f);
            scrollViewRect.pivot = new Vector2(0.5f, 1f);
            scrollViewRect.anchoredPosition = new Vector2(0f, y);

            var scrollRect = scrollViewGO.transform.Find("Scroll View").GetComponent<ScrollRect>();
            // Domyslne 1f przewija praktycznie niezauwazalnie; 250 potwierdzone w grze jako OK.
            scrollRect.scrollSensitivity = 250f;

            var content = scrollViewGO.transform.Find("Scroll View/Viewport/Content");
            // CreateScrollView bez poziomego paska nie steruje szerokoscia wierszy - kazdy
            // zostawal przy domyslnych 100 px, a prawa kolumna ("Label") ladowala na nazwach.
            content.GetComponent<VerticalLayoutGroup>().childControlWidth = true;

            // Lista zalezy od odkryc w AKTUALNYM swiecie, a panel powstaje przy wczytaniu sceny
            // (zanim swiat jest znany) - dlatego wiersze buduje RebuildCategoryList przy otwarciu.
            _categoryListContent = content;
            _categoryListDirty = true;
        }

        private Transform _categoryListContent;
        private readonly HashSet<string> _expandedGroups = new HashSet<string>();

        private void RebuildCategoryList()
        {
            if (_categoryListContent == null)
                return;
            EnsureDiscoveryLoaded();
            _categoryListDirty = false;

            // Destroy dziala dopiero pod koniec klatki - bez SetActive(false) uklad listy
            // policzylby na chwile stare i nowe wiersze naraz.
            foreach (Transform child in _categoryListContent.Cast<Transform>().ToList())
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            bool any = false;

            // Portale zostaja plaskie (jedna pozycja, nie warto grupowac). Statki maja dwie
            // niezalezne pozycje (zywy pin podczas zeglowania / trwaly znacznik po zejsciu z
            // pokladu), wiec dostaja wlasna, malutka grupe - ten sam mechanizm co reszta.
            if (IsShownInMenu(_portalCategory))
            {
                BuildScrollToggleRow(_categoryListContent, "Portals", _portalsEnabled, _portalCategory.ShowName, 0f);
                any = true;
            }
            var ships = new[] { _shipLiveCategory, _shipDockedCategory }.Where(IsShownInMenu).ToList();
            if (ships.Count > 0)
            {
                BuildScrollGroup(_categoryListContent, "Ships", ships);
                any = true;
            }

            // Reszta kategorii pogrupowana tematycznie (patrz GetGroupName) - lista 40+ pozycji
            // na plasko byla nieczytelna.
            foreach (var group in _categories.Where(IsShownInMenu).GroupBy(GetGroupName)
                         .OrderBy(g => g.Key == "Other" ? 1 : 0).ThenBy(g => g.Key))
            {
                BuildScrollGroup(_categoryListContent, group.Key, group.ToList());
                any = true;
            }

            if (!any)
                BuildEmptyListMessage(_categoryListContent);
        }

        private void BuildEmptyListMessage(Transform content)
        {
            var gui = GUIManager.Instance;
            var rowGO = new GameObject("EmptyMessage", typeof(RectTransform), typeof(LayoutElement));
            rowGO.transform.SetParent(content, false);
            rowGO.GetComponent<LayoutElement>().preferredHeight = MenuRowHeight * 3f;

            var textGO = gui.CreateText("Nothing discovered yet.\nCategories appear here as the mod marks them on your map.",
                rowGO.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                gui.AveriaSerifBold, 14, gui.ValheimBeige, false, Color.black, ListRowWidth - 20f, MenuRowHeight * 3f, false);
            var text = textGO.GetComponent<Text>();
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        // Grupowanie tylko dla wygody wyswietlania w menu (nie zmienia zadnej logiki skanowania)
        // - rudy i jagody nie maja wspolnego przedrostka klucza wiec sa wymienione wprost,
        // reszta po przedrostku klucza (Mushroom*/Crop*/Seed*/Struct*).
        private static readonly HashSet<string> OreKeys = new HashSet<string> { "Copper", "Silver", "Tin", "Iron", "Obsidian", "Meteorite" };
        private static readonly HashSet<string> BerryKeys = new HashSet<string> { "Blueberry", "Cloudberry", "Raspberry", "Lingonberry" };
        private static readonly HashSet<string> HerbKeys = new HashSet<string> { "Thistle", "Dandelion" };

        private static string GetGroupName(ResourceCategory c)
        {
            if (c.MenuGroup != null) return c.MenuGroup;
            if (OreKeys.Contains(c.Key)) return "Ores";
            if (BerryKeys.Contains(c.Key)) return "Berries";
            if (HerbKeys.Contains(c.Key)) return "Herbs";
            if (c.Key.StartsWith("Seed", StringComparison.Ordinal)) return "Seeds";
            if (c.Key.StartsWith("Mushroom", StringComparison.Ordinal)) return "Mushrooms";
            if (c.Key.StartsWith("Crop", StringComparison.Ordinal)) return "Crops";
            if (c.Key.StartsWith("Struct", StringComparison.Ordinal)) return "Structures";
            return "Other";
        }

        // Naglowek grupy: wlasny checkbox (wlacza/wylacza WSZYSTKIE pozycje grupy naraz) +
        // przycisk +/- do rozwijania. Pozycje w srodku sa domyslnie zwiniete (SetActive(false)) -
        // VerticalLayoutGroup na Content automatycznie pomija nieaktywne dzieci przy liczeniu
        // ukladu, wiec zwijanie/rozwijanie nie wymaga zadnej dodatkowej logiki przeliczania.
        private void BuildScrollGroup(Transform content, string groupName, List<ResourceCategory> items)
        {
            var gui = GUIManager.Instance;
            // Lista jest przebudowywana przy kazdym odkryciu - rozwiniete grupy maja takie zostac.
            bool expanded = _expandedGroups.Contains(groupName);

            var headerGO = new GameObject($"Group_{groupName}", typeof(RectTransform), typeof(LayoutElement));
            headerGO.transform.SetParent(content, false);
            headerGO.GetComponent<LayoutElement>().preferredHeight = MenuRowHeight;
            headerGO.GetComponent<LayoutElement>().flexibleWidth = 1f;

            var groupToggle = CreateRowToggle(headerGO.transform, 0f, PinColumnLeft);
            groupToggle.isOn = items.All(i => i.Enabled.Value);
            var groupLabelToggle = CreateRowToggle(headerGO.transform, 1f, -LabelColumnRightOffset);
            groupLabelToggle.isOn = items.All(i => i.ShowName.Value);

            var expandGO = gui.CreateButton(expanded ? "-" : "+", headerGO.transform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(PinColumnLeft + ToggleSize + 6f, 0f), 22f, 22f);
            // CreateButton zostawia pivot na srodku - bez tego pozycja wskazywalaby srodek
            // przycisku, a jego lewa polowa wchodzila na checkbox obok.
            expandGO.GetComponent<RectTransform>().pivot = new Vector2(0f, 0.5f);
            var expandText = expandGO.GetComponentInChildren<Text>();

            var nameGO = gui.CreateText($"{groupName} ({items.Count})", headerGO.transform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(64f, 0f),
                gui.AveriaSerifBold, 15, gui.ValheimOrange, false, Color.black,
                RowTextRight - 64f, MenuRowHeight, false);
            nameGO.GetComponent<RectTransform>().pivot = new Vector2(0f, 0.5f);
            nameGO.GetComponent<Text>().alignment = TextAnchor.MiddleLeft;

            var itemRows = items.Select(item => BuildScrollToggleRow(content, item.DisplayName, item.Enabled, item.ShowName, 40f)).ToList();
            foreach (var row in itemRows)
                row.Row.SetActive(expanded);

            groupToggle.onValueChanged.AddListener(v =>
            {
                foreach (var item in items)
                    item.Enabled.Value = v;
                foreach (var row in itemRows)
                    row.Show.SetIsOnWithoutNotify(v);
            });
            groupLabelToggle.onValueChanged.AddListener(v =>
            {
                foreach (var item in items)
                    item.ShowName.Value = v;
                foreach (var row in itemRows)
                    row.Label.SetIsOnWithoutNotify(v);
            });

            expandGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                expanded = !expanded;
                if (expanded) _expandedGroups.Add(groupName);
                else _expandedGroups.Remove(groupName);
                expandText.text = expanded ? "-" : "+";
                foreach (var row in itemRows)
                    row.Row.SetActive(expanded);
            });
        }

        private const float ListWidth = MenuPanelWidth - 40f;
        private const float ListHandleSize = 10f;
        private const float ListHandleDistance = 2f;
        // Szerokosc wiersza = szerokosc Content w CreateScrollView (lista minus pasek przewijania).
        private const float ListRowWidth = ListWidth - 2f * ListHandleDistance - ListHandleSize;
        private const float ToggleSize = 20f;
        private const float PinColumnLeft = 6f;
        // Odstep prawej kolumny ("Label") od prawej krawedzi wiersza.
        private const float LabelColumnRightOffset = 24f;
        // Nazwa w wierszu konczy sie przed prawa kolumna, zeby nie zaslaniala jej checkboxa.
        private const float RowTextRight = ListRowWidth - LabelColumnRightOffset - ToggleSize - 8f;

        private Toggle CreateRowToggle(Transform row, float anchorX, float x)
        {
            var toggleGO = GUIManager.Instance.CreateToggle(row, ToggleSize, ToggleSize);
            var rect = toggleGO.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(anchorX, 0.5f);
            rect.anchorMax = new Vector2(anchorX, 0.5f);
            rect.pivot = new Vector2(anchorX, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            return toggleGO.GetComponent<Toggle>();
        }

        // Podpisy kolumn nad lista: lewa kolumna chowa/pokazuje pin, prawa sam podpis pod nim.
        private float BuildListColumnHeaders(Transform parent, float y)
        {
            var gui = GUIManager.Instance;
            // Lista jest wysrodkowana w panelu - srodki checkboxow liczone od jej lewej krawedzi.
            float listLeft = (MenuPanelWidth - ListWidth) / 2f;
            float pinCenter = listLeft + PinColumnLeft + ToggleSize / 2f;
            float labelCenter = listLeft + ListRowWidth - LabelColumnRightOffset - ToggleSize / 2f;

            foreach (var (text, centerX) in new[] { ("Pin", pinCenter), ("Label", labelCenter) })
            {
                var header = gui.CreateText(text, parent, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(centerX, y), gui.AveriaSerifBold, 13, gui.ValheimOrange, false, Color.black, 60f, 20f, false);
                header.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 1f);
                header.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;
            }

            return y - 20f;
        }

        // Jeden wiersz wewnatrz przewijanej listy (Content ma VerticalLayoutGroup, wiec sam
        // ustawia pozycje pionowa kolejnych wierszy - trzeba mu tylko dac LayoutElement z
        // preferowana wysokoscia). W srodku wiersza toggle+tekst pozycjonujemy recznie.
        // "indent" przesuwa caly wiersz w prawo - uzywane dla pozycji wewnatrz grupy.
        private (GameObject Row, Toggle Show, Toggle Label) BuildScrollToggleRow(Transform content, string label,
            ConfigEntry<bool> config, ConfigEntry<bool> labelConfig, float indent)
        {
            var gui = GUIManager.Instance;

            var rowGO = new GameObject("Row", typeof(RectTransform), typeof(LayoutElement));
            rowGO.transform.SetParent(content, false);
            rowGO.GetComponent<LayoutElement>().preferredHeight = MenuRowHeight;
            rowGO.GetComponent<LayoutElement>().flexibleWidth = 1f;

            var toggle = CreateRowToggle(rowGO.transform, 0f, PinColumnLeft + indent);
            toggle.isOn = config.Value;
            toggle.onValueChanged.AddListener(v => config.Value = v);

            var labelToggle = CreateRowToggle(rowGO.transform, 1f, -LabelColumnRightOffset);
            labelToggle.isOn = labelConfig.Value;
            labelToggle.onValueChanged.AddListener(v => labelConfig.Value = v);

            var textGO = gui.CreateText(label, rowGO.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(32f + indent, 0f), gui.AveriaSerifBold, 15, Color.white, false, Color.black,
                RowTextRight - 32f - indent, MenuRowHeight, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.pivot = new Vector2(0f, 0.5f);
            textGO.GetComponent<Text>().alignment = TextAnchor.MiddleLeft;

            return (rowGO, toggle, labelToggle);
        }

        // Zwraca nowa pozycje Y (dla nastepnego wiersza) - kazdy wiersz to gotowy Toggle
        // (Jotunn.GUIManager.CreateToggle) z wlasnym opisem tekstowym obok, dwustronnie
        // zsynchronizowany z podanym ConfigEntry (czyta stan startowy, zapisuje zmiany).
        private float BuildToggleRow(Transform parent, float y, string label, ConfigEntry<bool> config)
        {
            var gui = GUIManager.Instance;
            var toggleGO = gui.CreateToggle(parent, 20f, 20f);
            var toggleRect = toggleGO.GetComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0f, 1f);
            toggleRect.anchorMax = new Vector2(0f, 1f);
            toggleRect.pivot = new Vector2(0f, 1f);
            toggleRect.anchoredPosition = new Vector2(10f, y);

            var toggle = toggleGO.GetComponent<Toggle>();
            toggle.isOn = config.Value;
            toggle.onValueChanged.AddListener(v => config.Value = v);

            var textGO = gui.CreateText(label, parent, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(36f, y), gui.AveriaSerifBold, 15, Color.white, false, Color.black,
                MenuPanelWidth - 90f, MenuRowHeight, false);
            textGO.GetComponent<RectTransform>().pivot = new Vector2(0f, 1f);
            textGO.GetComponent<Text>().alignment = TextAnchor.MiddleLeft;

            return y - MenuRowHeight;
        }

        private const float SliderHandleSize = 14f;

        // Raczka z tych samych grafik co checkboxy tego menu (ramka "checkbox" + pomaranczowy
        // znacznik "checkbox_marker", jak w GUIManager.ApplyToogleStyle). Suwaki w ustawieniach
        // gry tez maja znacznik jako raczke (potwierdzone w logu: GamepadSensitivity), ale
        // kopiowanie ich Image w calosci (kolor/material) robilo raczke niewidoczna w tym panelu.
        private static void ApplyGameSliderHandleStyle(Image handle)
        {
            var gui = GUIManager.Instance;
            var frame = gui.GetSprite("checkbox");
            var marker = gui.GetSprite("checkbox_marker");
            if (frame == null || marker == null)
            {
                Log.LogWarning("Suwak zasiegu: brak grafik 'checkbox'/'checkbox_marker' w grze - raczka zostaje prostym prostokatem.");
                return;
            }

            handle.sprite = frame;
            handle.type = Image.Type.Simple;
            handle.preserveAspect = true;
            handle.color = Color.white;

            var markerGO = new GameObject("Marker", typeof(RectTransform), typeof(Image));
            markerGO.transform.SetParent(handle.transform, false);
            var markerRect = markerGO.GetComponent<RectTransform>();
            markerRect.anchorMin = Vector2.zero;
            markerRect.anchorMax = Vector2.one;
            markerRect.offsetMin = Vector2.zero;
            markerRect.offsetMax = Vector2.zero;
            var markerImage = markerGO.GetComponent<Image>();
            markerImage.sprite = marker;
            markerImage.preserveAspect = true;
            markerImage.color = new Color(1f, 0.678f, 0.103f, 1f);
            markerImage.raycastTarget = false;
        }

        private float BuildScanRadiusRow(Transform parent, float y)
        {
            var gui = GUIManager.Instance;

            var labelGO = gui.CreateText($"Scan radius: {_scanRadius.Value:0} m", parent,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, y),
                gui.AveriaSerifBold, 15, gui.ValheimOrange, false, Color.black,
                MenuPanelWidth - 40f, MenuRowHeight, false);
            labelGO.name = "ScanRadiusLabel";
            labelGO.GetComponent<RectTransform>().pivot = new Vector2(0f, 1f);
            var labelText = labelGO.GetComponent<Text>();
            labelText.alignment = TextAnchor.MiddleLeft;

            var sliderGO = new GameObject("ScanRadiusSlider", typeof(RectTransform), typeof(Slider));
            sliderGO.transform.SetParent(parent, false);
            var sliderRect = sliderGO.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 1f);
            sliderRect.anchorMax = new Vector2(0f, 1f);
            sliderRect.pivot = new Vector2(0f, 1f);
            sliderRect.anchoredPosition = new Vector2(10f, y - MenuRowHeight);
            sliderRect.sizeDelta = new Vector2(MenuPanelWidth - 40f, 20f);

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(sliderGO.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0f, 0.25f);
            bgRect.anchorMax = new Vector2(1f, 0.75f);
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            var fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGO.transform.SetParent(sliderGO.transform, false);
            var fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0f, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1f, 0.75f);
            fillAreaRect.offsetMin = new Vector2(5f, 0f);
            fillAreaRect.offsetMax = new Vector2(-5f, 0f);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            var fillRect = fillGO.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.sizeDelta = new Vector2(10f, 0f);
            fillGO.GetComponent<Image>().color = GUIManager.Instance.ValheimOrange;

            var handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleAreaGO.transform.SetParent(sliderGO.transform, false);
            var handleAreaRect = handleAreaGO.GetComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(10f, 0f);
            handleAreaRect.offsetMax = new Vector2(-10f, 0f);

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            var handleRect = handleGO.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(SliderHandleSize, SliderHandleSize);
            handleGO.GetComponent<Image>().color = Color.white;

            ApplyGameSliderHandleStyle(handleGO.GetComponent<Image>());

            var slider = sliderGO.GetComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleGO.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 5f;
            slider.maxValue = 100f;
            slider.wholeNumbers = true;
            slider.value = _scanRadius.Value;
            slider.onValueChanged.AddListener(v =>
            {
                _scanRadius.Value = v;
                labelText.text = $"Scan radius: {v:0} m";
            });

            return y - MenuRowHeight - 20f;
        }
    }
}
