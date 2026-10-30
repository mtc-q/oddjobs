using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using OddJobs.Footprint;
using OddJobs.IO;
using OddJobs.Parity;
using OddJobs.Relabel;
using OddJobs.Tidy;
using OddJobs.Twins;

namespace OddJobs.Tests
{
    static class RelabelTests
    {
        static List<RenameItem> Items(TempDir dir, params string[] names) =>
            names.Select((n, i) =>
            {
                string p = dir.File(n, n);
                FastDir.TryGetEntry(p, out var e);
                return new RenameItem(p, e, i);
            }).ToList();

        [Test]
        static void Template_counter_and_case()
        {
            using (var dir = new TempDir())
            {
                var items = Items(dir, "IMG_2.JPG", "IMG_10.JPG", "IMG_1.JPG");
                var rules = new RenameRules { Template = "Trip {n:3}", Extension = ExtMode.Lower };
                var ordered = RenamePlanner.Sort(items, rules).ToList();
                Assert.Equal(null, RenamePlanner.Plan(ordered, rules));
                Assert.Equal("IMG_1.JPG", ordered[0].Name, "natural sort first");
                Assert.Equal("Trip 001.jpg", ordered[0].NewName);
                Assert.Equal("Trip 002.jpg", ordered[1].NewName);
                Assert.Equal("Trip 003.jpg", ordered[2].NewName);
                Assert.True(ordered.All(i => i.State == ItemState.Ready), "all ready");
            }
        }

        [Test]
        static void Find_replace_literal_and_regex()
        {
            using (var dir = new TempDir())
            {
                var items = Items(dir, "Report final (1).docx", "notes $5.txt");
                var rules = new RenameRules { Find = @" \(\d+\)", Replace = "", UseRegex = true };
                RenamePlanner.Plan(items, rules);
                Assert.Equal("Report final.docx", items[0].NewName);

                rules = new RenameRules { Find = "$5", Replace = "five", UseRegex = false };
                RenamePlanner.Plan(items, rules);
                Assert.Equal("notes five.txt", items[1].NewName, "literal $ handled");

                rules = new RenameRules { Find = "(", UseRegex = true };
                Assert.True(RenamePlanner.Plan(items, rules) != null, "bad regex reported");
            }
        }

        [Test]
        static void Conflicts_and_invalid_names_are_blocked()
        {
            using (var dir = new TempDir())
            {
                var items = Items(dir, "a.txt", "b.txt");
                dir.File("taken.txt");
                RenamePlanner.Plan(items, new RenameRules { Template = "same" });
                Assert.True(items.All(i => i.State == ItemState.Problem), "duplicate targets");

                RenamePlanner.Plan(items, new RenameRules { Template = "x:{name}" });
                Assert.True(items.All(i => i.State == ItemState.Problem), "colon is invalid");

                RenamePlanner.Plan(items, new RenameRules { Template = "CON" });
                Assert.True(items.All(i => i.State == ItemState.Problem), "reserved name");

                var one = new List<RenameItem> { items[0] };
                RenamePlanner.Plan(one, new RenameRules { Template = "taken" });
                Assert.Equal(ItemState.Problem, one[0].State, "existing file on disk");
            }
        }

        [Test]
        static void Swap_names_in_one_batch()
        {
            using (var dir = new TempDir())
            {
                string a = dir.File("a.txt", "A"), b = dir.File("b.txt", "B");
                var runner = new RenameRunner(dir.Sub("journal"));
                var outcome = runner.Run(new List<RenameRunner.Move> { new RenameRunner.Move { From = a, To = b }, new RenameRunner.Move { From = b, To = a } });
                Assert.Equal(null, outcome.Error);
                Assert.Equal("B", File.ReadAllText(a));
                Assert.Equal("A", File.ReadAllText(b));
            }
        }

        [Test]
        static void Case_only_rename()
        {
            using (var dir = new TempDir())
            {
                string p = dir.File("photo.JPG");
                var runner = new RenameRunner(dir.Sub("journal"));
                runner.Run(new List<RenameRunner.Move> { new RenameRunner.Move { From = p, To = Path.Combine(dir.Path, "photo.jpg") } });
                Assert.Equal("photo.jpg", Path.GetFileName(Directory.GetFiles(dir.Path, "photo.*")[0]));
            }
        }

