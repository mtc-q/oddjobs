using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OddJobs.Binder;
using OddJobs.IO;
using OddJobs.Lockbox;
using OddJobs.Relabel;
using OddJobs.Scrub;
using OddJobs.Untangle;
using OddJobs.Verity;

namespace OddJobs.Tests
{
    static class Images
    {
        /// <summary>A JPEG with camera, date, GPS and an EXIF orientation, like a phone photo.</summary>
        public static string PhonePhoto(TempDir dir, string name, int width = 64, int height = 32, ushort orientation = 1)
        {
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, Enumerable.Repeat((byte)128, width * height * 3).ToArray(), width * 3);
            var meta = new BitmapMetadata("jpg");
            meta.SetQuery("/app1/ifd/{ushort=271}", "Apple");
            meta.SetQuery("/app1/ifd/{ushort=272}", "iPhone 16");
            meta.SetQuery("/app1/ifd/{ushort=274}", orientation);
            meta.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:05:02 09:14:00");
            ulong R(uint n, uint d) => ((ulong)d << 32) | n;
            meta.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
            meta.SetQuery("/app1/ifd/gps/{ushort=2}", new[] { R(38, 1), R(42, 1), R(0, 1) });
            meta.SetQuery("/app1/ifd/gps/{ushort=3}", "W");
            meta.SetQuery("/app1/ifd/gps/{ushort=4}", new[] { R(9, 1), R(8, 1), R(2400, 100) });
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bmp, null, meta, null));
            string path = Path.Combine(dir.Path, name);
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        public static string Png(TempDir dir, string name, int width, int height, bool withText)
        {
            var pixels = new byte[width * height * 4];
            // Left half fully transparent, right half solid green.
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 10; pixels[i + 1] = 200; pixels[i + 2] = 30; pixels[i + 3] = (byte)((i / 4) % width < width / 2 ? 0 : 255); }
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            BitmapMetadata meta = null;
            if (withText)
            {
                meta = new BitmapMetadata("png");
                meta.SetQuery("/tEXt/{str=Author}", "Someone");
            }
            encoder.Frames.Add(BitmapFrame.Create(bmp, null, meta, null));
            string path = Path.Combine(dir.Path, name);
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
    }

    static class ScrubTests
    {
        [Test]
        static void Finds_location_and_device()
        {
            using (var dir = new TempDir())
            {
                var report = PhotoCleaner.Inspect(Images.PhonePhoto(dir, "a.jpg"));
                Assert.Equal(null, report.Error);
                Assert.True(report.HasLocation, "location found");
                Assert.True(Math.Abs(report.Latitude.Value - 38.7) < 0.001, "latitude " + report.Latitude);
                Assert.True(report.Longitude.Value < -9.13 && report.Longitude.Value > -9.14, "west is negative: " + report.Longitude);
                Assert.True(report.Camera != null && report.Camera.Contains("iPhone"), "device");
                Assert.True(report.Blocks.Contains("EXIF / XMP"), "EXIF block listed");
            }
        }

        [Test]
        static void Clean_copy_has_pixels_only_and_is_upright()
        {
            using (var dir = new TempDir())
            {
                string photo = Images.PhonePhoto(dir, "sideways.jpg", 40, 20, orientation: 6);
                string output = PhotoCleaner.Clean(photo, Path.Combine(dir.Path, "Cleaned"), new CleanOptions(), false, out _);
                Assert.Equal(null, PhotoCleaner.Verify(output), "verified clean at byte level");
                var report = PhotoCleaner.Inspect(output);
                Assert.True(!report.HasLocation && report.Camera == null && report.Taken == null, "nothing left");
                Assert.Equal(20, report.Width, "rotated: width");
                Assert.Equal(40, report.Height, "rotated: height");
                Assert.True(File.Exists(photo), "original untouched");
            }
        }

        [Test]
        static void Png_text_chunks_are_removed_and_resize_works()
        {
            using (var dir = new TempDir())
            {
                string png = Images.Png(dir, "shot.png", 300, 100, withText: true);
                Assert.True(PhotoCleaner.Verify(png) != null, "test image really has a text chunk");
                string output = PhotoCleaner.Clean(png, Path.Combine(dir.Path, "Cleaned"), new CleanOptions { MaxEdge = 150 }, false, out _);
                Assert.Equal(".png", Path.GetExtension(output));
                Assert.Equal(null, PhotoCleaner.Verify(output));
                var report = PhotoCleaner.Inspect(output);
                Assert.Equal(150, report.Width);
                Assert.Equal(50, report.Height);
            }
        }

        [Test]
        static void Verify_catches_leftover_metadata()
        {
            using (var dir = new TempDir())
            {
                Assert.True(PhotoCleaner.Verify(Images.PhonePhoto(dir, "dirty.jpg")) != null, "dirty JPEG is flagged");
            }
        }
    }

    static class UntangleTests
    {
        [Test]
        static void Fix_line_breaks_rejoins_sentences_and_words()
        {
            string text = "The quick start guide explains how to set up the de-\nvice in under ten minutes.\nBefore you begin\n\n- first item\n- second item\nmore text";
            string result = new FixLineBreaksStep().Apply(text);
            Assert.Equal("The quick start guide explains how to set up the device in under ten minutes. Before you begin\n\n- first item\n- second item more text", result);
        }

        [Test]
        static void Line_operations()
        {
            Assert.Equal("a\nB", new DedupeStep().Apply("a\nB\nA\nb"));
            Assert.Equal("file2\nfile10", new SortStep().Apply("file10\nfile2"));
            Assert.Equal("c\nb\na", new ReverseStep().Apply("a\nb\nc"));
            Assert.Equal("a\nb", new RemoveEmptyLinesStep().Apply("a\n   \n\nb"));
            Assert.Equal("1. a\n\n2. b", new NumberStep().Apply("a\n\nb"));
            Assert.Equal("a, b, c", new JoinStep().Apply("a\nb\n\nc"));
            Assert.Equal("a\nb\nc", new SplitStep { Separator = ";" }.Apply("a; b ;c"));
            Assert.Equal("'x'\n'y'", new AffixStep { Prefix = "'", Suffix = "'" }.Apply("x\ny"));
            Assert.Equal("keep me", new FilterStep { Text = "keep" }.Apply("keep me\ndrop me"));
        }

        [Test]
        static void Character_cleanups()
        {
            Assert.Equal("\"Hi\" - it's... fine", new StraightenStep().Apply("“Hi” — it’s… fine"));
            Assert.Equal("a b", new InvisibleStep().Apply("a​ b­"));
            Assert.Equal("a b c", new CollapseSpacesStep().Apply("a   b\t\tc"));
            Assert.Equal("Hello world. Next one", new CaseStep { Mode = 3 }.Apply("HELLO WORLD. NEXT ONE"));
            Assert.Equal("Tom & Jerry <3", new StripHtmlStep().Apply("<p>Tom &amp; <b>Jerry</b> &lt;3</p><script>alert(1)</script>"));
        }

        [Test]
        static void Replace_and_extract()
        {
            Assert.Equal("cost: $5", new ReplaceStep { Find = "price", Replace = "cost" }.Apply("price: $5"));
            Assert.Equal("2026-05-01", new ReplaceStep { Find = @"(\d+)/(\d+)/(\d+)", Replace = "$3-$2-$1", UseRegex = true }.Apply("01/05/2026"));
            Assert.Equal("a@b.co\nme@x.org", new ExtractStep().Apply("mail a@b.co or me@x.org."));
            Assert.Equal("https://example.com/x", new ExtractStep { Mode = 1 }.Apply("see https://example.com/x, thanks"));
        }
    }

    static class VerityTests
    {
        [Test]
        static void Known_digests()
        {
            using (var dir = new TempDir())
            {
                string path = dir.File("abc.txt", "abc");
                var h = FileHasher.Hash(path, Hashing.Names, null, CancellationToken.None);
                Assert.Equal("900150983cd24fb0d6963f7d28e17f72", h["MD5"]);
                Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", h["SHA-1"]);
                Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h["SHA-256"]);
                Assert.True(h["SHA-512"].StartsWith("ddaf35a193617aba"), "SHA-512");
            }
        }

        [Test]
        static void Accepts_what_people_paste()
        {
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                Hashing.NormalizeDigest(" BA78 16BF 8F01 CFEA 4141 40DE 5DAE 2223 B003 61A3 9617 7A9C B410 FF61 F200 15AD ", out var alg));
            Assert.Equal("SHA-256", alg);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                Hashing.NormalizeDigest("sha256-ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=", out alg));
            Assert.Equal(null, Hashing.NormalizeDigest("not a hash", out _));
            Assert.True(Hashing.SameDigest("ABCDEF", "abcdef") && !Hashing.SameDigest("abcdef", "abcdee"), "compare");
        }

        [Test]
        static void Parses_checksum_lists()
        {
            using (var dir = new TempDir())
            {
                string sums = dir.File("SHA256SUMS",
                    "# comment\n" +
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  sub/a.txt\n" +
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *b.bin\n" +
                    "SHA256 (c d.iso) = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\n" +
                    "900150983cd24fb0d6963f7d28e17f72  ../outside.txt\n");
                var list = ChecksumList.Parse(sums);
                Assert.Equal(4, list.Count);
                Assert.Equal(Path.Combine(dir.Path, "sub", "a.txt"), list[0].Path);
                Assert.Equal("b.bin", list[1].Name);
                Assert.Equal("c d.iso", list[2].Name);
                Assert.Equal("MD5", list[3].Algorithm);
                Assert.Equal(null, list[3].Path, "paths outside the folder are refused");
                Assert.True(ChecksumList.LooksLikeList(sums) && ChecksumList.LooksLikeList("x.zip.sha256") && !ChecksumList.LooksLikeList("photo.jpg"), "detection");
            }
        }
    }

    static class BinderTests
    {
        [Test]
        static void Pdf_structure_is_valid_and_carries_no_photo_metadata()
        {
            using (var dir = new TempDir())
            {
                string jpg = Images.PhonePhoto(dir, "upright.jpg", 80, 60);
                string png = Images.Png(dir, "screen.png", 50, 40, withText: false);
                string turned = Images.PhonePhoto(dir, "turned.jpg", 80, 60, orientation: 6);
                var options = new BindOptions();
                var pages = new[] { PageMaker.Make(jpg, 0, options), PageMaker.Make(png, 0, options), PageMaker.Make(turned, 0, options) };
                Assert.True(pages[0].Image.IsJpeg, "upright JPEG passes through");
                Assert.True(!pages[1].Image.IsJpeg, "PNG stays lossless");
                Assert.Equal(60, pages[2].Image.Width, "EXIF orientation applied");
                Assert.True(pages[0].Width > pages[0].Height, "landscape page for landscape photo");

                string pdf = Path.Combine(dir.Path, "out.pdf");
                using (var fs = File.Create(pdf)) PdfWriter.Write(fs, pages);
                var bytes = File.ReadAllBytes(pdf);
                string text = Encoding.GetEncoding(28591).GetString(bytes);

                Assert.True(text.StartsWith("%PDF-1.4"), "header");
                Assert.True(text.TrimEnd().EndsWith("%%EOF"), "trailer");
                Assert.True(text.Contains("/Count 3"), "page count");
                Assert.True(!text.Contains("Exif") && !text.Contains("iPhone"), "no EXIF inside the PDF");

                int startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
                Assert.True(text.Substring(startxref).StartsWith("xref"), "startxref points at xref");
                var entries = Regex.Matches(text.Substring(startxref), @"(\d{10}) 00000 n\r\n");
                Assert.Equal(3 + 3 * 3, entries.Count, "xref entries");
                int number = 1;
                foreach (Match e in entries)
                {
                    int offset = int.Parse(e.Groups[1].Value);
                    Assert.True(text.Substring(offset).StartsWith(number + " 0 obj"), "object " + number + " offset");
                    number++;
                }
            }
        }

        [Test]
        static void Zlib_stream_round_trips()
        {
            var raw = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
            var z = PdfWriter.Zlib(raw);
            Assert.Equal(0x78, (int)z[0]);
            using (var ms = new MemoryStream(z, 2, z.Length - 6))
            using (var inflate = new DeflateStream(ms, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                inflate.CopyTo(output);
                Assert.True(output.ToArray().SequenceEqual(raw), "decompresses to the input");
            }
        }

        [Test]
        static void Jpeg_metadata_is_stripped_losslessly()
        {
            using (var dir = new TempDir())
            {
                var original = File.ReadAllBytes(Images.PhonePhoto(dir, "p.jpg"));
                var stripped = JpegInfo.StripMetadata(original);
                Assert.True(stripped.Length < original.Length, "smaller");
                Assert.True(!Encoding.ASCII.GetString(stripped).Contains("Exif"), "no EXIF");
                Assert.True(JpegInfo.TryRead(stripped, out var info) && info.Width == 64 && info.Height == 32, "still a valid JPEG");
                using (var ms = new MemoryStream(stripped))
                    Assert.Equal(64, BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0].PixelWidth, "decodes");
            }
        }
    }

    static class LockboxTests
    {
        static byte[] Pw(string s) => Encoding.UTF8.GetBytes(s);
        static string Hex(byte[] b) => Hashing.Hex(b);

        [Test]
        static void Pbkdf2_matches_published_test_vectors()
        {
            var salt = Encoding.ASCII.GetBytes("salt");
            Assert.Equal("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", Hex(LockboxFormat.Pbkdf2Sha256(Pw("password"), salt, 1, 32)));
            Assert.Equal("ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43", Hex(LockboxFormat.Pbkdf2Sha256(Pw("password"), salt, 2, 32)));
            Assert.Equal("c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a", Hex(LockboxFormat.Pbkdf2Sha256(Pw("password"), salt, 4096, 32)));
        }

        [Test]
        static void File_round_trip_hides_the_name()
        {
            using (var dir = new TempDir())
            {
                var content = new byte[300_123];
                new Random(3).NextBytes(content);
                string source = dir.Bytes("tax return 2026.pdf", content);
                string locked = Path.Combine(dir.Path, "x" + LockboxFormat.Extension);
                LockboxFormat.Lock(source, locked, Pw("correct horse"), 100_000, null, CancellationToken.None);
                Assert.True(LockboxFormat.IsLockbox(locked), "recognised");
                Assert.True(!Encoding.UTF8.GetString(File.ReadAllBytes(locked)).Contains("tax return"), "name is encrypted");

                string outDir = dir.Sub("out");
                string restored = LockboxFormat.Unlock(locked, outDir, Pw("correct horse"), null, CancellationToken.None);
                Assert.Equal("tax return 2026.pdf", Path.GetFileName(restored));
                Assert.True(File.ReadAllBytes(restored).SequenceEqual(content), "identical bytes");
            }
        }

        [Test]
        static void Wrong_password_and_tampering_produce_nothing()
        {
            using (var dir = new TempDir())
            {
                string source = dir.File("note.txt", "the secret");
                string locked = Path.Combine(dir.Path, "note.txt.lockbox");
                LockboxFormat.Lock(source, locked, Pw("right password"), 100_000, null, CancellationToken.None);
                string outDir = dir.Sub("out");

                Assert.Throws<WrongPasswordException>(() => LockboxFormat.Unlock(locked, outDir, Pw("wrong password"), null, CancellationToken.None), "wrong password");

                var bytes = File.ReadAllBytes(locked);
                bytes[60] ^= 1;   // flip one bit of ciphertext
                File.WriteAllBytes(locked, bytes);
                Assert.Throws<WrongPasswordException>(() => LockboxFormat.Unlock(locked, outDir, Pw("right password"), null, CancellationToken.None), "tampered body");

                bytes[60] ^= 1;
                bytes[13] ^= 1;   // flip a bit of the salt in the header
                File.WriteAllBytes(locked, bytes);
                Assert.Throws<WrongPasswordException>(() => LockboxFormat.Unlock(locked, outDir, Pw("right password"), null, CancellationToken.None), "tampered header");

                Assert.Equal(0, Directory.GetFiles(outDir).Length, "no output, not even temp files");
            }
        }

        [Test]
        static void Folder_and_empty_file_round_trip()
        {
            using (var dir = new TempDir())
            {
                dir.File("Project/readme.md", "# hi");
                dir.File("Project/src/main.c", "int main(){}");
                dir.File("Project/empty.txt", "");
                string locked = Path.Combine(dir.Path, "Project.lockbox");
                LockboxFormat.Lock(Path.Combine(dir.Path, "Project"), locked, Pw("pw-123456789"), 100_000, null, CancellationToken.None);
                string outDir = dir.Sub("out");
                string folder = LockboxFormat.Unlock(locked, outDir, Pw("pw-123456789"), null, CancellationToken.None);
                Assert.Equal("int main(){}", File.ReadAllText(Path.Combine(folder, "src", "main.c")));
                Assert.Equal("", File.ReadAllText(Path.Combine(folder, "empty.txt")));

                string empty = dir.File("zero.bin", "");
                string lockedEmpty = Path.Combine(dir.Path, "zero.lockbox");
                LockboxFormat.Lock(empty, lockedEmpty, Pw("pw-123456789"), 100_000, null, CancellationToken.None);
                Assert.Equal(0L, new FileInfo(LockboxFormat.Unlock(lockedEmpty, dir.Sub("out2"), Pw("pw-123456789"), null, CancellationToken.None)).Length);
            }
        }

        [Test]
        static void Zip_entries_cannot_escape_the_folder()
        {
            using (var dir = new TempDir())
            {
                string zipPath = Path.Combine(dir.Path, "evil.zip");
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) w.Write("bad");
                    using (var w = new StreamWriter(zip.CreateEntry("ok/fine.txt").Open())) w.Write("good");
                }
                string target = Path.Combine(dir.Path, "target");
                LockboxFormat.ExtractSafely(zipPath, target, CancellationToken.None);
                Assert.True(!File.Exists(Path.Combine(dir.Path, "escaped.txt")), "escape blocked");
                Assert.Equal("good", File.ReadAllText(Path.Combine(target, "ok", "fine.txt")));
            }
        }

        [Test]
        static void Rejects_absurd_iteration_counts()
        {
            using (var dir = new TempDir())
            {
                string source = dir.File("a.txt", "a");
                string locked = Path.Combine(dir.Path, "a.lockbox");
                LockboxFormat.Lock(source, locked, Pw("password-1234"), 100_000, null, CancellationToken.None);
                var bytes = File.ReadAllBytes(locked);
                BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 9);
                File.WriteAllBytes(locked, bytes);
                Assert.Throws<InvalidDataException>(() => LockboxFormat.Unlock(locked, dir.Path, Pw("password-1234"), null, CancellationToken.None), "would take forever");
            }
        }

        [Test]
        static void Generated_passwords_are_strong_and_unique()
        {
            var a = Secret.Generate();
            var b = Secret.Generate();
            Assert.True(a != b, "unique");
            Assert.True(Regex.IsMatch(a, "^[A-Za-z2-9]{5}(-[A-Za-z2-9]{5}){3}$"), "format: " + a);
            Assert.True(!Regex.IsMatch(a, "[0O1lI]"), "no look-alikes");
        }
    }

    /// <summary>The apps do heavy work on background (MTA) threads; WPF imaging and the shell must cope.</summary>
    static class BackgroundThreadTests
    {
        static T OnPoolThread<T>(Func<T> work)
        {
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                Assert.True(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "really a background thread");
                return work();
            });
            return task.GetAwaiter().GetResult();
        }

        [Test]
        static void Scrub_flattens_transparency_off_the_ui_thread()
        {
            using (var dir = new TempDir())
            {
                string png = Images.Png(dir, "logo.png", 80, 40, withText: true);
                string output = OnPoolThread(() => PhotoCleaner.Clean(png, Path.Combine(dir.Path, "Cleaned"), new CleanOptions { AlwaysJpeg = true }, false, out _));
                Assert.Equal(".jpg", Path.GetExtension(output));
                Assert.Equal(null, PhotoCleaner.Verify(output));
                using (var fs = File.OpenRead(output))
                {
                    var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    var px = new byte[4];
                    new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), px, 4, 0);
                    Assert.True(px[0] > 230 && px[1] > 230 && px[2] > 230, "transparent pixel became white, not black");
                }
            }
        }

        [Test]
        static void Binder_makes_pages_off_the_ui_thread()
        {
            using (var dir = new TempDir())
            {
                string png = Images.Png(dir, "screen.png", 60, 30, withText: false);
                string jpg = Images.PhonePhoto(dir, "p.jpg", 40, 20, orientation: 3);
                var pages = OnPoolThread(() => new[] { PageMaker.Make(png, 90, new BindOptions()), PageMaker.Make(jpg, 0, new BindOptions { Compact = true }) });
                Assert.Equal(30, pages[0].Image.Width, "rotated by the user");
                Assert.True(pages[1].Image.IsJpeg, "compact mode re-encodes");
            }
        }

        /// <summary>Opt-in (set ODDJOBS_TEST_RECYCLE=1): it really puts a small file in your Recycle Bin.</summary>
        [Test]
        static void Recycle_bin_works_from_any_thread()
        {
            if (Environment.GetEnvironmentVariable("ODDJOBS_TEST_RECYCLE") != "1") return;
            using (var dir = new TempDir())
            {
                string a = dir.File("oddjobs-recycle-check-1.txt", "a");
                string b = dir.File("oddjobs-recycle-check-2.txt", "b");
                Assert.Equal(0, RecycleBin.Send(new[] { a }, IntPtr.Zero).Count, "from the test (STA) thread");
                Assert.Equal(0, OnPoolThread(() => RecycleBin.Send(new[] { b }, IntPtr.Zero)).Count, "from a pool (MTA) thread");
                Assert.True(!File.Exists(a) && !File.Exists(b), "both gone from the folder");
            }
        }
    }

    static class RelabelPhotoTests
    {
        [Test]
        static void Reads_date_taken()
        {
            using (var dir = new TempDir())
            {
                var date = PhotoDate.TryRead(Images.PhonePhoto(dir, "p.jpg"));
                Assert.Equal(new DateTime(2026, 5, 2, 9, 14, 0), date);
                Assert.Equal(null, PhotoDate.TryRead(dir.File("note.txt")));
            }
        }
    }
}
