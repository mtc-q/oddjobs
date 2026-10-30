using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OddJobs.IO;

namespace OddJobs.Untangle
{
    /// <summary>
    /// One transformation in the pipeline. Text is passed between steps with "\n" line endings.
    /// Steps never see the clipboard or the disk; they only turn one string into another.
    /// </summary>
    public abstract class Step : INotifyPropertyChanged
    {
        public abstract string Title { get; }
        public abstract string Hint { get; }
        public abstract string Apply(string text);

        bool _enabled = true;
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value, nameof(Enabled)); }

        string _error;
        public string Error { get => _error; set => Set(ref _error, value, nameof(Error)); }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected static string[] Lines(string text) => text.Split('\n');
        protected static string Join(IEnumerable<string> lines) => string.Join("\n", lines);
    }

    public sealed class FixLineBreaksStep : Step
    {
        public override string Title => "Fix line breaks";
        public override string Hint => "Rejoins lines broken mid-sentence, like text copied from a PDF or email";

        static readonly Regex ListItem = new Regex(@"^\s*([•‣◦⁃∙*\-–]|\d+[.)]|[a-zA-Z][.)])\s+", RegexOptions.Compiled);

        public override string Apply(string text)
        {
            var paragraphs = Regex.Split(text, @"\n[ \t]*\n");
            var result = new List<string>();
            foreach (var p in paragraphs)
            {
                var sb = new StringBuilder();
                foreach (var raw in p.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (sb.Length == 0) { sb.Append(line); continue; }
                    if (ListItem.IsMatch(line)) { sb.Append('\n').Append(line); continue; }
                    int n = sb.Length;
                    bool hyphenated = n >= 2 && sb[n - 1] == '-' && char.IsLetter(sb[n - 2]) && char.IsLower(line[0]);
                    if (hyphenated) sb.Length = n - 1;
                    else sb.Append(' ');
                    sb.Append(line);
                }
                if (sb.Length > 0) result.Add(sb.ToString());
            }
            return string.Join("\n\n", result);
        }
    }

    public sealed class TrimStep : Step
    {
        public override string Title => "Trim spaces";
        public override string Hint => "Removes spaces and tabs at the start and end of every line";
        public override string Apply(string text) => Join(Lines(text).Select(l => l.Trim()));
    }

    public sealed class CollapseSpacesStep : Step
    {
        public override string Title => "Collapse repeated spaces";
        public override string Hint => "Turns runs of spaces and tabs into a single space";
        static readonly Regex Runs = new Regex(@"[ \t ]{2,}", RegexOptions.Compiled);
        public override string Apply(string text) => Runs.Replace(text, " ");
    }

    public sealed class RemoveEmptyLinesStep : Step
    {
        public override string Title => "Remove empty lines";
        public override string Hint => "Deletes blank lines, including ones with only spaces";
        public override string Apply(string text) => Join(Lines(text).Where(l => l.Trim().Length > 0));
    }

    public sealed class DedupeStep : Step
    {
        public override string Title => "Remove duplicate lines";
        public override string Hint => "Keeps the first copy of each line, in order";

        bool _ignoreCase = true;
        public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value, nameof(IgnoreCase)); }

        public override string Apply(string text)
        {
            var seen = new HashSet<string>(IgnoreCase ? StringComparer.CurrentCultureIgnoreCase : StringComparer.Ordinal);
            return Join(Lines(text).Where(l => seen.Add(l.Trim())));
        }
    }

    public sealed class SortStep : Step
    {
        public override string Title => "Sort lines";
        public override string Hint => "Sorts the way people expect: 2 before 10";
        public static readonly string[] Modes = { "A to Z", "Z to A", "Shortest first", "Longest first" };

        int _mode;
        public int Mode { get => _mode; set => Set(ref _mode, value, nameof(Mode)); }

        public override string Apply(string text)
        {
            var lines = Lines(text);
            IEnumerable<string> sorted;
            switch (Mode)
            {
                case 1: sorted = lines.OrderByDescending(l => l, NaturalComparer.Instance); break;
                case 2: sorted = lines.OrderBy(l => l.Length).ThenBy(l => l, NaturalComparer.Instance); break;
                case 3: sorted = lines.OrderByDescending(l => l.Length).ThenBy(l => l, NaturalComparer.Instance); break;
                default: sorted = lines.OrderBy(l => l, NaturalComparer.Instance); break;
            }
            return Join(sorted);
        }
    }

    public sealed class ReverseStep : Step
    {
        public override string Title => "Reverse line order";
        public override string Hint => "Last line first";
        public override string Apply(string text) => Join(Lines(text).Reverse());
    }

    public sealed class CaseStep : Step
    {
        public override string Title => "Change case";
        public override string Hint => "lowercase, UPPERCASE, Title Case or Sentence case";
        public static readonly string[] Modes = { "lowercase", "UPPERCASE", "Title Case", "Sentence case" };

        int _mode;
        public int Mode { get => _mode; set => Set(ref _mode, value, nameof(Mode)); }

        static readonly Regex SentenceStart = new Regex(@"(^\s*|[.!?]\s+)(\p{Ll})", RegexOptions.Compiled | RegexOptions.Multiline);

        public override string Apply(string text)
        {
            var culture = CultureInfo.CurrentCulture;
            switch (Mode)
            {
                case 1: return text.ToUpper(culture);
                case 2: return culture.TextInfo.ToTitleCase(text.ToLower(culture));
                case 3: return SentenceStart.Replace(text.ToLower(culture), m => m.Groups[1].Value + m.Groups[2].Value.ToUpper(culture));
                default: return text.ToLower(culture);
            }
        }
    }

    public sealed class StraightenStep : Step
    {
        public override string Title => "Straighten quotes and dashes";
        public override string Hint => "Turns curly quotes, long dashes and … into plain keyboard characters";

        public override string Apply(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '‘': case '’': case '‚': case '′': sb.Append('\''); break;
                    case '“': case '”': case '„': case '″': case '«': case '»': sb.Append('"'); break;
                    case '–': case '—': case '−': sb.Append('-'); break;
                    case '…': sb.Append("..."); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    public sealed class InvisibleStep : Step
    {
        public override string Title => "Remove invisible characters";
        public override string Hint => "Zero-width spaces, soft hyphens and non-breaking spaces that break searches and code";

        public override string Apply(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '​' || c == '‌' || c == '‍' || c == '⁠' || c == '﻿' || c == '­') continue;
                if (c == ' ' || c == ' ' || c == ' ' || c == ' ' || c == ' ') { sb.Append(' '); continue; }
                if (char.IsControl(c) && c != '\n' && c != '\t') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public sealed class ReplaceStep : Step
    {
        public override string Title => "Find and replace";
        public override string Hint => "Plain text, or a regular expression with $1 for groups";

        string _find = "", _replace = "";
        bool _regex, _matchCase;
        public string Find { get => _find; set => Set(ref _find, value ?? "", nameof(Find)); }
        public string Replace { get => _replace; set => Set(ref _replace, value ?? "", nameof(Replace)); }
        public bool UseRegex { get => _regex; set => Set(ref _regex, value, nameof(UseRegex)); }
        public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value, nameof(MatchCase)); }

        public override string Apply(string text)
        {
            if (Find.Length == 0) return text;
            string find = UseRegex ? Find : Regex.Escape(Find.Replace("\r\n", "\n"));
            string replace = UseRegex ? Unescape(Replace) : Replace.Replace("$", "$$");
            var options = (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Multiline | RegexOptions.CultureInvariant;
            return Regex.Replace(text, find, replace, options, TimeSpan.FromSeconds(2));
        }

        /// <summary>Lets people type \n and \t in the replacement.</summary>
        static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");
    }

    public sealed class FilterStep : Step
    {
        public override string Title => "Keep or remove lines";
        public override string Hint => "Keeps only, or drops, the lines that contain some text";
        public static readonly string[] Modes = { "Keep lines containing", "Remove lines containing" };

        int _mode;
        string _text = "";
        bool _matchCase;
        public int Mode { get => _mode; set => Set(ref _mode, value, nameof(Mode)); }
        public string Text { get => _text; set => Set(ref _text, value ?? "", nameof(Text)); }
        public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value, nameof(MatchCase)); }

        public override string Apply(string text)
        {
            if (Text.Length == 0) return text;
            var cmp = MatchCase ? StringComparison.Ordinal : StringComparison.CurrentCultureIgnoreCase;
            bool keep = Mode == 0;
            return Join(Lines(text).Where(l => (l.IndexOf(Text, cmp) >= 0) == keep));
        }
    }

    public sealed class AffixStep : Step
    {
        public override string Title => "Add text to each line";
        public override string Hint => "Put something before and/or after every non-empty line";

        string _prefix = "", _suffix = "";
        public string Prefix { get => _prefix; set => Set(ref _prefix, value ?? "", nameof(Prefix)); }
        public string Suffix { get => _suffix; set => Set(ref _suffix, value ?? "", nameof(Suffix)); }

        public override string Apply(string text) => Join(Lines(text).Select(l => l.Length == 0 ? l : Prefix + l + Suffix));
    }

    public sealed class NumberStep : Step
    {
        public override string Title => "Number lines";
        public override string Hint => "Adds 1. 2. 3. in front of every non-empty line";

        string _format = "1. ";
        public string Pattern { get => _format; set => Set(ref _format, value ?? "", nameof(Pattern)); }

        public override string Apply(string text)
        {
            int n = 0;
            string pattern = Pattern.Contains("1") ? Pattern : "1" + Pattern;
            int at = pattern.IndexOf('1');
            return Join(Lines(text).Select(l => l.Length == 0 ? l : pattern.Substring(0, at) + (++n) + pattern.Substring(at + 1) + l));
        }
    }

    public sealed class JoinStep : Step
    {
        public override string Title => "Join lines";
        public override string Hint => "Puts every line on one line, with a separator between them";

        string _separator = ", ";
        public string Separator { get => _separator; set => Set(ref _separator, value ?? "", nameof(Separator)); }

        public override string Apply(string text) => string.Join(Separator.Replace("\\t", "\t"), Lines(text).Where(l => l.Length > 0));
    }

    public sealed class SplitStep : Step
    {
        public override string Title => "Split into lines";
        public override string Hint => "Starts a new line at every separator, e.g. commas or semicolons";

        string _separator = ",";
        public string Separator { get => _separator; set => Set(ref _separator, value ?? "", nameof(Separator)); }

        public override string Apply(string text)
        {
            string sep = Separator.Replace("\\t", "\t");
            if (sep.Length == 0) return text;
            return Join(text.Split(new[] { sep }, StringSplitOptions.None).Select(p => p.Trim()));
        }
    }

    public sealed class ExtractStep : Step
    {
        public override string Title => "Pull out emails, links or numbers";
        public override string Hint => "Replaces the text with a list of everything found, one per line";
        public static readonly string[] Modes = { "Email addresses", "Web links", "Numbers", "Phone numbers" };

        static readonly Regex[] Patterns =
        {
            new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled),
            new Regex(@"\b(?:https?://|www\.)[^\s<>""')\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"-?\d+(?:[.,]\d+)*", RegexOptions.Compiled),
            new Regex(@"\+?\d[\d\s().\-]{6,}\d", RegexOptions.Compiled),
        };

        int _mode;
        public int Mode { get => _mode; set => Set(ref _mode, value, nameof(Mode)); }

        public override string Apply(string text) =>
            Join(Patterns[Math.Max(0, Math.Min(Patterns.Length - 1, Mode))].Matches(text).Cast<Match>().Select(m => m.Value.TrimEnd('.', ',')));
    }

    public sealed class StripHtmlStep : Step
    {
        public override string Title => "Remove HTML tags";
        public override string Hint => "Keeps the words, drops <tags>, and turns &amp; codes back into characters";

        static readonly Regex Tags = new Regex(@"<(script|style)[^>]*>.*?</\1>|<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        static readonly Regex Entities = new Regex(@"&(#x[0-9a-fA-F]+|#\d+|amp|lt|gt|quot|apos|nbsp);", RegexOptions.Compiled);

        public override string Apply(string text)
        {
            string stripped = Tags.Replace(text, "");
            return Entities.Replace(stripped, m =>
            {
                string e = m.Groups[1].Value;
                switch (e)
                {
                    case "amp": return "&";
                    case "lt": return "<";
                    case "gt": return ">";
                    case "quot": return "\"";
                    case "apos": return "'";
                    case "nbsp": return " ";
                }
                try
                {
                    int code = e.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                        ? int.Parse(e.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : int.Parse(e.Substring(1), CultureInfo.InvariantCulture);
                    return char.ConvertFromUtf32(code);
                }
                catch (Exception) { return m.Value; }
            });
        }
    }

    public static class StepCatalog
    {
        public static readonly (string Name, Func<Step> Create)[] All =
        {
            ("Fix line breaks", () => new FixLineBreaksStep()),
            ("Trim spaces", () => new TrimStep()),
            ("Collapse repeated spaces", () => new CollapseSpacesStep()),
            ("Remove empty lines", () => new RemoveEmptyLinesStep()),
            ("Remove duplicate lines", () => new DedupeStep()),
            ("Sort lines", () => new SortStep()),
            ("Reverse line order", () => new ReverseStep()),
            ("Change case", () => new CaseStep()),
            ("Find and replace", () => new ReplaceStep()),
            ("Keep or remove lines", () => new FilterStep()),
            ("Add text to each line", () => new AffixStep()),
            ("Number lines", () => new NumberStep()),
            ("Join lines", () => new JoinStep()),
            ("Split into lines", () => new SplitStep()),
            ("Straighten quotes and dashes", () => new StraightenStep()),
            ("Remove invisible characters", () => new InvisibleStep()),
            ("Remove HTML tags", () => new StripHtmlStep()),
            ("Pull out emails, links or numbers", () => new ExtractStep()),
        };

        public static readonly (string Name, Func<List<Step>> Create)[] Recipes =
        {
            ("Text copied from a PDF", () => new List<Step> { new InvisibleStep(), new FixLineBreaksStep(), new CollapseSpacesStep(), new StraightenStep() }),
            ("Tidy a list", () => new List<Step> { new TrimStep(), new RemoveEmptyLinesStep(), new DedupeStep(), new SortStep() }),
            ("Comma-separated list", () => new List<Step> { new TrimStep(), new RemoveEmptyLinesStep(), new JoinStep() }),
            ("Quoted list for SQL", () => new List<Step> { new TrimStep(), new RemoveEmptyLinesStep(), new DedupeStep(), new AffixStep { Prefix = "'", Suffix = "'" }, new JoinStep { Separator = ", " } }),
            ("Emails from any text", () => new List<Step> { new ExtractStep(), new DedupeStep(), new SortStep() }),
        };
    }
}
