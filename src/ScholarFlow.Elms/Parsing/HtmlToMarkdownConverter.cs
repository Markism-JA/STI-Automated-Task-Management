using System.Text;
using AngleSharp.Dom;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Elms.Parsing;

/// <summary>
/// Converts eLMS HTML content (including MS Word MsoNormal/MsoListParagraph markup) into clean Obsidian Markdown.
/// </summary>
public static class HtmlToMarkdownConverter
{
    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var doc = HtmlParsingHelpers.ParseDocument(html);
        var body = doc.Body;
        if (body is null)
            return string.Empty;

        // Remove dangerous or non-content tags
        foreach (var node in body.QuerySelectorAll("script, style, svg, nav, button, noscript").ToList())
            node.Remove();

        var sb = new StringBuilder();
        RenderChildren(body, sb);

        var lines = sb.ToString()
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        var collapsed = new List<string>();
        bool prevBlank = true;
        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && prevBlank)
                continue;
            collapsed.Add(line);
            prevBlank = isBlank;
        }

        return string.Join('\n', collapsed).Trim();
    }

    /// <summary>
    /// Formats a structured Obsidian Markdown note for an eLMS assignment, including YAML frontmatter,
    /// instructions, submission metadata, and relative links to attachments.
    /// </summary>
    public static string FormatAssignmentNote(ElmsAssignment assignment)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"lms_id: \"{assignment.LmsId}\"");
        sb.AppendLine($"course_id: \"{assignment.CourseId}\"");
        sb.AppendLine($"type: \"{assignment.Kind}\"");
        sb.AppendLine($"status: \"{assignment.Status}\"");
        if (assignment.DueDate.HasValue)
            sb.AppendLine($"due_date: \"{assignment.DueDate.Value:yyyy-MM-dd HH:mm}\"");
        if (!string.IsNullOrWhiteSpace(assignment.TermHeader))
            sb.AppendLine($"term: \"{assignment.TermHeader}\"");
        if (assignment.MaxScore.HasValue)
            sb.AppendLine($"max_score: {assignment.MaxScore.Value}");
        if (!string.IsNullOrWhiteSpace(assignment.Score))
            sb.AppendLine($"score: \"{assignment.Score}\"");
        if (assignment.WeightPercent.HasValue)
            sb.AppendLine($"weight_percent: {assignment.WeightPercent.Value}");
        sb.AppendLine($"url: \"{assignment.Url}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {assignment.Title}");
        sb.AppendLine();

        var metaParts = new List<string> { $"**Status:** {assignment.Status}" };
        if (assignment.DueDate.HasValue)
            metaParts.Add($"**Due:** {assignment.DueDate.Value:yyyy-MM-dd h:mm tt}");
        if (assignment.MaxScore.HasValue)
            metaParts.Add($"**Max Score:** {assignment.MaxScore.Value}");
        if (!string.IsNullOrWhiteSpace(assignment.Score))
            metaParts.Add($"**Score:** {assignment.Score}");
        if (!string.IsNullOrWhiteSpace(assignment.ParentModuleTitle))
            metaParts.Add($"**Module:** {assignment.ParentModuleTitle}");

        sb.AppendLine(string.Join(" | ", metaParts));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(assignment.InstructionsMarkdown))
        {
            sb.AppendLine("## Instructions");
            sb.AppendLine();
            sb.AppendLine(assignment.InstructionsMarkdown);
            sb.AppendLine();
        }

        if (assignment.Attachments.Count > 0)
        {
            sb.AppendLine("## Attachments");
            sb.AppendLine();
            foreach (var att in assignment.Attachments)
                sb.AppendLine($"- [{att.DisplayName}]({att.SanitizedFileName})");
            sb.AppendLine();
        }

        var sub = assignment.SubmissionInfo;
        if (sub.IsSubmitted || sub.SubmittedFiles.Count > 0 || sub.Comments.Count > 0)
        {
            sb.AppendLine("## Submission");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(sub.SubmittedAtText))
                sb.AppendLine($"- **Submitted:** {sub.SubmittedAtText}");
            sb.AppendLine($"- **Attempts:** {sub.Attempts}" + (sub.MaxAttempts.HasValue ? $" / {sub.MaxAttempts.Value}" : ""));
            foreach (var file in sub.SubmittedFiles)
                sb.AppendLine($"- **Submitted File:** [{file.DisplayName}]({file.SanitizedFileName})");
            foreach (var comment in sub.Comments)
                sb.AppendLine($"- **Comment:** {comment}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void RenderChildren(INode parent, StringBuilder sb)
    {
        foreach (var child in parent.ChildNodes)
            RenderNode(child, sb);
    }

    private static void RenderNode(INode node, StringBuilder sb)
    {
        if (node is IText textNode)
        {
            var text = HtmlParsingHelpers.NormalizeWhitespace(textNode.TextContent);
            if (!string.IsNullOrEmpty(text))
                sb.Append(text).Append(' ');
            return;
        }

        if (node is not IElement el)
            return;

        var tag = el.TagName.ToLowerInvariant();
        switch (tag)
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
                var level = tag[1] - '0';
                var heading = HtmlParsingHelpers.NormalizeWhitespace(el.TextContent);
                if (!string.IsNullOrEmpty(heading))
                    sb.Append('\n').Append(new string('#', level)).Append(' ').Append(heading).Append("\n\n");
                break;

            case "p":
            case "div":
                sb.Append('\n');
                RenderChildren(el, sb);
                sb.Append('\n');
                break;

            case "br":
                sb.Append('\n');
                break;

            case "strong":
            case "b":
                var bold = HtmlParsingHelpers.NormalizeWhitespace(el.TextContent);
                if (!string.IsNullOrEmpty(bold))
                    sb.Append($"**{bold}** ");
                break;

            case "em":
            case "i":
                var italic = HtmlParsingHelpers.NormalizeWhitespace(el.TextContent);
                if (!string.IsNullOrEmpty(italic))
                    sb.Append($"*{italic}* ");
                break;

            case "li":
                sb.Append("\n- ");
                RenderChildren(el, sb);
                break;

            case "a":
                var href = el.GetAttribute("href");
                var label = HtmlParsingHelpers.NormalizeWhitespace(el.TextContent);
                if (!string.IsNullOrWhiteSpace(href) && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                {
                    var display = string.IsNullOrEmpty(label) ? href : label;
                    sb.Append($"[{display}]({href}) ");
                }
                else if (!string.IsNullOrEmpty(label))
                {
                    sb.Append(label).Append(' ');
                }
                break;

            default:
                RenderChildren(el, sb);
                break;
        }
    }
}
