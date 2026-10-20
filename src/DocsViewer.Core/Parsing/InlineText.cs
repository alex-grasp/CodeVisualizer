using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocsViewer.Core.Parsing;

/// <summary>A back-ticked span inside a paragraph, with the plain text around it.</summary>
internal sealed record CodeSpan(string Code, string Before, string After)
{
    /// <summary>The last word before the span ("mounts", "calls", "in"), lower-case, or null.</summary>
    public string? PrecedingWord
    {
        get
        {
            var words = Before.Trim().TrimEnd(',', ':', ';', '(').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 0 ? null : words[^1].ToLowerInvariant();
        }
    }
}

/// <summary>Plain-text rendering of Markdig inline trees and extraction of code spans.</summary>
internal static class InlineText
{
    public static string Plain(Inline? inline)
    {
        var sb = new StringBuilder();
        Append(sb, inline);
        return Normalise(sb.ToString());
    }

    public static string Plain(LeafBlock? block) => block == null ? string.Empty : Plain(block.Inline);

    public static string Plain(TableCell? cell)
    {
        if (cell == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var b in cell)
        {
            if (b is LeafBlock lb) Append(sb, lb.Inline);
            sb.Append(' ');
        }
        return Normalise(sb.ToString());
    }

    public static ContainerInline? FirstInline(TableCell? cell)
        => cell?.OfType<LeafBlock>().FirstOrDefault()?.Inline;

    /// <summary>All code spans in document order with their surrounding plain text.</summary>
    public static List<CodeSpan> CodeSpans(Inline? inline)
    {
        var result = new List<CodeSpan>();
        var parts = new List<(string? code, string text)>();
        Flatten(inline, parts);
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].code == null) continue;
            var before = new StringBuilder();
            for (var j = i - 1; j >= 0 && parts[j].code == null; j--) before.Insert(0, parts[j].text);
            var after = new StringBuilder();
            for (var j = i + 1; j < parts.Count && parts[j].code == null; j++) after.Append(parts[j].text);
            result.Add(new CodeSpan(parts[i].code!.Trim(), before.ToString(), after.ToString()));
        }
        return result;
    }

    public static List<CodeSpan> CodeSpans(LeafBlock? block) => block == null ? new List<CodeSpan>() : CodeSpans(block.Inline);

    public static List<CodeSpan> CodeSpans(TableCell? cell)
    {
        var list = new List<CodeSpan>();
        if (cell == null) return list;
        foreach (var lb in cell.OfType<LeafBlock>()) list.AddRange(CodeSpans(lb.Inline));
        return list;
    }

    private static void Flatten(Inline? inline, List<(string? code, string text)> parts)
    {
        for (var node = inline; node != null; node = node.NextSibling)
        {
            switch (node)
            {
                case CodeInline code:
                    parts.Add((code.Content, code.Content));
                    break;
                case LiteralInline lit:
                    parts.Add((null, lit.Content.ToString()));
                    break;
                case LineBreakInline:
                    parts.Add((null, " "));
                    break;
                case HtmlEntityInline ent:
                    parts.Add((null, ent.Transcoded.ToString()));
                    break;
                case HtmlInline html:
                    parts.Add((null, html.Tag));
                    break;
                case AutolinkInline auto:
                    parts.Add((null, auto.Url));
                    break;
                case ContainerInline container:
                    Flatten(container.FirstChild, parts);
                    break;
            }
        }
    }

    private static void Append(StringBuilder sb, Inline? inline)
    {
        var parts = new List<(string? code, string text)>();
        Flatten(inline, parts);
        foreach (var p in parts) sb.Append(p.text);
    }

    private static string Normalise(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString().Trim();
    }
}
