using System;
using System.Collections.Generic;

namespace VU1WPF
{
    class ConfigThreshold
    {
        public int value { get; set; }
        public int red { get; set; }
        public int green { get; set; }
        public int blue { get; set; }
    }

    class ConfigContentsDial
    {
        public String dial_uid { get; set; } = "";
        public String sensor_identifier { get; set; } = "";
        public float scaling_min { get; set; } = 0;    // Value to be used as 0%
        public float scaling_max { get; set; } = 100;  // Value to be used as 100%

        public List<ConfigThreshold> thresholds { get; set; } = new List<ConfigThreshold>();

        /// <summary>Name of the saved image design last sent to this dial (empty if none).</summary>
        public String image_design { get; set; } = "";

        /// <summary>Physical finish of the dial: "black" or "silver".</summary>
        public String finish { get; set; } = "black";

        /// <summary>Colour rules on/off. Null in old config files = on when rules exist.</summary>
        public bool? rules_enabled { get; set; }

        /// <summary>Fixed backlight colour [red, green, blue] (0..100). Null/empty = not set.</summary>
        public List<int>? fixed_color { get; set; }
    }

    class ConfigContentsRoot
    {
        /// <summary>Base URL of the VU1 server, e.g. http://localhost:5340</summary>
        public String serverUrl { get; set; } = "";
        public String masterKey { get; set; } = "";
        public float dialUpdatePeriod { get; set; }

        /// <summary>Metric &amp; scaling section expanded on all dial cards.</summary>
        public bool metricsExpanded { get; set; } = true;

        /// <summary>Identify mode (double-click a dial): backlight colour [r, g, b] 0..100 and duration.</summary>
        public List<int> identifyColor { get; set; } = new List<int> { 100, 100, 100 };
        public int identifyDurationMs { get; set; } = 1500;

        /// <summary>Dial UIDs in the order the user arranged them.</summary>
        public List<String> dialOrder { get; set; } = new List<String>();

        public List<ConfigContentsDial> dial_metrics { get; set; }

        public ConfigContentsRoot()
        {
            dial_metrics = new List<ConfigContentsDial>();
        }
    }
}
