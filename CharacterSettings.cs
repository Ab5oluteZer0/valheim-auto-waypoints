using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace AutoWaypoints
{
    // Ustawienia z okna ustawien (przelaczniki kategorii i podpisow, "Show all pins", "Show pin
    // labels", "Replace boss icons", "Replace location icons", zasieg skanu) sa zapisywane osobno
    // dla kazdej postaci. Plik konfiguracji BepInEx trzyma ustawienia startowe dla postaci, ktora
    // nie ma jeszcze wlasnych (dlatego zmiany w grze nie sa do niego zapisywane). Opcje
    // diagnostyczne zostaja wspolne.
    public partial class AutoWaypointsPlugin
    {
        [Serializable]
        private class CharacterSettingsFile
        {
            // Rownolegle listy prostych typow - JsonUtility gubi listy wlasnych klas.
            public List<string> Keys = new List<string>();
            // Wartosci zapisane tak jak w pliku konfiguracji (tekst) - dowolny typ ustawienia.
            public List<string> Serialized = new List<string>();
            // Do 1.0.5 tylko przelaczniki (bool) - odczyt starych plikow.
            public List<bool> Values = new List<bool>();
        }

        private static readonly HashSet<string> CharacterGeneralKeys = new HashSet<string>
        {
            "ShowAutoPins", "ReplaceBossAltars", "ReplaceLocationIcons", "ScanRadius"
        };

        private readonly Dictionary<string, ConfigEntryBase> _characterEntries = new Dictionary<string, ConfigEntryBase>();
        private readonly List<(Toggle Toggle, ConfigEntry<bool> Config)> _menuToggles = new List<(Toggle, ConfigEntry<bool>)>();
        private Slider _scanRadiusSlider;
        private Text _scanRadiusLabel;
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
                var section = entry.Definition.Section;
                if (section == "Categories" || section == "Labels" || CharacterGeneralKeys.Contains(entry.Definition.Key))
                    _characterEntries[EntryKey(entry)] = entry;
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
                    if (!ApplyCharacterSettings(data))
                    {
                        Log.LogWarning($"Plik ustawien postaci {_characterSettingsPath} jest uszkodzony - uzywam ustawien startowych.");
                        Config.Reload();
                    }
                    // Plik z wersji sprzed zapisu wszystkich typow (bez zasiegu skanu) - uzupelnienie.
                    if (data?.Serialized == null || data.Serialized.Count != data.Keys?.Count)
                        _characterSettingsDirty = true;
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
            if (_scanRadiusSlider != null)
                _scanRadiusSlider.SetValueWithoutNotify(_scanRadius.Value);
            if (_scanRadiusLabel != null)
                _scanRadiusLabel.text = $"Scan radius: {_scanRadius.Value:0} m";
            _categoryListDirty = true;
            ApplyLabelVisibilityToAllPins();
        }

        private bool ApplyCharacterSettings(CharacterSettingsFile data)
        {
            if (data?.Keys == null)
                return false;
            if (data.Serialized != null && data.Serialized.Count == data.Keys.Count)
            {
                for (int i = 0; i < data.Keys.Count; i++)
                    if (_characterEntries.TryGetValue(data.Keys[i], out var entry))
                        entry.SetSerializedValue(data.Serialized[i]);
                return true;
            }
            if (data.Values != null && data.Values.Count == data.Keys.Count)
            {
                for (int i = 0; i < data.Keys.Count; i++)
                    if (_characterEntries.TryGetValue(data.Keys[i], out var entry) && entry is ConfigEntry<bool> flag)
                        flag.Value = data.Values[i];
                return true;
            }
            return false;
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
                    data.Serialized.Add(kv.Value.GetSerializedValue());
                }
                string json = JsonUtility.ToJson(data);
                var check = JsonUtility.FromJson<CharacterSettingsFile>(json);
                if (check?.Keys == null || check.Keys.Count != data.Keys.Count || check.Serialized?.Count != data.Serialized.Count)
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
