using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Policy;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System.DirectoryServices;
using HidSharp.Utility;
using Newtonsoft.Json;
using Mono.Unix.Native;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;
using System.Collections.Specialized;
using System.IO;

namespace KR_VU1_Server
{
    public class Backlight
    {
        public int red { get; set; }
        public int green { get; set; }
        public int blue { get; set; }
    }

    public class DialInfo
    {
        public string uid { get; set; }
        public string dial_name { get; set; }
        public int value { get; set; }
        public Backlight backlight { get; set; }
        public object image_file { get; set; }
    }

    public class VU1JSONDialListResponse
    {
        public string? status { get; set; }
        public List<DialInfo>? data { get; set; }
    }

    public class VU1JSONStandardResponse
    {
        public string? status { get; set; }
    }

    internal class VU1_Server
    {
        private readonly String _server_ip = "localhost";
        private readonly int _server_port = 5340;
        private readonly String _api_key = "";
        private List<DialInfo> gDialInfo = new List<DialInfo>();
        RestClient DialAPIClient;
        private static readonly HttpClient client = new HttpClient();

        public VU1_Server(String API_Key) :
            this("localhost", 5340, API_Key)
        { }

        public VU1_Server(String ServerIP = "localhost", int ServerPort = 5340, string API_Key = "")
        {
            _server_ip = ServerIP;
            _server_port = ServerPort;
            _api_key = API_Key;

            DialAPIClient = new RestClient(get_api_url());
        }

        public String get_api_url()
        {
            return String.Format("http://{0}:{1}/api/v0", _server_ip, _server_port);
        }

        public String get_api_key()
        {
            return _api_key;
        }


        public bool RefreshDialList()
        {
            try
            {
                var request = new RestRequest(String.Format("dial/list?key={0}", _api_key));
                //var response = await DialAPIClient.GetAsync(request);
                var httpRes = DialAPIClient.Execute<VU1JSONDialListResponse>(request);

                if (httpRes.StatusCode == HttpStatusCode.OK)
                {
                    if (httpRes.Content == null) return false;

                    VU1JSONDialListResponse dialData = JsonConvert.DeserializeObject<VU1JSONDialListResponse>(httpRes.Content);

                    gDialInfo.Clear();
                    gDialInfo = dialData.data;

                    return true;

                }
                else
                {
                    Trace.WriteLine($"HTTP Code: {httpRes.StatusCode}");
                    return false;
                }


            }
            catch (Exception e)
            {
                Trace.WriteLine(e.ToString());
                return false;
            }
        }


        public List<DialInfo> GetDialList()
        {
            return gDialInfo;
        }


        public bool UpdateDialName(string uid, string dialName)
        {
            if (dialName == null) return false;
            if (uid == null) return false;

            var request = new RestRequest(String.Format("dial/{0}/name?name={1}&key={2}", uid, dialName, _api_key));
            var httpRes = DialAPIClient.Execute<VU1JSONStandardResponse>(request);
            if (httpRes.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }

            return false;
        }


        public bool UpdateDialValue(string uid, int val)
        {
            if (uid == null || uid == "") return false;

            // Check val
            val = Math.Clamp(val, 0, 100);

            var request = new RestRequest(String.Format("dial/{0}/set?value={1}&key={2}", uid, val, _api_key));
            var httpRes = DialAPIClient.Execute<VU1JSONStandardResponse>(request);
            if (httpRes.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }

            return false;
        }


        public bool UpdateDialBacklight(string uid, int red, int green, int blue, bool values_as_percent=false)
        {
            if (uid == null || uid == "") return false;

            if(values_as_percent)
            {
                red = Math.Clamp(red, 0, 100);
                green = Math.Clamp(green, 0, 100);
                blue = Math.Clamp(blue, 0, 100);
            }
            else
            {
                red = Math.Clamp(red, 0, 255);
                green = Math.Clamp(green, 0, 255);
                blue = Math.Clamp(blue, 0, 255);

                red = (int)Math.Ceiling((decimal)(red * 100 / 255));
                green = (int)Math.Ceiling((decimal)(green * 100 / 255));
                blue = (int)Math.Ceiling((decimal)(blue * 100 / 255));
            }


            var request = new RestRequest(String.Format("dial/{0}/backlight?red={1}&green={2}&blue={3}&white=0&key={4}", uid, red, green, blue, _api_key));
            var httpRes = DialAPIClient.Execute<VU1JSONStandardResponse>(request);
            if (httpRes.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }

            return false;
        }


        public bool UpdateDialBackgroundImage(String uid, string filepath)
        {
            try
            {
                NameValueCollection values = new NameValueCollection();
                NameValueCollection files = new NameValueCollection();
                values.Add("key", get_api_key());
                files.Add("imgfile", filepath);
                sendHttpRequest(String.Format("{0}/dial/{1}/image/set", get_api_url(), uid), values, files);
                
                return true;
            }
            catch (Exception err)
            {
                Trace.WriteLine(err.Message);
                return false;
            }
        }


        private static string sendHttpRequest(string url, NameValueCollection values, NameValueCollection files = null)
        {
            string boundary = "----------------------------" + DateTime.Now.Ticks.ToString("x");
            // The first boundary
            byte[] boundaryBytes = System.Text.Encoding.UTF8.GetBytes("\r\n--" + boundary + "\r\n");
            // The last boundary
            byte[] trailer = System.Text.Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            // The first time it itereates, we need to make sure it doesn't put too many new paragraphs down or it completely messes up poor webbrick
            byte[] boundaryBytesF = System.Text.Encoding.ASCII.GetBytes("--" + boundary + "\r\n");

            // Create the request and set parameters
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.Method = "POST";
            request.KeepAlive = true;
            request.Credentials = System.Net.CredentialCache.DefaultCredentials;

            // Get request stream
            Stream requestStream = request.GetRequestStream();

            foreach (string key in values.Keys)
            {
                // Write item to stream
                byte[] formItemBytes = System.Text.Encoding.UTF8.GetBytes(string.Format("Content-Disposition: form-data; name=\"{0}\";\r\n\r\n{1}", key, values[key]));
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                requestStream.Write(formItemBytes, 0, formItemBytes.Length);
            }

            if (files != null)
            {
                foreach (string key in files.Keys)
                {
                    if (File.Exists(files[key]))
                    {
                        int bytesRead = 0;
                        byte[] buffer = new byte[2048];
                        byte[] formItemBytes = System.Text.Encoding.UTF8.GetBytes(string.Format("Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"\r\nContent-Type: application/octet-stream\r\n\r\n", key, files[key]));
                        requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                        requestStream.Write(formItemBytes, 0, formItemBytes.Length);

                        using (FileStream fileStream = new FileStream(files[key], FileMode.Open, FileAccess.Read))
                        {
                            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                // Write file content to stream, byte by byte
                                requestStream.Write(buffer, 0, bytesRead);
                            }

                            fileStream.Close();
                        }
                    }
                }
            }

            // Write trailer and close stream
            requestStream.Write(trailer, 0, trailer.Length);
            requestStream.Close();

            using (StreamReader reader = new StreamReader(request.GetResponse().GetResponseStream()))
            {
                return reader.ReadToEnd();
            };
        }

    }


}

