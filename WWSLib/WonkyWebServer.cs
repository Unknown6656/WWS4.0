using System.Security.Cryptography.X509Certificates;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Security;
using System.Linq;
using System.Text;
using System.Web;
using System.Net;
using System.IO;
using System;

using WWS.Internals;


namespace WWS
{
    /// <summary>
    /// Represents a delegate for the processing of incoming WWS4.0 (HTTP/HTTPS) requests.
    /// </summary>
    /// <param name="data">The WWS4.0 request data</param>
    /// <returns>The WWS4.0 response data</returns>
    public delegate WWSResponse WWSRequestHandler(WWSRequest data);

    /// <summary>
    /// Represents a WonkyWebServer™ for event-based HTTP/HTTPS request processing.
    /// </summary>
    public sealed class WonkyWebServer
    {
        private WWSConfiguration _config;
        private HTTPServer _server;

        /// <summary>
        /// The server's configuration
        /// </summary>
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
        /// <summary>
        /// Inidicates, whether the server is running
        /// </summary>
        public bool IsRunning { private set; get; }

        /// <summary>
        /// The event is fired upon each incoming HTTP or HTTPS request.
        /// </summary>
        public event WWSRequestHandler OnIncomingRequest;


        /// <summary>
        /// Creates a new WonkyWebServer™ instance with the default HTTP configuration
        /// </summary>
        public WonkyWebServer()
            : this (WWSConfiguration.DefaultHTTPConfiguration)
        {       
        }

        /// <summary>
        /// Creates a new WonkyWebServer™ instance using the given configuration
        /// </summary>
        /// <param name="config">Server configuration</param>
        public WonkyWebServer(WWSConfiguration config) => Configuration = config;

        /// <summary>
        /// Starts the server
        /// </summary>
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

        /// <summary>
        /// Stops the server
        /// </summary>
        public void Stop()
        {
            lock (this)
            {
                _server?.Stop();
                _server = null;

                IsRunning = false;
            }
        }

        /// <summary>
        /// Returns whether the server currently has a valid configuration
        /// </summary>
        /// <returns>Configuration verification result</returns>
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

        private static byte[] GetRequestPostData(HttpListenerRequest request)
        {
            if (request.HasEntityBody)
                using (Stream s = request.InputStream)
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);

                    return ms.ToArray();
                }