        [Test]
        static void Failure_rolls_everything_back()
        {
            using (var dir = new TempDir())
            {
                string a = dir.File("a.txt", "A"), b = dir.File("b.txt", "B"), c = dir.File("c.txt", "C");
                var runner = new RenameRunner(dir.Sub("journal"));
                using (new FileStream(c, FileMode.Open, FileAccess.Read, FileShare.None))   // c can't be moved while open
                {
                    var outcome = runner.Run(new List<RenameRunner.Move>
                    {
                        new RenameRunner.Move { From = a, To = Path.Combine(dir.Path, "1.txt") },
                        new RenameRunner.Move { From = b, To = Path.Combine(dir.Path, "2.txt") },
                        new RenameRunner.Move { From = c, To = Path.Combine(dir.Path, "3.txt") },
                    });
                    Assert.True(outcome.RolledBack, "rolled back");
                }
                Assert.Equal("A", File.ReadAllText(a));
                Assert.Equal("B", File.ReadAllText(b));
                Assert.Equal("C", File.ReadAllText(c));
                Assert.Equal(3, Directory.GetFiles(dir.Path).Length, "no temp files left");
            }
        }

        [Test]
        static void Undo_restores_previous_names()
        {
            using (var dir = new TempDir())
            {
                string a = dir.File("a.txt", "A");
                var runner = new RenameRunner(dir.Sub("journal"));
                string to = Path.Combine(dir.Path, "renamed.txt");
                runner.Run(new List<RenameRunner.Move> { new RenameRunner.Move { From = a, To = to } });
                Assert.True(File.Exists(to), "renamed");
                var undo = runner.UndoLast(out int skipped);
                Assert.Equal(1, undo.Renamed);
                Assert.Equal(0, skipped);
                Assert.True(File.Exists(a) && !File.Exists(to), "back to original");
            }
        }

        [Test]
        static void Interrupted_rename_is_recovered()
        {
            using (var dir = new TempDir())
            {
                string journal = dir.Sub("journal");
                string a = dir.File("a.txt", "A");
                string temp = Path.Combine(dir.Path, ".relabel-x-0.tmp");
                File.Move(a, temp);   // as if the app died between the two phases
                File.WriteAllText(Path.Combine(journal, "pending.txt"), a + "\t" + temp + "\t" + Path.Combine(dir.Path, "new.txt"));
                int restored = new RenameRunner(journal).RecoverInterrupted();
                Assert.Equal(1, restored);
                Assert.Equal("A", File.ReadAllText(a));
            }
        }
    }

