using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace OddJobs.Lockbox
{
    /// <summary>Password handling that keeps copies of the secret in memory short-lived.</summary>
    public static class Secret
    {
        /// <summary>UTF-8 bytes of a SecureString. The caller must wipe the result with LockboxFormat.Wipe.</summary>
        public static byte[] ToUtf8(SecureString secure)
        {
            if (secure == null || secure.Length == 0) return new byte[0];
            IntPtr ptr = IntPtr.Zero;
            var chars = new char[secure.Length];
            try
            {
                ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
                Marshal.Copy(ptr, chars, 0, chars.Length);
                return Encoding.UTF8.GetBytes(chars);
            }
            finally
            {
                Array.Clear(chars, 0, chars.Length);
                if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }

        public static bool Equal(SecureString a, SecureString b)
        {
            var x = ToUtf8(a);
            var y = ToUtf8(b);
            try { return LockboxFormat.FixedTimeEquals(x, y); }
            finally
            {
                LockboxFormat.Wipe(x);
                LockboxFormat.Wipe(y);
            }
        }

        /// <summary>Rough strength in bits, from length and the kinds of characters used.</summary>
        public static double EstimateBits(SecureString secure)
        {
            var bytes = ToUtf8(secure);
            try
            {
                string s = Encoding.UTF8.GetString(bytes);
                if (s.Length == 0) return 0;
                int pool = 0;
                bool lower = false, upper = false, digit = false, symbol = false, other = false;
                foreach (char c in s)
                {
                    if (c >= 'a' && c <= 'z') lower = true;
                    else if (c >= 'A' && c <= 'Z') upper = true;
                    else if (c >= '0' && c <= '9') digit = true;
                    else if (c < 128) symbol = true;
                    else other = true;
                }
                if (lower) pool += 26;
                if (upper) pool += 26;
                if (digit) pool += 10;
                if (symbol) pool += 33;
                if (other) pool += 100;

                // Repeated characters add little; count distinct characters more than length.
                int distinct = 0;
                var seen = new System.Collections.Generic.HashSet<char>();
                foreach (char c in s) if (seen.Add(char.ToLowerInvariant(c))) distinct++;
                double effectiveLength = Math.Min(s.Length, distinct * 1.5 + 1);
                return effectiveLength * Math.Log(Math.Max(pool, 2), 2);
            }
            finally
            {
                LockboxFormat.Wipe(bytes);
            }
        }

        const string Alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        /// <summary>A random password like "k7Qm2-xP9rT-..." (about 115 bits), avoiding look-alike characters.</summary>
        public static string Generate(int groups = 4, int groupLength = 5)
        {
            var sb = new StringBuilder();
            using (var rng = RandomNumberGenerator.Create())
            {
                var buffer = new byte[4];
                for (int g = 0; g < groups; g++)
                {
                    if (g > 0) sb.Append('-');
                    for (int i = 0; i < groupLength; i++)
                    {
                        // Rejection sampling keeps every character equally likely.
                        uint limit = uint.MaxValue - (uint.MaxValue % (uint)Alphabet.Length);
                        uint value;
                        do
                        {
                            rng.GetBytes(buffer);
                            value = BitConverter.ToUInt32(buffer, 0);
                        } while (value >= limit);
                        sb.Append(Alphabet[(int)(value % (uint)Alphabet.Length)]);
                    }
                }
            }
            return sb.ToString();
        }
    }
}
