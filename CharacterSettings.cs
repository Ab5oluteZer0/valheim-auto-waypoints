using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace AutoWaypoints
{
    // Ustawienia pinow (przelaczniki kategorii i podpisow, "Show all pins", "Show pin labels",
    // "Replace boss icons") sa zapisywane osobno dla kazdej postaci - piny mapy naleza do postaci.
    // Plik konfiguracji BepInEx trzyma ustawienia startowe dla postaci, ktora nie ma jeszcze
    // wlasnych (dlatego zmiany w grze nie sa do niego zapisywane). Promien skanu i opcje
    // diagnostyczne zostaja wspolne - to ustawienia komputera, nie postaci.
    public partial class AutoWaypointsPlugin
    {
        [Serializable]
        private class CharacterSettingsFile
        {
            // Rownolegle listy prostych typow - JsonUtility gubi listy wlasnych klas.
            public List<string> Keys = new List<string>();
            public List<bool> Values = new List<bool>();
        }

        private readonly Dictionary<string, ConfigEntry<bool>> _characterEntries = new Dictionary<string, ConfigEntry<bool>>();
        private readonly List<(Toggle Toggle, ConfigEntry<bool> Config)> _menuToggles = new List<(Toggle, ConfigEntry<bool>)>();
        private string _characterSettingsPath;
        private bool _applyingCharacterSettings;
        private bool _characterSettingsDirty;

        private static string EntryKey(ConfigEntryBase entry) => entry.Definition.Section + "/" + entry.Definition.Key;

        // Po zbudowaniu konfiguracji: zapis stanu startowego do pliku BepInEx i odciecie dalszych
        // zapisow - od teraz zmiany ida do pliku postaci.
        private void SetUpCharacterSettings()
        {
            foreach (var pair in Config)
            {
                var entry = pair.Value;
                if (!(entry is ConfigEntry<bool> flag))
                    continue;
                var section = entry.Definition.Section;
                var key = entry.Definition.Key;
                if (section == "Categories" || section == "Labels" ||
                    key == "ShowAutoPins" || key == "ReplaceBossAltars" || key == "ReplaceLocationIcons")
                    _characterEntries[EntryKey(entry)] = flag;
            }
            Config.Save();
            Config.SaveOnConfigSet = false;
            Config.SettingChanged += (_, e) =>
            {
                if (!_applyingCharacterSettings && _characterEntries.ContainsKey(EntryKey(e.ChangedSetting)))
                    _characterSettingsDirty = true;
            };
        }

        // Wczytuje ustawienia postaci, a gdy ich nie ma - ustawienia startowe z pliku konfiguracji.
        // Skutki zmian (chowanie/przywracanie pinow) sa wstrzymane - pelne wyrownanie mapy robi
        // RefreshAllVisibility po wczytaniu ukrytych pinow tej postaci.
        private void LoadCharacterSettings(string character)
        {
            _characterSettingsPath = Path.Combine(SaveDir, $"settings_{SafeFileName(character)}.json");
            _applyingCharacterSettings = true;
            try
            {
                if (File.Exists(_characterSettingsPath))
                {
                    var data = JsonUtility.FromJson<CharacterSettingsFile>(File.ReadAllText(_characterSettingsPath));
                    if (data?.Keys != null && data.Values != null && data.Keys.Count == data.Values.Count)
                    {
                        for (int i = 0; i < data.Keys.Count; i++)
                            if (_characterEntries.TryGetValue(data.Keys[i], out var entry))
                                entry.Value = data.Values[i];
                    }
                    else
                    {
                        Log.LogWarning($"Plik ustawien postaci {_characterSettingsPath} jest uszkodzony - uzywam ustawien startowych.");
                        Config.Reload();
                    }
                }
                else
                {
                    Config.Reload();
                    _characterSettingsDirty = true; // postac dostaje wlasny plik od razu
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie wczytac ustawien postaci ({_characterSettingsPath}): {e}");
            }
            finally
            {
                _applyingCharacterSettings = false;
            }

            foreach (var (toggle, config) in _menuToggles)
                if (toggle != null)
                    toggle.SetIsOnWithoutNotify(config.Value);
            _categoryListDirty = true;
            ApplyLabelVisibilityToAllPins();
        }

        private void SaveCharacterSettingsIfDirty()
        {
            if (!_characterSettingsDirty || _characterSettingsPath == null)
                return;
            _characterSettingsDirty = false;
            try
            {
                var data = new CharacterSettingsFile();
                foreach (var kv in _characterEntries)
                {
                    data.Keys.Add(kv.Key);
                    data.Values.Add(kv.Value.Value);
                }
                string json = JsonUtility.ToJson(data);
                var check = JsonUtility.FromJson<CharacterSettingsFile>(json);
                if (check?.Keys == null || check.Keys.Count != data.Keys.Count || check.Values.Count != data.Values.Count)
                {
                    Log.LogError($"Zapis ustawien postaci: serializacja zgubila dane ('{json}') - NIE nadpisuje pliku.");
                    return;
                }
                WriteFileAtomically(_characterSettingsPath, json);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie zapisac ustawien postaci: {e}");
            }
        }
    }
}
