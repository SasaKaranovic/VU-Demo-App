using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using VU1WPF;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;
using static KR_VU1_Sensors.ClassVUSensors;
using Serilog;


namespace KR_VU1_ConfigurationManager
{
    public class ClassConfigurationManager
    {
        private float default_update_period = 0.5f;
        private string default_master_key = "cTpAWYuRpA2zx75Yh961Cg";
        private string pathConfigFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\KaranovicResearch\VU1-DemoApp\";
        private string pathFileName = "vu1demo_config.yaml";
        private ConfigContentsRoot localConfig;


    public ClassConfigurationManager() 
        {
            Log.Information("The global logger has been configured");

            localConfig = new ConfigContentsRoot();

            LoadConfigFile();

            Log.Information(String.Format("Dial update period: {0}", localConfig.dialUpdatePeriod));

            // Check update period
            if (localConfig.dialUpdatePeriod == 0 || localConfig.dialUpdatePeriod < 0.2F)
            {
                Log.Error("Dial update period set to `{0}`. Limiting to 0.2 seconds.", localConfig.dialUpdatePeriod);
                localConfig.dialUpdatePeriod = 0.2F;
            }

            if (localConfig.masterKey == null || localConfig.masterKey == String.Empty) 
            {
                localConfig.masterKey = default_master_key;
            }

            SaveConfigFile();   // Save default if no config file exists
        }

        private void Initialize_EmptyConfigFile()
        {
            string path = pathConfigFile + pathFileName;
            Log.Information("Initializing empty config file at: {0}", path);

            // Set default values
            localConfig.dialUpdatePeriod = default_update_period;
            localConfig.masterKey = default_master_key;

            Log.Information("Set update period to {0}.", localConfig.dialUpdatePeriod);
            Log.Information("Set Master Key to {0}.", localConfig.masterKey);

            // Check if file exists
            if (!File.Exists(path))
            {
                System.IO.Directory.CreateDirectory(pathConfigFile);
                using (StreamWriter sw = File.AppendText(path))
                {
                }
            }

            // Save new config file
            SaveConfigFile();
        }

        public float GetDialUpdatePeriod()
        {
            return localConfig.dialUpdatePeriod;
        }

        public String GetMasterKey()
        {
            return localConfig.masterKey;
        }

        public bool UpdateDialConfig(ClassDialGUI sensor, bool saveAfter)
        {
            var dial = localConfig.dial_metrics.Find(item => item.dial_uid == sensor.UID);
            if (dial != null)
            {
                dial.dial_uid = sensor.UID;
                dial.scaling_min = sensor.ScaleMin;
                dial.scaling_max = sensor.ScaleMax;
                dial.thresholds = DialGUI_to_ConfigThresholds(sensor.Thresholds);

                // If sensor is defined
                if(sensor.Sensor != null)
                {
                    dial.sensor_identifier = sensor.Sensor.Identifier.ToString();
                }
                else
                {
                    dial.sensor_identifier = "";
                }
                 
            }
            else
            {
                ConfigContentsDial tmp = new ConfigContentsDial { dial_uid = sensor.UID, sensor_identifier = sensor.Metric.ToString(), thresholds = DialGUI_to_ConfigThresholds(sensor.Thresholds) };
                localConfig.dial_metrics.Add(tmp);
            }

            if (saveAfter)
            {
                SaveConfigFile();
            }

            return true;
        }


        private List<ConfigThreshold> DialGUI_to_ConfigThresholds(List<ClassDialThreshold>? dialThresholds)
        {
            List<ConfigThreshold> ret = new();
        
            if (dialThresholds == null)
            {
                return ret;
            }

            foreach (ClassDialThreshold item in dialThresholds)
            {
                ConfigThreshold tmp = new ConfigThreshold
                {
                    value = item.Threshold,
                    red = item.BacklightRed,
                    green = item.BacklightGreen,
                    blue = item.BacklightBlue
                };
                ret.Add(tmp);
            }

            return ret;
        }

