using System.Collections.Generic;
using System.IO;
using System;

using NetFwTypeLib;
using System.Security.Cryptography.X509Certificates;

namespace WWS.Internals
{
    internal static class Util
    {
        internal static string Sys32Path { get; } = Environment.ExpandEnvironmentVariables("%systemroot%/system32");


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
