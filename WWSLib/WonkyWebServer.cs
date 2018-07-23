using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;

using WWS.Internals;


namespace WWS
{
    public delegate WWSResponse WWSRequestHandler(WWSRequest data);


    public sealed class WonkyWebServer
    {
        private WWSConfiguration _config;
        private HTTPServer _server;


        public WWSConfiguration Configuration
        {
            set
            {
                if (IsRunning)
                    throw new InvalidOperationException("The configuration cannot be set while the web server is running. Please stop the server before changing its configuration.");

                if (value is WWSConfiguration c)
                    _config = c;
            }
            get => _config;
        }

        public bool IsRunning { private set; get; }


        public event WWSRequestHandler OnIncomingRequest;


        public WonkyWebServer(WWSConfiguration config) => Configuration = config;

        public void Start()
        {
            lock (this)
            {
                if (IsRunning)
                    throw new InvalidOperationException("The configuration cannot be set while the web server is running. Please stop the server before changing its configuration.");

                VerifyConfig();

                _server = new HTTPServer(Configuration.ListeningPort, Configuration.ListeningPath, Configuration.HTTPS != null)
                {
                    Handler = OnIncoming
                };
                _server.Start();

                IsRunning = true;
            }
        }

        public void Stop()
        {
            lock (this)
            {
                _server?.Stop();
                _server = null;

                IsRunning = false;
            }
        }

