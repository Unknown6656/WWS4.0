using System.Security;

namespace WWS
{
    /// <summary>
    /// A module containing public utility and extension methods
    /// </summary>
    public static class PublicUtils
    {
        /// <summary>
        /// Returns a secure string from the source string
        /// </summary>
        /// <param name="s">Source string</param>
        /// <returns>Secure string</returns>
        public static SecureString ToSecureString(this string s)
        {
            SecureString res = new SecureString();

            foreach (char c in s ?? "")
                res.AppendChar(c);

            return res;
        }
    }
}
