using System;
using System.Security.Cryptography;
using System.Text;

namespace OddJobs.IO
{
    /// <summary>
    /// Hash algorithms backed by Windows CNG. On .NET Framework, SHA256.Create() returns a slow
    /// managed implementation; these use the CPU's hardware instructions where available.
    /// </summary>
    public static class Hashing
    {
        public static readonly string[] Names = { "MD5", "SHA-1", "SHA-256", "SHA-384", "SHA-512" };

        public static HashAlgorithm Create(string name)
        {
            switch (name)
            {
                case "MD5": return new MD5Cng();
                case "SHA-1": return new SHA1Cng();
                case "SHA-384": return new SHA384Cng();
                case "SHA-512": return new SHA512Cng();
                default: return new SHA256Cng();
            }
        }

        public static HashAlgorithm Sha256() => new SHA256Cng();

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Which algorithm produces a hex digest of this length.</summary>
        public static string GuessFromHexLength(int length)
        {
            switch (length)
            {
                case 32: return "MD5";
                case 40: return "SHA-1";
                case 64: return "SHA-256";
                case 96: return "SHA-384";
                case 128: return "SHA-512";
                default: return null;
            }
        }

        /// <summary>
        /// Accepts what people paste: hex in any case, with spaces, colons or dashes, or the
        /// "sha256-BASE64" form used by web integrity attributes. Returns lowercase hex, or null.
        /// </summary>
        public static string NormalizeDigest(string input, out string algorithm)
        {
            algorithm = null;
            if (string.IsNullOrWhiteSpace(input)) return null;
            string s = input.Trim();

            foreach (var (prefix, name) in new[] { ("sha256-", "SHA-256"), ("sha384-", "SHA-384"), ("sha512-", "SHA-512") })
            {
                if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    algorithm = name;
                    return Hex(Convert.FromBase64String(s.Substring(prefix.Length)));
                }
                catch (FormatException) { return null; }
            }

            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || c == ':' || c == '-') continue;
                if (!Uri.IsHexDigit(c)) return null;
                sb.Append(char.ToLowerInvariant(c));
            }
            algorithm = GuessFromHexLength(sb.Length);
            return algorithm == null ? null : sb.ToString();
        }

        /// <summary>Compares two digests in constant time.</summary>
        public static bool SameDigest(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
            return diff == 0;
        }
    }
}
