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
        public String dial_uid { get; set; }
        public String sensor_identifier { get; set; }
        public float scaling_min { get; set; }  // Value to be used as 0%
        public float scaling_max { get; set; } // Value to be used as 100%

        public List<ConfigThreshold> thresholds { get; set; }
    }

    class ConfigContentsRoot
    {
        public String masterKey { get; set; }
        public float dialUpdatePeriod { get; set; }

        public List<ConfigContentsDial> dial_metrics { get; set; }

        public ConfigContentsRoot()
        {
            dial_metrics = new List<ConfigContentsDial>();
        }
    }
}