        public List<ClassDialThreshold> GetDialThresholds(String uid)
        {
            List<ClassDialThreshold> ret = new();

            ConfigContentsDial sid = localConfig.dial_metrics.Find(item => item.dial_uid == uid);

            if (sid == null)
            {
                return ret;
            }
            else if (sid.thresholds == null)
            {
                return ret;
            }
            else if (sid.thresholds.Count <= 0)
            {
                return ret;
            }

            foreach (ConfigThreshold item in sid.thresholds)
            {

                ClassDialThreshold tmp = new ClassDialThreshold
                {
                    Threshold = item.value,
                    BacklightRed = item.red,
                    BacklightGreen = item.green,
                    BacklightBlue = item.blue
                };
                ret.Add(tmp);
            }

            return ret;
        }

        public string GetDialMetric(String uid)
        {
            var sid = localConfig.dial_metrics.Find(item => item.dial_uid == uid);

            if (sid != null)
            {
                return sid.sensor_identifier.ToString();
            }
            else
            {
                return "";
            }
        }

        public float GetDialMin(String uid)
        {
            var sid = localConfig.dial_metrics.Find(item => item.dial_uid == uid);

            if (sid != null)
            {
                float fValue = 100;
                float.TryParse(sid.scaling_min.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out fValue);
                return fValue;
            }

            return 100;
        }


        public float GetDialMax(String uid)
        {
            var sid = localConfig.dial_metrics.Find(item => item.dial_uid == uid);

            if (sid != null)
            {
                float fValue = 100;
                float.TryParse(sid.scaling_max.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out fValue);
                return fValue;
            }
            
            return 100;
        }


        private void LoadConfigFile()
        {
            try
            {
                string path = pathConfigFile + pathFileName;
                string fileContents;

                if (!File.Exists(path))
                {
                    Log.Debug("App config does not existing. Initalizing default one.");
                    Initialize_EmptyConfigFile();
                }
                else
                {
                    Log.Debug("Config exists. Reusing.");
                }

                using (StreamReader streamReader = new StreamReader(path, Encoding.UTF8))
                {
                    fileContents = streamReader.ReadToEnd();
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                // TODO: Demote this to Verbose after verifying config loading works properly
                Log.Debug("---- Config contents: ---");
                Log.Debug(fileContents.ToString());
                Log.Debug("---- END debug info ---");

                var p = deserializer.Deserialize<ConfigContentsRoot>(fileContents);

                if (p == null)
                {
                    Log.Error("Config content is null. Aborting load.");
                    Log.Error("---- Additional debug info ---");
                    Log.Error("Config contents:");
                    Log.Error(fileContents.ToString());
                    Log.Error("---- END debug info ---");

                    localConfig.dialUpdatePeriod = default_update_period;
                    localConfig.masterKey = default_master_key;
                    return;
                }

                localConfig.dialUpdatePeriod = p.dialUpdatePeriod;
                localConfig.masterKey = p.masterKey;

                foreach (ConfigContentsDial dial in p.dial_metrics)
                {
                    Log.Debug($"{dial.dial_uid} uses {dial.sensor_identifier}.");
                    localConfig.dial_metrics.Add(dial);
                }

            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), "YAML read process failed.", MessageBoxButton.OK, MessageBoxImage.Warning);
                Log.Error("Encountered exception while loading config.");
                Log.Error(e.ToString());
                
                // Let's close the app now
                System.Windows.Forms.Application.Exit();
            }

        }


        public void SaveConfigFile()
        {
            string path = pathConfigFile + pathFileName;

            try
            {
                using (StreamWriter streamWriter = new StreamWriter(path))
                {
                    Serializer serializer = (Serializer)new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
                    serializer.Serialize(streamWriter, localConfig);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save config file");
                throw;
            }


        }
    }
}
