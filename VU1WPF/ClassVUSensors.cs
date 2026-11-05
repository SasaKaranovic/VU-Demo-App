using LibreHardwareMonitor.Hardware;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

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
                // (Previously this checked the not-yet-set DisplayName instead of the `name` argument.)
                DisplayName = string.IsNullOrEmpty(name) ? sens.Identifier.ToString() : name;
            }
        }

        public class VU1_SensorManager : IDisposable
        {
            private readonly List<VU1_Sensor> gSensorsAvailable = new List<VU1_Sensor>();
            private readonly object _updateLock = new();
            private bool _isOpen;

            private readonly Computer computer = new Computer
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
                try
                {
                    computer.Open();
                    _isOpen = true;
                    computer.Accept(new UpdateVisitor());
                }
                catch (Exception ex)
                {
                    // Missing driver/permissions must not prevent the app from starting.
                    Log.Error(ex, "Failed to initialise hardware monitor. Sensors may be unavailable.");
                }
                reload_available_sensors();
            }

            public List<VU1_Sensor> get_available_sensors() => gSensorsAvailable;

            public List<VU1_Sensor> get_used_sensors() => gSensorsAvailable;

            public void reload_available_sensors()
            {
                gSensorsAvailable.Clear();   // avoid duplicates when called again

                try
                {
                    foreach (IHardware hardware in computer.Hardware)
                    {
                        AddSensors(hardware);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to enumerate sensors");
                }

                Log.Information("Found {Count} usable sensors", gSensorsAvailable.Count);
            }

            /// <summary>Adds sensors of the hardware and all of its sub-hardware (e.g. SuperIO chips on motherboards).</summary>
            private void AddSensors(IHardware hardware)
            {
                foreach (ISensor sensor in hardware.Sensors)
                {
                    // Filter and use only sensors we are interested in
                    if (UsedSensors.Contains(sensor.SensorType.ToString()))
                    {
                        gSensorsAvailable.Add(new VU1_Sensor(sensor));
                    }
                }

                foreach (IHardware sub in hardware.SubHardware)
                {
                    AddSensors(sub);
                }
            }

            public VU1_Sensor? FindSensorByIdentifier(string identifier)
            {
                if (string.IsNullOrEmpty(identifier)) return null;
                return gSensorsAvailable.Find(item => item.Sensor.Identifier.ToString() == identifier);
            }

            /// <summary>
            /// Refresh the readings of the given sensors. Each piece of hardware is updated only once,
            /// even when several dials use sensors from the same hardware.
            /// </summary>
            public void UpdateSensors(IEnumerable<ISensor> sensors)
            {
                if (!_isOpen) return;

                lock (_updateLock)
                {
                    foreach (IHardware hw in sensors.Where(s => s != null).Select(s => s.Hardware).Distinct())
                    {
                        try
                        {
                            hw.Update();
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to update hardware {Hardware}", hw.Identifier);
                        }
                    }
                }
            }

            public void Dispose()
            {
                if (!_isOpen) return;
                _isOpen = false;
                try
                {
                    lock (_updateLock)
                    {
                        computer.Close();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error closing hardware monitor");
                }
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
