using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OddJobs.IO
{
    /// <summary>
    /// Tiny per-app preferences file in %LOCALAPPDATA%\OddJobs\&lt;App&gt;\settings.txt.
    /// Only remembers choices (last folder, options). Never stores file contents or passwords.
    /// </summary>
    public sealed class Settings
    {
        readonly string _path;
        readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string DataFolder(string app) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OddJobs", app);

        public Settings(string app)
        {
            _path = Path.Combine(DataFolder(app), "settings.txt");
            try
            {
                if (!File.Exists(_path)) return;
                foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) _values[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public string Get(string key, string fallback = null) => _values.TryGetValue(key, out var v) ? v : fallback;
        public bool GetBool(string key, bool fallback) => _values.TryGetValue(key, out var v) ? v == "1" : fallback;
        public int GetInt(string key, int fallback) => _values.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : fallback;

        public void Set(string key, string value)
        {
            if (value == null) _values.Remove(key);
            else _values[key] = value;
        }
        public void Set(string key, bool value) => Set(key, value ? "1" : "0");
        public void Set(string key, int value) => Set(key, value.ToString());

        public void Save()
        {
#if SCREENSHOT
            return;   // screenshot runs use demo folders; never let them overwrite real preferences
#endif
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var sb = new StringBuilder();
                foreach (var pair in _values) sb.Append(pair.Key).Append('=').Append(Escape(pair.Value)).Append('\n');
                AtomicFile.WriteAllText(_path, sb.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

        static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' ? '\n' : n == 'r' ? '\r' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }

    public static class AtomicFile
    {
        /// <summary>Writes to a temporary file next to the target, then swaps it in, so a crash never leaves half a file.</summary>
        public static void Write(string path, Action<Stream> write)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            string temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    write(fs);
                    fs.Flush(true);
                }
                if (!File.Exists(path)) File.Move(temp, path);
                else
                {
                    try { File.Replace(temp, path, null, true); }
                    catch (IOException) { File.Copy(temp, path, true); }          // e.g. FAT drives, network shares
                    catch (PlatformNotSupportedException) { File.Copy(temp, path, true); }
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public static void WriteAllText(string path, string text) =>
            Write(path, s =>
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            });
    }
}
