using System.Security.Cryptography.X509Certificates;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security;
using System.Linq;
using System.Text;
using System.Web;
using System.Net;
using System;

using WWS.Internals;


namespace WWS
{
    /// <summary>
    /// Represents a delegate for the processing of incoming WWS4.0 (HTTP/HTTPS) requests.
    /// </summary>
    /// <param name="sender">The sender which raised the event</param>
    /// <param name="data">The WWS4.0 request data</param>
    /// <returns>The WWS4.0 response data</returns>
    public delegate Task<WWSResponse> WWSRequestHandler(WonkyWebServer sender, WWSRequest data);


    /// <summary>
    /// Represents a WonkyWebServer™ for event-based HTTP/HTTPS request processing.
    /// </summary>
    /// <inheritdoc />
    public sealed class WonkyWebServer
        : IWebServer<IWSConfiguration>
    {
        private IWSConfiguration _config;
        private HTTPServer _server;

        /// <inheritdoc />
        public IWSConfiguration Configuration
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
        /// <inheritdoc />
        public bool IsRunning { private set; get; }

        /// <summary>
        /// The event is fired upon each incoming HTTP or HTTPS request.
        /// </summary>
        public event WWSRequestHandler OnIncomingRequest;
        /// <summary>
        /// The event is fired upon an internal (server-side) error which results in an HTTP-500 status code.
        /// </summary>
        public event Func<WonkyWebServer, WWSRequest, Exception, Task<WWSResponse>> On500Error;


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
        public WonkyWebServer(IWSConfiguration config) => Configuration = config;

        /// <inheritdoc />
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

        /// <inheritdoc />
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
            if (Configuration.TextEncoding is null)
                throw new InvalidConfigurationException($"The property '{nameof(WWSConfiguration.TextEncoding)}' cannot be null.");

            // TODO : ?
        }

        private async Task<WWSResponse> OnIncoming(HttpListenerRequest req, byte[] content, HttpListenerResponse res)
        {
            if (req is null || res is null || content is null)
                return null;

            DateTime utcnow = DateTime.UtcNow;
            IPEndPoint ipep_server = req?.LocalEndPoint ?? new IPEndPoint(IPAddress.IPv6Any, _config.ListeningPort);
            IPEndPoint ipep_remote = req.RemoteEndPoint ?? new IPEndPoint(IPAddress.Broadcast, ushort.MaxValue - 1);
            WWSRequestHandler handler;
            IWSConfiguration config;
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
                ["LOCALAPPDATA"] = Environment.ExpandEnvironmentVariables("%localappdata%"),
                ["APPDATA"] = Environment.ExpandEnvironmentVariables("%appdata%"),
                ["COMSPEC"] = Environment.ExpandEnvironmentVariables("%comspec%"),
                ["PATH"] = Environment.ExpandEnvironmentVariables("%path%"),
                ["PATHEXT"] = Environment.ExpandEnvironmentVariables("%pathext%"),
                ["WINDIR"] = Environment.ExpandEnvironmentVariables("%windir%"),
                ["SERVER_SOFTWARE"] = $"<address>WWS/4.0 (Win{(Environment.Is64BitOperatingSystem ? 64 : 32)}) WonkySSL/4.0",
                ["SERVER_PORT"] = ipep_server.Port.ToString(),
                ["SERVER_ADDR"] = ipep_server.Address.ToString(),
                ["REMOTE_PORT"] = ipep_remote.Port.ToString(),
                ["REMOTE_ADDR"] = ipep_remote.Address.ToString(),
            };

            servervars["SCRIPT_NAME"] = servervars["REQUEST_PATH"];
            servervars["HTTP_HOST"] =
            servervars["SERVER_NAME"] = servervars["SERVER_ADDR"].Contains(':') ? $"[{servervars["SERVER_ADDR"]}]" : servervars["SERVER_ADDR"];
            servervars["SERVER_SIGNATURE"] = $"<address>{servervars["SERVER_SOFTWARE"]} Server at {ipep_remote.Address} Port {ipep_remote.Port}</address>";
            
            WWSResponse rdat = (HttpStatusCode.ServiceUnavailable, "<h1>service unavailable</h1>", config.TextEncoding);
            WWSRequest wwsrq = new WWSRequest
            {
                RawRequest = req,
                RawResponse = res,
                Cookies = cookies,
                Content = content,
                UTCRequestTime = utcnow,
                ServerVariables = new ReadOnlyDictionary<string, string>(servervars),
                GETVariables = new ReadOnlyDictionary<string, string>(Util.DecomposeQueryString(req.Url.Query)),
            };

            if (req.ContentType?.ToLower() == "application/x-www-form-urlencoded" && req.HttpMethod?.ToLower() == "post")
                wwsrq.POSTVariables = new ReadOnlyDictionary<string, string>(Util.DecomposeQueryString(wwsrq.ContentString));

