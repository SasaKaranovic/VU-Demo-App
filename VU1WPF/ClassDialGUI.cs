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
        private ISensor gSensor;
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

        public ISensor Sensor
        {
            get { return gSensor; }
            set { gSensor = value; }
        }

        public List<ClassDialThreshold> Thresholds
        {
            get { return gThresholds; }
            set { gThresholds = value; }
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
            new BacklightPresetColor { Name = "Teal",  Red = 0, Green = 100, Blue = 100 },
            new BacklightPresetColor { Name = "Orange",  Red = 100, Green = 10, Blue = 0 },
            new BacklightPresetColor { Name = "Purple",  Red = 100, Green = 10, Blue = 100 },
            new BacklightPresetColor { Name = "White(ish)",  Red = 100, Green = 30, Blue = 45 }
        };

        public static List<BacklightPresetColor> AvailablePresets => gBacklightPresets;
    }

}
