using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MonsterMusumeTDMod.Android;

public sealed class ConfigEntry<T>
{
    public T Value { get; set; }
    internal ConfigEntry(T value) => Value = value;
}

// Keep the shared settings contract while persisting to Android's writable UserData.
public sealed class ConfigFile
{
    private readonly string _path;
    private Dictionary<string, JsonElement> _values = new();
    private readonly Dictionary<string, Action<JsonElement>> _readers = new();
    private readonly Dictionary<string, Func<object>> _writers = new();

    public ConfigFile(string path)
    {
        _path = path;
        Reload();
    }

    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
    {
        var name = section + "." + key;
        // PC bundles and update manifests contain platform-specific shaders.
        if (name == "Translation.Font.FontAssetPath")
            defaultValue = (T)(object)"alimama-android";
        if (name == "Translation.Update.AssetBaseUrl")
            defaultValue = (T)(object)"https://raw.githubusercontent.com/Berumic/MonsterMusumeTDChineseTranslation/main/assets/android";
        if (name.StartsWith("Debug.", StringComparison.Ordinal))
            defaultValue = (T)(object)false;
        var entry = new ConfigEntry<T>(defaultValue);
        _readers[name] = value => entry.Value = value.Deserialize<T>();
        _writers[name] = () => entry.Value;
        if (_values.TryGetValue(name, out var stored))
            ReadEntry(name, stored);
        return entry;
    }

    public void Reload()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_path));
            if (values == null)
                throw new InvalidDataException("Configuration must be a JSON object");
            _values = values;
            foreach (var pair in _values)
                ReadEntry(pair.Key, pair.Value);
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"Configuration reload failed: {ex.Message}");
        }
    }

    private void ReadEntry(string key, JsonElement value)
    {
        if (!_readers.TryGetValue(key, out var read))
            return;
        try { read(value); }
        catch (Exception ex) { Core.Plugin.Log?.LogWarning($"Invalid setting {key}: {ex.Message}"); }
    }

    public void Save()
    {
        var result = new Dictionary<string, object>();
        foreach (var pair in _values)
            result[pair.Key] = pair.Value;
        foreach (var pair in _writers)
            result[pair.Key] = pair.Value();
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_path + ".tmp", _path, true);
    }
}
