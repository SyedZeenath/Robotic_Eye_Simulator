using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Loads KEY=VALUE pairs from a .env file placed in Assets/StreamingAssets/.
// StreamingAssets is the one Unity folder guaranteed to be copied into the
// build output automatically (Editor and every standalone platform alike),
// so this works whether run from the Editor or a shared built exe, as long
// as the whole build output folder (exe + its _Data folder) is shared
// together, not the exe file alone.
public static class EnvLoader
{
    private static Dictionary<string, string> _values;

    public static string Get(string key)
    {
        string fromSystemEnv = System.Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(fromSystemEnv)) return fromSystemEnv;

        EnsureLoaded();
        return _values.TryGetValue(key, out string value) ? value : null;
    }

    private static void EnsureLoaded()
    {
        if (_values != null) return;
        _values = new Dictionary<string, string>();

        string path = Path.Combine(Application.streamingAssetsPath, "env");

        if (!File.Exists(path))
        {
            Debug.LogError($"[EnvLoader] No .env file found at {path}. " +
                            "Place it at Assets/StreamingAssets/env in the project, " +
                            "and make sure the whole build output folder is shared, not just the .exe.");
            return;
        }

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int separator = line.IndexOf('=');
            if (separator < 0) continue;

            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);

            _values[key] = value;
        }

        Debug.Log($"[EnvLoader] Loaded {_values.Count} value(s) from {path}");
    }
}