using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Estimates one-way Arduino-send to Unity-receive latency for the
/// continuous telemetry stream (the "TS:" field added to broadcastState()).
///
/// Arduino's millis() and Unity's clock are two different, unsynchronized
/// clocks, so you can't just subtract their raw values - the gap between
/// them (the "offset") is unknown and could be anything. This class works
/// around that WITHOUT a separate handshake step.
///
/// Needs a few hundred samples to settle (a few seconds at 10ms/line) -
/// don't trust the numbers in the first 2-3 seconds after connecting.
/// </summary>
public static class LatencyMonitor
{
    private static long minRawDiff = long.MaxValue;
    private static readonly List<long> latencies = new List<long>();
    private const int MAX_SAMPLES = 5000;

    // Call this once per received serial line, passing the integer you
    // parsed out of the new ",TS:<millis>" field at the end of the line.
    public static void Record(long arduinoMillis)
    {
        long unityNowMs = (long)(Time.realtimeSinceStartup * 1000.0);
        long rawDiff = unityNowMs - arduinoMillis;

        if (rawDiff < minRawDiff) minRawDiff = rawDiff;

        long latencyMs = rawDiff - minRawDiff; // always >= 0
        if (latencies.Count >= MAX_SAMPLES) latencies.RemoveAt(0);
        latencies.Add(latencyMs);
    }

    public struct Stats { public float mean; public float sd; public long max; public int n; }

    public static Stats GetStats()
    {
        var s = new Stats();
        if (latencies.Count == 0) return s;
        long sum = 0, max = 0;
        foreach (var l in latencies) { sum += l; if (l > max) max = l; }
        s.n = latencies.Count;
        s.mean = (float)sum / s.n;
        double sq = 0;
        foreach (var l in latencies) sq += (l - s.mean) * (l - s.mean);
        s.sd = (float)System.Math.Sqrt(sq / s.n);
        s.max = max;
        return s;
    }

    public static void Reset()
    {
        minRawDiff = long.MaxValue;
        latencies.Clear();
    }
}