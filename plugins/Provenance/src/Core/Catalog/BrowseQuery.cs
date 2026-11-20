using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Provenance.Core.Catalog;

/// <summary>
/// What a search needs to know about the character, read live: job levels (for <c>level:mine</c> and
/// <c>is:ready</c>), learned master books and folklore tomes, and specialist jobs.
/// </summary>
public sealed record BrowseCharacter(Func<uint, int> JobLevel, Func<uint, bool> BookLearned, Func<uint, bool> TomeLearned, Func<uint, bool> Specialist)
{
    /// <summary>Nobody logged in: no levels, everything learned, no specialists.</summary>
    public static readonly BrowseCharacter Nobody = new(_ => 0, _ => true, _ => true, _ => false);
}

/// <summary>Words a search understands that come from the game: job names and item categories, in both languages.</summary>
public sealed class BrowseVocabulary
{
    /// <summary>English job abbreviations and names, always understood.</summary>
    private static readonly (string Word, uint Job)[] EnglishJobs =
    {
        ("crp", 8), ("carpenter", 8), ("bsm", 9), ("blacksmith", 9), ("arm", 10), ("armorer", 10), ("armourer", 10),
        ("gsm", 11), ("goldsmith", 11), ("ltw", 12), ("leatherworker", 12), ("wvr", 13), ("weaver", 13),
        ("alc", 14), ("alchemist", 14), ("cul", 15), ("culinarian", 15), ("min", 16), ("miner", 16),
        ("btn", 17), ("botanist", 17), ("fsh", 18), ("fisher", 18),
    };

    private static readonly uint[] Crafters = { 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly uint[] Gatherers = { 16, 17, 18 };

    private readonly Dictionary<string, uint[]> jobs = new();

    /// <param name="jobWords">Extra job names and abbreviations (the client's language).</param>
    /// <param name="categories">Every item category name rows can have, in any language.</param>
    public BrowseVocabulary(IEnumerable<(string Word, uint ClassJobId)>? jobWords = null, IEnumerable<string>? categories = null)
    {
        foreach (var (word, job) in EnglishJobs) AddJob(word, job);
        foreach (var (word, job) in jobWords ?? Array.Empty<(string, uint)>()) AddJob(word, job);
        foreach (var word in new[] { "crafter", "crafters", "doh", "hand" }) jobs[word] = Crafters;
        foreach (var word in new[] { "gatherer", "gatherers", "dol", "land" }) jobs[word] = Gatherers;
        Categories = (categories ?? Array.Empty<string>()).Select(BrowseQuery.Fold).Where(c => c.Length > 0).Distinct().ToList();
    }

    private void AddJob(string word, uint job)
    {
        var key = BrowseQuery.Fold(word);
        if (key.Length == 0) return;
        jobs[key] = jobs.TryGetValue(key, out var known) && !known.Contains(job) ? known.Append(job).ToArray() : new[] { job };
    }

    /// <summary>The jobs a word names ("fsh", "fisher", "Fischer", "crafter"), or null.</summary>
    public uint[]? Jobs(string folded) => jobs.GetValueOrDefault(folded);

    public IEnumerable<string> JobWords => jobs.Keys;

    /// <summary>Category names, folded.</summary>
    public IReadOnlyList<string> Categories { get; }
}

/// <summary>
/// The Browse search: plain words search item names; <c>field:value</c> filters narrow further.
/// A space or <c>and</c> means AND, <c>or</c> binds tighter than AND (<c>job:min or job:btn is:timed</c> is
/// (MIN or BTN) and timed), <c>-</c> or <c>not</c> leaves out, parentheses group, and a comma in a value means
/// "one of" (<c>job:fsh,min</c>). Nothing a search doesn't understand is fatal: it's left out and named in
/// <see cref="Problems"/>, with the closest valid word when there is one.
/// </summary>
public sealed class BrowseQuery
{
    private readonly Func<BrowseEntry, BrowseCharacter, bool>? test;

    private BrowseQuery(Func<BrowseEntry, BrowseCharacter, bool>? test, List<QueryProblem> problems, List<string> words, bool hasFilters, bool usesCharacter)
    {
        this.test = test;
        Problems = problems;
        Words = words;
        HasFilters = hasFilters;
        UsesCharacter = usesCharacter;
    }

