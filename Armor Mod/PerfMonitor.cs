using System.Diagnostics;

namespace ArmorFragments
{
    // The release build has no frame-time monitor; only the stopwatch helper that a few log lines use remains.
    internal static class PerfMonitor
    {
        public static double Since(long timestamp)
        {
            return (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
