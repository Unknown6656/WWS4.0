using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Linq;
using System.IO;

using WWS.Internals;


[assembly: Guid(CertificateInstaller.GUID)]

namespace WWS.Internals
{
    /// <summary>
    /// Manages cryptographic certificates required for an HTTPS-server
    /// </summary>
    public static class CertificateInstaller
    {
        /// <summary>
        /// This assembly's GUID
        /// </summary>
        public const string GUID = "2B0FA332-A7EF-40BE-BC3E-886D6AFCE62C";


        /// <summary>
        /// Checks whether the given .X509 certificate store contains the given .X509 certificate and returns the check result
        /// </summary>
        /// <param name="store">The .X509 store</param>
        /// <param name="path">Path to the certificate's .cer-file</param>
        /// <returns>Check result ('true' -> the certificate store contains the given .X509 certificate)</returns>
        public static bool ContainsCertificate(StoreName store, string path)
        {
            using (X509Certificate cert = X509Certificate.CreateFromCertFile(path))
            using (X509Store s = new X509Store(store, StoreLocation.LocalMachine))
            {
                s.Open(OpenFlags.ReadOnly);

                foreach (X509Certificate2 c in s.Certificates)
                    if (c.GetHash() == cert.GetHash())
                        return true;

                return false;
            }
        }

        /// <summary>
        /// Installs the given certificate to the given .X509-store
        /// </summary>
        /// <param name="store">The .X509 store</param>
        /// <param name="path">Path to the certificate's .cer-file</param>
        public static void InstallCertificate(StoreName store, string path)
        {
            using (X509Certificate cert = X509Certificate.CreateFromCertFile(path))
            using (X509Store s = new X509Store(store, StoreLocation.LocalMachine))
            {
                s.Open(OpenFlags.ReadWrite);

                foreach (X509Certificate2 c in s.Certificates)
                    if (c.GetHash() == cert.GetHash())
                        return;

                s.Add(new X509Certificate2(cert));
            }
        }

        /// <summary>
        /// Uninstalls the given certificate from the given .X509-store
        /// </summary>
        /// <param name="store">The .X509 store</param>
        /// <param name="path">Path to the certificate's .cer-file</param>
        public static void UninstallCertificate(StoreName store, string path)
        {
            using (X509Certificate cert = X509Certificate.CreateFromCertFile(path))
                UninstallCertificate(store, cert);
        }

        /// <summary>
        /// Uninstalls the given certificate from the given .X509-store
        /// </summary>
        /// <param name="store">The .X509 store</param>
        /// <param name="cert">The .X509 certificate</param>
        public static void UninstallCertificate(StoreName store, X509Certificate cert)
        {
            using (X509Store s = new X509Store(store, StoreLocation.LocalMachine))
            {
                s.Open(OpenFlags.ReadWrite);

                foreach (X509Certificate2 c in s.Certificates.OfType<X509Certificate2>().ToArray())
                    if (c.GetHash() == cert.GetHash())
                    {
                        s.Remove(c);

                        break;
                    }
            }
        }


        /// <summary>
        /// Tries to bind the given certificate from the given store to the given ip:port and returns whether the operation was successfull
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="port">TCP/UDP Port</param>
        /// <param name="path">Path to the .X509 certificate file</param>
        /// <param name="store">The .X509 certificate store, into which the certificate will be installed if it is not present</param>
        /// <returns>Operation success result (false indicates, that the port has already been bound to an other certificate)</returns>
        public static bool BindCertificatePort(string ip, ushort port, string path, StoreName store) =>
            BindCertificatePort(ip, port, path, store, false);

        /// <summary>
        /// Tries to bind the given certificate from the given store to the given ip:port and returns whether the operation was successfull
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="port">TCP/UDP Port</param>
        /// <param name="cert">The .X509 certificate</param>
        /// <param name="store">The .X509 certificate store, into which the certificate will be installed if it is not present</param>
        /// <returns>Operation success result (false indicates, that the port has already been bound to an other certificate)</returns>
        public static bool BindCertificatePort(string ip, ushort port, X509Certificate cert, StoreName store) =>
            BindCertificatePort(ip, port, cert, store, false);

        /// <summary>
        /// Binds the given certificate from the given store to the given ip:port and returns whether the operation was successfull
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="port">TCP/UDP Port</param>
        /// <param name="cert">The .X509 certificate</param>
        /// <param name="store">The .X509 certificate store, into which the certificate will be installed if it is not present</param>
        /// <param name="force">Indicates that current existing port bindings shall be overwritten</param>
        /// <returns>Operation success result (false indicates, that the port has already been bound to an other certificate)</returns>
        public static bool BindCertificatePort(string ip, ushort port, X509Certificate cert, StoreName store, bool force)
        {
            if (!NeedsBinding(ip, port))
                if (force)
                    UnbindPort(ip, port);
                else
                    return false;

            using (Process proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    Verb = "runas",
                    UseShellExecute = false,
                    WorkingDirectory = Util.Sys32Path,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = $@"{Util.Sys32Path}\netsh.exe",
                    Arguments = $@"http add sslcert ipport=""{ip}:{port}"" certhash={cert.GetCertHashString()} appid={{{GUID}}}", //  clientcertnegotiation=enable
                }
            })
            {
                proc.Start();
                proc.WaitForExit();

                return true;
            }
        }

        /// <summary>
        /// Binds the given certificate from the given store to the given ip:port and returns whether the operation was successfull
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="port">TCP/UDP Port</param>
        /// <param name="path">Path to the .X509 certificate file</param>
        /// <param name="store">The .X509 certificate store, into which the certificate will be installed if it is not present</param>
        /// <param name="force">Indicates that current existing port bindings shall be overwritten</param>
        /// <returns>Operation success result (false indicates, that the port has already been bound to an other certificate)</returns>
        public static bool BindCertificatePort(string ip, ushort port, string path, StoreName store, bool force)
        {
            using (X509Certificate cert = X509Certificate.CreateFromCertFile(path))
                return BindCertificatePort(ip, port, cert, store, force);
        }

        /// <summary>
        /// Returns, whether the given ip:port needs any service binding
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="port">TCP/UDP Port</param>
        /// <returns>Service binding requirement</returns>
        public static bool NeedsBinding(string ip, ushort port)
        {
            using (Process nstat = Process.Start(new ProcessStartInfo
            {
                Verb = "runas",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WorkingDirectory = Util.Sys32Path,
                FileName = $@"{Util.Sys32Path}\cmd.exe",
                Arguments = $@"/c ""netstat -o -n -a | find /c ""{ip}:{port}""""",
                WindowStyle = ProcessWindowStyle.Hidden,
            }))
            using (StreamReader cout = nstat.StandardOutput)
            {
                nstat.WaitForExit();

                return !int.TryParse(cout.ReadToEnd(), out int val) || val == 0;
            }
        }

        /// <summary>
        /// Unbinds the given ip:port from any previous certificates
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="port">TCP/UDP Port</param>
        public static void UnbindPort(string ip, ushort port)
        {
            using (Process proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    Verb = "runas",
                    UseShellExecute = false,
                    WorkingDirectory = Util.Sys32Path,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = $@"{Util.Sys32Path}\netsh.exe",
                    Arguments = $@"http delete sslcert ipport=""{ip}:{port}"""
                }
            })
            {
                proc.Start();
                proc.WaitForExit();
            }
        }
    }
}