    static class TwinsTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFile, string existing, IntPtr security);

        [Test]
        static void Finds_true_duplicates_only()
        {
            using (var dir = new TempDir())
            {
                var big = new byte[100_000];
                new Random(1).NextBytes(big);
                var bigTwin = (byte[])big.Clone();
                var bigImpostor = (byte[])big.Clone();
                bigImpostor[50_000] ^= 0xFF;   // same size, same first and last 16 KB, different middle

                dir.Bytes("one/big.bin", big);
                dir.Bytes("two/big copy.bin", bigTwin);
                dir.Bytes("two/impostor.bin", bigImpostor);
                dir.File("one/small.txt", "hello world");
                dir.File("two/small-copy.txt", "hello world");
                dir.File("two/other.txt", "hello wor1d");   // same size, different content
                dir.File("one/empty1.txt", "");
                dir.File("two/empty2.txt", "");
                string original = dir.File("one/linked.txt", "linked content");
                Assert.True(CreateHardLink(Path.Combine(dir.Path, "two", "hardlink.txt"), original, IntPtr.Zero), "hard link created");

                var result = DuplicateScanner.Scan(new ScanOptions { Folders = { dir.Path, Path.Combine(dir.Path, "one") } }, null, CancellationToken.None);
                Assert.Equal(2, result.Groups.Count, "groups");
                var bigGroup = result.Groups.Single(g => g.Size == 100_000);
                Assert.Equal(2, bigGroup.Files.Count, "impostor excluded");
                Assert.True(bigGroup.Files.All(f => !f.Path.Contains("impostor")), "impostor excluded");
                var smallGroup = result.Groups.Single(g => g.Size == 11);
                Assert.Equal(2, smallGroup.Files.Count, "small pair");
                Assert.Equal(100_000L, result.Groups.Sum(g => g.Wasted) - 11, "wasted");
            }
        }

        [Test]
        static void Nested_roots_are_not_double_counted()
        {
            var roots = DuplicateScanner.DistinctRoots(new[] { @"C:\a\b", @"C:\a", @"C:\a\b\c", @"C:\z" });
            Assert.Equal(2, roots.Count);
        }
    }

    static class FootprintTests
    {
        [Test]
        static void Sizes_add_up_and_small_files_are_grouped()
        {
            using (var dir = new TempDir())
            {
                for (int i = 0; i < 50; i++) dir.File("many/f" + i + ".txt", new string('x', i + 1));
                dir.File("deep/a/b/c.txt", new string('y', 1000));
                var stats = new ScanStats();
                var root = DiskScanner.Scan(dir.Path, stats, CancellationToken.None);
                long expected = Enumerable.Range(1, 50).Sum() + 1000;
                Assert.Equal(expected, root.Size, "total size");
                Assert.Equal(51L, root.FileCount, "file count");
                var many = root.Children.Single(c => c.Name == "many");
                Assert.True(many.Children.Count <= 41 && many.Children.Any(c => c.IsAggregate), "aggregated small files");
                Assert.Equal(Enumerable.Range(1, 50).Sum(), (int)many.Children.Sum(c => c.Size), "aggregate keeps the bytes");
            }
        }

        [Test]
        static void Treemap_tiles_fill_the_area_without_overlap()
        {
            var parent = new Node { IsDir = true };
            var sizes = new long[] { 600, 300, 200, 100, 50, 40, 9, 1 };
            parent.Children = sizes.Select(s => new Node { Size = s, Parent = parent }).ToList();
            var bounds = new System.Windows.Rect(0, 0, 400, 300);
            var tiles = Treemap.Layout(parent.Children, bounds);
            Assert.Equal(sizes.Length, tiles.Count, "one tile per item");
            double total = sizes.Sum();
            foreach (var t in tiles)
            {
                double expectedArea = bounds.Width * bounds.Height * t.Node.Size / total;
                Assert.True(Math.Abs(t.Rect.Width * t.Rect.Height - expectedArea) < 0.5, "area proportional to size");
                Assert.True(t.Rect.Left >= -0.001 && t.Rect.Top >= -0.001 && t.Rect.Right <= 400.001 && t.Rect.Bottom <= 300.001, "inside bounds");
            }
            for (int i = 0; i < tiles.Count; i++)
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    var overlap = System.Windows.Rect.Intersect(tiles[i].Rect, tiles[j].Rect);
                    Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 0.01, "no overlap");
                }
        }

        [Test]
        static void Detach_updates_every_parent()
        {
            var root = new Node { IsDir = true, Size = 100, FileCount = 2 };
            var folder = new Node { IsDir = true, Parent = root, Size = 60, FileCount = 1 };
            var file = new Node { Parent = folder, Size = 60, FileCount = 1 };
            folder.Children = new List<Node> { file };
            root.Children = new List<Node> { folder, new Node { Parent = root, Size = 40, FileCount = 1 } };
            file.Detach();
            Assert.Equal(0L, folder.Size);
            Assert.Equal(40L, root.Size);
            Assert.Equal(1L, root.FileCount);
        }
    }

    static class TidyTests
    {
        [Test]
        static void Plans_by_type_and_skips_what_it_should()
        {
            using (var dir = new TempDir())
            {
                var old = DateTime.Now.AddDays(-10);
                dir.File("report.pdf", "p", old);
                dir.File("photo.JPG", "j", old);
                dir.File("setup.exe", "e", old);
                dir.File("song.mp3", "m", old);
                dir.File("weird.xyz", "w", old);
                dir.File("fresh.pdf", "f", DateTime.Now);
                dir.File("big.iso.crdownload", "c", old);
                string hidden = dir.File("secret.txt", "h", old);
                File.SetAttributes(hidden, FileAttributes.Hidden);
                dir.File("Documents/report.pdf", "already there", old);   // forces a unique name
                dir.Sub("a folder");

                var plan = TidyPlanner.Plan(dir.Path, new TidyOptions { SkipRecentDays = 1 }, DateTime.Now, out int recent, out int other);
                Assert.Equal(5, plan.Count, "moves");
                Assert.Equal(1, recent, "recent skipped");
                Assert.Equal(2, other, "partial download and hidden file skipped");
                Assert.Equal(Path.Combine(dir.Path, "Documents", "report (2).pdf"), plan.Single(m => m.From.EndsWith("report.pdf")).To, "unique name");
                Assert.Equal("Images", plan.Single(m => m.From.EndsWith("photo.JPG")).Group);
                Assert.Equal("Other", plan.Single(m => m.From.EndsWith("weird.xyz")).Group);
            }
        }

        [Test]
        static void Run_and_undo_round_trip()
        {
            using (var dir = new TempDir())
            {
                var old = DateTime.Now.AddDays(-40);
                dir.File("a.pdf", "a", old);
                dir.File("b.png", "b", old);
                var plan = TidyPlanner.Plan(dir.Path, new TidyOptions { Grouping = Grouping.ByTypeAndMonth, SkipRecentDays = 0 }, DateTime.Now, out _, out _);
                var runner = new TidyRunner(dir.Sub(".data"));
                var (moved, errors) = runner.Run(dir.Path, plan);
                Assert.Equal(2, moved);
                Assert.Equal(0, errors.Count);
                string month = old.ToString("yyyy-MM");
                Assert.True(File.Exists(Path.Combine(dir.Path, "Documents", month, "a.pdf")), "moved into type/month");

                var history = runner.History();
                Assert.Equal(1, history.Count);
                var (restored, skipped) = runner.Undo(history[0]);
                Assert.Equal(2, restored);
                Assert.Equal(0, skipped);
                Assert.True(File.Exists(Path.Combine(dir.Path, "a.pdf")) && File.Exists(Path.Combine(dir.Path, "b.png")), "back in place");
                Assert.True(!Directory.Exists(Path.Combine(dir.Path, "Documents")) && !Directory.Exists(Path.Combine(dir.Path, "Images")), "empty folders removed");
                Assert.Equal(0, runner.History().Count, "journal cleared");
            }
        }
    }

    static class ParityTests
    {
        [Test]
        static void Classifies_with_fat_and_dst_tolerance()
        {
            var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            DirEntry E(long size, DateTime when) => new DirEntry { Size = size, LastWriteUtcTicks = when.ToFileTimeUtc() };
            Assert.Equal(DiffKind.Same, FolderComparer.Classify(E(10, t), E(10, t.AddSeconds(2))), "FAT 2 s steps");
            Assert.Equal(DiffKind.Same, FolderComparer.Classify(E(10, t), E(10, t.AddHours(1))), "DST hour on FAT");
            Assert.Equal(DiffKind.LeftNewer, FolderComparer.Classify(E(10, t.AddDays(1)), E(10, t)));
            Assert.Equal(DiffKind.RightNewer, FolderComparer.Classify(E(12, t), E(10, t.AddMinutes(5))));
            Assert.Equal(DiffKind.Different, FolderComparer.Classify(E(12, t), E(10, t)));
        }

        [Test]
        static void Update_right_copies_and_never_deletes()
        {
            using (var dir = new TempDir())
            {
                var old = DateTime.Now.AddDays(-30);
                string left = dir.Sub("left"), right = dir.Sub("right");
                dir.File("left/same.txt", "same", old);
                dir.File("right/same.txt", "same", old);
                dir.File("left/new/only-left.txt", "L", old);
                dir.File("right/only-right.txt", "R", old);
                dir.File("left/changed.txt", "new version", DateTime.Now.AddDays(-1));
                dir.File("right/changed.txt", "old", old);

                var items = FolderComparer.Compare(left, right, new CompareOptions(), null, CancellationToken.None);
                var ops = items.Select(i => Syncer.Plan(i, SyncMode.UpdateRight, left, right)).Where(o => o != null).ToList();
                Assert.Equal(2, ops.Count, "copy new + replace changed");
                var (done, errors, recycle) = Syncer.Run(ops, null, CancellationToken.None);
                Assert.Equal(2, done);
                Assert.Equal(0, errors.Count);
                Assert.Equal(0, recycle.Count, "nothing removed");
                Assert.Equal("L", File.ReadAllText(Path.Combine(right, "new", "only-left.txt")));
                Assert.Equal("new version", File.ReadAllText(Path.Combine(right, "changed.txt")));
                Assert.True(File.Exists(Path.Combine(right, "only-right.txt")), "right-only kept");
                Assert.True(Math.Abs((File.GetLastWriteTimeUtc(Path.Combine(right, "changed.txt")) - File.GetLastWriteTimeUtc(Path.Combine(left, "changed.txt"))).TotalSeconds) < 1, "date kept");

                var after = FolderComparer.Compare(left, right, new CompareOptions { CheckContents = true }, null, CancellationToken.None);
                Assert.Equal(1, after.Count(i => i.Kind != DiffKind.Same), "only the right-only file differs now");
            }
        }

        [Test]
        static void Mirror_sets_aside_newer_and_extra_files()
        {
            using (var dir = new TempDir())
            {
                var old = DateTime.Now.AddDays(-30);
                string left = dir.Sub("left"), right = dir.Sub("right");
                dir.File("left/doc.txt", "older", old);
                dir.File("right/doc.txt", "newer on right", DateTime.Now);
                dir.File("right/extra.txt", "extra", old);
                var items = FolderComparer.Compare(left, right, new CompareOptions(), null, CancellationToken.None);
                var ops = items.Select(i => Syncer.Plan(i, SyncMode.MirrorToRight, left, right)).Where(o => o != null).ToList();
                var (done, errors, recycle) = Syncer.Run(ops, null, CancellationToken.None);
                Assert.Equal(0, errors.Count);
                Assert.Equal("older", File.ReadAllText(Path.Combine(right, "doc.txt")));
                Assert.Equal(2, recycle.Count, "extra file and the replaced newer copy go to the Recycle Bin");
                Assert.True(recycle.Any(p => p.Contains("(replaced by Parity)") && File.ReadAllText(p) == "newer on right"), "newer copy preserved for the bin");
            }
        }

        [Test]
        static void Refuses_nested_folders()
        {
            using (var dir = new TempDir())
            {
                string inner = dir.Sub("inner");
                Assert.Throws<InvalidOperationException>(() => FolderComparer.Compare(dir.Path, inner, new CompareOptions(), null, CancellationToken.None), "nested");
            }
        }
    }
}