            if (handler != null)
                try
                {
                    rdat = await handler(this, wwsrq);
                }
                catch (Exception ex)
                when (!Debugger.IsAttached)
                {
                    bool handled = false;

                    if (On500Error != null)
                        try
                        {
                            rdat = await On500Error(this, wwsrq, ex);
                            handled = true;
                        }
                        catch
                        {
                        }

                    if (!handled)
                        rdat = (HttpStatusCode.InternalServerError, $@"
<!doctype html>
<html lang=""en"">
    <head>
        <title>{ex.GetType().FullName}</title>
        <style>
            body {{
                font-family: sans-serif;
                white-space: pre;
            }}
        </style>
    </head>
    <body>
        <h1>An exception of the type <code>{ex.GetType().FullName}</code> was thrown:</h1>
<code style=""font-size: 1.5em"">{HttpUtility.HtmlEncode(ex.PrintException())}</code>
    </body>
</html>
", config.TextEncoding);
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
        /// The requested (unescaped) HTTP/HTTPS URL path. In case of the requested URI 'https://example.org/folder/my%20file?param=value' the path would be '/folder/my file'.
        /// </summary>
        public string RequestedURLPath => Uri.UnescapeDataString(RequestedURL.AbsolutePath);
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
        /// <summary>
        /// The timestamp of the request (UTC)
        /// </summary>
        public DateTime UTCRequestTime { internal set; get; }

        internal HttpListenerResponse RawResponse { set; get; }


        internal WWSRequest()
        {
        }

        /// <inheritdoc />
        public override string ToString() => $"{RequestID} --> {HTTPRequestMethod} {RawRequest?.RawUrl} (c: {string.Concat(Cookies.Select(kvp => $"{kvp.Key}={kvp.Value}"))})";
    }

    /// <summary>
    /// Represents a simple HTTP/HTTPS response
    /// </summary>
    public sealed class WWSResponse
    {
        internal static readonly Encoding ENC_ANSI = Encoding.GetEncoding(1252);


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
        /// The HTTP/HTTPS response's codepage
        /// </summary>
        public Encoding Codepage { get; } = WWSConfiguration.DefaultHTTPConfiguration.TextEncoding;


        /// <summary>
        /// Creates a new HTTP/HTTPS response from the given UTF-16 string (converted to UTF-8)
        /// </summary>
        /// <param name="code">The HTTP/HTTPS status code</param>
        /// <param name="text">UTF-16 string</param>
        /// <param name="enc">The output encoding</param>
        public WWSResponse(HttpStatusCode code, string text, Encoding enc)
            : this(code, ENC_ANSI.GetBytes(text ?? "")) => Codepage = enc;

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
        /// Converts the given UTF-16 string and status code to an HTTP/HTTPS response
        /// </summary>
        /// <param name="t">Data</param>
        public static implicit operator WWSResponse((HttpStatusCode, string, Encoding) t) => new WWSResponse(t.Item1, t.Item2, t.Item3);
        /// <summary>
        /// Converts the given byte array to an HTTP/HTTPS response
        /// </summary>
        /// <param name="bytes">Byte array</param>
        public static explicit operator WWSResponse(byte[] bytes) => (HttpStatusCode.OK, bytes);
        /// <summary>
        /// Converts the given UTF-16 string to a HTTP/HTTPS response
        /// </summary>
        /// <param name="text">UTF-16 string</param>
        public static explicit operator WWSResponse(string text) => (HttpStatusCode.OK, text, WWSConfiguration.DefaultHTTPConfiguration.TextEncoding);
    }

    /// <summary>
    /// Represents a server configuration for a WonkyWebServer™
    /// </summary>
    /// <inheritdoc />
    public struct WWSConfiguration
        : IWSConfiguration
    {
        /// <inheritdoc />
        public WWSHTTPSConfiguration? HTTPS { set; get; }
        /// <inheritdoc />
        public bool UseConnectionUpgrade { set; get; }
        /// <inheritdoc />
        public ushort CachingAge { set; get; }
        /// <inheritdoc />
        public string ServerString { set; get; }
        /// <inheritdoc />
        public string ListeningPath { set; get; }
        /// <inheritdoc />
        public ushort ListeningPort { set; get; }
        /// <inheritdoc />
        public Encoding TextEncoding { get; set; }


        /// <summary>
        /// The default HTTP configuration
        /// </summary>
        public static WWSConfiguration DefaultHTTPConfiguration { get; } = new WWSConfiguration
        {
            ListeningPath = "",
            ListeningPort = 8080,
            TextEncoding = Encoding.UTF8,
            ServerString = "WonkyWebStack™ (WWS4.0) Server",
            HTTPS = null
        };
        /// <summary>
        /// The default HTTPS configuration
        /// </summary>
        public static WWSConfiguration DefaultHTTPSConfiguration { get; } = new WWSConfiguration
        {
            ListeningPort = 4430,
            ListeningPath = DefaultHTTPConfiguration.ListeningPath,
            TextEncoding = DefaultHTTPConfiguration.TextEncoding,
            ServerString = DefaultHTTPConfiguration.ServerString,
            HTTPS = WWSHTTPSConfiguration.DefaultConfiguration
        };


        internal static WWSConfiguration Convert(IWSConfiguration conf)
        {
            switch (conf)
            {
                case null:
                    return DefaultHTTPConfiguration;
                case WWSConfiguration c:
                    return c;
                default:
                    return new WWSConfiguration
                    {
                        HTTPS = conf.HTTPS,
                        CachingAge = conf.CachingAge,
                        TextEncoding = conf.TextEncoding,
                        ServerString = conf.ServerString,
                        ListeningPath = conf.ListeningPath,
                        ListeningPort = conf.ListeningPort,
                        UseConnectionUpgrade = conf.UseConnectionUpgrade,
                    };
            }
        }
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

    /// <summary>
    /// Reprents a HTTPS certificate policy regarding the keeping or disposal of encryption certificates bound to the TCP/UDP HTTPS port after server shutdown.
    /// </summary>
    public enum WWSHTTPSCertificatePolicy
    {
        /// <summary>
        /// The certificate should be kept in the Firewall rules, Registry entries and the local certificate store after server shutdown.
        /// It is possible, that duplicate entries may exist in the listed sites after an involuntary shutdown.
        /// </summary>
        KeepCertificate,
        /// <summary>
        /// The certificate should be removed from the Firewall rules, Registry entries and the local certificate store after server shutdown.
        /// </summary>
        DisposeCertificate
    }
}