    public static readonly BrowseQuery Empty = new(null, new(), new(), false, false);

    /// <summary>Parts of the search that were left out, in the order they were typed.</summary>
    public IReadOnlyList<QueryProblem> Problems { get; }

    /// <summary>The plain words and "phrases" (not left out with <c>-</c>), lower case: what favourites are searched by.</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>There's at least one <c>field:value</c> filter (not only words).</summary>
    public bool HasFilters { get; }

    /// <summary>The result depends on the character (<c>level:mine</c>, <c>is:ready</c>, <c>is:unlearned</c>), so it can change by itself.</summary>
    public bool UsesCharacter { get; }

    /// <summary>Nothing to filter by: every row matches.</summary>
    public bool IsEmpty => test == null;

    public bool Matches(BrowseEntry entry, BrowseCharacter? character = null) => test?.Invoke(entry, character ?? BrowseCharacter.Nobody) ?? true;

    // ------------------------------------------------------------------ the words it understands

    private enum Field { Job, Level, Stars, ItemLevel, Category, In, Is, Name }

    private static readonly Dictionary<string, Field> Fields = new()
    {
        ["job"] = Field.Job, ["jobs"] = Field.Job, ["class"] = Field.Job, ["classjob"] = Field.Job, ["j"] = Field.Job,
        ["level"] = Field.Level, ["lvl"] = Field.Level, ["lv"] = Field.Level, ["levels"] = Field.Level, ["level.range"] = Field.Level,
        ["levelrange"] = Field.Level, ["level_range"] = Field.Level, ["lvl.range"] = Field.Level, ["range"] = Field.Level,
        ["stars"] = Field.Stars, ["star"] = Field.Stars,
        ["ilvl"] = Field.ItemLevel, ["ilv"] = Field.ItemLevel, ["il"] = Field.ItemLevel, ["itemlevel"] = Field.ItemLevel,
        ["item.level"] = Field.ItemLevel, ["item_level"] = Field.ItemLevel,
        ["cat"] = Field.Category, ["cats"] = Field.Category, ["category"] = Field.Category, ["categories"] = Field.Category, ["type"] = Field.Category,
        ["in"] = Field.In, ["content"] = Field.In, ["place"] = Field.In, ["where"] = Field.In,
        ["is"] = Field.Is, ["has"] = Field.Is, ["flag"] = Field.Is,
        ["name"] = Field.Name, ["n"] = Field.Name, ["item"] = Field.Name,
    };

    /// <summary>The name each field is shown with in suggestions.</summary>
    private static string Canonical(Field f) => f switch
    {
        Field.Job => "job", Field.Level => "level", Field.Stars => "stars", Field.ItemLevel => "ilvl", Field.Category => "cat",
        Field.In => "in", Field.Is => "is", _ => "name",
    };

    private enum Flag { Recipe, Gather, Fish, Timed, Reduction, Expert, Hq, Book, Unlearned, Specialist, Ready }