            return new byte[0];
        }

        private WWSResponse OnIncoming(HttpListenerRequest req, byte[] content, HttpListenerResponse res)
        {
            if (req is null || res is null || content is null)
                return null;

            DateTime utcnow = DateTime.UtcNow;
            WWSRequestHandler handler;
            WWSConfiguration config;
            var timestamp = new
            {
                UTCRaw = utcnow,
                UTCSinceUnix = new DateTimeOffset(utcnow).ToUnixTimeMilliseconds(),
            };

            lock (this)
            {
                config = Configuration;
                handler = OnIncomingRequest;
            }

            res.Headers[HttpResponseHeader.Server] = config.ServerString;
            res.Headers[HttpResponseHeader.CacheControl] = "max-cache=" + config.CachingAge;
            res.Headers[HttpResponseHeader.Date] = $"{utcnow:ddd, dd MMM yyyy HH:mm:ss} UTC";
            res.Headers[HttpResponseHeader.Allow] = "GET, HEAD, POST, PUT";

            if (config.UseConnectionUpgrade)
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

            if (handler != null)
                try
                {
                    WWSRequest wwsrq = new WWSRequest
                    {
                        RawRequest = req,
                        Cookies = cookies,
                        Content = content,
                        ServerVariables = new ReadOnlyDictionary<string, string>(servervars),
                        GETVariables = new ReadOnlyDictionary<string, string>(Util.DecomposeQueryString(req.Url.Query)),
                    };

                    if (req.ContentType?.ToLower() == "application/x-www-form-urlencoded" && req.HttpMethod?.ToLower() == "post")
                        wwsrq.POSTVariables = new ReadOnlyDictionary<string, string>(Util.DecomposeQueryString(wwsrq.ContentString));

                    rdat = handler(wwsrq);
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
        /// <summary>
        /// The HTTP/HTTPS reuqest's GET variables (read-only)
        /// </summary>
        public IReadOnlyDictionary<string, string> GETVariables { internal set; get; }
        /// <summary>
        /// The HTTP/HTTPS reuqest's POST variables (read-only)
        /// </summary>
        public IReadOnlyDictionary<string, string> POSTVariables { internal set; get; }
        /// <summary>
        /// The binary content sent with the HTTP/HTTPS request. This can e.g. be POST data.
        /// </summary>
        public byte[] Content { internal set; get; }
        /// <summary>
        /// The length of the binary content sent with the HTTP/HTTPS request in bytes.
        /// </summary>
        public int ContentLength => Content.Length;
        /// <summary>
        /// The decoded (string) content sent with the HTTP/HTTPS request.
        /// </summary>
        public string ContentString => RawRequest.ContentEncoding.GetString(Content);
        /// <summary>
        /// The requested HTTP/HTTPS URL.
        /// </summary>
        public Uri RequestedURL => RawRequest.Url;
        /// <summary>
        /// The requested HTTP/HTTPS URL path. In case of the requested URI 'https://example.org/my/file?param=value' the path would be '/my/file'.
        /// </summary>
        public string RequestedURLPath => RequestedURL.AbsolutePath;
        /// <summary>
        /// The HTTP/HTTP request method, e.g. GET, HEAD, POST, PUT, etc.
        /// </summary>
        public string HTTPRequestMethod => RawRequest.HttpMethod;
        /// <summary>
        /// The underlying (raw) HTTP/HTTPS request
        /// </summary>
        public HttpListenerRequest RawRequest { internal set; get; }
        /// <summary>
        /// The sender's public IP information
        /// </summary>
        public IPEndPoint Sender => RawRequest.RemoteEndPoint;
        /// <summary>
        /// A unique ID of the HTTP/HTTPS request
        /// </summary>
        // ReSharper disable once NestedStringInterpolation
        public string RequestID => $"{{{$"{RawRequest.RequestTraceIdentifier:D}"}}}/{Sender}";
        /// <summary>
        /// The sender's browser user agent
        /// </summary>
        public string UserAgent => RawRequest.UserAgent;

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
        /// <summary>
        /// The HTTP/HTTPS status return code
        /// </summary>
        public HttpStatusCode StatusCode { get; }


        /// <summary>
        /// Creates a new HTTP/HTTPS response from the given UTF-16 string (converted to UTF-8)
        /// </summary>
        /// <param name="code">The HTTP/HTTPS status code</param>
        /// <param name="text">UTF-16 string</param>
        public WWSResponse(HttpStatusCode code, string text)
            : this(code, Codepage.GetBytes(text ?? ""))
        {
        }

        /// <summary>
        /// Creates a new HTTP/HTTPS response from the given byte array
        /// </summary>
        /// <param name="code">The HTTP/HTTPS status code</param>
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

    /// <summary>
    /// Represents a server configuration
    /// </summary>
    public struct WWSConfiguration
    {
        /// <summary>
        /// The optional HTTPS configuration (a value of 'null' represents a HTTP server)
        /// </summary>
        public WWSHTTPSConfiguration? HTTPS { set; get; }

        public bool UseConnectionUpgrade { set; get; }
        
        public ushort CachingAge { set; get; }
        /// <summary>
        /// The server's string (i.e. its name)
        /// </summary>
        public string ServerString { set; get; }
        /// <summary>
        /// The listening path (default is empty)
        /// </summary>
        public string ListeningPath { set; get; }
        /// <summary>
        /// The TCP/UDP port, on which the server is listening
        /// </summary>
        public ushort ListeningPort { set; get; }
        
        /// <summary>
        /// The default HTTP configuration
        /// </summary>
        public static WWSConfiguration DefaultHTTPConfiguration { get; } = new WWSConfiguration
        {
            ListeningPath = "",
            ListeningPort = 8080,
            ServerString = "WonkyWebStack™ (WWS4.0) Server",
            HTTPS = null
        };
        /// <summary>
        /// The default HTTPS configuration
        /// </summary>
        public static WWSConfiguration DefaultHTTPSConfiguration { get; } = new WWSConfiguration
        {
            ListeningPath = "",
            ListeningPort = 4430,
            ServerString = "WonkyWebStack™ (WWS4.0) Server",
            HTTPS = WWSHTTPSConfiguration.DefaultConfiguration
        };
    }

    /// <summary>
    /// Represents an HTTPS configuration
    /// </summary>
    public struct WWSHTTPSConfiguration
    {
        public WWSHTTPSCertificatePolicy CertificatePolicy { set; get; }
        /// <summary>
        /// The .X509 encryption and signature certificate
        /// </summary>
        public X509Certificate Certificate { set; get; }
        /// <summary>
        /// The file path pointing to an .X509 encryption and signature certificate (usually with the file extension .cer)
        /// </summary>
        public string X509_CertificatePath
        {
            set => Certificate = value != null ? X509Certificate.CreateFromCertFile(value) : throw new ArgumentNullException(nameof(value));
        }
        /// <summary>
        /// The file path and password (can also be empty) pointing to an PKCS#12  encryption and signature certificate (usually with the file extension .pfx or .p12)
        /// </summary>
        public (string Path, SecureString Password) PKCS12_Certificate
        {
            set
            {
                if (value.Path is string path)
                    Certificate = new X509Certificate2(path, value.Password ?? new SecureString());
            }
        }

        /// <summary>
        /// The default HTTPS configuration
        /// </summary>
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
