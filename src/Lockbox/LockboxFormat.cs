using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OddJobs.IO;

namespace OddJobs.Lockbox
{
    public sealed class WrongPasswordException : Exception
    {
        public WrongPasswordException() : base("Wrong password, or the file was changed or damaged.") { }
    }

    /// <summary>
    /// The .lockbox file format, version 1.
    ///
    ///   offset size  field
    ///   0      8     magic "LOCKBOX" + 0x01
    ///   8      1     key derivation: 1 = PBKDF2-HMAC-SHA256
    ///   9      4     iterations (uint32, little endian)
    ///   13     16    salt (random)
    ///   29     16    IV (random)
    ///   45     1     flags: bit 0 = the payload is a zip of a folder
    ///   46     2     reserved, zero
    ///   48     ...   AES-256-CBC (PKCS#7) ciphertext of: [uint16 name length][name, UTF-8][contents]
    ///   end-32 32    HMAC-SHA256 over everything before it
    ///
    /// The password is stretched with PBKDF2 into a 32-byte master key; separate encryption and MAC keys
    /// are derived from it with HMAC. The MAC is checked over the whole file before a single byte is
    /// decrypted, so a wrong password or a tampered file never produces output. The original file name
    /// is inside the encrypted part, so it is hidden too.
    /// </summary>
    public static class LockboxFormat
    {
        public const string Extension = ".lockbox";
        public const int DefaultIterations = 600_000;
        const int HeaderSize = 48;
        const int MacSize = 32;
        const int MinIterations = 100_000;
        const int MaxIterations = 20_000_000;
        static readonly byte[] Magic = { (byte)'L', (byte)'O', (byte)'C', (byte)'K', (byte)'B', (byte)'O', (byte)'X', 0x01 };

        public static bool IsLockbox(string path)
        {
            if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var head = new byte[8];
                    return fs.Length >= HeaderSize + 16 + MacSize && fs.Read(head, 0, 8) == 8 && head.SequenceEqual(Magic);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // ---------- Locking -------------------------------------------------------------

        /// <summary>Encrypts a file or folder into <paramref name="outputPath"/> and proves it decrypts correctly.</summary>
        public static void Lock(string sourcePath, string outputPath, byte[] password, int iterations, Action<double> progress, CancellationToken ct)
        {
            bool isFolder = Directory.Exists(sourcePath);
            string name = Path.GetFileName(sourcePath.TrimEnd('\\'));

            var salt = Random(16);
            var iv = Random(16);
            var header = new byte[HeaderSize];
            Buffer.BlockCopy(Magic, 0, header, 0, 8);
            header[8] = 1;
            BitConverter.GetBytes((uint)iterations).CopyTo(header, 9);
            Buffer.BlockCopy(salt, 0, header, 13, 16);
            Buffer.BlockCopy(iv, 0, header, 29, 16);
            header[45] = (byte)(isFolder ? 1 : 0);

            byte[] encKey = null, macKey = null;
            string temp = TempNextTo(outputPath);
            byte[] plainHash;
            try
            {
                DeriveKeys(password, salt, iterations, out encKey, out macKey);
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var hmac = new HMACSHA256(macKey))
                using (var aes = NewAes())
                using (var plainSha = Hashing.Sha256())
                {
                    file.Write(header, 0, header.Length);
                    hmac.TransformBlock(header, 0, header.Length, null, 0);

                    var mac = new MacStream(file, hmac);
                    using (var crypto = new CryptoStream(mac, aes.CreateEncryptor(encKey, iv), CryptoStreamMode.Write))
                    using (var hashing = new HashingStream(crypto, plainSha))
                    {
                        var nameBytes = Encoding.UTF8.GetBytes(name);
                        if (nameBytes.Length > 1024) nameBytes = Encoding.UTF8.GetBytes(name.Substring(0, 200));
                        hashing.Write(BitConverter.GetBytes((ushort)nameBytes.Length), 0, 2);
                        hashing.Write(nameBytes, 0, nameBytes.Length);

                        if (isFolder) WriteFolderZip(sourcePath, hashing, progress, ct);
                        else CopyWithProgress(sourcePath, hashing, progress, ct);
                        hashing.Flush();
                        crypto.FlushFinalBlock();
                    }
                    plainSha.TransformFinalBlock(new byte[0], 0, 0);
                    plainHash = plainSha.Hash;
                    hmac.TransformFinalBlock(new byte[0], 0, 0);
                    file.Write(hmac.Hash, 0, MacSize);
                    file.Flush(true);
                }

                // Read the file back and make sure it decrypts to exactly what went in before calling it done.
                byte[] check = DecryptToHash(temp, encKey, macKey, ct);
                if (!FixedTimeEquals(check, plainHash))
                    throw new IOException("The locked file did not read back correctly, so it was not saved. The disk may be failing.");

                File.Move(temp, outputPath);
            }
            finally
            {
                Wipe(encKey);
                Wipe(macKey);
                TryDelete(temp);
            }
        }

