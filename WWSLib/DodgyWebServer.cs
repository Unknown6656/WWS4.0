using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Net;
using System.Web;
using System.IO;
using System;

using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using WWS.Internals;


/*
 * TODO: PERFORMANCE IMPROVEMENTS:
 *  o  COMPILE COMMON SCRIPT AT STARTUP
 */

namespace WWS
{
    /// <summary>
    /// Represents a DodgyWebServer™ for file-based HTTP/HTTPS request processing including in-file scripting.
    /// </summary>
    /// <inheritdoc />
    public sealed class DodgyWebServer
        : IWebServer<DWSConfiguration>
    {
        private static readonly Regex _ws_regex = new Regex(@"<\?(?<print>\=)?\s*(?<expr>.*?)\s*\?>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Dictionary<string, ScriptRunner<string>> _precompiled = new Dictionary<string, ScriptRunner<string>>();
        private readonly WonkyWebServer _wws;
        private DWSConfiguration _config;


        /// <inheritdoc/>
        public bool IsRunning => _wws.IsRunning;

        /// <inheritdoc/>
        public DWSConfiguration Configuration => _config;


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
            _wws = wws ?? new WonkyWebServer();
            _wws.On500Error += (r, e) =>
            {
                using (Task<WWSResponse> task = Handle500(r, e))
                {
                    task.RunSynchronously();

                    return task.Result;
                }
            };
            _wws.OnIncomingRequest += OnIncomingRequestAsync;
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (_config.Webroot is null)
                _config.Webroot = DWSConfiguration.DefaultHTTPConfiguration.Webroot;

            if (!_config.Webroot.Exists)
                _config.Webroot.Create();

            lock (_precompiled)
                _precompiled.Clear();

            _wws.Start();
        }

        /// <inheritdoc/>
        public void Stop() => _wws.Stop();

        /// <summary>
        /// Cleans the internal data storages (a bit)
        /// </summary>
        public void Cleanup()
        {
            lock (_precompiled)
                _precompiled.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        
        private async Task<WWSResponse> OnIncomingRequestAsync(WWSRequest data)
        {
            Console.WriteLine($"{data.RequestID} acessing {data.RequestedURL}");

            string rpath = Configuration.Webroot.FullName + data.RequestedURLPath;
            DirectoryInfo rdir = new DirectoryInfo(rpath);

            if (rdir.Exists)
                if (rdir.EnumerateFiles().FirstOrDefault(fi => Regex.IsMatch(fi.Name, Configuration.IndexRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase)) is FileInfo _fi)
                    rpath = _fi.FullName;
                else if(Configuration.EnumDirectories)
                    return EnumDirectory(data, rdir);

            try
            {
                FileInfo nfo = new FileInfo(rpath);

                if (nfo.Exists)
                    if (Regex.IsMatch(nfo.Name, Configuration.DisallowRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase))
                        return await Handle403(data);
                    else if (Regex.IsMatch(nfo.Name, Configuration.ProcessableRegex, RegexOptions.Compiled | RegexOptions.IgnoreCase))
                        return await ProcessFileAsync(nfo, data);
                    else
                    {
                        byte[] bytes = new byte[nfo.Length];

                        using (FileStream fs = nfo.OpenRead())
                            fs.Read(bytes, 0, bytes.Length);

                        data.RawResponse.ContentType = nfo.GetMIMEType();

                        return (HttpStatusCode.OK, bytes);
                    }
            }
            catch (Exception ex) when (ex is ArgumentNullException | ex is PathTooLongException | ex is NotSupportedException | ex is ArgumentException)
            {
            }
            catch
            {
                return await Handle403(data);
            }

            return await Handle404(data);
        }

        private static string GenerateCSCode(FileInfo nfo)
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
                content = rd.ReadToEnd();

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

        private static ScriptRunner<string> PrecompileAsync(FileInfo nfo)
        {
            ScriptRunner<string> del;
            string hash;
            bool res;

            using (MD5 md5 = MD5.Create())
            using (FileStream fs = nfo.OpenRead())
                hash = string.Concat(md5.ComputeHash(fs).Select(b => b.ToString("x2")));

            lock (_precompiled)
                res = _precompiled.TryGetValue(hash, out del);

            if (!res)
            {
                string code = GenerateCSCode(nfo);
                ScriptOptions options = ScriptOptions.Default.WithReferences(typeof(ServerHost).Assembly);

                del = CSharpScript.Create<string>(code, options, typeof(ServerHost))
                                  .CreateDelegate();

                lock (_precompiled)
                    _precompiled[hash] = del;
            }

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
                    _GET = create_indexer(req.GETVariables),
                    _POST = create_indexer(req.POSTVariables),
                    _SERVER = create_indexer(req.ServerVariables),
                    _COOKIES = new Indexer<string, (string Value, DateTime Expiration)>(
                        c => req.Cookies.TryGetValue(c, out var v) ? v : ("", DateTime.MinValue),
                        (c, v) => req.Cookies[c] = v
                    )
                };

                mod?.Invoke(host);

                string res = await PrecompileAsync(nfo)(host);

                return (HttpStatusCode.OK, res, _config.TextEncoding);
            }
            catch (Exception ex)
            {
                return await Handle500(req, ex);
            }
        }

        private async Task<WWSResponse> HandleError(WWSRequest req, string dpath, Action<ServerHost> mod, Func<WWSResponse> otherwise)
        {
            FileInfo nfo = new FileInfo(dpath);

            return nfo.Exists ? await ProcessFileAsync(nfo, req, mod) : otherwise();
        }

        private async Task<WWSResponse> Handle403(WWSRequest req) => await HandleError(req, Configuration._404Path, null, () => (HttpStatusCode.Forbidden, $@"
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

        private async Task<WWSResponse> Handle404(WWSRequest req) => await HandleError(req, Configuration._404Path, null, () => (HttpStatusCode.NotFound, $@"
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

        private async Task<WWSResponse> Handle500(WWSRequest req, Exception ex) => await HandleError(req, Configuration._500Path, h => h._EXCEPTION = ex, () => (HttpStatusCode.InternalServerError, $@"
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

        private WWSResponse EnumDirectory(WWSRequest req, DirectoryInfo dir)
        {
            EnumerationData curr = new EnumerationData
            {
                Info = dir,
                Size = -1L,
                Name = ".",
                Icon = null,
                Type = "Directory"
            };

            IEnumerable<EnumerationData> entries = (req.RequestedURLPath != "/" ? new[] {
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
            }));

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
        {entries.Count()} Entries:
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
    /// <inheritdoc />
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
                Webroot = new DirectoryInfo("./www"),
            };
        }
    }

    /// <summary>
    /// Represents the webserver host from a scripting point of view (internal functionality only)
    /// </summary>
    [DebuggerNonUserCode, DebuggerStepThrough]
    public sealed class ServerHost
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public Exception _EXCEPTION { internal set; get; }
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public ReadOnlyIndexer<string, string> _GET { internal set; get; }
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public ReadOnlyIndexer<string, string> _POST { internal set; get; }
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public ReadOnlyIndexer<string, string> _SERVER { internal set; get; }
        [DebuggerBrowsable(DebuggerBrowsableState.Never), DebuggerHidden]
        public Indexer<string, (string Value, DateTime Expiration)> _COOKIES { internal set; get; }

        internal ServerHost()
        {
        }
    }
}
