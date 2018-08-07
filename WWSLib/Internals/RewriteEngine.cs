// ReSharper disable PossibleMultipleEnumeration

using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System;


namespace WWS.Internals
{
    /// <summary>
    /// Represents an HTTP rewrite engine for HTTP requests
    /// </summary>
    public static class HTTPRewriteEngine
    {
        internal static string ProcessCondition(WWSRequest req, DWSConfiguration config, Uri uri, string cond) =>
            Regex.Replace(cond, @"\%\{(?<var>[a-z_]\w*)\}", (Match m) =>
                          {
                              switch (m.Groups["var"].ToString().ToLower())
                              {
                                  case "HTTP_USER_AGENT":
                                      return req.UserAgent;
                                  case "HTTP_COOKIE":
                                      return req.CookieString;
                                  case "HTTP_HOST":
                                      return uri.Host;
                                  case "REMOTE_ADDR":
                                      return req.Sender.Address.ToString();
                                  case "REMOTE_HOST":
                                      return Dns.GetHostEntry(req.Sender.Address).HostName;
                                  case "REMOTE_USER":
                                  case "REMOTE_IDENT":
                                      return req.Sender.ToString();
                                  case "REQUEST_METHOD":
                                      return req.HTTPRequestMethod;
                                  case "SCRIPT_FILENAME":
                                      return req.RequestedURLPath;
                                  case "QUERY_STRING":
                                      return uri.Query;
                                  case "DOCUMENT_ROOT":
                                      return config.Webroot.FullName;
                                  case "SERVER_NAME":
                                      return Dns.GetHostEntry(uri.Host).HostName;
                                  case "SERVER_ADDR":
                                      return Dns.GetHostEntry(uri.Host).AddressList[0].ToString();
                                  case "SERVER_PORT":
                                      return config.ListeningPort.ToString();
                                  case "SERVER_PROTOCOL":
                                      return config.ServerString;
                                  case "SERVER_SOFTWARE":
                                      return Environment.OSVersion.ToString();
                                  case "TIME_YEAR":
                                      return req.UTCRequestTime.Year.ToString();
                                  case "TIME_MON":
                                      return req.UTCRequestTime.Month.ToString();
                                  case "TIME_DAY":
                                      return req.UTCRequestTime.Day.ToString();
                                  case "TIME_HOUR":
                                      return req.UTCRequestTime.Hour.ToString();
                                  case "TIME_MIN":
                                      return req.UTCRequestTime.Minute.ToString();
                                  case "TIME_SEC":
                                      return req.UTCRequestTime.Second.ToString();
                                  case "TIME_WDAY":
                                      return req.UTCRequestTime.DayOfWeek.ToString();
                                  case "TIME":
                                      return WWSDatabaseConnector.SQLEscape(req.UTCRequestTime).Trim('\'');
                                  case "API_VERSION":
                                      return "WWS4.0";
                                  case "REQUEST_URI":
                                      return uri.ToString();
                                  case "REQUEST_FILENAME":
                                      return uri.AbsolutePath;
                                  default:
                                      return $"%{{{m.Groups["var"]}}}";
                              }
                          }, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Processes the given WWS4.0™ Request according to the given rewrite rules and returns the rewrite-engine's result
        /// </summary>
        /// <param name="req">Input URI</param>
        /// <param name="config">The server's configuration</param>
        /// <param name="rules">Set of HTTP rewrite rules</param>
        /// <returns>Rewritten URI</returns>
        public static HTTPRewriteResult ProcessURI(WWSRequest req, DWSConfiguration config, IEnumerable<HTTPRewriteRule> rules)
        {
            Regex gen_regex(HTTPRewriteRule rule, out HTTPRewriteFlags[] flags)
            {
                flags = (rule.Flags ?? new HTTPRewriteFlags[0]).Distinct().ToArray();

                RegexOptions ropt = RegexOptions.CultureInvariant | RegexOptions.Compiled;

                if (flags.Contains(HTTPRewriteFlags.NC))
                    ropt |= RegexOptions.IgnoreCase;
                
                return new Regex(rule.MatchRegex, ropt);
            }

            rules = (rules ?? new HTTPRewriteRule[0]).Distinct();

            Dictionary<string, (string, TimeSpan)> d_coo = new Dictionary<string, (string, TimeSpan)>();
            Dictionary<string, string> d_env = new Dictionary<string, string>();
            Uri uri = req.RequestedURL;
            string server_s = null;
            bool previous = false;
            bool chained = false;
            string mime_t = null;
            int nmaxcnt = 32000;
            int ncount = 0;
            int skip = 0;
begin:
            if (rules.Any() && nmaxcnt > ncount)
                foreach (HTTPRewriteRule rule in rules)
                    if (skip == 0)
                    {
                        Regex regex = gen_regex(rule, out HTTPRewriteFlags[] flags);

                        if (!chained || previous)
                            if (rule.Conditional is string cond)
                            {
                                cond = ProcessCondition(req, config, uri, cond);
                                previous = regex.IsMatch(cond);
                            }
                            else
                            {
                                string qstr = flags.Contains(HTTPRewriteFlags.NQ) ? uri.AbsolutePath : uri.PathAndQuery;

                                qstr = Uri.UnescapeDataString(qstr);

                                if (qstr.StartsWith("/"))
                                    qstr = qstr.Substring(1);

                                if (regex.IsMatch(qstr))
                                {
                                    string res = rule.OutputExpression;

                                    if (res != "-")
                                        res = regex.Replace(qstr, rule.OutputExpression);

                                    bool has_q = res.Contains('?');

                                    if (flags.Contains(HTTPRewriteFlags.QSA))
                                        res += has_q ? '&' + uri.Query.Substring(1) : uri.Query;

                                    if (!Uri.IsWellFormedUriString(res, UriKind.Absolute))
                                        res = $"{uri.Scheme}://{uri.Authority}/{(res.StartsWith("/") ? res.Substring(1) : res)}";

                                    if (uri.Fragment.Length > 0)
                                        res += uri.Fragment;

                                    if (!flags.Contains(HTTPRewriteFlags.BNP))
                                        res = res.Replace(' ', '+');

                                    uri = new Uri(res);
                                    previous = true;
                                }
                                else
                                    previous = false;
                            }

                        chained = flags.Contains(HTTPRewriteFlags.C);

                        if (previous)
                        {
                            foreach (HTTPRewriteFlags f in flags)
                                if (f is HTTPRewriteFlags.Cookie fco)
                                    d_coo[fco.Name] = (fco.Value, fco.ExpirationDate);
                                else if (f is HTTPRewriteFlags.EnvironmentVariable fe)
                                    d_env[fe.Name] = fe.Value;
                                else if (f is HTTPRewriteFlags.ServerString fss)
                                    server_s = fss.String;
                                else if (f is HTTPRewriteFlags.MimeType ft)
                                    mime_t = ft.Type;
                                else if (f is HTTPRewriteFlags.Skip fs && fs.Count > 0)
                                    skip = fs.Count;
                                else if (f is HTTPRewriteFlags.Next fn)
                                {
                                    nmaxcnt = Math.Max(nmaxcnt, fn.IterationCount);

                                    ++ncount;

                                    goto begin;
                                }

                            if (flags.Contains(HTTPRewriteFlags.L))
                                break;
                        }
                    }
                    else
                    {
                        --skip;
                    }

            return new HTTPRewriteResult
            {
                MimeType = mime_t,
                ServerString = server_s,
                Cookies = d_coo,
                EnvironmentVariables = d_env,
                OriginalUri = req.RequestedURL,
                RewrittenUri = uri
            };
        }

        /// <summary>
        /// Processes the given WWS4.0™ Request according to the given rewrite rules and returns the rewrite-engine's result
        /// </summary>
        /// <param name="req">Input URI</param>
        /// <param name="config">The server's configuration</param>
        /// <param name="rules">Set of HTTP rewrite rules</param>
        /// <returns>Rewritten URI</returns>
        public static HTTPRewriteResult ProcessURI(WWSRequest req, DWSConfiguration config, params HTTPRewriteRule[] rules) => ProcessURI(req, config, rules as IEnumerable<HTTPRewriteRule>);

        /// <summary>
        /// Parses the given .htdocs/mod_rewrite contents and returns the parsed set of HTTP rewrite rules
        /// </summary>
        /// <param name="htaccess">.htaccess content</param>
        /// <returns>Set of HTTP rewrite rules</returns>
        public static HTTPRewriteRule[] ParseRules(string htaccess) => ParseRules(htaccess, true);

        /// <summary>
        /// Parses the given .htdocs/mod_rewrite contents and returns the parsed set of HTTP rewrite rules
        /// </summary>
        /// <param name="htaccess">.htaccess content</param>
        /// <param name="engine_state">Initial rewrite engine state (`true` == On, `false` == Off)</param>
        /// <returns>Set of HTTP rewrite rules</returns>
        public static HTTPRewriteRule[] ParseRules(string htaccess, bool engine_state)
        {
            List<HTTPRewriteRule> rls = new List<HTTPRewriteRule>();

            foreach (string l in (htaccess ?? "").Split('\r', '\n')
                                              .Select(line => line.Contains('#') ? line.Remove(line.LastIndexOf('#')) : line)
                                              .Where(line => !string.IsNullOrWhiteSpace(line))
                                              .Select(line => line.Trim()))
                if (l.Match(@"^rewrite\-?engine\s+(?<v>.+)$", out GroupCollection c))
                    switch (c["v"].ToString().ToLower())
                    {
                        case "on":
                        case "yes":
                        case "true":
                            engine_state = true;

                            break;
                        case "off":
                        case "no":
                        case "false":
                            engine_state = false;

                            break;
                        default:
                            throw  new ArgumentException($"Invalid option '{c["v"]}' for enabling/disabling the HTTP rewrite engine", nameof(htaccess));
                    }
                else if (engine_state && l.Match("^rewrite.+$", out c))
                    rls.Add(HTTPRewriteRule.Parse(l));

            return rls.Distinct().ToArray();
        }
    }

