using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ScholarFlow.Core.Pdf;

/// <summary>
/// Parses STI Course Syllabus PDFs (*Syllabus*Outline*.pdf) to determine exact
/// module-to-term boundaries (Prelims, Midterms, Prefinals, Finals) and infer
/// academic terms for unnumbered tasks/activities.
/// </summary>
public static partial class SyllabusTermParser
{
    public static readonly string[] TermOrder = ["Prelims", "Midterms", "Prefinals", "Finals"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "your", "are", "was", "were",
        "will", "can", "have", "has", "had", "not", "but", "use", "using", "used", "such", "how",
        "what", "when", "where", "which", "who", "why", "all", "any", "each", "more", "most", "other",
        "some", "than", "then", "them", "they", "their", "there", "these", "those", "its", "about",
        "over", "under", "between", "through", "during", "before", "after", "above", "below",
        // STI syllabus / handout boilerplate
        "sti", "college", "education", "services", "group", "course", "outline", "syllabus",
        "material", "materials", "page", "pages", "handout", "handouts", "property", "reproduction",
        "strictly", "prohibited", "objectives", "learning", "outcomes", "intended", "topics",
        "teaching", "activities", "activity", "assessment", "tasks", "task", "resources", "week",
        "weeks", "module", "lesson", "student", "students", "explain", "identify", "describe",
        "discuss", "define", "understand", "apply", "analyze", "evaluate", "create", "demonstrate",
        "elms", "quiz", "quizzes", "performance", "exam", "examination", "preliminary", "midterm",
        "prefinal", "final", "finals", "prelims", "midterms", "prefinals", "laboratory", "lab"
    };

