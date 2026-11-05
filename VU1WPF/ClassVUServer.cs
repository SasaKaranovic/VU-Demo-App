using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace KR_VU1_Server
{
    // Numbers are double so both "39" and "39.0" from the server deserialize
    public class Backlight
    {
        public double red { get; set; }
        public double green { get; set; }
        public double blue { get; set; }
    }

    public class DialInfo
    {
        public string uid { get; set; } = "";
        public string dial_name { get; set; } = "";
        public double value { get; set; }
        public Backlight? backlight { get; set; }
        public object? image_file { get; set; }
    }

    public class VU1JSONDialListResponse
    {
        public string? status { get; set; }
        public List<DialInfo>? data { get; set; }
    }

    public class VU1JSONStandardResponse
    {
        public string? status { get; set; }
        public string? message { get; set; }
    }

    /// <summary>
    /// Thin, thread-safe async client for the VU1 server REST API.
    /// - Every call has a short timeout so an offline server never blocks the app.
    /// - All query values are URL-encoded.
    /// - Calls never throw for network/server errors; they return false/null and log instead.
    /// </summary>
    internal sealed class VU1_Server : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        private readonly string _baseUrl;
        private readonly string _api_key;
        private readonly HttpClient _http;
        private List<DialInfo> _dialInfo = new();

        // Track connectivity so we log state changes once instead of spamming the log every tick.
        private volatile bool _isOnline;
        private int _consecutiveFailures;

        /// <summary>Raised (on a thread-pool thread) when connectivity to the server changes.</summary>
        public event Action<bool>? ConnectionStateChanged;

        public bool IsOnline => _isOnline;
        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        public const string DefaultServerUrl = "http://localhost:5340";

        /// <param name="serverUrl">Base URL of the VU1 server, e.g. http://localhost:5340</param>
        public VU1_Server(string serverUrl, string API_Key, TimeSpan? requestTimeout = null)
        {
            _baseUrl = TryNormalizeServerUrl(serverUrl, out string normalized) ? normalized : DefaultServerUrl;
            _api_key = API_Key ?? "";

            _http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            {
                BaseAddress = new Uri(get_api_url() + "/"),
                Timeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            };
        }

        /// <summary>
        /// Validates and normalises a server URL entered by the user:
        /// adds "http://" when missing, removes trailing "/" and a trailing "/api/v0".
        /// </summary>
        public static bool TryNormalizeServerUrl(string? input, out string normalized)
        {
            normalized = DefaultServerUrl;
            string url = (input ?? "").Trim();
            if (url.Length == 0) return false;

            if (!url.Contains("://", StringComparison.Ordinal))
            {
                url = "http://" + url;
            }

            url = url.TrimEnd('/');
            if (url.EndsWith("/api/v0", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Substring(0, url.Length - "/api/v0".Length);
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(uri.Host))
            {
                return false;
            }

            normalized = url;
            return true;
        }

        public string ServerUrl => _baseUrl;

        public string get_api_url() => _baseUrl + "/api/v0";

        public string get_api_key() => _api_key;

        public List<DialInfo> GetDialList() => _dialInfo;

        // ------------------------------------------------------------------ dial list

        public async Task<bool> RefreshDialListAsync(CancellationToken ct = default)
        {
            var response = await GetJsonAsync<VU1JSONDialListResponse>($"dial/list?key={Enc(_api_key)}", ct).ConfigureAwait(false);
            if (response == null)
            {
                return false;
            }

            // Server may return `data: null` when no dials are attached - treat as empty list.
            _dialInfo = response.data ?? new List<DialInfo>();
            return true;
        }

        // ------------------------------------------------------------------ dial updates

        public Task<bool> UpdateDialNameAsync(string uid, string dialName, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid) || dialName == null) return Task.FromResult(false);
            return GetOkAsync($"dial/{Enc(uid)}/name?name={Enc(dialName)}&key={Enc(_api_key)}", ct);
        }

        public Task<bool> UpdateDialValueAsync(string uid, int val, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid)) return Task.FromResult(false);
            val = Math.Clamp(val, 0, 100);
            return GetOkAsync($"dial/{Enc(uid)}/set?value={val}&key={Enc(_api_key)}", ct);
        }

        /// <summary>
        /// Sets dial backlight.
        /// NOTE: when <paramref name="values_as_percent"/> is false (the default used throughout the app)
        /// the 0..255 input is scaled to 0..100%. The GUI sliders/presets are 0..100 but are passed through
        /// this scaling, which caps brightness at ~39%. That is the existing (tuned) behaviour and is kept as-is.
        /// </summary>
        public Task<bool> UpdateDialBacklightAsync(string uid, int red, int green, int blue, bool values_as_percent = false, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid)) return Task.FromResult(false);

            (red, green, blue) = ScaleBacklight(red, green, blue, values_as_percent);
            return GetOkAsync($"dial/{Enc(uid)}/backlight?red={red}&green={green}&blue={blue}&white=0&key={Enc(_api_key)}", ct);
        }

        public static (int r, int g, int b) ScaleBacklight(int red, int green, int blue, bool values_as_percent)
        {
            if (values_as_percent)
            {
                return (Math.Clamp(red, 0, 100), Math.Clamp(green, 0, 100), Math.Clamp(blue, 0, 100));
            }

            // Same integer math as the original implementation so dial colours do not change.
            static int Scale(int v) => Math.Clamp(v, 0, 255) * 100 / 255;
            return (Scale(red), Scale(green), Scale(blue));
        }

        /// <summary>Uploads an image file from disk as the dial background (sent as-is).</summary>
        public async Task<bool> UpdateDialBackgroundImageAsync(string uid, string filepath, CancellationToken ct = default)
        {
            try
            {
                byte[] bytes = await System.IO.File.ReadAllBytesAsync(filepath, ct).ConfigureAwait(false);
                return await UploadDialImageAsync(uid, bytes, System.IO.Path.GetFileName(filepath), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Could not read image file {File}", filepath);
                return false;
            }
        }

        /// <summary>Uploads an in-memory image (PNG recommended) as the dial background.</summary>
        public async Task<bool> UploadDialImageAsync(string uid, byte[] imageBytes, string fileName = "dial.png", CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid) || imageBytes == null || imageBytes.Length == 0) return false;

            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(_api_key), "key");

                var fileContent = new ByteArrayContent(imageBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(fileName));
                form.Add(fileContent, "imgfile", fileName);

                // Image upload can take a bit longer than a value update
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

                using var response = await _http.PostAsync($"dial/{Enc(uid)}/image/set?key={Enc(_api_key)}", form, cts.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                bool ok = response.IsSuccessStatusCode && !IsFailStatus(body);
                MarkResult(true);
                if (!ok)
                {
                    Log.Warning("Image upload for dial {Uid} failed: HTTP {Code} {Body}", uid, (int)response.StatusCode, Truncate(body));
                }
                else
                {
                    Log.Information("Uploaded image ({Bytes} bytes) to dial {Uid}", imageBytes.Length, uid);
                }
                return ok;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                MarkResult(false, ex);
                return false;
            }
        }

        /// <summary>
        /// Downloads the image currently shown on the dial (GET /dial/{uid}/image/get).
        /// Returns null if the server is unreachable or the dial has no image.
        /// </summary>
        public async Task<byte[]?> GetDialImageAsync(string uid, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            try
            {
                using var response = await _http.GetAsync($"dial/{Enc(uid)}/image/get?key={Enc(_api_key)}", ct).ConfigureAwait(false);
                MarkResult(true);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Debug("No image for dial {Uid}: HTTP {Code}", uid, (int)response.StatusCode);
                    return null;
                }

                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

                // The server answers with JSON when something went wrong
                if (bytes.Length == 0 || (mediaType != null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Debug("Dial {Uid} image request returned {Type} ({Bytes} bytes)", uid, mediaType, bytes.Length);
                    return null;
                }

                return bytes;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                MarkResult(false, ex);
                return null;
            }
        }

        // ------------------------------------------------------------------ helpers

        private async Task<bool> GetOkAsync(string relativeUrl, CancellationToken ct)
        {
            var response = await GetJsonAsync<VU1JSONStandardResponse>(relativeUrl, ct).ConfigureAwait(false);
            return response != null;
        }

        /// <summary>Performs GET, returns parsed body or null on any failure.</summary>
        private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken ct) where T : class
        {
            try
            {
                using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                MarkResult(true);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("VU1 server returned HTTP {Code} for {Url}: {Body}", (int)response.StatusCode, StripKey(relativeUrl), Truncate(body));
                    return null;
                }

                if (IsFailStatus(body))
                {
                    Log.Warning("VU1 server reported failure for {Url}: {Body}", StripKey(relativeUrl), Truncate(body));
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(body, JsonOptions);
                }
                catch (JsonException jex)
                {
                    Log.Warning(jex, "Unexpected response from VU1 server for {Url}: {Body}", StripKey(relativeUrl), Truncate(body));
                    return null;
                }
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                MarkResult(false, ex);
                return null;
            }
        }

        private static bool IsTransient(Exception ex, CancellationToken ct)
        {
            // Caller-requested cancellation should propagate
            if (ex is OperationCanceledException && ct.IsCancellationRequested) return false;
            return ex is HttpRequestException or TaskCanceledException or OperationCanceledException or System.IO.IOException;
        }

        private void MarkResult(bool reachedServer, Exception? ex = null)
        {
            if (reachedServer)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                if (!_isOnline)
                {
                    _isOnline = true;
                    Log.Information("VU1 server reachable at {Url}", get_api_url());
                    ConnectionStateChanged?.Invoke(true);
                }
            }
            else
            {
                int failures = Interlocked.Increment(ref _consecutiveFailures);
                if (_isOnline || failures == 1)
                {
                    _isOnline = false;
                    Log.Warning("VU1 server not reachable at {Url}: {Error}", get_api_url(), ex?.Message);
                    ConnectionStateChanged?.Invoke(false);
                }
            }
        }

        private static bool IsFailStatus(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.String)
                {
                    return string.Equals(status.GetString(), "fail", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (JsonException)
            {
                // Not JSON - nothing to inspect
            }
            return false;
        }

        private static string Enc(string value) => Uri.EscapeDataString(value ?? "");

        private static string StripKey(string url)
        {
            int idx = url.IndexOf("key=", StringComparison.Ordinal);
            return idx < 0 ? url : url.Substring(0, idx) + "key=***";
        }

        private static string Truncate(string s) => s.Length <= 300 ? s : s.Substring(0, 300) + "...";

        private static string GuessMime(string fileName)
        {
            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => "application/octet-stream",
            };
        }

        public void Dispose() => _http.Dispose();
    }
}