    /// <summary>
    /// Represents the result of an HTTP URI rewriting process
    /// </summary>
    public sealed class HTTPRewriteResult
    {
        /// <summary>
        /// The environment variables to be set
        /// </summary>
        public IReadOnlyDictionary<string, string> EnvironmentVariables { get; internal set; }
        /// <summary>
        /// The cookies to be set
        /// </summary>
        public IReadOnlyDictionary<string, (string Value, TimeSpan Expiration)> Cookies { get; internal set; }
        /// <summary>
        /// The [optional] server string
        /// </summary>
        public string ServerString { get; internal set; }
        /// <summary>
        /// The rewritten URI
        /// </summary>
        public Uri RewrittenUri { get; internal set; }
        /// <summary>
        /// The original URI
        /// </summary>
        public Uri OriginalUri { get; internal set; }
        /// <summary>
        /// The [optional] mime type
        /// </summary>
        public string MimeType { get; internal set; }


        internal HTTPRewriteResult()
        {       
        }
    }

    /// <summary>
    /// Represents an HTTP rewrite rule
    /// </summary>
    public sealed class HTTPRewriteRule
    {
        /// <summary>
        /// The conditional input string
        /// </summary>
        public string Conditional { get; }
        /// <summary>
        /// Matching regular expression
        /// </summary>
        public string MatchRegex { get; }
        /// <summary>
        /// Replacement string
        /// </summary>
        public string OutputExpression { get; }
        /// <summary>
        /// A collection of rewrite flags
        /// </summary>
        public HTTPRewriteFlags[] Flags { get; }