        public bool HasValidConfiguration()
        {
            try
            {
                VerifyConfig();
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void VerifyConfig()
        {
            // TODO
        }

        private WWSResponse OnIncoming(HttpListenerRequest req, byte[] content, HttpListenerResponse res)
        {
            if (req is null || res is null || content is null)
                return null;

            DateTime utcnow = DateTime.UtcNow;
            string path = req.Url.LocalPath;

            var timestamp = new
            {
                UTCRaw = utcnow,
                UTCSinceUnix = new DateTimeOffset(utcnow).ToUnixTimeMilliseconds(),
            };

            res.Headers[HttpResponseHeader.Server] = Configuration.ServerString;
            res.Headers[HttpResponseHeader.CacheControl] = "max-cache=" + Configuration.CachingAge;
            res.Headers[HttpResponseHeader.Date] = $"{utcnow:ddd, dd MMM yyyy HH:mm:ss} UTC";
            res.Headers[HttpResponseHeader.Allow] = "GET, HEAD, POST";

            if (Configuration.UseConnectionUpgrade)
            {
                res.Headers[HttpResponseHeader.Connection] = "Upgrade";
                res.Headers[HttpResponseHeader.Upgrade] = "h2c";
            }
            else
                res.Headers[HttpResponseHeader.Connection] = "keep-alive";

            Dictionary<string, (string Value, DateTime Expiration)> cookies = req.Cookies.Cast<Cookie>().ToDictionary(c => c.Name, c => (c.Value, c.Expires));
            Dictionary<string, string> servervars = new Dictionary<string, string>
            {
                ["HTTP_CONNECTION"] = res.Headers[HttpResponseHeader.Connection],
                ["HTTP_CACHE_CONTROL"] = res.Headers[HttpResponseHeader.CacheControl],
                ["REQUEST_METHOD"] = req.HttpMethod,
                ["REQUEST_URI"] = req.RawUrl,
                ["REQUEST_TIME"] = timestamp.UTCSinceUnix.ToString(),
                ["REQUEST_SCHEME"] = req.Url.Scheme,
                ["REQUEST_PATH"] = req.Url.AbsolutePath,
                ["QUERY_STRING"] = req.Url.Query,
                ["SERVER_PROTOCOL"] = "HTTP/" + req.ProtocolVersion,
                ["DATE_UTC"] = utcnow.ToString("yyyy-MM-dd"),
                ["TIME_UTC"] = utcnow.ToString("HH:mm:ss"),
                ["PATH"] = Environment.ExpandEnvironmentVariables("%path%"),
                ["PATHEXT"] = Environment.ExpandEnvironmentVariables("%pathext%"),
                ["WINDIR"] = Environment.ExpandEnvironmentVariables("%windir%"),
                ["SERVER_SOFTWARE"] = $"<address>WWS/4.0 (Win{(Environment.Is64BitOperatingSystem ? 64 : 32)}) WonkySSL/4.0",
                ["SERVER_PORT"] = req.LocalEndPoint.Port.ToString(),
                ["SERVER_ADDR"] = req.LocalEndPoint.Address.ToString(),
                ["REMOTE_PORT"] = req.RemoteEndPoint.Port.ToString(),
                ["REMOTE_ADDR"] = req.RemoteEndPoint.Address.ToString(),
            };

            servervars["SCRIPT_NAME"] = servervars["REQUEST_PATH"];
            servervars["HTTP_HOST"] =
            servervars["SERVER_NAME"] = servervars["SERVER_ADDR"].Contains(':') ? $"[{servervars["SERVER_ADDR"]}]" : servervars["SERVER_ADDR"];
            servervars["SERVER_SIGNATURE"] = $"<address>{servervars["SERVER_SOFTWARE"]} Server at {req.LocalEndPoint.Address} Port {req.LocalEndPoint.Port}</address>";
            
            WWSResponse rdat = (HttpStatusCode.ServiceUnavailable, "<h1>service unavailable</h1>");

            if (OnIncomingRequest != null)
                try
                {
                    rdat = OnIncomingRequest(new WWSRequest
                    {
                        Cookies = cookies,
                        ServerVariables = new ReadOnlyDictionary<string, string>(servervars),
                    });
                }
                catch (Exception ex)
                {
                    StringBuilder sb = new StringBuilder();
                    Exception e = ex ?? new Exception("An unexpected internal error occured.");

                    while (ex != null)
                    {
                        sb.Insert(0, $"[{ex.GetType().FullName}] {ex.Message}:\n{ex.StackTrace}\n");

                        ex = ex.InnerException;
                    }

                    rdat = (HttpStatusCode.InternalServerError, $@"
<!doctype html>
<html lang=""en"">
    <head>
        <title>{e.GetType().FullName}</title>
        <style>
            body {{
                font-family: sans-serif;
                white-space: pre;
            }}
        </style>
    </head>
    <body>
        <h1>An exception of the type <code>{e.GetType().FullName}</code> was thrown:</h1>
<code style=""font-size: 1.5em"">{HttpUtility.HtmlEncode(sb.ToString())}</code>
    </body>
</html>
");
                }

            res.StatusCode = (int)rdat.StatusCode;
            res.StatusDescription = rdat.StatusCode.ToString();
            res.Cookies = new CookieCollection();

            foreach (string key in cookies.Keys)
                res.Cookies.Add(new Cookie(key, cookies[key].Value, "/")
                {
                    Expires = cookies[key].Expiration
                });

            return rdat;
        }
    }

    /// <summary>
    /// Represents a simple HTTP/HTTPS request
    /// </summary>
    public sealed class WWSRequest
    {
        /// <summary>
        /// The server's variables (read-only)
        /// </summary>
        public IReadOnlyDictionary<string, string> ServerVariables { internal set; get; }
        /// <summary>
        /// The client's cookies (read/write)
        /// </summary>
        public Dictionary<string, (string Value, DateTime Expiration)> Cookies { internal set; get; }


        internal WWSRequest()
        {
        }
    }

    /// <summary>
    /// Represents a simple HTTP/HTTPS response
    /// </summary>
    public sealed class WWSResponse
    {
        /// <summary>
        /// The HTTP/HTTPS response's codepage
        /// </summary>
        public static Encoding Codepage { get; } = Encoding.UTF8; // Unicode or UTF8 or GetEncoding(1252) ?

        /// <summary>
        /// The response bytes
        /// </summary>
        public byte[] Bytes { get; }
        /// <summary>
        /// The response's length (in bytes)
        /// </summary>
        public int Length => Bytes?.Length ?? 0;
        /// <summary>
        /// The response's textual representation
        /// </summary>
        public string Text => Codepage.GetString(Bytes);

        public HttpStatusCode StatusCode { get; }


        /// <summary>
        /// Creates a new HTTP/HTTPS response from the given UTF-16 string (converted to UTF-8)
        /// </summary>
        /// <param name="text">UTF-16 string</param>
        public WWSResponse(HttpStatusCode code, string text)
            : this(code, Codepage.GetBytes(text ?? ""))
        {
        }

        /// <summary>
        /// Creates a new HTTP/HTTPS response from the given byte array
        /// </summary>
        /// <param name="bytes">Byte array</param>
        public WWSResponse(HttpStatusCode code, byte[] bytes)
        {
            StatusCode = code;
            Bytes = bytes ?? new byte[0];
        }

        /// <summary>
        /// Converts the given byte array and status code to an HTTP/HTTPS response
        /// </summary>
        /// <param name="t">Data</param>
        public static implicit operator WWSResponse((HttpStatusCode, byte[]) t) => new WWSResponse(t.Item1, t.Item2);
        /// <summary>
        /// Converts the given UTF-16 string and status code to an UTF-8 encoded HTTP/HTTPS response
        /// </summary>
        /// <param name="t">Data</param>
        public static implicit operator WWSResponse((HttpStatusCode, string) t) => new WWSResponse(t.Item1, t.Item2);
        /// <summary>
        /// Converts the given byte array to an HTTP/HTTPS response
        /// </summary>
        /// <param name="bytes">Byte array</param>
        public static explicit operator WWSResponse(byte[] bytes) => (HttpStatusCode.OK, bytes);
        /// <summary>
        /// Converts the given UTF-16 string to an UTF-8 encoded HTTP/HTTPS response
        /// </summary>
        /// <param name="text">UTF-16 strings</param>
        public static explicit operator WWSResponse(string text) => (HttpStatusCode.OK, text);
    }

    public struct WWSConfiguration
    {
        public WWSHTTPSConfiguration? HTTPS { set; get; }
        public bool UseConnectionUpgrade { set; get; }
        public ushort CachingAge { set; get; }
        public string ServerString { set; get; }
        public string ListeningPath { set; get; }
        public ushort ListeningPort { set; get; }


        public static WWSConfiguration DefaultHTTPConfiguration { get; } = new WWSConfiguration
        {
            ListeningPath = "",
            ListeningPort = 8080,
            ServerString = "WonkyWebStack™ (WWS4.0) Server",
            HTTPS = null
        };
        public static WWSConfiguration DefaultHTTPSConfiguration { get; } = new WWSConfiguration
        {
            ListeningPath = "",
            ListeningPort = 4430,
            ServerString = "WonkyWebStack™ (WWS4.0) Server",
            HTTPS = WWSHTTPSConfiguration.DefaultConfiguration
        };
    }

    public struct WWSHTTPSConfiguration
    {
        public WWSHTTPSCertificatePolicy CertificatePolicy { set; get; }
        public X509Certificate Certificate { set; get; }
        public string CertificatePath
        {
            set => Certificate = value != null ? X509Certificate.CreateFromCertFile(value) : throw new ArgumentNullException(nameof(value));
        }


        public static WWSHTTPSConfiguration DefaultConfiguration { get; } = new WWSHTTPSConfiguration
        {
            CertificatePolicy = WWSHTTPSCertificatePolicy.KeepCertificate,
            Certificate = null,
        };
    }

    public enum WWSHTTPSCertificatePolicy
    {
        KeepCertificate,
        DisposeCertificate
    }
}
