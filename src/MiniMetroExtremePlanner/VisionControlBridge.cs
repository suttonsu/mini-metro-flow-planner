using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace MiniMetroExtremePlanner
{
    internal enum VisionControlMode
    {
        NativeFallback,
        Aligned,
        HoldContext,
        HoldStationMismatch
    }

    internal sealed class VisionDecisionState
    {
        public VisionControlMode Mode = VisionControlMode.NativeFallback;
        public bool ControlEnabled;
        public string Context = "unavailable";
        public float ContextConfidence;
        public int VisionStationCount = -1;
        public int NativeStationCount = -1;
        public float MeanStationConfidence;
        public int Sequence;
        public string Reason = "vision unavailable";

        public bool HoldsTopology
        {
            get
            {
                return Mode == VisionControlMode.HoldContext
                    || Mode == VisionControlMode.HoldStationMismatch;
            }
        }

        public string Summary
        {
            get
            {
                return Mode.ToString()
                    + ":" + Context
                    + "@" + ContextConfidence.ToString("0.00", CultureInfo.InvariantCulture)
                    + ":v" + VisionStationCount
                    + "/n" + NativeStationCount
                    + ":s" + Sequence;
            }
        }
    }

    internal static class VisionControlBridge
    {
        private const int SchemaVersion = 1;
        private const float FreshnessSeconds = 6.0f;
        private const float MinimumContextConfidence = 0.35f;
        private const float MinimumStationConfidence = 0.55f;
        private static VisionDecisionState current = new VisionDecisionState();
        private static float nextReadAt;
        private static string lastLoggedSummary = String.Empty;

        public static VisionDecisionState Current { get { return current; } }

        public static void Reset()
        {
            current = new VisionDecisionState();
            nextReadAt = 0f;
            lastLoggedSummary = String.Empty;
        }

        public static VisionDecisionState Read(int nativeStationCount)
        {
            float now = Time.unscaledTime;
            if (now < nextReadAt)
            {
                return current;
            }
            nextReadAt = now + 0.35f;

            VisionDecisionState updated = Load(nativeStationCount);
            current = updated;
            string summary = updated.Summary + ":" + updated.Reason;
            if (!String.Equals(summary, lastLoggedSummary, StringComparison.Ordinal))
            {
                lastLoggedSummary = summary;
                UnityEngine.Debug.Log(
                    "[Auto Planner][VisionControl] mode=" + updated.Mode
                    + " enabled=" + updated.ControlEnabled
                    + " context=" + updated.Context
                    + " contextConfidence=" + updated.ContextConfidence.ToString("0.000", CultureInfo.InvariantCulture)
                    + " visualStations=" + updated.VisionStationCount
                    + " nativeStations=" + updated.NativeStationCount
                    + " detectorConfidence=" + updated.MeanStationConfidence.ToString("0.000", CultureInfo.InvariantCulture)
                    + " sequence=" + updated.Sequence
                    + " reason=" + updated.Reason.Replace(' ', '_'));
            }
            return updated;
        }

        private static VisionDecisionState Load(int nativeStationCount)
        {
            VisionDecisionState state = new VisionDecisionState();
            state.NativeStationCount = nativeStationCount;
            string path = Path.Combine(Application.persistentDataPath, "vision-control-state.txt");
            try
            {
                if (!File.Exists(path))
                {
                    state.Reason = "state file missing";
                    return state;
                }
                Dictionary<string, string> values = Parse(ReadLines(path));
                double published = Double(values, "published_unix", -1.0);
                double unixNow = (DateTime.UtcNow - new DateTime(
                    1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double age = unixNow - published;
                if (age < -2.0 || age > FreshnessSeconds)
                {
                    state.Reason = "state stale";
                    return state;
                }

                int schema = Integer(values, "schema", -1);
                if (schema != SchemaVersion)
                {
                    state.Reason = "unsupported schema " + schema;
                    return state;
                }
                state.ControlEnabled = Boolean(values, "control_enabled", false);
                state.Context = Text(values, "context_label", "unknown");
                state.ContextConfidence = Float(values, "context_confidence", 0f);
                state.VisionStationCount = Integer(values, "station_count", -1);
                state.MeanStationConfidence = Float(values, "mean_station_confidence", 0f);
                state.Sequence = Integer(values, "sequence", 0);
                if (!state.ControlEnabled)
                {
                    state.Reason = "advisory state";
                    return state;
                }
                if (!String.Equals(state.Context, "game_play", StringComparison.OrdinalIgnoreCase)
                    || state.ContextConfidence < MinimumContextConfidence)
                {
                    state.Mode = VisionControlMode.HoldContext;
                    state.Reason = "visual context not confidently playable";
                    return state;
                }
                if (state.VisionStationCount <= 0
                    || state.MeanStationConfidence < MinimumStationConfidence)
                {
                    state.Reason = "detector confidence insufficient";
                    return state;
                }

                int mismatch = Math.Abs(state.VisionStationCount - nativeStationCount);
                int tolerance = Math.Max(2, (int)Math.Ceiling(nativeStationCount * 0.25f));
                if (mismatch >= tolerance)
                {
                    state.Mode = VisionControlMode.HoldStationMismatch;
                    state.Reason = "station disagreement " + mismatch + ">=" + tolerance;
                    return state;
                }
                state.Mode = VisionControlMode.Aligned;
                state.Reason = "visual and native state aligned";
                return state;
            }
            catch (Exception exception)
            {
                state.Reason = "read recovered " + exception.GetType().Name;
                return state;
            }
        }

        private static Dictionary<string, string> Parse(string[] lines)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf('=');
                if (separator <= 0) continue;
                values[lines[i].Substring(0, separator).Trim()] = lines[i].Substring(separator + 1).Trim();
            }
            return values;
        }

        private static string[] ReadLines(string path)
        {
            List<string> lines = new List<string>();
            using (StreamReader reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        private static string Text(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static int Integer(Dictionary<string, string> values, string key, int fallback)
        {
            int value;
            return Int32.TryParse(Text(values, key, String.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        private static float Float(Dictionary<string, string> values, string key, float fallback)
        {
            float value;
            return Single.TryParse(Text(values, key, String.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        private static double Double(Dictionary<string, string> values, string key, double fallback)
        {
            double value;
            return System.Double.TryParse(Text(values, key, String.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        private static bool Boolean(Dictionary<string, string> values, string key, bool fallback)
        {
            bool value;
            return System.Boolean.TryParse(Text(values, key, String.Empty), out value) ? value : fallback;
        }
    }
}