    [GeneratedRegex(@"\b(0[1-9]|1[0-9])\b(?=\s+(?:Quiz|Activity|Task|Lab|Laboratory|Performance|Seatwork|Handout|Group|eLMS|Assignment|Exam|Case|Exercise|Project|Review|Reflection|Discussion|Homework|Worksheet)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixBeforeAssessmentRegex();

    [GeneratedRegex(@"\b(?:Module|Handout|Lesson)\s+(0[1-9]|1[0-9])\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixAfterModuleRegex();

    [GeneratedRegex(@"\b([a-zA-Z]{3,})\b")]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"^(\d{2})_")]
    private static partial Regex LeadingTwoDigitFolderRegex();

    public sealed record SyllabusSchedule(
        Dictionary<string, List<string>> TermPrefixes,
        Dictionary<string, List<string>> TermLines);

    /// <summary>
    /// Searches a course directory (excluding completed, ongoing, hidden, and *_Master.pdf)
    /// for an STI Course Syllabus / Outline PDF.
    /// </summary>
    public static string? FindSyllabusPdfPath(string courseDir)
    {
        if (string.IsNullOrWhiteSpace(courseDir) || !Directory.Exists(courseDir))
            return null;

        var candidates = new List<string>();
        CollectPdfCandidates(courseDir, candidates, depth: 0);

        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(p =>
            {
                var name = Path.GetFileName(p);
                int score = 0;
                if (name.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)) score += 10;
                if (name.Contains("Outline", StringComparison.OrdinalIgnoreCase)) score += 5;
                if (name.Contains("Course", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Couse", StringComparison.OrdinalIgnoreCase)) score += 2;
                return score;
            })
            .ThenBy(p => p.Length)
            .FirstOrDefault(p =>
            {
                var name = Path.GetFileName(p);
                var parent = Path.GetFileName(Path.GetDirectoryName(p) ?? "");
                return name.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Outline", StringComparison.OrdinalIgnoreCase)
                    || parent.Contains("Syllabus", StringComparison.OrdinalIgnoreCase)
                    || parent.Contains("Outline", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static void CollectPdfCandidates(string dir, List<string> results, int depth)
    {
        if (depth > 3) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.pdf"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase))
                    continue;
                results.Add(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') ||
                    name.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ongoing", StringComparison.OrdinalIgnoreCase))
                    continue;

                CollectPdfCandidates(sub, results, depth + 1);
            }
        }
        catch
        {
            // Ignore unreadable directories
        }
    }

    /// <summary>
    /// Parses the Course Outline table from the syllabus PDF and maps both explicit
    /// prefixes (Pass 1) and local module handout PDFs (Pass 2) into the 4 academic terms.
    /// </summary>
    public static SyllabusSchedule ParseTermScheduleFromSyllabus(string syllabusPdfPath, string courseDir)
    {
        var termLines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Prelims"] = [],
            ["Midterms"] = [],
            ["Prefinals"] = [],
            ["Finals"] = []
        };

        var termPrefixes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Prelims"] = [],
            ["Midterms"] = [],
            ["Prefinals"] = [],
            ["Finals"] = []
        };

        if (!File.Exists(syllabusPdfPath))
            return new SyllabusSchedule(termPrefixes, termLines);

        var allLines = ExtractReadingOrderLines(syllabusPdfPath);
        SplitLinesIntoTermBuckets(allLines, termLines);

        // Pass 1: Extract explicit 2-digit prefixes from each bucket's assessment/module mentions
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in TermOrder)
        {
            var bucketText = string.Join("\n", termLines[term]);
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in PrefixBeforeAssessmentRegex().Matches(bucketText))
                found.Add(m.Groups[1].Value);

            foreach (Match m in PrefixAfterModuleRegex().Matches(bucketText))
                found.Add(m.Groups[1].Value);

            foreach (var p in found)
            {
                if (assigned.Add(p))
                    termPrefixes[term].Add(p);
            }
        }

        // Pass 2: For any local numbered module folder (01_*, 02_*, ...) not yet mapped,
        // extract Page 1 text of its handout PDF and match against each term bucket's topics.
        if (!string.IsNullOrWhiteSpace(courseDir) && Directory.Exists(courseDir))
        {
            var localModules = DiscoverLocalNumberedModules(courseDir);
            var bucketProfiles = BuildBucketTokenProfiles(termLines);

            foreach (var (prefix, samplePdfPath) in localModules)
            {
                if (assigned.Contains(prefix))
                    continue;

                if (string.IsNullOrWhiteSpace(samplePdfPath) || !File.Exists(samplePdfPath))
                    continue;

                var handoutText = ExtractTitleHeaderText(samplePdfPath);
                var bestTerm = ScoreTextAgainstBuckets(handoutText, bucketProfiles);
                if (!string.IsNullOrWhiteSpace(bestTerm))
                {
                    assigned.Add(prefix);
                    termPrefixes[bestTerm].Add(prefix);
                }
            }
        }

        foreach (var term in TermOrder)
            termPrefixes[term].Sort(StringComparer.OrdinalIgnoreCase);

        return new SyllabusSchedule(termPrefixes, termLines);
    }

    /// <summary>
    /// Infers the academic term for an unnumbered task/activity using the syllabus's
    /// assessment tasks and topic buckets.
    /// </summary>
    public static string? InferTaskTermFromSyllabus(
        string syllabusPdfPath,
        string courseDir,
        string? taskTitle,
        string? sectionTitle = null)
    {
        if (string.IsNullOrWhiteSpace(syllabusPdfPath) || !File.Exists(syllabusPdfPath))
            return null;

        var schedule = ParseTermScheduleFromSyllabus(syllabusPdfPath, courseDir);
        var query = $"{sectionTitle} {taskTitle}".Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        // First check if any Pass 1/Pass 2 prefix matches a 2-digit prefix in the query
        foreach (Match m in Regex.Matches(query, @"\b(0[1-9]|1[0-9])\b"))
        {
            var prefix = m.Groups[1].Value;
            foreach (var term in TermOrder)
            {
                if (schedule.TermPrefixes[term].Contains(prefix, StringComparer.OrdinalIgnoreCase))
                    return term;
            }
        }

        // Otherwise score query tokens and title phrases against each term bucket
        var bucketProfiles = BuildBucketTokenProfiles(schedule.TermLines);
        var cleanTitle = Regex.Replace(taskTitle ?? "", @"^[^a-zA-Z0-9]+", "").Trim();

        double bestScore = 0;
        string? bestTerm = null;

        var queryTokens = Tokenize(query);
        var queryBigrams = BuildBigrams(queryTokens);

        foreach (var term in TermOrder)
        {
            var profile = bucketProfiles[term];
            double score = 0;

            // Strong boost if the exact task title appears in the bucket's syllabus lines
            if (cleanTitle.Length >= 5 &&
                schedule.TermLines[term].Any(l => l.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase)))
            {
                score += 12.0;
            }

            foreach (var token in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (profile.Tokens.TryGetValue(token, out int count))
                {
                    double idf = profile.TokenIdf.GetValueOrDefault(token, 1.0);
                    score += Math.Min(count, 3) * idf;
                }
            }

            foreach (var bigram in queryBigrams)
            {
                if (profile.Bigrams.Contains(bigram))
                    score += 3.0;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestTerm = term;
            }
        }

        return bestScore >= 1.5 ? bestTerm : null;
    }

    private static List<string> ExtractReadingOrderLines(string pdfPath)
    {
        var lines = new List<string>();
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            foreach (var page in doc.GetPages())
            {
                var words = page.GetWords().ToList();
                if (words.Count == 0) continue;

                // Sort top-to-bottom (Bottom descending), then left-to-right
                var sorted = words
                    .OrderByDescending(w => w.BoundingBox.Bottom)
                    .ThenBy(w => w.BoundingBox.Left)
                    .ToList();

                var currentLine = new List<Word>();
                double currentY = sorted[0].BoundingBox.Bottom;

                foreach (var w in sorted)
                {
                    if (Math.Abs(w.BoundingBox.Bottom - currentY) <= 4.0)
                    {
                        currentLine.Add(w);
                    }
                    else
                    {
                        EmitLine(currentLine, lines);
                        currentLine.Clear();
                        currentLine.Add(w);
                        currentY = w.BoundingBox.Bottom;
                    }
                }

                EmitLine(currentLine, lines);
            }
        }
        catch
        {
            // Return whatever was extracted
        }

        return lines;
    }

