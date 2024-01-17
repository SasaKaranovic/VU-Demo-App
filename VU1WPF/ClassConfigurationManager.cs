using HidSharp.Reports;
using Microsoft.VisualBasic.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VU1WPF;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static KR_VU1_Sensors.ClassVUSensors;

namespace KR_VU1_ConfigurationManager
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
        public String masterKey { get; set; } = "cTpAWYuRpA2zx75Yh961Cg";
        public float dialUpdatePeriod { get; set; } = 0.5F;

        public List<ConfigContentsDial> dial_metrics { get; set; }

        public ConfigContentsRoot()
        {
            dial_metrics = new List<ConfigContentsDial>();
        }
    }

    public class ClassConfigurationManager
    {
        private String pathConfigFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\KaranovicResearch\VU1-DemoApp\";
        private String pathFileName = "vu1demo_config.yaml";
        private ConfigContentsRoot localConfig;

        public ClassConfigurationManager() 
        {
            localConfig = new ConfigContentsRoot();

            InitializeConfigFile();
            LoadConfigFile();

            Trace.WriteLine(String.Format("Dial update period: {0}", localConfig.dialUpdatePeriod));

            // Check update period
            if (localConfig.dialUpdatePeriod == 0 || localConfig.dialUpdatePeriod < 0.2F)
            {
                localConfig.dialUpdatePeriod = 0.2F;
            }

            SaveConfigFile();   // Save default if no config file exists
        }

        private void InitializeConfigFile()
        {
            Trace.WriteLine(pathConfigFile);          
            System.IO.Directory.CreateDirectory(pathConfigFile);

            string path = pathConfigFile + pathFileName;
            using (StreamWriter sw = File.AppendText(path))
            {
            }
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
                using (StreamReader streamReader = new StreamReader(path, Encoding.UTF8))
                {
                    fileContents = streamReader.ReadToEnd();
                }

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                Trace.WriteLine(fileContents);
                var p = deserializer.Deserialize<ConfigContentsRoot>(fileContents);

                if (p == null)
                {
                    return;
                }

                localConfig.dialUpdatePeriod = p.dialUpdatePeriod;
                localConfig.masterKey = p.masterKey;

                foreach (ConfigContentsDial dial in p.dial_metrics)
                {
                    Trace.WriteLine($"{dial.dial_uid} uses {dial.sensor_identifier}.");
                    localConfig.dial_metrics.Add(dial);
                }

            }
            catch (Exception e)
            {
                Trace.WriteLine(e, "YAML read process failed.");
                return;
            }

        }


        public void SaveConfigFile()
        {
            string path = pathConfigFile + pathFileName;
/*
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(localConfig);
*/
            // Trace.WriteLine(yaml);

            using (StreamWriter streamWriter = new StreamWriter(path))
{
                Serializer serializer = (Serializer)new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
                serializer.Serialize(streamWriter, localConfig);
            }


            /*
                        using (var fs = new FileStream(path, FileMode.OpenOrCreate))
                        using (var sw = new StreamWriter(fs))
                        {
                            sw.WriteLine(yaml);
                        }*/

        }
    }
}
