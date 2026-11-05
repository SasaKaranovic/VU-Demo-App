using System;
using System.IO;
using System.Reflection;


namespace VU1WPF
{
    public class LogWriter
    {
        private string m_exePath = string.Empty;
        private string module_name = string.Empty;

        public LogWriter(string module)
        {
            module_name = module;
        }
        public void LogWrite(string logMessage)
        {
            m_exePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\KaranovicResearch\VU1-DemoApp";
            try
            {
                using (StreamWriter w = File.AppendText(m_exePath + "\\" + "log.txt"))
                {
                    Log(logMessage, w);
                }
            }
            catch (Exception ex)
            {
            }
        }

        public void Log(string logMessage, TextWriter txtWriter)
        {
            try
            {
                txtWriter.WriteLine("{0} [{1}]: {2}", DateTime.Now.ToString("yyyy-mm-dd HH:mm:ss"), module_name, logMessage);
            }
            catch (Exception ex)
            {
            }
        }
    }
}
