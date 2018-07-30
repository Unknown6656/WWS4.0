using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System;
using System.Diagnostics;
using System.IO;


namespace WWS.Internals
{
    /// <summary>
    /// Represents a delegate for the processing of incoming HTTP/HTTPS requests.
    /// </summary>
    /// <param name="req">The HTTP/HTTPS request data</param>
    /// <param name="content">The HTTP/HTTPS request content</param>
    /// <param name="res">The HTTP/HTTPS response data</param>
    /// <returns>The WWS response data</returns>
    public delegate Task<WWSResponse> RequestHandler(HttpListenerRequest req, byte[] content, HttpListenerResponse res);

    /// <summary>
    /// Represents a simple HTTP/HTTPS server
    /// </summary>
    public sealed class HTTPServer
        : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        

        /// <summary>
        /// The TCP/UDP port, on which the server is listening for incomming HTTP/HTTPS requests
        /// </summary>
        public ushort Port { get; }
        /// <summary>
        /// Indicates, whether the current instance has already been disposed
        /// </summary>
        public bool IsDisposed { get; private set; }
        /// <summary>
        /// The request processing handler
        /// </summary>
        public RequestHandler Handler { set; get; }

        /// <summary>
        /// An event which will be fired upon occurence of internal errors
        /// </summary>
        public event EventHandler<Exception> OnInternalError;


        /// <summary>
        /// Creates a simple HTTP/HTTPS server listening at the given port number
        /// </summary>
        /// <param name="port">TCP/UDP Port</param>
        public HTTPServer(ushort port)
            : this(port, "")
        {
        }

        /// <summary>
        /// Creates a simple HTTP/HTTPS server listening at the given port number
        /// </summary>
        /// <param name="subpath">The HTTP/HTTPS path following the domain, on which the server shall listen (the default is an empty string in order to listen to all subpaths)</param>
        /// <param name="port">TCP/UDP Port</param>
        public HTTPServer(ushort port, string subpath)
            : this(port, subpath, false)
        {
        }

        /// <summary>
        /// Creates a simple HTTP/HTTPS server listening at the given port number
        /// </summary>
        /// <param name="port">TCP/UDP Port</param>
        /// <param name="subpath">The HTTP/HTTPS path following the domain, on which the server shall listen (the default is an empty string in order to listen to all subpaths)</param>
        /// <param name="https"><c>true</c> if HTTPS should be used, otherwise <c>false</c></param>
        public HTTPServer(ushort port, string subpath, bool https)
        {
            if (!HttpListener.IsSupported)
                throw new NotSupportedException("Requires Windows XP SP2, Server 2003 or a newer operating system.");

            Port = port;
            _listener.Prefixes.Add($"http{(https ? "s" : "")}://*:{port}/{subpath ?? ""}");
            _listener.TimeoutManager.MinSendBytesPerSecond = uint.MaxValue;
        }

        /// <summary>
        /// Disposes the current instance and releases all underlying resources
        /// </summary>
        ~HTTPServer() => Dispose();

        /// <summary>
        /// Starts the HTTP/HTTPS server
        /// </summary>
        public void Start()
        {
            lock (this)
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(HTTPServer));

            if (FirewallUtils.IsPortOpen(Port))
                FirewallUtils.ClosePort(Port);

            FirewallUtils.OpenPort(Port, "WWS4.0 HTTP/HTTPS Server");

            _listener.Start();

            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    lock (this)
                        if (IsDisposed)
                            break;

                    if (_listener.IsListening)
                        await Listen(_listener);
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Stops the server and releases all underlying resources
        /// </summary>
        public void Stop()
        {
            lock (this)
                if (!IsDisposed)
                {
                    if (_listener.IsListening)
                        _listener.Stop();

                    if (FirewallUtils.IsPortOpen(Port))
                        FirewallUtils.ClosePort(Port);

                    _listener.Close();
                    IsDisposed = true;
                }
        }

        /// <summary>
        /// Disposes the current instance and releases all underlying resources
        /// </summary>
        public void Dispose() => Stop();

        internal async Task Listen(HttpListener listener)
        {
            if (Handler is null)
                return;

            HttpListenerContext ctx = await listener.GetContextAsync();

            try
            {
                byte[] content = ctx.Request.InputStream.ToBytes();
                WWSResponse resp = await Handler(ctx.Request, content, ctx.Response);
                byte[] ret = resp?.Bytes ?? new byte[0];

                ctx.Response.ContentEncoding = resp.Codepage;
                ctx.Response.ContentLength64 = ret.Length;

                using (Stream os = ctx.Response.OutputStream)
                    await os.WriteAsync(ret, 0, ret.Length);
            }
            catch (Exception ex)
            when (!Debugger.IsAttached)
            {
                OnInternalError?.Invoke(this, ex);
            }
        }
    }
}