    private static void EmitLine(List<Word> words, List<string> lines)
    {
        if (words.Count == 0) return;
        var text = string.Join(" ", words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)).Trim();
        if (!string.IsNullOrWhiteSpace(text))
            lines.Add(text);
    }

    private static void SplitLinesIntoTermBuckets(List<string> lines, Dictionary<string, List<string>> termLines)
    {
        int outlineStart = lines.FindIndex(l =>
            l.Contains("Course Outline", StringComparison.OrdinalIgnoreCase));

        int startIndex = outlineStart >= 0 ? outlineStart + 1 : 0;
        int currentTermIdx = 0; // 0=Prelims, 1=Midterms, 2=Prefinals, 3=Finals

        for (int i = startIndex; i < lines.Count; i++)
        {
            var line = lines[i];

            // Skip grading-system percentage rows if Course Outline header wasn't matched
            if (outlineStart < 0 && line.Contains('%'))
                continue;

            if (line.Contains("PRELIMINARY EXAMINATION", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("PRELIM EXAMINATION", StringComparison.OrdinalIgnoreCase))
            {
                termLines[TermOrder[currentTermIdx]].Add(line);
                if (currentTermIdx < 1) currentTermIdx = 1;
                continue;
            }

            if (line.Contains("MIDTERM EXAMINATION", StringComparison.OrdinalIgnoreCase))
            {
                termLines[TermOrder[currentTermIdx]].Add(line);
                if (currentTermIdx < 2) currentTermIdx = 2;
                continue;
            }

            if (line.Contains("PRE-FINAL EXAMINATION", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("PREFINAL EXAMINATION", StringComparison.OrdinalIgnoreCase))
            {
                termLines[TermOrder[currentTermIdx]].Add(line);
                if (currentTermIdx < 3) currentTermIdx = 3;
                continue;
            }

            if (line.Contains("FINAL EXAMINATION", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("PRE-FINAL", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("PREFINAL", StringComparison.OrdinalIgnoreCase))
            {
                termLines[TermOrder[currentTermIdx]].Add(line);
                break; // End of Finals in Course Outline
            }

            termLines[TermOrder[currentTermIdx]].Add(line);
        }
    }

    private static List<(string Prefix, string? SamplePdf)> DiscoverLocalNumberedModules(string courseDir)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(courseDir))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ongoing", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith('.'))
                    continue;

                var m = LeadingTwoDigitFolderRegex().Match(name);
                if (!m.Success) continue;

                var prefix = m.Groups[1].Value;
                var pdf = Directory.EnumerateFiles(dir, "*.pdf")
                    .Where(p => !Path.GetFileName(p).EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                result[prefix] = pdf;
            }

            foreach (var file in Directory.EnumerateFiles(courseDir, "*.pdf"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var m = LeadingTwoDigitFolderRegex().Match(name);
                if (!m.Success) continue;

                var prefix = m.Groups[1].Value;
                result.TryAdd(prefix, file);
            }
        }
        catch
        {
            // Ignore unreadable directories
        }

        return result
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private static string ExtractTitleHeaderText(string pdfPath)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var firstPage = doc.GetPages().FirstOrDefault();
            if (firstPage is null) return "";

            // Take the top ~60 words of Page 1 (course code + module title + subheadings),
            // avoiding lower body-prose paragraphs that contain generic repeated terms.
            var headerWords = firstPage.GetWords()
                .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom / 4.0) * 4.0)
                .ThenBy(w => w.BoundingBox.Left)
                .Take(60)
                .Select(w => w.Text);

            return string.Join(" ", headerWords);
        }
        catch
        {
            return "";
        }
    }

    private sealed record BucketProfile(
        Dictionary<string, int> Tokens,
        HashSet<string> Bigrams,
        Dictionary<string, double> TokenIdf);

    private static Dictionary<string, BucketProfile> BuildBucketTokenProfiles(
        Dictionary<string, List<string>> termLines)
    {
        var rawTokens = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var rawBigrams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in TermOrder)
        {
            var text = string.Join(" ", termLines[term]);
            var tokens = Tokenize(text);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens)
                counts[t] = counts.GetValueOrDefault(t) + 1;

            rawTokens[term] = counts;
            rawBigrams[term] = BuildBigrams(tokens);

            foreach (var uniqueToken in counts.Keys)
                docFreq[uniqueToken] = docFreq.GetValueOrDefault(uniqueToken) + 1;
        }

        var profiles = new Dictionary<string, BucketProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in TermOrder)
        {
            var idf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in rawTokens[term].Keys)
            {
                int df = docFreq.GetValueOrDefault(token, 1);
                idf[token] = Math.Log(1.0 + 4.0 / df);
            }

            profiles[term] = new BucketProfile(rawTokens[term], rawBigrams[term], idf);
        }

        return profiles;
    }

    private static string? ScoreTextAgainstBuckets(
        string text,
        Dictionary<string, BucketProfile> profiles)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return null;

        var uniqueTokens = tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bigrams = BuildBigrams(tokens);

        double bestScore = 0;
        string? bestTerm = null;

        foreach (var term in TermOrder)
        {
            var profile = profiles[term];
            double score = 0;

            foreach (var token in uniqueTokens)
            {
                if (profile.Tokens.TryGetValue(token, out int count))
                {
                    double idf = profile.TokenIdf.GetValueOrDefault(token, 1.0);
                    score += Math.Min(count, 3) * idf;
                }
            }

            foreach (var bg in bigrams)
            {
                if (profile.Bigrams.Contains(bg))
                    score += 3.0;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestTerm = term;
            }
        }

        return bestScore > 0.5 ? bestTerm : null;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in WordTokenRegex().Matches(text))
        {
            var word = m.Groups[1].Value.ToLowerInvariant();
            if (!StopWords.Contains(word))
                tokens.Add(word);
        }
        return tokens;
    }

    private static HashSet<string> BuildBigrams(List<string> tokens)
    {
        var bigrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tokens.Count - 1; i++)
            bigrams.Add($"{tokens[i]} {tokens[i + 1]}");
        return bigrams;
    }
}
