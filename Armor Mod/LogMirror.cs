using System;
using BepInEx.Logging;

namespace ArmorFragments
{
    // Listens to BepInEx's log and keeps the latest lines written by THIS plugin,
    // so the in-game panel can show them. Optionally also prints them to the
    // game's developer console (Unity's Debug.Log).
    internal sealed class LogMirror : ILogListener
    {
        private const int Capacity = 30;

        private static readonly object Lock = new object();
        private static readonly string[] Lines = new string[Capacity];
        private static readonly string[] Times = new string[Capacity];
        private static int next;
        private static int count;

        // Goes up every time a line is added, so the panel knows when to redraw.
        public static int Version { get; private set; }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (ArmorFragmentsPlugin.Log == null) return;
            if (eventArgs.Source.SourceName != ArmorFragmentsPlugin.Log.SourceName) return;

            string text = eventArgs.Data == null ? "" : eventArgs.Data.ToString();
            string prefix = eventArgs.Level == LogLevel.Info ? "" : "[" + eventArgs.Level + "] ";

            lock (Lock)
            {
                Lines[next] = prefix + text;
                Times[next] = DateTime.Now.ToString("HH:mm:ss");
                next = (next + 1) % Capacity;
                if (count < Capacity) count++;
                Version++;
            }

            if (ArmorFragmentsPlugin.MirrorLogToGameConsole != null &&
                ArmorFragmentsPlugin.MirrorLogToGameConsole.Value)
            {
                UnityEngine.Debug.Log("[Armor Fragments] " + prefix + text);
            }
        }

        // The newest 'max' lines, newest first.
        public static void Latest(int max, out string[] times, out string[] lines)
        {
            lock (Lock)
            {
                int n = Math.Min(max, count);
                times = new string[n];
                lines = new string[n];
                for (int i = 0; i < n; i++)
                {
                    int index = (next - 1 - i + Capacity * 2) % Capacity;
                    times[i] = Times[index];
                    lines[i] = Lines[index];
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