        private HTTPRewriteRule(string match_regex, string output_expression, string conditional, HTTPRewriteFlags[] flags)
        {
            try
            {
                _ = new Regex(MatchRegex = (match_regex ?? "^$")).ToString();
            }
            catch
            {
                throw new ArgumentException($"Invalid regex expression: '{MatchRegex}'.", nameof(match_regex));
            }

            Conditional = conditional;
            OutputExpression = output_expression ?? "$0";
            Flags = flags ?? new HTTPRewriteFlags[0];

            if (Flags.Any(f => f is null))
                throw new ArgumentNullException(nameof(flags));
        }

        /// <summary>
        /// Creates a new HTTP rewrite rule
        /// </summary>
        /// <param name="match_regex">Matching regular expression</param>
        /// <param name="output_expression">Replacement string</param>
        /// <param name="flags">Optional flags</param>
        /// <exception cref="ArgumentException">Thrown if an invalid regex exception has been passed</exception>
        /// <exception cref="ArgumentNullException">Thrown if any flag is null</exception>
        public static HTTPRewriteRule CreateRule(string match_regex, string output_expression, params HTTPRewriteFlags[] flags) => new HTTPRewriteRule(match_regex, output_expression, null, flags);

        /// <summary>
        /// Creates a new HTTP conditional rule
        /// </summary>
        /// <param name="condition_input">The condition's input string</param>
        /// <param name="match_regex">Matching regular expression</param>
        /// <param name="flags">Optional flags</param>
        /// <exception cref="ArgumentException">Thrown if an invalid regex exception has been passed</exception>
        /// <exception cref="ArgumentNullException">Thrown if any flag is null</exception>
        public static HTTPRewriteRule CreateCondition(string condition_input, string match_regex, params HTTPRewriteFlags[] flags) => new HTTPRewriteRule(match_regex, null, condition_input, flags);

