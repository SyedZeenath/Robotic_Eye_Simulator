using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Loads KEY=VALUE pairs from a .env file at the Unity project root
// (sibling of Assets/), so secrets like API keys don't get hardcoded
// into source files that are committed to git.
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

        string path = Path.Combine(Application.dataPath, "..", ".env");
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[EnvLoader] No .env file found at {path}");
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
    }
}
