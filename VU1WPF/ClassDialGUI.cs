using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VU1WPF
{
    public class ClassDialGUI
    {
        private string gFriendlyName = "";
        private string gUID = "";
        private string gMetric = "";
        private float gScaleMin = 0;
        private float gScaleMax = 100;
        private int gValue = 0;
        private string gSensorIdentifier = "";
        private string gSensorName = "";
        private ISensor? gSensor;
        private List<ClassDialThreshold> gThresholds = new List<ClassDialThreshold>();

        public string FriendlyName 
        { 
            get { return gFriendlyName; }
            set { gFriendlyName = value; }
        }

        public string UID 
        { 
            get { return gUID; } 
            set { gUID = value; } 
        }
        public string Metric 
        { 
            get { return gMetric; } 
            set { gMetric = value; } 
        }

        public float ScaleMin
        {
            get { return gScaleMin; }
            set { gScaleMin = value; }
        }

        public float ScaleMax
        {
            get { return gScaleMax; }
            set { gScaleMax = value; }
        }

        public int Value
        {
            get { return gValue;  }
            set { gValue = value; }
        }

        public string SensorIdentifier
        {
            get { return gSensorIdentifier; }
            set { gSensorIdentifier = value; }
        }

        public string SensorName
        {
            get { return gSensorName; }
            set { gSensorName = value; }
        }

        public ISensor? Sensor
        {
            get { return gSensor; }
            set { gSensor = value; }
        }

        public List<ClassDialThreshold> Thresholds
        {
            get { return gThresholds; }
            set { gThresholds = value ?? new List<ClassDialThreshold>(); }
        }

        /// <summary>Physical finish: "black" or "silver".</summary>
        public string Finish { get; set; } = "black";

        /// <summary>When true (and rules exist) the colour rules drive the backlight.</summary>
        public bool RulesEnabled { get; set; } = true;

        /// <summary>Fixed backlight colour (0..100 per channel), used when rules are off. Null = not set.</summary>
        public (int r, int g, int b)? FixedColor { get; set; }

        /// <summary>Colour the backlight should currently have, or null to leave it alone.</summary>
        public (int r, int g, int b)? DesiredBacklight(int dialValue)
        {
            if (RulesEnabled && Thresholds.Count > 0)
            {
                var match = DialMath.FindThreshold(Thresholds.ToArray(), dialValue);
                if (match != null) return (match.BacklightRed, match.BacklightGreen, match.BacklightBlue);
            }
            return FixedColor;
        }

        /// <summary>True when the backlight follows the colour rules.</summary>
        public bool UsesRules => RulesEnabled && Thresholds.Count > 0;

        /// <summary>True while identify mode flashes this dial; the update loop leaves the backlight alone.</summary>
        internal volatile bool Identifying;

        /// <summary>Backlight reported by the server at start-up (already in server percent units).</summary>
        internal (int r, int g, int b)? ServerBacklight;

        // ---- Runtime state used by the update loop to only send changes to the server ----
        internal int LastSentValue = -1;
        internal (int r, int g, int b)? LastSentBacklight;
        internal DateTime LastValueSentUtc = DateTime.MinValue;
        internal DateTime LastBacklightSentUtc = DateTime.MinValue;

        /// <summary>Forget what was sent so the next update loop iteration re-sends everything.</summary>
        internal void ResetSentState()
        {
            LastSentValue = -1;
            LastSentBacklight = null;
            LastValueSentUtc = DateTime.MinValue;
            LastBacklightSentUtc = DateTime.MinValue;
        }
    }


    /// <summary>Pure helpers for converting sensor readings to dial values (no UI / IO).</summary>
    public static class DialMath
    {
        /// <summary>Scales <paramref name="sensorValue"/> into 0..100 % using the dial's min/max.</summary>
        public static int ScaleToPercent(float sensorValue, float scaleMin, float scaleMax)
        {
            if (float.IsNaN(sensorValue)) return 0;

            float lo = Math.Min(scaleMin, scaleMax);
            float hi = Math.Max(scaleMin, scaleMax);

            // Same min and max -> show 0%
            if (hi - lo < float.Epsilon || sensorValue <= lo) return 0;
            if (sensorValue >= hi) return 100;

            float scaled = (sensorValue - lo) / (hi - lo) * 100f;
            return (int)Math.Round(Math.Clamp(scaled, 0f, 100f));
        }

        /// <summary>
        /// Returns the first rule (by ascending threshold) whose threshold is &gt;= value,
        /// i.e. "when value is less or equal to X% use this colour". Null if none matches.
        /// </summary>
        public static ClassDialThreshold? FindThreshold(IEnumerable<ClassDialThreshold>? thresholds, int dialValue)
        {
            if (thresholds == null) return null;
            return thresholds
                .Where(t => t != null)
                .OrderBy(t => t.Threshold)
                .FirstOrDefault(t => t.Threshold >= dialValue);
        }
    }


    public class ClassDialThreshold
    {
        private int gThreshold = 0;
        private int gBacklightRed = 0;
        private int gBacklightGreen = 0;
        private int gBacklightBlue = 0;

        public int Threshold
        {
            get { return gThreshold; }
            set { gThreshold = value; }

        }

        public int BacklightRed
        {
            get { return gBacklightRed; }
            set { gBacklightRed = value; }
        }

        public int BacklightGreen
        {
            get { return gBacklightGreen; }
            set { gBacklightGreen = value; }
        }

        public int BacklightBlue
        {
            get { return gBacklightBlue; }
            set { gBacklightBlue = value; }
        }
    }

    public class BacklightPresetColor
    {
        private string gName = "";
        private int gRed;
        private int gGreen;
        private int gBlue;

        public string Name
        {
            set { gName = value; }
            get { return gName; }
        }

        public int Red
        {
            set { gRed = value; }
            get { return gRed; }
        }


        public int Green
        {
            set { gGreen = value; }
            get { return gGreen; }
        }


        public int Blue
        {
            set { gBlue = value; }
            get { return gBlue; }
        }
    }

    public static class BacklightPresets
    {
        private static readonly List<BacklightPresetColor> gBacklightPresets = new List<BacklightPresetColor>
        {
            new BacklightPresetColor { Name = "Off",  Red = 0, Green = 0, Blue = 0 },
            new BacklightPresetColor { Name = "Red",  Red = 100, Green = 0, Blue = 0 },
            new BacklightPresetColor { Name = "Green",  Red = 0, Green = 100, Blue = 0 },
            new BacklightPresetColor { Name = "Blue",  Red = 0, Green = 0, Blue = 100 },
            new BacklightPresetColor { Name = "Cyan",  Red = 0, Green = 61, Blue = 100 },
            new BacklightPresetColor { Name = "Magenta",  Red = 54, Green = 0, Blue = 100 },
            new BacklightPresetColor { Name = "Yellow",  Red = 39, Green = 34, Blue = 2 },
            new BacklightPresetColor { Name = "Orange",  Red = 100, Green = 49, Blue = 0 },
            new BacklightPresetColor { Name = "White",  Red = 100, Green = 100, Blue = 100 }
        };

        public static List<BacklightPresetColor> AvailablePresets => gBacklightPresets;
    }

}