    private static readonly Dictionary<string, Flag> Flags = new()
    {
        ["recipe"] = Flag.Recipe, ["recipes"] = Flag.Recipe, ["craft"] = Flag.Recipe, ["crafted"] = Flag.Recipe,
        ["gather"] = Flag.Gather, ["gathered"] = Flag.Gather, ["gathering"] = Flag.Gather, ["node"] = Flag.Gather,
        ["fish"] = Flag.Fish, ["fished"] = Flag.Fish, ["fishing"] = Flag.Fish,
        ["timed"] = Flag.Timed, ["timednode"] = Flag.Timed, ["timed.node"] = Flag.Timed, ["unspoiled"] = Flag.Timed,
        ["legendary"] = Flag.Timed, ["ephemeral"] = Flag.Timed,
        ["reduction"] = Flag.Reduction, ["reduce"] = Flag.Reduction, ["reduced"] = Flag.Reduction, ["aetherial"] = Flag.Reduction,
        ["aetherialreduction"] = Flag.Reduction, ["aetherial.reduction"] = Flag.Reduction, ["aetherial-reduction"] = Flag.Reduction,
        ["aetherial_reduction"] = Flag.Reduction,
        ["expert"] = Flag.Expert,
        ["hq"] = Flag.Hq, ["canhq"] = Flag.Hq, ["highquality"] = Flag.Hq, ["high.quality"] = Flag.Hq,
        ["book"] = Flag.Book, ["books"] = Flag.Book, ["masterbook"] = Flag.Book, ["master.book"] = Flag.Book, ["tome"] = Flag.Book,
        ["folklore"] = Flag.Book, ["needsbook"] = Flag.Book,
        ["unlearned"] = Flag.Unlearned, ["notlearned"] = Flag.Unlearned, ["not.learned"] = Flag.Unlearned, ["missingbook"] = Flag.Unlearned,
        ["specialist"] = Flag.Specialist, ["spec"] = Flag.Specialist, ["specialists"] = Flag.Specialist,
        ["ready"] = Flag.Ready, ["canmake"] = Flag.Ready, ["available"] = Flag.Ready, ["doable"] = Flag.Ready,
    };

    private static string Canonical(Flag f) => f.ToString().ToLowerInvariant();

    private static readonly Dictionary<string, Place> PlaceWords = new()
    {
        ["world"] = Place.OpenWorld, ["openworld"] = Place.OpenWorld, ["open.world"] = Place.OpenWorld, ["open"] = Place.OpenWorld, ["field"] = Place.OpenWorld,
        ["diadem"] = Place.Diadem, ["thediadem"] = Place.Diadem,
        ["cosmic"] = Place.CosmicExploration, ["cosmicexploration"] = Place.CosmicExploration, ["cosmic.exploration"] = Place.CosmicExploration,
        ["exploration"] = Place.CosmicExploration, ["ce"] = Place.CosmicExploration,
        ["ocean"] = Place.OceanFishing, ["oceanfishing"] = Place.OceanFishing, ["ocean.fishing"] = Place.OceanFishing, ["voyage"] = Place.OceanFishing,
    };

    private static string Canonical(Place p) => p switch
    {
        Place.OpenWorld => "world", Place.Diadem => "diadem", Place.CosmicExploration => "cosmic", _ => "ocean",
    };

    // ------------------------------------------------------------------ reading the text

    private enum Kind { Term, And, Or, Not, Open, Close }

