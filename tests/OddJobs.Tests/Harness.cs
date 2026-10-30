using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OddJobs.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    sealed class TestAttribute : Attribute { }

    sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    static class Assert
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new AssertException(what);
        }

        public static void Equal<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException((what != null ? what + ": " : "") + "expected <" + expected + "> but got <" + actual + ">");
        }

        public static TException Throws<TException>(Action action, string what) where TException : Exception
        {
            try { action(); }
            catch (TException ex) { return ex; }
            catch (Exception ex) { throw new AssertException(what + ": expected " + typeof(TException).Name + " but got " + ex.GetType().Name + " (" + ex.Message + ")"); }
            throw new AssertException(what + ": expected " + typeof(TException).Name + " but nothing was thrown");
        }
    }

    /// <summary>A fresh folder per test, deleted afterwards.</summary>
    sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oddjobs-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(Path);
        }

        public string File(string relative, string content = "x", DateTime? modified = null)
        {
            string full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            System.IO.File.WriteAllText(full, content);
            if (modified.HasValue) System.IO.File.SetLastWriteTime(full, modified.Value);
            return full;
        }

        public string Bytes(string relative, byte[] content)
        {
            string full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            System.IO.File.WriteAllBytes(full, content);
            return full;
        }

        public string Sub(string relative)
        {
            string full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => args.Length == 0 || args.Any(a => (m.DeclaringType.Name + "." + m.Name).IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.MetadataToken)
                .ToList();

            int failed = 0;
            var total = Stopwatch.StartNew();
            foreach (var test in tests)
            {
                string name = test.DeclaringType.Name + "." + test.Name;
                var watch = Stopwatch.StartNew();
                try
                {
                    test.Invoke(null, null);
                    Console.WriteLine("  pass  {0,-58} {1,6} ms", name, watch.ElapsedMilliseconds);
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    var inner = ex.InnerException;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  FAIL  {0}\n        {1}: {2}", name, inner.GetType().Name, inner.Message);
                    if (!(inner is AssertException)) Console.WriteLine(inner.StackTrace);
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
            Console.WriteLine("{0} tests, {1} failed, {2:0.0} s", tests.Count, failed, total.Elapsed.TotalSeconds);
            return failed == 0 ? 0 : 1;
        }
    }
}
