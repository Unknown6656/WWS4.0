using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.Text;
using System.Net;
using System.Web;
using System.IO;
using System;

using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using Newtonsoft.Json;

using WWS.Internals;


namespace WWS
{
    /// <summary>
    /// Represents a DodgyWebServer™ for file-based HTTP/HTTPS request processing including in-file scripting.
    /// </summary>
    /// <inheritdoc cref="WebServer{T}"/>
    public sealed class DodgyWebServer
        : WebServer<DodgyWebServer>
        , IConfigurableWebServer<DWSConfiguration>
    {
        private static readonly Regex _ws_regex = new Regex(@"<\?(?<print>\=)?\s*(?<expr>.*?)\s*\?>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private readonly Dictionary<string, ScriptRunner<string>> _precompiled = new Dictionary<string, ScriptRunner<string>>();
        private readonly Queue<string> _log_queue = new Queue<string>();
        private readonly WonkyWebServer _wws;

        private volatile int _conn_cnt;
        private WWSDatabaseConnector _dbcon;
        private DWSConfiguration _config;


        /// <inheritdoc/>
        public override bool IsRunning => _wws.IsRunning;
        /// <inheritdoc/>
        public DWSConfiguration Configuration => _config;

        /// <summary>
        /// Raised when a HTTP(S) request has been recieved.
        /// </summary>
        public event ServerEvent<DodgyWebServer, WWSRequest> OnRequestRecieved;
        /// <summary>
        /// Raised when a HTTP(S) request has been processed by the webserver.
        /// </summary>
        public event ServerEvent<DodgyWebServer, WWSResponse> OnRequestProcessed;


        /// <summary>
        /// Creates a new server instance with the default configuration
        /// </summary>
        public DodgyWebServer()
            : this(DWSConfiguration.DefaultHTTPConfiguration)
        {       
        }

        /// <summary>
        /// Creates a new server instance using the given configuration
        /// </summary>
        /// <param name="config">Webserver configuration</param>
        public DodgyWebServer(DWSConfiguration config)
            : this(new WonkyWebServer(WWSConfiguration.Convert(config))) => _config = config;

        private DodgyWebServer(WonkyWebServer wws)
        {
            _log_queue.Enqueue(new string('-', 150));
            EnqueueLog("Server .ctor called");

            _wws = wws ?? new WonkyWebServer();
            _wws.On500Error += async (_, r, e) => await Handle500Async(r, e);
            _wws.OnIncomingRequest += OnIncomingRequestAsync;
            
            EnqueueLog("Server created");
        }

        /// <summary>
        /// Default destructor for <see cref="DodgyWebServer"/>
        /// </summary>
        ~DodgyWebServer() => Stop();

#pragma warning disable CS0809

        /// <inheritdoc/>
        [Obsolete("Use `WWS.DodgyWebServer.StartAsync : void -> Task` instead.")]
        public override void Start()
        {
            using (Task task = StartAsync())
            {
                bool c = false;

                task.GetAwaiter().OnCompleted(() => c = true);
                task.Wait();

                while (!c)
                    Thread.Sleep(0);
            }
        }

#pragma warning restore CS0809

        /// <summary>
        /// Starts the webserver
        /// </summary>
        public async Task StartAsync()
        {
            VerifyConfiguration();

            lock (_precompiled)
                _precompiled.Clear();

            _wws.Start();

            EnqueueLog("Server started");
            EnqueueLog($"Configuration:\n{JsonConvert.SerializeObject(_config, Formatting.Indented)}");
            StartLogger();

            if (_config.UseDatabase)
                await StartDatabaseAsync();
        }

        /// <inheritdoc/>
        public override void Stop()
        {
            EnqueueLog("Server stopped");

            _wws.Stop();

            if (_config.UseDatabase)
                StopDatabase();
        }

        /// <summary>
        /// Cleans the internal data storages (a bit)
        /// </summary>
        public void Cleanup()
        {
            EnqueueLog("Server cleanup");

            lock (_precompiled)
                _precompiled.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

#region INTERNALS

        private void VerifyConfiguration()
        {
            void verify_regex(string reg, string desc)
            {
                try
                {
                    Regex _ = new Regex(reg);
                }
                catch
                {
                    throw new InvalidConfigurationException($"The configuration has an invalid regular expression '{reg}' for the property '{desc}'. The server was unable to start.");
                }
            }

            _config.Webroot = _config.Webroot ?? DWSConfiguration.DefaultHTTPConfiguration.Webroot;
            _config._403Path = _config._403Path ?? DWSConfiguration.DefaultHTTPConfiguration._403Path;
            _config._404Path = _config._404Path ?? DWSConfiguration.DefaultHTTPConfiguration._404Path;
            _config._500Path = _config._500Path ?? DWSConfiguration.DefaultHTTPConfiguration._500Path;

            verify_regex(_config.IndexRegex, nameof(DWSConfiguration.IndexRegex));
            verify_regex(_config.DisallowRegex, nameof(DWSConfiguration.DisallowRegex));
            verify_regex(_config.ProcessableRegex, nameof(DWSConfiguration.ProcessableRegex));

            if (!_config.Webroot.Exists)
                _config.Webroot.Create();
        }

        private async Task StartDatabaseAsync()
        {
            _dbcon = new WWSDatabaseConnector(_config.DatabasePath);
            _dbcon.OnSQLCommandIssued += (_, sql) =>
            {
                sql = Regex.Replace(sql, @"[\r\n\s ]+", " ");
                sql = Regex.Replace(sql, @"'[^\']+'", m => m.Length > 20 ? "'.....'" : m.ToString());

                EnqueueLog($"SQL command issued: \"{sql}\"");
            };

            await _dbcon.ConnectAsync();
        }

        private void StopDatabase() => _dbcon.Disconnect();

        private void StartLogger() => Task.Run(async delegate
        {
            FileInfo log = new FileInfo(_config.LogPath);

            if (!log.Exists)
            {
                if (!log.Directory?.Exists ?? false)
                    log.Directory.Create();
            }

            using (FileStream fs = new FileStream(log.FullName, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (StreamWriter wr = new StreamWriter(fs, Encoding.Default))
            {
                void write()
                {
                    lock (_log_queue)
                        while (_log_queue.Count > 0)
                            wr.WriteLine(_log_queue.Dequeue().Trim());

                    wr.Flush();
                }

                EnqueueLog("Logging thread started");

                while (IsRunning)
                {
                    await Task.Delay(100);

                    write();
                }

                EnqueueLog("Logging thread stopped");

                await Task.Delay(400);

                write();
            }
        });

        private void EnqueueLog(string msg)
        {
            lock (_log_queue)
                _log_queue.Enqueue($"[UTC: {DateTime.UtcNow:ddd, yyyy-MMM-dd HH:mm:ss.ffffff}]  {msg.Trim()}");
        }

        private async Task<WWSResponse> DebugSQL(WWSRequest req)
        {
            try
            {
                string q = req.GETVariables["q"];
                IDictionary<string, dynamic>[] res = await _dbcon.ExecuteAsync(q);

                return (HttpStatusCode.OK, $@"
<html>
    <style>
        body {{
            font-family: monospace;
            font-size: 12pt;
        }}

        td, th {{
            border: 1px solid #888;
            white-space: nowrap;
            padding: 1px 4px;
        }}

        hr {{
            width: 100%;
        }}
    
        table {{
            border-collapse: collapse;
            white-space: nowrap;
            min-width: 100%;
        }}
    </style>
    <body>
        {res.Length} entries:
        <hr/>
        <table>
            <tr>{string.Concat(res.FirstOrDefault()?.Keys.Select(k => $"<th>{k}</th>") ?? new string[0])}</tr>
            {string.Concat(res.Select(row => $"<tr>{string.Concat(row.Keys.Select(k => $"<td>{row[k]}</td>"))}</tr>"))}
        </table>
    </body>
</html>
", _config.TextEncoding);
            }
            catch (Exception ex)
            {
                return await Handle500Async(req, ex);
            }
        }

        private HTTPRewriteResult RewriteRequest(WWSRequest req)
        {
            string p_rwr = Configuration.RewritePath ?? "/.htaccess";
            HTTPRewriteRule[] rules = Configuration.AdditionalRewriteRules;

            if (p_rwr.Replace('\\', '/').StartsWith("/"))
                p_rwr = p_rwr.Substring(1);

            try
            {
                FileInfo nfo = new FileInfo(_config.Webroot.FullName + '/' + p_rwr);

                if (nfo.Exists)
                {
                    string htaccess = File.ReadAllText(nfo.FullName);

                    rules = HTTPRewriteEngine.ParseRules(htaccess).Concat(rules).ToArray();
                }
            }
            catch { /* ignored */ }

            return HTTPRewriteEngine.ProcessURI(req, Configuration, rules);
        }

        private async Task<WWSResponse> OnIncomingRequestAsync(WonkyWebServer srv, WWSRequest req)
        {
            HTTPRewriteResult rwres = RewriteRequest(req);

            if (rwres != null)
                if (!rwres.RewrittenUri?.Equals(rwres.OriginalUri) ?? rwres.OriginalUri != null)
                {
                    Uri r_uri = rwres.RewrittenUri ?? rwres.OriginalUri;

                    req.RequestedURL = r_uri;
                    req.GETVariables = new ReadOnlyDictionary<string, string>(Util.DecomposeQueryString(r_uri.Query));
                    req.ServerVariables["REQUEST_URI"] = r_uri.ToString();
                    req.ServerVariables["REQUEST_PATH"] = r_uri.AbsolutePath;
                    req.ServerVariables["QUERY_STRING"] = r_uri.Query;

                    if (rwres.Cookies != null)
                        foreach (KeyValuePair<string, string> kvp in rwres.EnvironmentVariables)
                            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value, EnvironmentVariableTarget.Process);

                    if (rwres.Cookies != null)
                        foreach (KeyValuePair<string, (string, TimeSpan)> cookie in rwres.Cookies)
                            req.Cookies[cookie.Key] = (cookie.Value.Item1, req.UTCRequestTime.Add(cookie.Value.Item2));

                    lock (srv._ot_mutex)
                        srv._ot_override = rwres;
                }

            OnRequestRecieved?.Invoke(this, req);

            WWSResponse res = await ProcessRequestAsync(req);

            if (Configuration.AllowSQLDebugging)
            {
                if (req.RequestedURLPath.ToLower() == "/~sql")
                    res = await DebugSQL(req);

                if (req.GETVariables.ContainsKey("pl"))
                {
                    OnRequestProcessed?.Invoke(this, res);

                    return res;
                }
            }

            EnqueueLog($"RSP: {req} {res.Length} bytes ({Util.BytesToString(res.Length)})");

            _dbcon.LogConnection(req, res);

            _conn_cnt = (_conn_cnt + 1) % 10;

            if (_conn_cnt == 0 || res.Length > 0x1FFFFF)
                GC.Collect();

            OnRequestProcessed?.Invoke(this, res);

            return res;
        }

        private async Task<WWSResponse> ProcessRequestAsync(WWSRequest data)
        {
            EnqueueLog($"REQ: {data}");

            string rpath = Configuration.Webroot.FullName + data.RequestedURLPath;

            try
            {
                DirectoryInfo rdir = new DirectoryInfo(rpath);

                if (rdir.Exists)
                    if (rdir.EnumerateFiles().FirstOrDefault(fi => Regex.IsMatch(fi.Name, Configuration.IndexRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase)) is FileInfo _fi)
                        rpath = _fi.FullName;
                    else if (Configuration.EnumDirectories)
                        return EnumDirectory(data, rdir);
            }
            catch { /* ignored */ }

            try
            {
                FileInfo nfo = new FileInfo(rpath);

                if (nfo.Exists)
                    if (Regex.IsMatch(nfo.Name, Configuration.DisallowRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase))
                        return await Handle403Async(data);
                    else if (Regex.IsMatch(nfo.Name, Configuration.ProcessableRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase))
                        return await ProcessFileAsync(nfo, data);
                    else
                    {
                        byte[] bytes = new byte[nfo.Length];

                        using (FileStream fs = nfo.OpenRead())
                            await fs.ReadAsync(bytes, 0, bytes.Length);

                        data.RawResponse.ContentType = nfo.GetMIMEType();

                        EnqueueLog($"200: {data} ({data.RawResponse.ContentType})");

                        return (HttpStatusCode.OK, bytes);
                    }
            }
            catch (Exception ex) when (ex is ArgumentNullException | ex is PathTooLongException | ex is NotSupportedException | ex is ArgumentException)
            {
            }
            catch
            {
                return await Handle403Async(data);
            }

            return await Handle404Async(data);
        }

        private static async Task<string> GenerateCSCodeAsync(FileInfo nfo)
        {
            int lind = 0;
            string content;
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(@"
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Data;
using System.Net;
using System.Web;
using System.IO;
using System;

using static System.Math;

StringBuilder OUT = new StringBuilder();

void echo(object o) => OUT.Append(o?.ToString() ?? """");
void echoln(object o) => OUT.AppendLine(o?.ToString() ?? """");
");

            using (FileStream fs = nfo.OpenRead())
            using (StreamReader rd = new StreamReader(fs, WWSResponse.ENC_ANSI))
                content = await rd.ReadToEndAsync();

            void print_esc(string s) => sb.AppendLine($"echo(\"{string.Concat((s ?? "").Select(x => $"\\u{(int)x:x4}"))}\");");

            foreach (Match match in _ws_regex.Matches(content))
            {
                print_esc(content.Substring(lind, match.Index - lind));

                sb.AppendLine(match.Groups["print"].Length > 0 ? $"echo({match.Groups["expr"]});" : match.Groups["expr"].ToString().Trim() + ';');

                lind = match.Index + match.Length;
            }

            print_esc(content.Substring(lind));

            return sb.Append(@"
return OUT.ToString();
").ToString();
        }

        private async Task<ScriptRunner<string>> PrecompileAsync(FileInfo nfo)
        {
            EnqueueLog($"Accessing script file '{nfo.FullName}'.");

            ScriptRunner<string> del;
            string hash;
            bool res;

            using (MD5 md5 = MD5.Create())
            using (FileStream fs = nfo.OpenRead())
                hash = string.Concat(md5.ComputeHash(fs).Select(b => b.ToString("X2")));

            lock (_precompiled)
                res = _precompiled.TryGetValue(hash, out del);

            if (!res)
            {
                EnqueueLog($"Precompiling '{nfo.FullName}'...");

                string code = await GenerateCSCodeAsync(nfo);
                ScriptOptions options = ScriptOptions.Default.WithReferences(typeof(ServerHost).Assembly);

                del = CSharpScript.Create<string>(code, options, typeof(ServerHost))
                                  .CreateDelegate();

                lock (_precompiled)
                    _precompiled[hash] = del;
            }
            else
                EnqueueLog($"'{nfo.FullName}' cached with the hash {hash}.");

            return del;
        }

        private async Task<WWSResponse> ProcessFileAsync(FileInfo nfo, WWSRequest req, Action<ServerHost> mod = null)
        {
            try
            {
                ReadOnlyIndexer<string, string> create_indexer(IReadOnlyDictionary<string, string> dic)
                {
                    Dictionary<string, string> ddic = dic?.ToArray().ToDictionary(k => _config.IgnoreCaseOnVariables ? k.Key.ToLower() : k.Key, v => v.Value) ?? new Dictionary<string, string>();

                    return new ReadOnlyIndexer<string, string>(s => ddic.TryGetValue(_config.IgnoreCaseOnVariables ? s?.ToLower() ?? "" : s, out string v) ? v : "");
                }

                ServerHost host = new ServerHost
                {
                    _DATABASE = _dbcon,
                    _GET = create_indexer(req.GETVariables),
                    _POST = create_indexer(req.POSTVariables),
                    _GETVARS = req.GETVariables.Keys.ToArray(),
                    _POSTVARS = req.GETVariables.Keys.ToArray(),
                    _SERVERVARS = req.ServerVariables.Keys.ToArray(),
                    _SERVER = create_indexer(req.ServerVariables),
                    _COOKIES = new Indexer<string, (string Value, DateTime Expiration)>(
                        c => req.Cookies.TryGetValue(c, out var v) ? v : ("", DateTime.MinValue),
                        (c, v) => req.Cookies[c] = v
                    )
                };

                mod?.Invoke(host);

                string res = await (await PrecompileAsync(nfo))(host);

                EnqueueLog($"200: {req}");

                return (HttpStatusCode.OK, res, _config.TextEncoding);
            }
            catch (Exception ex)
            {
                return await Handle500Async(req, ex);
            }
        }

        private async Task<WWSResponse> HandleErrorAsync(WWSRequest req, string dpath, Action<ServerHost> mod, Func<WWSResponse> otherwise)
        {
            if (dpath.Replace('\\', '/').StartsWith("/"))
                dpath = dpath.Substring(1);

            FileInfo nfo = new FileInfo(_config.Webroot.FullName + '/' + dpath);

            return nfo.Exists ? await ProcessFileAsync(nfo, req, mod) : otherwise();
        }

        private async Task<WWSResponse> Handle403Async(WWSRequest req)
        {
            EnqueueLog($"403: {req}");

            return await HandleErrorAsync(req, Configuration._404Path, null, () => (HttpStatusCode.Forbidden, $@"
<!doctype html>
<html lang=""en"">
    <head>
        <title>403 - FORBIDDEN</title>
        <style>
            body {{
                font-family: monospace;
                white-space: pre;
            }}
        </style>
    </head>
    <body>
        <h1>Access denied</h1>
        Your access to the resource <b>""{req.RequestedURLPath}""</b> has been denied due to the webserver's policy.
        However, the failed acccess will be logged and the server's administrator will be notified.<br/>
        The following dataset will also be recorded:
        <table width=""100%"">
            <tr>
                <td><b>Access time (UTC):</b></td>
                <td>{req.UTCRequestTime}</td>
            </tr>
            <tr>
                <td><b>Access URI:</b></td>
                <td>{req.UTCRequestTime}</td>
            </tr>
	    	<tr>
	    		<td><b>Request ID:</b></td>
	    		<td>{req.RequestID}</td>
	    	</tr>
	    	<tr>
	    		<td><b>Your IP address:</b></td>
	    		<td>{req.Sender.Address} ({req.Sender.AddressFamily})</td>
	    	</tr>
	    	<tr>
	    		<td><b>Your TCP/UDP port:</b></td>
	    		<td>{req.Sender.Port}</td>
	    	</tr>
	    	<tr>
	    		<td><b>Your user agent:</b></td>
	    		<td>{HttpUtility.HtmlEncode(req.UserAgent)}</td>
	    	</tr>
        </table>
    </body>
</html>
", _config.TextEncoding));
        }

        private async Task<WWSResponse> Handle404Async(WWSRequest req)
        {
            EnqueueLog($"404: {req}");

            return await HandleErrorAsync(req, Configuration._404Path, null, () => (HttpStatusCode.NotFound, $@"
<!doctype html>
<html lang=""en"">
    <head>
        <title>404 - NOT FOUND</title>
        <style>
            body {{
                font-family: monospace;
                white-space: pre;
            }}
        </style>
    </head>
    <body>
        <h1>Resource not found</h1>
        The requested resource <b>""{req.RequestedURLPath}""</b> could not be found, accessed or resolved.
    </body>
</html>
", _config.TextEncoding));
        }

        private async Task<WWSResponse> Handle500Async(WWSRequest req, Exception ex)
        {
            EnqueueLog($"Internal Exception:\n{ex.PrintException()}");
            EnqueueLog($"500: {req}");

            return await HandleErrorAsync(req, Configuration._500Path, h => h._EXCEPTION = ex, () => (HttpStatusCode.InternalServerError, $@"
<!doctype html>
<html lang=""en"">
    <head>
        <title>500 - INTERNAL SERVER ERROR</title>
        <style>
            body {{
                font-family: sans-serif;
                white-space: pre;
            }}
        </style>
    </head>
    <body>
        <h1>An exception of the type <code>{ex.GetType().FullName}</code> was thrown:</h1>
<code>{HttpUtility.HtmlEncode(ex.PrintException())}</code>
    </body>
</html>
", _config.TextEncoding));
        }

        private WWSResponse EnumDirectory(WWSRequest req, DirectoryInfo dir)
        {
            EnqueueLog($"200: Enumerating directory '{dir}', {req}");

            EnumerationData curr = new EnumerationData
            {
                Info = dir,
                Size = -1L,
                Name = ".",
                Icon = null,
                Type = "Directory"
            };

            EnumerationData[] entries = (req.RequestedURLPath != "/" ? new[] {
                curr, new EnumerationData
                {
                    Info = dir.Parent,
                    Size = -1L,
                    Name = "..",
                    Icon = null,
                    Type = "Directory"
                }
            } : new[] {curr})
            .Concat(dir.EnumerateDirectories().Select(di => new EnumerationData
            {
                Info = di,
                Size = -1L,
                Icon = null,
                Name = di.Name,
                Type = "Directory"
            })).Concat(dir.EnumerateFiles().Select(fi => new EnumerationData
            {
                Info = fi,
                Icon = null,
                Size = fi.Length,
                Name = fi.Name,
                Type = $"{fi.GetMIMEType()} ({fi.Extension})"
            })).ToArray();

            string printentry(EnumerationData entry)
            {
                return $@"
<tr>
    <td>-</td>
    <td><a href=""{req.RequestedURLPath.TrimEnd('/')}/{Uri.EscapeDataString(entry.Name)}"">{HttpUtility.HtmlEncode(entry.Name)}</a></td>
    <td>{HttpUtility.HtmlEncode(entry.Type)}</td>
    <td>{(entry.Size < 0 ? "" : entry.Size.BytesToString())}</td>
    <td>{entry.DTCreated}</td>
    <td>{entry.DTModified}</td>
    <td>{entry.DTAccessed}</td>
    <td>{entry.Info.Attributes}</td>
</tr>
";
            }

            return (HttpStatusCode.OK, $@"
<!DOCTYPE html>
<html lang=""en"">
    <head>
		<title>Index of '{dir.Name}'</title>
        <meta http-equiv=""expires"" content=""0"">
		<style type=""text/css"">
			body {{
				font-family: monospace;
			}}

            hr {{
                border: 0px solid transparent;
                border-bottom: 1px solid #aaa;
            }}
		</style>
    </head>
    <body>
    	<h1>
    		<i>Index of '{dir.Name}'</i>
    	</h1>
        <hr/>
    	<p>
    		You are accessing the directory '{req.RequestedURLPath}' using the following data:
    		<table width=""100%"" border=""0"">
	    		<tr>
	    			<td><b>Your IP address:</b></td>
	    			<td>{req.Sender.Address} ({req.Sender.AddressFamily})</td>
	    		</tr>
	    		<tr>
	    			<td><b>Your TCP/UDP port:</b></td>
	    			<td>{req.Sender.Port}</td>
	    		</tr>
	    		<tr>
	    			<td><b>Your user agent:</b></td>
	    			<td>{HttpUtility.HtmlEncode(req.UserAgent)}</td>
	    		</tr>
	    		<tr>
	    			<td><b>Request URI:</b></td>
	    			<td>{HttpUtility.HtmlEncode(req.RequestedURL.ToString())}</td>
	    		</tr>
	    		<tr>
	    			<td><b>Server time (UTC):</b></td>
	    			<td>{req.UTCRequestTime}</td>
	    		</tr>
	    		<tr>
	    			<td><b>Request ID:</b></td>
	    			<td>{req.RequestID}</td>
	    		</tr>
	    		<tr>
	    			<td><b>Real path:</b></td>
	    			<td>{dir.FullName}</td>
	    		</tr>
	    	</table>
    	</p>
        <hr/>
        <a href=""."">Refresh directory index</a>
    	<br/>
    	<br/>
        {entries.Length} Entries:
    	<br/>
    	<table width=""100%"" border=""0"">
    		<tr>
    			<th></th>
    			<th><u>Name</u></th>
    			<th><u>File type</u></th>
    			<th><u>File size</u></th>
    			<th><u>Date created (UTC)</u></th>
    			<th><u>Date last changed (UTC)</u></th>
    			<th><u>Date last accessed (UTC)</u></th>
    			<th><u>Attributes</u></th>
    		</tr>
            {string.Concat(entries.Select(printentry))}
    	</table>
    	<br/>
    	<br/>
        <a href=""."">Refresh directory index</a>
	</body>
</html>
", _config.TextEncoding);
        }

#endregion
    }

    internal struct EnumerationData
    {
        public long Size;
        public string Name;
        public object Icon;
        public string Type;
        public FileSystemInfo Info;
        public DateTime DTCreated => Info.CreationTimeUtc;
        public DateTime DTModified => Info.LastWriteTimeUtc;
        public DateTime DTAccessed => Info.LastAccessTimeUtc;
    }

    /// <summary>
    /// Represents a server configuration for a DodgyWebServer™
    /// </summary>
    /// <inheritdoc cref="IWSConfiguration"/>
    public struct DWSConfiguration
        : IWSConfiguration
    {
        /// <inheritdoc/>
        public WWSHTTPSConfiguration? HTTPS { get; set; }
        /// <inheritdoc/>
        public bool UseConnectionUpgrade { get; set; }
        /// <inheritdoc/>
        public ushort CachingAge { get; set; }
        /// <inheritdoc/>
        public string ServerString { get; set; }
        /// <inheritdoc/>
        public string ListeningPath { get; set; }
        /// <inheritdoc/>
        public ushort ListeningPort { get; set; }
        /// <inheritdoc/>
        public Encoding TextEncoding { get; set; }
        /// <summary>
        /// The regular expression which matches index files for a directory.
        /// </summary>
        public string IndexRegex { get; set; }
        /// <summary>
        /// The regular expression which matches files not permitted to be displayed.
        /// </summary>
        public string DisallowRegex { get; set; }
        /// <summary>
        /// The regular expression which matches files which can be processed by the scripting engine
        /// </summary>
        public string ProcessableRegex { get; set; }
        /// <summary>
        /// The webserver enumerates local directories if no index file could be found and this option has been set to 'true' (default).
        /// </summary>
        public bool EnumDirectories { get; set; }
        /// <summary>
        /// The path (from the webserver's document root directory) pointing to the file containing all HTTP rewrite rules (usually '.htaccess')
        /// </summary>
        public string RewritePath { get; set; }
        /// <summary>
        /// The path (from the webserver's document root directory) pointing to the 403 error handling document (aka 'Forbidden').
        /// </summary>
        public string _403Path { get; set; }
        /// <summary>
        /// The path (from the webserver's document root directory) pointing to the 404 error handling document (aka 'Not Found')..
        /// </summary>
        public string _404Path { get; set; }
        /// <summary>
        /// The path (from the webserver's document root directory) pointing to the 500 error handling document (aka 'Server Error').
        /// </summary>
        public string _500Path { get; set; }
        /// <summary>
        /// The path pointing to the the webserver's document root directory.
        /// </summary>
        public DirectoryInfo Webroot { set; get; }
        /// <summary>
        /// Ignores the case for the server variables '_GET', '_POST' and '_SERVER'
        /// </summary>
        public bool IgnoreCaseOnVariables { set; get; }
        /// <summary>
        /// Determines whether a physical log file should be created and filled with inbound connection data.
        /// </summary>
        public bool UseLog { set; get; }
        /// <summary>
        /// The path pointing to the physical connection log file (relative to the current working directory).
        /// </summary>
        public string LogPath { set; get; }
        /// <summary>
        /// The path pointing to the WWS4.0™ Database file (default: './database/wws.mdf')
        /// </summary>
        public string DatabasePath { set; get; }
        /// <summary>
        /// The database's data source (default: '.\SQLEXPRESS')
        /// </summary>
        public string DatabaseSource { set; get; }
        /// <summary>
        /// Determines whether the T-SQL database should be used in the background
        /// </summary>
        public bool UseDatabase { set; get; }
        /// <summary>
        /// If the property is set to `true`, T-SQL commands can be executed by calling the website `http(s)://......:.../~sql?q=....` where the command is passed directly as the GET-parameter `q`.
        /// The optional GET-parameter `pl` prevents the T-SQL command from being logged.
        /// </summary>
        public bool AllowSQLDebugging { set; get; }
        /// <summary>
        /// Specifies additional HTTP URL rewrite rules which shall be applied on all incoming requests
        /// </summary>
        public HTTPRewriteRule[] AdditionalRewriteRules { set; get; }

        /// <summary>
        /// The default HTTP configuration
        /// </summary>
        public static DWSConfiguration DefaultHTTPConfiguration { get; }
        /// <summary>
        /// The default HTTPS configuration
        /// </summary>
        public static DWSConfiguration DefaultHTTPSConfiguration { get; }


        static DWSConfiguration()
        {
            DefaultHTTPConfiguration = Convert(WWSConfiguration.DefaultHTTPConfiguration);
            DefaultHTTPSConfiguration = Convert(WWSConfiguration.DefaultHTTPSConfiguration);
        }

        internal static DWSConfiguration Convert(IWSConfiguration conf)
        {
            if (conf is DWSConfiguration c)
                return c;

            conf = conf ?? WWSConfiguration.DefaultHTTPConfiguration;

            const string ext = @"\.(php|s?html?|(cs|vb)x?html|csx?|[wd]wsx?|aspx?)";

            return new DWSConfiguration
            {
                HTTPS = conf.HTTPS,
                CachingAge = conf.CachingAge,
                ServerString = conf.ServerString == WWSConfiguration.DefaultHTTPConfiguration.ServerString ? "DodgyWebServer™ (WWS4.0) Server" : conf.ServerString,
                TextEncoding = conf.TextEncoding,
                ListeningPath = conf.ListeningPath,
                ListeningPort = conf.ListeningPort,
                UseConnectionUpgrade = conf.UseConnectionUpgrade,
                IndexRegex = $@"^(index|home|default)({ext})?$",
                ProcessableRegex = $"{ext}$",
                DisallowRegex = @"^\..+$",
                IgnoreCaseOnVariables = true,
                EnumDirectories = true,
                _403Path = "/403.html",
                _404Path = "/404.html",
                _500Path = "/500.html",
                LogPath = "wws.log",
                RewritePath = "/.htaccess",
                DatabasePath = "database/wws.mdf",
                DatabaseSource = @"(LocalDB)\MSSQLLocalDB",
                AllowSQLDebugging = false,
                UseDatabase = true,
                UseLog = true,
                Webroot = new DirectoryInfo("./www"),
                AdditionalRewriteRules = new HTTPRewriteRule[0],
            };
        }
    }

    /// <summary>
    /// Represents the webserver host from a scripting point of view (internal functionality only)
    /// </summary>
    [DebuggerNonUserCode, DebuggerStepThrough]
    public sealed class ServerHost
    {
        /// <summary>
        /// The webserver's exception (only on 500-Errors)
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public Exception _EXCEPTION { internal set; get; }
        /// <summary>
        /// The request's GET variables
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public ReadOnlyIndexer<string, string> _GET { internal set; get; }
        /// <summary>
        /// The request's POST variables
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public ReadOnlyIndexer<string, string> _POST { internal set; get; }
        /// <summary>
        /// The webserver's environment variables
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public ReadOnlyIndexer<string, string> _SERVER { internal set; get; }
        /// <summary>
        /// The request's GET variable names
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public string[] _GETVARS { internal set; get; }
        /// <summary>
        /// The request's POST variable names
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public string[] _POSTVARS { internal set; get; }
        /// <summary>
        /// The webserver's environment variable names
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public string[] _SERVERVARS { internal set; get; }
        /// <summary>
        /// The Connection's cookies
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public Indexer<string, (string Value, DateTime Expiration)> _COOKIES { internal set; get; }
        /// <summary>
        /// The webserver's T-SQL database connector
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public WWSDatabaseConnector _DATABASE { internal set; get; }


        internal ServerHost()
        {
        }
    }
}