    /// <param name="Start">Where the token starts in the text (with its leading "-").</param>
    /// <param name="Field">Before the first colon, lower case; null for a plain word.</param>
    /// <param name="Quoted">The value was in quotes: taken as it is, commas included.</param>
    private sealed record Token(Kind Kind, int Start, int Length, string Text = "", string? Field = null, string Value = "",
        bool Quoted = false, bool Negated = false);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new Token(Kind.Open, i, 1)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(Kind.Close, i, 1)); i++; continue; }
            if (c is '|' or '&')
            {
                var n = i + 1 < text.Length && text[i + 1] == c ? 2 : 1;
                tokens.Add(new Token(c == '|' ? Kind.Or : Kind.And, i, n));
                i += n;
                continue;
            }

            var start = i;
            var negated = false;
            if (c == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != ')')
            {
                negated = true;
                i++;
            }
            // A word: up to a space or a bracket, with "quoted parts" kept whole.
            var raw = new StringBuilder();
            var quoted = false;
            int colon = -1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')'))
            {
                if (text[i] == '"')
                {
                    var close = text.IndexOf('"', i + 1);
                    var end = close < 0 ? text.Length : close;
                    raw.Append(text, i + 1, end - i - 1);
                    quoted = true;
                    i = close < 0 ? text.Length : close + 1;
                    continue;
                }
                if (text[i] == ':' && colon < 0 && !quoted) colon = raw.Length;
                raw.Append(text[i]);
                i++;
            }
            var word = raw.ToString();
            var length = i - start;
            if (!negated && !quoted)
            {
                var lower = word.ToLowerInvariant();
                if (lower == "and") { tokens.Add(new Token(Kind.And, start, length)); continue; }
                if (lower == "or") { tokens.Add(new Token(Kind.Or, start, length)); continue; }
                if (lower == "not") { tokens.Add(new Token(Kind.Not, start, length)); continue; }
            }
            if ((word.Length == 0 || word == "-") && !quoted) continue;
            if (colon > 0)
                tokens.Add(new Token(Kind.Term, start, length, text.Substring(start, length), word[..colon].ToLowerInvariant(), word[(colon + 1)..], quoted, negated));
            else
                tokens.Add(new Token(Kind.Term, start, length, text.Substring(start, length), null, word, quoted, negated));
        }
        return tokens;
    }

    // ------------------------------------------------------------------ building the test

    private delegate bool Test(BrowseEntry e, BrowseCharacter c);

    public static BrowseQuery Parse(string text, BrowseVocabulary vocabulary)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var p = new Parser(Tokenize(text), vocabulary);
        var root = p.Top();
        if (root == null && p.Problems.Count == 0 && p.Words.Count == 0) return Empty;
        return new BrowseQuery(root == null ? null : (e, c) => root(e, c), p.Problems, p.Words, p.HasFilters, p.UsesCharacter);
    }

    private sealed class Parser(List<Token> tokens, BrowseVocabulary vocabulary)
    {
        private int pos;
        public readonly List<QueryProblem> Problems = new();
        public readonly List<string> Words = new();
        public bool HasFilters;
        public bool UsesCharacter;

        private Kind? Peek => pos < tokens.Count ? tokens[pos].Kind : null;

        public Test? Top()
        {
            var parts = new List<Test>();
            while (pos < tokens.Count)
            {
                if (ParseAnd() is { } t) parts.Add(t);
                if (Peek == Kind.Close) pos++; // a bracket that closes nothing
            }
            return All(parts);
        }

        /// <summary>Terms side by side (or with "and") must all match.</summary>
        private Test? ParseAnd()
        {
            var parts = new List<Test>();
            while (pos < tokens.Count && Peek != Kind.Close)
            {
                if (Peek is Kind.And or Kind.Or) { pos++; continue; } // stray, or at the start
                if (ParseOr() is { } t) parts.Add(t);
            }
            return All(parts);
        }

        /// <summary>"or" binds tighter than "and".</summary>
        private Test? ParseOr()
        {
            var parts = new List<Test>();
            if (ParseUnary() is { } first) parts.Add(first);
            while (Peek == Kind.Or)
            {
                pos++;
                if (Peek is null or Kind.Close or Kind.And) break;
                if (ParseUnary() is { } next) parts.Add(next);
            }
            return Any(parts);
        }

        private Test? ParseUnary()
        {
            switch (Peek)
            {
                case Kind.Not:
                    pos++;
                    return ParseUnary() is { } negated ? (e, c) => !negated(e, c) : null;
                case Kind.Open:
                {
                    pos++;
                    var inner = ParseAnd();
                    if (Peek == Kind.Close) pos++;
                    return inner;
                }
                case Kind.Term:
                    return Term(tokens[pos++]);
                default:
                    pos++;
                    return null;
            }
        }

        private static Test? All(List<Test> parts) => parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => (e, c) => { foreach (var t in parts) if (!t(e, c)) return false; return true; },
        };

        private static Test? Any(List<Test> parts) => parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => (e, c) => { foreach (var t in parts) if (t(e, c)) return true; return false; },
        };

        private Test? Term(Token t)
        {
            var test = TermTest(t);
            if (test == null || !t.Negated) return test;
            return (e, c) => !test(e, c);
        }

        private Test? TermTest(Token t)
        {
            if (t.Field == null)
            {
                // A dotted flag on its own ("aetherial.reduction") is the flag; any other word searches names.
                if (!t.Quoted && t.Value.Contains('.') && Flags.TryGetValue(t.Value.ToLowerInvariant(), out var dotted))
                {
                    HasFilters = true;
                    return FlagTest(dotted);
                }
                return NameTest(t.Value, positive: !t.Negated);
            }
            if (!Fields.TryGetValue(t.Field, out var field))
            {
                var near = Closest(t.Field, Fields.Keys);
                Problems.Add(new QueryProblem(t.Start, t.Length, $"Ignored \"{t.Text}\": there's no \"{t.Field}\" filter",
                    near == null ? null : Rebuild(t, Canonical(Fields[near]), t.Value)));
                return null;
            }
            // "job:" while still typing: nothing to filter by yet, and nothing wrong either.
            if (t.Value.Trim().Length == 0) return null;
            HasFilters = true;
            if (field == Field.Name) return NameTest(t.Value, positive: !t.Negated);

            var values = t.Quoted ? new[] { t.Value } : t.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tests = new List<Test>();
            foreach (var value in values)
            {
                var (test, problem, fix) = Value(field, value);
                if (test != null) tests.Add(test);
                else Problems.Add(new QueryProblem(t.Start, t.Length, $"Ignored \"{(values.Length > 1 ? Canonical(field) + ":" + value : t.Text)}\"{problem}",
                    fix == null ? null : Rebuild(t, Canonical(field), values.Length > 1 ? string.Join(",", values.Select(v => v == value ? fix : v)) : fix)));
            }
            return Any(tests);
        }

        private Test NameTest(string value, bool positive)
        {
            var q = value.ToLowerInvariant();
            if (positive && q.Length > 0) Words.Add(q);
            return (e, _) => e.NameLower.Contains(q, StringComparison.Ordinal);
        }

        /// <summary>A test for one value of a field, or why it was left out (and what was likely meant).</summary>
        private (Test? Test, string Problem, string? Fix) Value(Field field, string value)
        {
            var folded = Fold(value);
            switch (field)
            {
                case Field.Job:
                {
                    if (vocabulary.Jobs(folded) is { } jobs) return ((e, _) => Array.IndexOf(jobs, e.ClassJobId) >= 0, "", null);
                    return (null, "", Closest(folded, vocabulary.JobWords));
                }
                case Field.Level:
                    if (folded is "mine" or "my" or "me")
                    {
                        UsesCharacter = true;
                        return ((e, c) => c.JobLevel(e.ClassJobId) >= e.Level, "", null);
                    }
                    return Number(value, e => e.Level);
                case Field.Stars:
                    return Number(value, e => e.Stars);
                case Field.ItemLevel:
                    return Number(value, e => e.ItemLevel);
                case Field.In:
                    if (PlaceWords.TryGetValue(folded, out var place)) return ((e, _) => (e.Places & place) != 0, "", null);
                    return (null, "", Closest(folded, PlaceWords.Keys) is { } p ? Canonical(PlaceWords[p]) : null);
                case Field.Is:
                    if (Flags.TryGetValue(folded, out var flag)) return (FlagTest(flag), "", null);
                    return (null, "", Closest(folded, Flags.Keys) is { } f ? Canonical(Flags[f]) : null);
                case Field.Category:
                {
                    if (vocabulary.Categories.Count == 0 || vocabulary.Categories.Any(c => c.Contains(folded, StringComparison.Ordinal)))
                        return ((e, _) => e.CategoryFolded.Contains(folded, StringComparison.Ordinal) || e.CategoryEnglishFolded.Contains(folded, StringComparison.Ordinal), "", null);
                    var near = Closest(folded, vocabulary.Categories.SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Concat(vocabulary.Categories).Distinct());
                    return (null, $": no item category has \"{value}\" in it", near == null ? null : near.Contains(' ') ? "\"" + near + "\"" : near);
                }
                default:
                    return (null, "", null);
            }
        }

        private Test FlagTest(Flag flag)
        {
            if (flag is Flag.Unlearned or Flag.Ready) UsesCharacter = true;
            return flag switch
            {
                Flag.Recipe => (e, _) => e.Kind == BrowseKind.Recipe,
                Flag.Gather => (e, _) => e.Kind == BrowseKind.Gather,
                Flag.Fish => (e, _) => e.Kind == BrowseKind.Fish,
                Flag.Timed => (e, _) => e.TimedOnly,
                Flag.Reduction => (e, _) => e.Reduced,
                Flag.Expert => (e, _) => e.Expert,
                Flag.Hq => (e, _) => e.CanHq,
                Flag.Book => (e, _) => e.MasterBook != 0 || e.Tome != 0,
                Flag.Unlearned => (e, c) => (e.MasterBook != 0 && !c.BookLearned(e.MasterBook)) || (e.Tome != 0 && !c.TomeLearned(e.Tome)),
                Flag.Specialist => (e, _) => e.Specialist,
                _ => (e, c) => c.JobLevel(e.ClassJobId) >= e.Level
                               && (e.MasterBook == 0 || c.BookLearned(e.MasterBook))
                               && (e.Tome == 0 || c.TomeLearned(e.Tome))
                               && (!e.Specialist || c.Specialist(e.ClassJobId)),
            };
        }

        private static (Test? Test, string Problem, string? Fix) Number(string value, Func<BrowseEntry, int> of)
            => NumberRange.TryParse(value, out var lo, out var hi)
                ? ((e, _) => of(e) >= lo && of(e) <= hi, "", null)
                : (null, ": use a number or a range like 50-70, >=50 or 90+", null);

        /// <summary>The token again with another field and value, keeping its "-".</summary>
        private static string Rebuild(Token t, string field, string value)
            => (t.Negated ? "-" : "") + field + ":" + (t.Quoted && value.Contains(' ') && !value.StartsWith('"') ? "\"" + value + "\"" : value);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Lower case without accents ("Gärtner" is "gartner"), for comparing words.</summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var d = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>The candidate closest to a mistyped word: a near spelling, or one it starts. Null when nothing is close.</summary>
    public static string? Closest(string word, IEnumerable<string> candidates)
    {
        word = Fold(word);
        if (word.Length == 0) return null;
        string? best = null;
        var bestScore = int.MaxValue;
        var allowed = word.Length <= 3 ? 1 : 2;
        foreach (var c in candidates)
        {
            var d = Distance(word, c);
            // Typing the start of a word counts as close too ("fis" for "fisher").
            if (d > allowed && word.Length >= 3 && c.StartsWith(word, StringComparison.Ordinal)) d = allowed;
            if (d > allowed) continue;
            var score = d * 100 + c.Length;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>Edits between two words, a swapped pair of letters counting as one ("mni" is one from "min").</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3) return int.MaxValue;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }
}

