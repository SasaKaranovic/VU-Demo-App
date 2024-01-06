using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace KR_VU1_Sensors
{
    class ClassVUSensors
    {
        public class VU1_Sensor
        {
            public ISensor Sensor { get; set; } // Actual Sensor node
            public String DisplayName { get; set; } // "Pretty" name displayed in the GUI

            public VU1_Sensor(ISensor sens, String name = "")
            {
                Sensor = sens;

                if (DisplayName == "" || DisplayName == null)
                {
                    DisplayName = sens.Identifier.ToString();
                }
                else
                {
                    DisplayName = name;
                }
                
            }
        }

        public class VU1_SensorManager
        {
            private List<VU1_Sensor> gSensorsAvailable = new List<VU1_Sensor>();
            private List<VU1_Sensor> gSensorsUsed = new List<VU1_Sensor>();

            private Computer computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsPsuEnabled = true,
                IsBatteryEnabled = true
            };

            public String[] UsedSensors =
            {
                "Voltage",
                //"Current",
                "Power",
                "Clock",
                "Temperature",
                "Load",
                //"Frequency",
                //"Fan",
                //"Flow",
                //"Control",
                //"Level",
                //"Factor",
                //"Data",
                //"SmallData",
                //"Throughput",
                //"TimeSpan",
                //"Energy",
                //"Noise"
            };

            public VU1_SensorManager()
            {
                computer.Open();
                computer.Accept(new UpdateVisitor());
                reload_available_sensors();
            }

            public List<VU1_Sensor> get_available_sensors()
            {
                return gSensorsAvailable;
            }

            public List<VU1_Sensor> get_used_sensors()
            {
                return gSensorsUsed;
            }


            public void reload_available_sensors()
            {
                foreach (IHardware hardware in computer.Hardware)
                {
                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        // Filter and use only sensors we are interested in
                        if (UsedSensors.Contains(sensor.SensorType.ToString()))
                        {
                            VU1_Sensor tmp = new VU1_Sensor(sensor);
                            gSensorsAvailable.Add(tmp);
                            gSensorsUsed.Add(tmp);
                        }

                    }
                }
            }

            public VU1_Sensor FindSensorByIdentifier(string identifier)
            {
                VU1_Sensor sens = gSensorsAvailable.Find(item => item.Sensor.Identifier.ToString() == identifier);
                return sens;
            }


        }


        public class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer)
            {
                computer.Traverse(this);
            }
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
    }
}