        /// <summary>
        /// Parses an HTTP rewrite rule from the given mod_rewrite-compatible rule
        /// </summary>
        /// <param name="rule">mod_rewrite-compatible rule</param>
        /// <returns>Parsed HTTP rewrite rule</returns>
        public static HTTPRewriteRule Parse(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule))
                throw new ArgumentException("The rule string must not be null or empty.", nameof(rule));

            rule = rule.Trim();

            if (!rule.EndsWith("]"))
                rule += " []";

            const string fin = @"\s+(?<pat>([^\s]+|\"".+\""))\s+(?<repl>([^\s]+|\"".+\""))\s*\[(?<flags>.*)\]$";
            HTTPRewriteFlags[] oflags = new HTTPRewriteFlags[0];
            string pat, repl, flgs;
            bool iscond = false;
            
            if (rule.Match(@"^rewrite\-?cond" + fin, out GroupCollection c))
            {
                iscond = true;
                pat = c["pat"].ToString();
                repl = c["repl"].ToString();
                flgs = c["flags"].ToString();
            }
            else if (rule.Match(@"^rewrite\-?rule" + fin, out c))
            {
                pat = c["pat"].ToString();
                repl = c["repl"].ToString();
                flgs = c["flags"].ToString();
            }
            else
                throw new ArgumentException($"Unable to parse unknown rewrite rule '{rule}'", nameof(rule));

            if (pat.StartsWith("\"") && pat.EndsWith("\""))
                pat = pat.Substring(1, pat.Length - 2);

            if (repl.StartsWith("\"") && repl.EndsWith("\""))
                repl = repl.Substring(1, repl.Length - 2);

            foreach ((string name, string arg) in from f in flgs.Split(',')
                                                  where !string.IsNullOrWhiteSpace(f)
                                                  let eidx = f.IndexOf('=')
                                                  select eidx < 0 ? (f.Trim(), "") : (f.Substring(0, eidx).Trim(), f.Substring(eidx + 1).Trim()))
                switch (name.ToUpper())
                {
                    case "C":
                        oflags |= HTTPRewriteFlags.C;

                        break;
                    case "CO":
                    {
                        int cidx = arg.IndexOf(':');

                        if (cidx < 0)
                            throw new InvalidOperationException("The rewrite-flag `CO` must contain at least one colon in its argument.");

                        string[] sargs = arg.Split(':');
                        int expr = 3600 * 24;

                        if (sargs.Length > 2 && uint.TryParse(sargs[2], out uint eui))
                            expr = (int)eui;

                        oflags |= HTTPRewriteFlags.CO(sargs[0], sargs[1], TimeSpan.FromSeconds(expr));
                    } break;
                    case "E":
                    {
                        int cidx = arg.IndexOf(':');

                        if (cidx < 0)
                            throw new InvalidOperationException("The rewrite-flag `E` must contain a colon in its argument.");

                        oflags |= HTTPRewriteFlags.E(arg.Substring(0, cidx), arg.Substring(cidx + 1));
                    } break;
                    case "F":
                        oflags |= HTTPRewriteFlags.F;

                        break;
                    case "G":
                        oflags |= HTTPRewriteFlags.G;

                        break;
                    case "L":
                    case "END":
                        oflags |= HTTPRewriteFlags.L;

                        break;
                    case "N":
                        oflags |= int.TryParse(arg, out int i) ? HTTPRewriteFlags.N(i)
                            : arg.Length == 0 ? HTTPRewriteFlags.N()
                            : throw new InvalidOperationException($"Invalid value '{arg}' for the rewrite flag `N`.");

                        break;
                    case "NC":
                        oflags |= HTTPRewriteFlags.NC;

                        break;
                    case "NE":
                        oflags |= HTTPRewriteFlags.NE;

                        break;
                    case "NQ":
                    case "QSD":
                        oflags |= HTTPRewriteFlags.NQ;

                        break;
                    case "R":
                        oflags |= int.TryParse(arg, out i) ? HTTPRewriteFlags.R((HttpStatusCode)i)
                                : arg.Length == 0          ? HTTPRewriteFlags.R()
                                : throw new InvalidOperationException($"Invalid value '{arg}' for the rewrite flag `R`.");

                        break;
                    case "QSA":
                        oflags |= HTTPRewriteFlags.QSA;

                        break;
                    case "S":
                        if (uint.TryParse(arg, out uint ui))
                        {
                            oflags |= HTTPRewriteFlags.S((int)ui);

                            break;
                        }
                        else
                            throw new InvalidOperationException($"Invalid value '{arg}' for the rewrite flag `S`.");
                    case "SS":
                        oflags |= HTTPRewriteFlags.SS(arg);

                        break;
                    case "T":
                        oflags |= HTTPRewriteFlags.T(arg);

                        break;
                    default:
                        throw new InvalidOperationException($"Unknown rewrite flag '{name}'.");
                }

            return iscond ? CreateCondition(pat, repl, oflags) : CreateRule(pat, repl, oflags);
        }
    }

    /// <summary>
    /// Represents a set of possible HTTP rewrite flags
    /// </summary>
    public abstract class HTTPRewriteFlags
    {
        private HTTPRewriteFlags()
        {       
        }


        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `BNP`-rule ("Backreference: no plus").
        /// </summary>
        public static HTTPRewriteFlags BNP => new Chained();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `C`-rule ("Chained").
        /// </summary>
        public static HTTPRewriteFlags C => new Chained();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `CO`-rule ("Cookie").
        /// </summary>
        /// <param name="name">The cookie's name</param>
        /// <param name="value">The cookie's value</param>
        /// <param name="exprdt">The cookie's expiration time span</param>
        /// <returns>HTTP rewrite flag</returns>
        public static HTTPRewriteFlags CO(string name, string value, TimeSpan exprdt) => new Cookie(name, value, exprdt);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `E`-rule ("Set environment variable").
        /// </summary>
        /// <param name="name">The environment variable's name</param>
        /// <param name="value">The environment variable's value</param>
        /// <returns>HTTP rewrite flag</returns>
        public static HTTPRewriteFlags E(string name, string value) => new EnvironmentVariable(name, value);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `F`-rule ("HTTP:Forbidden").
        /// </summary>
        public static HTTPRewriteFlags F => R(HttpStatusCode.Forbidden);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `G`-rule ("HTTP:Gone").
        /// </summary>
        public static HTTPRewriteFlags G => R(HttpStatusCode.Gone);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `L`-rule ("Last rule").
        /// </summary>
        public static HTTPRewriteFlags L => new Last();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `N`-rule ("Next rule").
        /// </summary>
        /// <param name="count">The number of maximum iterations</param>
        public static HTTPRewriteFlags N(int count = 32000) => new Next(count);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `NC`-rule ("Ignore case").
        /// </summary>
        public static HTTPRewriteFlags NC => new CaseInsensitive();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `NE`-rule ("Don't escape").
        /// </summary>
        public static HTTPRewriteFlags NE => new DontEscape();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `NQ`-rule ("Ignore query string").
        /// </summary>
        public static HTTPRewriteFlags NQ => new IgnoreQueryString();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `R`-rule ("Return code/Status code").
        /// </summary>
        /// <param name="code"></param>
        /// <returns>HTTP rewrite flag</returns>
        public static HTTPRewriteFlags R(HttpStatusCode code = HttpStatusCode.TemporaryRedirect) => new ReturnCode(code);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `QSA`-rule ("Append query string").
        /// </summary>
        public static HTTPRewriteFlags QSA => new AppendQueryString();
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `S`-rule ("Skip following N rules").
        /// </summary>
        /// <param name="count">Number of rules to be skipped</param>
        /// <returns>HTTP rewrite flag</returns>
        public static HTTPRewriteFlags S(int count) => new Skip(count);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `T`-rule ("MIME-Type").
        /// </summary>
        /// <param name="type">The MIME type to be returned</param>
        /// <returns>HTTP rewrite flag</returns>
        public static HTTPRewriteFlags T(string type) => new MimeType(type);
        /// <summary>
        /// Represents the <code>mod_rewrite</code>'s `SS`-rule ("Server string").
        /// </summary>
        /// <param name="server_str">The server string to be returned</param>
        /// <returns>HTTP rewrite flag</returns>
        public static HTTPRewriteFlags SS(string server_str) => new ServerString(server_str);

        /// <summary>
        /// Concatenates an existing collection of flags to the given single flag and returns the created collection
        /// </summary>
        /// <param name="fs">Flags</param>
        /// <param name="f">Single flag</param>
        /// <returns>Resulting flag array</returns>
        public static HTTPRewriteFlags[] operator |(HTTPRewriteFlags[] fs, HTTPRewriteFlags f) => fs.Concat(new[] { f }).ToArray();

        /// <summary>
        /// Implicitly converts a single flag to an array of flags
        /// </summary>
        /// <param name="f">Single flag</param>
        /// <returns>Array of flags</returns>
        public static implicit operator HTTPRewriteFlags[] (HTTPRewriteFlags f) => new[] { f };

#pragma warning disable CS0659

        /// <inheritdoc cref="HTTPRewriteFlags.BNP"/>
        public sealed class DontUsePlus
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is DontUsePlus;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.C"/>
        public sealed class Chained
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is Chained;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.CO"/>
        public sealed class Cookie
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The cookie's name
            /// </summary>
            public string Name { get; }
            /// <summary>
            /// The cookie's value
            /// </summary>
            public string Value { get; }
            /// <summary>
            /// The cookie's expiration time span
            /// </summary>
            public TimeSpan ExpirationDate { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="name">The cookie's name</param>
            /// <param name="value">The cookie's value</param>
            /// <param name="exprdt">The cookie's expiration time span</param>
            public Cookie(string name, string value, TimeSpan exprdt)
            {
                Name = name;
                Value = value;
                ExpirationDate = exprdt;
            }

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is Cookie c && c.Name == Name && c.Value == Value;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.E"/>
        public sealed class EnvironmentVariable
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The environment variable's name
            /// </summary>
            public string Name { get; }
            /// <summary>
            /// The environment variable's value
            /// </summary>
            public string Value { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="name">Name</param>
            /// <param name="value">Value</param>
            public EnvironmentVariable(string name, string value)
            {
                Name = name;
                Value = value;
            }

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is EnvironmentVariable ev && ev.Name == Name && ev.Value == Value;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.L"/>
        public sealed class Last
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is Last;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.N"/>
        public sealed class Next
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The maximum iteration count
            /// </summary>
            public int IterationCount { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="iterationCount">The maximum iteration count</param>
            public Next(int iterationCount) => IterationCount = iterationCount;

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is Next;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.NC"/>
        public sealed class CaseInsensitive
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is CaseInsensitive;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.NE"/>
        public sealed class DontEscape
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is DontEscape;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.NQ"/>
        public sealed class IgnoreQueryString
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is IgnoreQueryString;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.R"/>
        public sealed class ReturnCode
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The HTTP status code to be returned
            /// </summary>
            public HttpStatusCode StatusCode { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="code">The HTTP status code to be returned</param>
            public ReturnCode(HttpStatusCode code) => StatusCode = code;

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is ReturnCode rc && rc.StatusCode == StatusCode;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.QSA"/>
        public sealed class AppendQueryString
            : HTTPRewriteFlags
        {
            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is AppendQueryString;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.S"/>
        public sealed class Skip
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The amount of following rules to skip
            /// </summary>
            public int Count { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="count">The amount of following rules to skip</param>
            public Skip(int count) => Count = count;

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is Skip s && s.Count == Count;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.SS"/>
        public sealed class ServerString
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The server string to be returned
            /// </summary>
            public string String { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="server_str">The server string to be returned</param>
            public ServerString(string server_str) => String = server_str;

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is ServerString ss && ss.String == String;
        }

        /// <inheritdoc cref="HTTPRewriteFlags.T"/>
        public sealed class MimeType
            : HTTPRewriteFlags
        {
            /// <summary>
            /// The MIME-type to be returned
            /// </summary>
            public string Type { get; }


            /// <summary>
            /// Creates a new instance
            /// </summary>
            /// <param name="type">The MIME-type to be returned</param>
            public MimeType(string type) => Type = type?.ToLower() ?? "text/plain";

            /// <inheritdoc/>
            public override bool Equals(object obj) => obj is MimeType m && m.Type == Type;
        }

#pragma warning restore CS0659
    }
}