/// <summary>A part of the search that was left out: where it is in the text, why, and what likely was meant.</summary>
/// <param name="Fix">Text to put at [Start, Start + Length) instead (the whole token), or null.</param>
public sealed record QueryProblem(int Start, int Length, string Message, string? Fix);

/// <summary>Number filters: <c>70</c>, <c>50-70</c>, <c>50..70</c>, <c>&gt;=50</c>, <c>&gt;50</c>, <c>&lt;=70</c>, <c>&lt;70</c>, <c>=70</c>, <c>90+</c>.</summary>
public static class NumberRange
{
    public static bool TryParse(string text, out int lo, out int hi)
    {
        lo = int.MinValue;
        hi = int.MaxValue;
        var t = text.Trim().Replace(" ", "");
        if (t.Length == 0) return false;
        static bool Int(string s, out int v) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        if (t.StartsWith(">=")) return Int(t[2..], out lo);
        if (t.StartsWith("<=")) return Int(t[2..], out hi);
        if (t.StartsWith('>')) { if (!Int(t[1..], out var v)) return false; lo = v + 1; return true; }
        if (t.StartsWith('<')) { if (!Int(t[1..], out var v)) return false; hi = v - 1; return true; }
        if (t.StartsWith('=')) t = t[1..];
        if (t.EndsWith('+')) return Int(t[..^1], out lo);
        var dots = t.IndexOf("..", StringComparison.Ordinal);
        var dash = dots >= 0 ? dots : t.IndexOf('-');
        if (dash > 0)
        {
            var rest = t[(dash + (dots >= 0 ? 2 : 1))..];
            if (!Int(t[..dash], out var a) || !Int(rest, out var b)) return false;
            (lo, hi) = a <= b ? (a, b) : (b, a);
            return true;
        }
        if (!Int(t, out var n)) return false;
        lo = hi = n;
        return true;
    }
}
