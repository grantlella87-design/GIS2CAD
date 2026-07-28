using Autodesk.AutoCAD.EditorInput;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NG.GIS.CAD.Exporter.Auth
{
    public sealed class ArcGisPortalToken
    {
        public string AccessToken { get; set; }
                public string RefreshToken { get; set; }
public string RawJson { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    public static class ArcGisPortalOAuth
    {
        private const string ClientId = "48XCGWtLoUxA3klq";
        private const string RedirectUri = "http://localhost:8080/";
        private const string PortalRoot = "https://gis.nationalgrid.com/portal";
        private static readonly string TokenCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NGGisCadExporter", "arcgis_portal_token.json");
        public static string CurrentAccessToken { get; private set; } = string.Empty;

        public static ArcGisPortalToken LoginAndGetToken(Editor editor = null)
        {
            DeleteExpiredOrBadCache(editor);
            ArcGisPortalToken cached = TryReadCachedToken();
            if (cached != null && !string.IsNullOrWhiteSpace(cached.AccessToken) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            {
                CurrentAccessToken = cached.AccessToken;
                editor?.WriteMessage("\nArcGIS Portal token loaded from cache.");
                return cached;
            }
            string code = CaptureAuthorizationCode(editor);
            ArcGisPortalToken token = ExchangeCodeForToken(code);
            WriteTokenCache(token);
            CurrentAccessToken = token.AccessToken;
            editor?.WriteMessage("\nArcGIS Portal token captured and cached.");
            return token;
        }

        public static void ClearTokenCache()
        {
            if (File.Exists(TokenCachePath)) File.Delete(TokenCachePath);
        }

        private static string CaptureAuthorizationCode(Editor editor)
        {
            string callbackTitle = "NG ArcGIS OAuth Complete " + Guid.NewGuid().ToString("N");
            using (HttpListener listener = new HttpListener())
            {
                listener.Prefixes.Add(RedirectUri);
                listener.Start();
                string authorizeUrl = PortalRoot + "/sharing/rest/oauth2/authorize" +
                    "?client_id=" + Uri.EscapeDataString(ClientId) +
                    "&response_type=code" +
                    "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                    "&expiration=20160";
                editor?.WriteMessage("\nOpening ArcGIS Portal OAuth sign-in...");
                Process.Start(new ProcessStartInfo { FileName = authorizeUrl, UseShellExecute = true });
                HttpListenerContext context = listener.GetContext();
                string code = context.Request.QueryString["code"];
                string oauthError = context.Request.QueryString["error"];
                string oauthErrorDescription = context.Request.QueryString["error_description"];
                WriteCallbackResponse(context, callbackTitle);
                CloseCallbackTab(callbackTitle);
                listener.Stop();
                if (!string.IsNullOrWhiteSpace(oauthError)) throw new InvalidOperationException("ArcGIS OAuth authorize failed: " + oauthError + " " + oauthErrorDescription);
                if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("ArcGIS OAuth callback did not include a code.");
                return code;
            }
        }

        private static void WriteCallbackResponse(HttpListenerContext context, string callbackTitle)
        {
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + HtmlEncode(callbackTitle) + "</title><script>(function(){try{window.open('', '_self');}catch(e){}try{window.close();}catch(e){}})();</script></head><body></body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }

        private static void CloseCallbackTab(string callbackTitle)
        {
            Thread.Sleep(500);
            object shell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                shell = Activator.CreateInstance(shellType);
                bool activated = false;
                for (int i = 0; i < 10; i++)
                {
                    activated = Convert.ToBoolean(shellType.InvokeMember("AppActivate", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { callbackTitle }));
                    if (activated) break;
                    Thread.Sleep(200);
                }
                if (activated)
                {
                    Thread.Sleep(150);
                    shellType.InvokeMember("SendKeys", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { "^w" });
                }
            }
            catch { }
            finally
            {
                if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        }

        private static ArcGisPortalToken ExchangeCodeForToken(string code)
        {
            string tokenUrl = PortalRoot + "/sharing/rest/oauth2/token";
            string body = "client_id=" + Uri.EscapeDataString(ClientId) +
                "&grant_type=authorization_code" +
                "&code=" + Uri.EscapeDataString(code) +
                "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                "&f=json";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(tokenUrl);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = bodyBytes.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(bodyBytes, 0, bodyBytes.Length);
            string json;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream())) json = reader.ReadToEnd();
            if (json.Contains("\"error\"")) throw new InvalidOperationException("ArcGIS token exchange failed: " + json);
            string accessToken = ExtractJsonString(json, "access_token");
            string refreshToken = ExtractJsonString(json, "refresh_token");
            int expiresInSeconds = ExtractJsonInt(json, "expires_in", 1800);
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("ArcGIS token exchange returned no access_token: " + json);
            return new ArcGisPortalToken
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                RawJson = json,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds - 60))
            };
        }

        private static ArcGisPortalToken RefreshAccessToken(ArcGisPortalToken cached)
        {
            if (cached == null || string.IsNullOrWhiteSpace(cached.RefreshToken))
            {
                throw new InvalidOperationException("No ArcGIS refresh_token is available in the token cache.");
            }

            string tokenUrl = PortalRoot + "/sharing/rest/oauth2/token";
            string body = "client_id=" + Uri.EscapeDataString(ClientId) +
                "&grant_type=refresh_token" +
                "&refresh_token=" + Uri.EscapeDataString(cached.RefreshToken) +
                "&f=json";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(tokenUrl);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = bodyBytes.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(bodyBytes, 0, bodyBytes.Length);
            string json;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream())) json = reader.ReadToEnd();
            if (json.Contains("\"error\"")) throw new InvalidOperationException("ArcGIS refresh_token failed: " + json);
            string accessToken = ExtractJsonString(json, "access_token");
            string refreshToken = ExtractJsonString(json, "refresh_token");
            int expiresInSeconds = ExtractJsonInt(json, "expires_in", 1800);
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("ArcGIS refresh_token returned no access_token: " + json);
            if (string.IsNullOrWhiteSpace(refreshToken)) refreshToken = cached.RefreshToken;
            return new ArcGisPortalToken
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                RawJson = json,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds - 60))
            };
        }
        private static void WriteTokenCache(ArcGisPortalToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenCachePath));
            string json = "{\"access_token\":" + JsonString(token.AccessToken) +
                ",\"refresh_token\":" + JsonString(token.RefreshToken ?? string.Empty) +
                ",\"expires_utc\":" + JsonString(token.ExpiresUtc.ToString("o")) +
                ",\"raw_json\":" + JsonString(token.RawJson) + "}";
            File.WriteAllText(TokenCachePath, json, Encoding.UTF8);
        }

        private static ArcGisPortalToken TryReadCachedToken()
        {
            try
            {
                if (!File.Exists(TokenCachePath)) return null;
                string json = File.ReadAllText(TokenCachePath, Encoding.UTF8);
                string accessToken = ExtractJsonString(json, "access_token");
                string refreshToken = ExtractJsonString(json, "refresh_token");
                string expiresUtcText = ExtractJsonString(json, "expires_utc");
                string rawJson = ExtractJsonString(json, "raw_json");
                if (string.IsNullOrWhiteSpace(refreshToken) && !string.IsNullOrWhiteSpace(rawJson))
                {
                    refreshToken = ExtractJsonString(rawJson, "refresh_token");
                }
                DateTime expiresUtc;
                if (string.IsNullOrWhiteSpace(accessToken) || !DateTime.TryParse(expiresUtcText, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out expiresUtc)) return null;
                return new ArcGisPortalToken
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    RawJson = rawJson,
                    ExpiresUtc = expiresUtc.ToUniversalTime()
                };
            }
            catch { return null; }
        }

        private static void DeleteExpiredOrBadCache(Editor editor)
        {
            ArcGisPortalToken cached = TryReadCachedToken();
            if (cached == null && File.Exists(TokenCachePath)) { try { File.Delete(TokenCachePath); } catch { } }
                        if (cached != null && cached.ExpiresUtc <= DateTime.UtcNow.AddMinutes(5))
            {
                if (!string.IsNullOrWhiteSpace(cached.RefreshToken))
                {
                    try
                    {
                        ArcGisPortalToken refreshed = RefreshAccessToken(cached);
                        WriteTokenCache(refreshed);
                        CurrentAccessToken = refreshed.AccessToken;
                        editor?.WriteMessage("\nCached ArcGIS access token refreshed silently.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        editor?.WriteMessage("\nArcGIS refresh_token failed; starting OAuth sign-in. " + ex.Message);
                    }
                }
                try { File.Delete(TokenCachePath); } catch { }
                editor?.WriteMessage("\nCached ArcGIS token expired and no usable refresh_token was available. Starting OAuth sign-in.");
            }
        }

        private static string ExtractJsonString(string json, string propertyName)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
            return match.Success ? Regex.Unescape(match.Groups["v"].Value) : null;
        }

        private static int ExtractJsonInt(string json, string propertyName, int fallback)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*(?<v>\\d+)", RegexOptions.IgnoreCase);
            int value;
            return match.Success && int.TryParse(match.Groups["v"].Value, out value) ? value : fallback;
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static string HtmlEncode(string value)
        {
            return (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}