        static void WriteFolderZip(string folder, Stream into, Action<double> progress, CancellationToken ct)
        {
            var files = FastDir.WalkFiles(folder, true, () => ct.IsCancellationRequested).ToList();
            long total = Math.Max(1, files.Sum(f => f.Entry.Size)), done = 0;
            // The zip's internal offsets must count from the start of the zip, not from the start of the payload.
            using (var zip = new ZipArchive(new ZipOutput(into), ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, entry) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = PathUtil.Relative(folder, path).Replace('\\', '/');
                    var zipEntry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                    zipEntry.LastWriteTime = new DateTimeOffset(entry.LastWriteLocal);
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                    using (var output = zipEntry.Open())
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            done += read;
                            progress?.Invoke((double)done / total);
                        }
                    }
                }
            }
        }

        static void CopyWithProgress(string path, Stream into, Action<double> progress, CancellationToken ct)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                long total = Math.Max(1, input.Length), done = 0;
                var buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    into.Write(buffer, 0, read);
                    done += read;
                    progress?.Invoke((double)done / total);
                }
            }
        }

        // ---------- Unlocking -----------------------------------------------------------

        /// <summary>Decrypts a .lockbox file into <paramref name="outputFolder"/>. Returns the path that was created.</summary>
        public static string Unlock(string lockboxPath, string outputFolder, byte[] password, Action<double> progress, CancellationToken ct)
        {
            var header = ReadHeader(lockboxPath, out int iterations, out var salt, out var iv, out bool isFolder);
            byte[] encKey = null, macKey = null;
            string temp = TempNextTo(Path.Combine(outputFolder, "unlock"));
            try
            {
                DeriveKeys(password, salt, iterations, out encKey, out macKey);
                VerifyMac(lockboxPath, header, macKey, p => progress?.Invoke(p * 0.4), ct);

                string name;
                using (var file = new FileStream(lockboxPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var aes = NewAes())
                {
                    long cipherLength = file.Length - HeaderSize - MacSize;
                    file.Position = HeaderSize;
                    var limited = new SubStream(file, cipherLength);
                    using (var crypto = new CryptoStream(limited, aes.CreateDecryptor(encKey, iv), CryptoStreamMode.Read))
                    {
                        var lengthBytes = ReadExactly(crypto, 2);
                        int nameLength = BitConverter.ToUInt16(lengthBytes, 0);
                        name = Encoding.UTF8.GetString(ReadExactly(crypto, nameLength));
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                        {
                            var buffer = new byte[1 << 20];
                            long done = 0;
                            int read;
                            while ((read = crypto.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                                done += read;
                                progress?.Invoke(0.4 + 0.6 * done / Math.Max(1, cipherLength));
                            }
                            output.Flush(true);
                        }
                    }
                }

                // The name came from inside the encrypted file; still treat it as untrusted.
                string safeName = PathUtil.SanitizeFileName(Path.GetFileName(name.Replace('/', '\\')));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = Path.GetFileNameWithoutExtension(lockboxPath);

                if (!isFolder)
                {
                    string target = PathUtil.UniquePath(outputFolder, safeName);
                    File.Move(temp, target);
                    return target;
                }

                string folderTarget = PathUtil.UniquePath(outputFolder, safeName);
                ExtractSafely(temp, folderTarget, ct);
                return folderTarget;
            }
            finally
            {
                Wipe(encKey);
                Wipe(macKey);
                TryDelete(temp);
            }
        }

        /// <summary>Unzips without letting any entry escape the target folder ("zip slip").</summary>
        internal static void ExtractSafely(string zipPath, string targetFolder, CancellationToken ct)
        {
            Directory.CreateDirectory(targetFolder);
            string root = PathUtil.Normalize(targetFolder) + "\\";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('/', '\\').TrimStart('\\');
                    if (relative.Length == 0) continue;
                    string full = Path.GetFullPath(Path.Combine(targetFolder, relative));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.EndsWith("\\")) { Directory.CreateDirectory(full); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    entry.ExtractToFile(full, false);
                }
            }
        }

        static byte[] DecryptToHash(string path, byte[] encKey, byte[] macKey, CancellationToken ct)
        {
            var header = ReadHeader(path, out _, out _, out var iv, out _);
            VerifyMac(path, header, macKey, null, ct);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var aes = NewAes())
            using (var sha = Hashing.Sha256())
            {
                file.Position = HeaderSize;
                var limited = new SubStream(file, file.Length - HeaderSize - MacSize);
                using (var crypto = new CryptoStream(limited, aes.CreateDecryptor(encKey, iv), CryptoStreamMode.Read))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = crypto.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        static byte[] ReadHeader(string path, out int iterations, out byte[] salt, out byte[] iv, out bool isFolder)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < HeaderSize + 16 + MacSize) throw new InvalidDataException("This is not a Lockbox file, or it is damaged.");
                var header = ReadExactly(fs, HeaderSize);
                if (!header.Take(8).SequenceEqual(Magic))
                    throw new InvalidDataException(header.Take(7).SequenceEqual(Magic.Take(7))
                        ? "This file was made by a newer version of Lockbox."
                        : "This is not a Lockbox file.");
                if (header[8] != 1) throw new InvalidDataException("Unknown key derivation in this file.");
                long iter = BitConverter.ToUInt32(header, 9);
                if (iter < MinIterations || iter > MaxIterations) throw new InvalidDataException("This file's settings are outside safe limits, so it was not opened.");
                iterations = (int)iter;
                salt = header.Skip(13).Take(16).ToArray();
                iv = header.Skip(29).Take(16).ToArray();
                isFolder = (header[45] & 1) != 0;
                return header;
            }
        }

        static void VerifyMac(string path, byte[] header, byte[] macKey, Action<double> progress, CancellationToken ct)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var hmac = new HMACSHA256(macKey))
            {
                long macStart = fs.Length - MacSize;
                long cipherLength = macStart - HeaderSize;
                if (cipherLength <= 0 || cipherLength % 16 != 0) throw new WrongPasswordException();
                hmac.TransformBlock(header, 0, HeaderSize, null, 0);
                fs.Position = HeaderSize;
                var buffer = new byte[1 << 20];
                long left = cipherLength;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) throw new WrongPasswordException();
                    hmac.TransformBlock(buffer, 0, read, null, 0);
                    left -= read;
                    progress?.Invoke(1 - (double)left / cipherLength);
                }
                hmac.TransformFinalBlock(new byte[0], 0, 0);
                var stored = ReadExactly(fs, MacSize);
                if (!FixedTimeEquals(hmac.Hash, stored)) throw new WrongPasswordException();
            }
        }

        // ---------- Keys --------------------------------------------------------------

        public static void DeriveKeys(byte[] password, byte[] salt, int iterations, out byte[] encKey, out byte[] macKey)
        {
            var master = Pbkdf2Sha256(password, salt, iterations, 32);
            try
            {
                using (var h = new HMACSHA256(master))
                {
                    encKey = h.ComputeHash(Encoding.ASCII.GetBytes("lockbox v1 encryption\x01"));
                    macKey = h.ComputeHash(Encoding.ASCII.GetBytes("lockbox v1 authentication\x01"));
                }
            }
            finally
            {
                Wipe(master);
            }
        }

        /// <summary>PBKDF2-HMAC-SHA256 using Windows' native implementation, with a managed fallback.</summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            var output = new byte[length];
            IntPtr alg = IntPtr.Zero;
            try
            {
                if (BCryptOpenAlgorithmProvider(out alg, "SHA256", null, BCRYPT_ALG_HANDLE_HMAC_FLAG) == 0 &&
                    BCryptDeriveKeyPBKDF2(alg, password, password.Length, salt, salt.Length, (ulong)iterations, output, output.Length, 0) == 0)
                    return output;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally
            {
                if (alg != IntPtr.Zero) BCryptCloseAlgorithmProvider(alg, 0);
            }
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }

        const uint BCRYPT_ALG_HANDLE_HMAC_FLAG = 0x8;

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, string pszAlgId, string pszImplementation, uint dwFlags);

        [DllImport("bcrypt.dll")]
        static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

        [DllImport("bcrypt.dll")]
        static extern int BCryptDeriveKeyPBKDF2(IntPtr hPrf, byte[] pbPassword, int cbPassword, byte[] pbSalt, int cbSalt,
            ulong cIterations, byte[] pbDerivedKey, int cbDerivedKey, uint dwFlags);

        // ---------- Helpers -------------------------------------------------------------

        static Aes NewAes()
        {
            var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes;
        }

        static byte[] Random(int n)
        {
            var bytes = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static void Wipe(byte[] data)
        {
            if (data != null) Array.Clear(data, 0, data.Length);
        }

        static byte[] ReadExactly(Stream s, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = s.Read(buffer, offset, count - offset);
                if (n <= 0) throw new InvalidDataException("The file ends too early.");
                offset += n;
            }
            return buffer;
        }

        static string TempNextTo(string path) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "." + Guid.NewGuid().ToString("N").Substring(0, 10) + ".lockbox-tmp");

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Passes writes through to a stream while feeding them to an HMAC.</summary>
        sealed class MacStream : Stream
        {
            readonly Stream _inner;
            readonly HMAC _mac;
            public MacStream(Stream inner, HMAC mac) { _inner = inner; _mac = mac; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _mac.TransformBlock(buffer, offset, count, null, 0);
                _inner.Write(buffer, offset, count);
            }
            public override void Flush() => _inner.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { /* the file stream is owned by the caller */ }
        }

        /// <summary>Hashes plaintext on its way into the cipher, so the result can be verified afterwards.</summary>
        sealed class HashingStream : Stream
        {
            readonly Stream _inner;
            readonly HashAlgorithm _hash;
            long _written;
            public HashingStream(Stream inner, HashAlgorithm hash) { _inner = inner; _hash = hash; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _hash.TransformBlock(buffer, offset, count, null, 0);
                _inner.Write(buffer, offset, count);
                _written += count;
            }
            public override void Flush() { }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            // The zip writer asks how far it has written even on streams it can't seek.
            public override long Position { get => _written; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { /* the cipher stream is owned by the caller */ }
        }

        /// <summary>Write-only pass-through that reports how many bytes went through it, for the zip writer.</summary>
        sealed class ZipOutput : Stream
        {
            readonly Stream _inner;
            long _written;
            public ZipOutput(Stream inner) { _inner = inner; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _written += count;
            }
            public override void Flush() => _inner.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _written; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { }
        }

        /// <summary>A read-only window onto part of another stream.</summary>
        sealed class SubStream : Stream
        {
            readonly Stream _inner;
            long _left;
            public SubStream(Stream inner, long length) { _inner = inner; _left = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) return 0;
                int n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= n;
                return n;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { }
        }
    }
}
