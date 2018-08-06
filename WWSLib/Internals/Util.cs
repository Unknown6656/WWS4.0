using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System;

using NetFwTypeLib;

namespace WWS.Internals
{
    internal static class Util
    {
        internal static string Sys32Path { get; } = Environment.ExpandEnvironmentVariables("%systemroot%/system32");


        internal static string BytesToString(this long sz)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

            if (sz == 0)
                return "0" + suf[0];

            long bytes = Math.Abs(sz);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));

            return Math.Sign(sz) * Math.Round(bytes / Math.Pow(1024, place), 1) + suf[place];
        }

        internal static string GetHash(this X509Certificate cert) => cert is null ? "" : cert.GetCertHashString();

        internal static byte[] ToBytes(this Stream s)
        {
            if (s is null)
                return new byte[0];

            byte[] arr = new byte[s.Length];

            s.Position = 0;
            s.Read(arr, 0, arr.Length);

            return arr;
        }

        internal static async Task<byte[]> ToBytesAsync(this Stream s)
        {
            if (s is null)
                return new byte[0];

            s.Position = 0;

            byte[] arr = new byte[s.Length];

            await s.ReadAsync(arr, 0, arr.Length);

            return arr;
        }

        internal static Dictionary<string, string> DecomposeQueryString(string query)
        {
            Dictionary<string, string> dic = new Dictionary<string, string>();

            if (query is string s)
            {
                if (s.StartsWith("?"))
                    s = s.Length > 1 ? s.Substring(1) : "";
                
                s += '&';

                while (s.Contains("&"))
                {
                    int idx_amp = s.IndexOf('&');
                    string subq = s.Remove(idx_amp);
                    int idx_eq = subq.IndexOf('=');

                    if (idx_eq >= 0)
                        dic[Uri.UnescapeDataString(subq.Remove(idx_eq))] = Uri.UnescapeDataString(subq.Substring(idx_eq + 1));
                    else if (subq.Length > 0)
                        dic[Uri.UnescapeDataString(subq)] = "";

                    s = s.Substring(idx_amp + 1);
                }

                if (s.Length > 0)
                    dic[Uri.UnescapeDataString(s)] = "";
            }

            return dic;
        }

        internal static void TimeoutRetry(Action action, int timeout, Action<Exception> handler = null)
        {
            Stopwatch time = Stopwatch.StartNew();

            while (time.ElapsedMilliseconds < timeout)
                try
                {
                    action();

                    return;
                }
                catch (Exception e)
                {
                    handler?.Invoke(e);
                }

            throw new TimeoutException("Failed perform action within allowed time.");
        }

        internal static bool Match(this string s, string p, out Match m, RegexOptions o = RegexOptions.Compiled | RegexOptions.IgnoreCase) => (m = Regex.Match(s, p, o)).Success;

        internal static bool Match(this string s, string p, out GroupCollection c, RegexOptions o = RegexOptions.Compiled | RegexOptions.IgnoreCase)
        {
            bool r = s.Match(p, out Match m, o);

            c = m.Groups;

            return r;
        }
    }

    internal static class FirewallUtils
    {
        private static INetFwProfile _profile;

        private static Dictionary<string, string> GUIDS { get; } = new Dictionary<string, string>
        {
            ["INetFwMgr"] = "{304CE942-6E39-40D8-943A-B913C40C9CD4}",
            ["INetAuthApp"] = "{EC9846B3-2762-4A6B-A214-6ACB603462D2}",
            ["INetOpenPort"] = "{0CA545C6-37AD-4A6C-BF92-9F7610067EF5}",
        };


        internal static bool IsPortOpen(int port)
        {
            EnsureSetup();

            if (Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwMgr")) is INetFwMgr fw)
                foreach (object curr in fw.LocalPolicy.CurrentProfile.GloballyOpenPorts)
                    if (curr is INetFwOpenPort p && p.Port == port)
                        return true;

            return false;
        }

        internal static void OpenPort(int port, string appname)
        {
            EnsureSetup();

            if (!IsPortOpen(port) && GetInstance("INetOpenPort") is INetFwOpenPort fwport)
            {
                fwport.Port = port;
                fwport.Enabled = true;
                fwport.Name = appname;
                fwport.Scope = NET_FW_SCOPE_.NET_FW_SCOPE_ALL;
                fwport.Protocol = NET_FW_IP_PROTOCOL_.NET_FW_IP_PROTOCOL_TCP;
                fwport.IpVersion = NET_FW_IP_VERSION_.NET_FW_IP_VERSION_ANY;

                _profile.GloballyOpenPorts.Add(fwport);
            }
        }

        internal static void ClosePort(int port)
        {
            EnsureSetup();

            if (IsPortOpen(port))
                _profile.GloballyOpenPorts.Remove(port, NET_FW_IP_PROTOCOL_.NET_FW_IP_PROTOCOL_TCP);
        }

        private static void EnsureSetup()
        {
            if (_profile is null)
                _profile = (GetInstance("INetFwMgr") as INetFwMgr)?.LocalPolicy?.CurrentProfile;
        }

        private static object GetInstance(string name) => Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(GUIDS[name])));
    }
}
