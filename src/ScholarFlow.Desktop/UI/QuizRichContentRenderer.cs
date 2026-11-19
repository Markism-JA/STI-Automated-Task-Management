using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure-C# rich content renderer for specialized quiz modalities:
/// - Monospace Code Snippet blocks with language pill &amp; one-click copy
/// - LaTeX Mathematical Equations (stacked visual fractions + Unicode math typography)
/// - Visual Node-Flow / Architecture Diagrams
/// </summary>
public static partial class QuizRichContentRenderer
{
    private static readonly FontFamily MonospaceFont =
        new("JetBrains Mono, Fira Code, Cascadia Mono, Consolas, monospace");

    private static readonly FontFamily MathFont =
        new("Cambria Math, STIX Two Math, Latin Modern Math, Georgia, serif");

    private static readonly (string Latex, string Symbol)[] LatexReplacements =
    [
        (@"\leq", "≤"),
        (@"\le", "≤"),
        (@"\geq", "≥"),
        (@"\ge", "≥"),
        (@"\neq", "≠"),
        (@"\ne", "≠"),
        (@"\approx", "≈"),
        (@"\equiv", "≡"),
        (@"\pm", "±"),
        (@"\times", "×"),
        (@"\cdot", "·"),
        (@"\div", "÷"),
        (@"\infty", "∞"),
        (@"\Rightarrow", "⇒"),
        (@"\Leftrightarrow", "⇔"),
        (@"\rightarrow", "→"),
        (@"\to", "→"),
        (@"\leftarrow", "←"),
        (@"\forall", "∀"),
        (@"\exists", "∃"),
        (@"\in", "∈"),
        (@"\notin", "∉"),
        (@"\subseteq", "⊆"),
        (@"\subset", "⊂"),
        (@"\cup", "∪"),
        (@"\cap", "∩"),
        (@"\emptyset", "∅"),
        (@"\sum", "∑"),
        (@"\prod", "∏"),
        (@"\int", "∫"),
        (@"\partial", "∂"),
        (@"\nabla", "∇"),
        (@"\Delta", "Δ"),
        (@"\Theta", "Θ"),
        (@"\Lambda", "Λ"),
        (@"\Sigma", "Σ"),
        (@"\Omega", "Ω"),
        (@"\alpha", "α"),
        (@"\beta", "β"),
        (@"\gamma", "γ"),
        (@"\delta", "δ"),
        (@"\epsilon", "ε"),
        (@"\theta", "θ"),
        (@"\lambda", "λ"),
        (@"\mu", "μ"),
        (@"\pi", "π"),
        (@"\sigma", "σ"),
        (@"\tau", "τ"),
        (@"\phi", "φ"),
        (@"\omega", "ω"),
        (@"\log", "log"),
        (@"\ln", "ln"),
        (@"\sin", "sin"),
        (@"\cos", "cos"),
        (@"\tan", "tan"),
        (@"\lim", "lim"),
        (@"\max", "max"),
        (@"\min", "min")
    ];

    private static readonly Dictionary<char, char> SuperscriptMap = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['n'] = 'ⁿ', ['i'] = 'ⁱ', ['x'] = 'ˣ', ['y'] = 'ʸ', ['k'] = 'ᵏ', ['m'] = 'ᵐ'
    };

    private static readonly Dictionary<char, char> SubscriptMap = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['i'] = 'ᵢ', ['j'] = 'ⱼ', ['k'] = 'ₖ',
        ['m'] = 'ₘ', ['n'] = 'ₙ', ['o'] = 'ₒ', ['p'] = 'ₚ', ['r'] = 'ᵣ',
        ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ', ['v'] = 'ᵥ', ['x'] = 'ₓ'
    };

    /// <summary>
    /// Formats inline LaTeX math tokens ($...$, \frac, \sqrt, superscripts, Greek symbols) into readable mathematical Unicode.
    /// </summary>
    public static string FormatInlineMath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();
        if (!s.Contains('\\') && !s.Contains('$') && !s.Contains('^') && !s.Contains('_'))
            return s;

        s = s.Replace("$$", "").Replace("$", "");
        s = Regex.Replace(s, @"\\frac\{([^{}]+)\}\{([^{}]+)\}", "($1) / ($2)");
        s = Regex.Replace(s, @"\\sqrt\{([^{}]+)\}", "√($1)");
        s = Regex.Replace(s, @"\\text\{([^{}]+)\}", "$1");
        s = Regex.Replace(s, @"\\mathbf\{([^{}]+)\}", "$1");

        foreach (var (latex, sym) in LatexReplacements)
            s = s.Replace(latex, sym, StringComparison.Ordinal);

        // Superscripts ^{...} or ^x
        s = Regex.Replace(s, @"\^\{([^{}]+)\}", m => TryConvertScript(m.Groups[1].Value, SuperscriptMap, '^'));
        s = Regex.Replace(s, @"\^([0-9ni])", m => TryConvertScript(m.Groups[1].Value, SuperscriptMap, '^'));

        // Subscripts _{...} or _x
        s = Regex.Replace(s, @"_\{([^{}]+)\}", m => TryConvertScript(m.Groups[1].Value, SubscriptMap, '_'));
        s = Regex.Replace(s, @"_([0-9ijknm])", m => TryConvertScript(m.Groups[1].Value, SubscriptMap, '_'));

        s = s.Replace(@"\left", "").Replace(@"\right", "").Replace("{", "").Replace("}", "");
        return s.Trim();
    }

    private static string TryConvertScript(string content, Dictionary<char, char> map, char fallbackPrefix)
    {
        var chars = new char[content.Length];
        for (int i = 0; i < content.Length; i++)
        {
            if (!map.TryGetValue(content[i], out chars[i]))
                return $"{fallbackPrefix}({content})";
        }
        return new string(chars);
    }

    public static Control BuildCodeSnippetCard(string codeSnippet, string? language)
    {
        var cleanCode = (codeSnippet ?? string.Empty).Trim();
        var langLabel = string.IsNullOrWhiteSpace(language) ? "CODE" : language.Trim().ToUpperInvariant();

        var outerStack = new StackPanel { Spacing = 6 };
        var topBar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };

        var leftBadge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        leftBadge.Children.Add(UiBuilder.Icon(MaterialIconKind.CodeTags, 14, ThemeService.Brush(ThemeService.AccentPrimary)));
        leftBadge.Children.Add(new TextBlock
        {
            Text = $"CODE SNIPPET · {langLabel}",
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.6,
            Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
            VerticalAlignment = VerticalAlignment.Center
        });
        topBar.Children.Add(leftBadge);

        var codeBox = new TextBox
        {
            Text = cleanCode,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = MonospaceFont,
            FontSize = 12.5,
            Background = ThemeService.Brush(ThemeService.BgBase),
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            TextWrapping = TextWrapping.NoWrap
        };

        var copyBtn = UiBuilder.GhostButton(
            "Copy Code",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(codeBox);
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(cleanCode);
            });
        Grid.SetColumn(copyBtn, 1);
        topBar.Children.Add(copyBtn);
        outerStack.Children.Add(topBar);

        outerStack.Children.Add(new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Child = codeBox
        });

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = outerStack
        };
    }

    public static Control BuildMathLatexCard(string rawLatex)
    {
        var stack = new StackPanel { Spacing = 8 };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topRow.Children.Add(UiBuilder.Icon(MaterialIconKind.FunctionVariant, 14, ThemeService.Brush(ThemeService.PriorityP3Fg)));
        topRow.Children.Add(new TextBlock
        {
            Text = "MATHEMATICAL EXPRESSION",
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.6,
            Foreground = ThemeService.Brush(ThemeService.PriorityP3Fg),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(topRow);

        Control equationVisual;
        var clean = rawLatex.Trim().Replace("$$", "").Replace("$", "");
        var fracMatch = Regex.Match(clean, @"^(?<pre>.*?)\\frac\{(?<num>[^{}]+)\}\{(?<den>[^{}]+)\}(?<post>.*)$");
        if (fracMatch.Success)
        {
            // Render stacked visual fraction (pre + [num / horizontal bar / den] + post)
            var horiz = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var pre = FormatInlineMath(fracMatch.Groups["pre"].Value);
            if (!string.IsNullOrWhiteSpace(pre))
            {
                horiz.Children.Add(new TextBlock
                {
                    Text = pre,
                    FontFamily = MathFont,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var fracStack = new StackPanel
            {
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            fracStack.Children.Add(new TextBlock
            {
                Text = FormatInlineMath(fracMatch.Groups["num"].Value),
                FontFamily = MathFont,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            fracStack.Children.Add(new Border
            {
                Height = 1.5,
                MinWidth = 48,
                Background = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
            fracStack.Children.Add(new TextBlock
            {
                Text = FormatInlineMath(fracMatch.Groups["den"].Value),
                FontFamily = MathFont,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            horiz.Children.Add(fracStack);

            var post = FormatInlineMath(fracMatch.Groups["post"].Value);
            if (!string.IsNullOrWhiteSpace(post))
            {
                horiz.Children.Add(new TextBlock
                {
                    Text = post,
                    FontFamily = MathFont,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            equationVisual = horiz;
        }
        else
        {
            equationVisual = new TextBlock
            {
                Text = FormatInlineMath(clean),
                FontFamily = MathFont,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
        }

        stack.Children.Add(new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12),
            Child = equationVisual
        });

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = stack
        };
    }

    public static Control BuildDiagramCard(string diagramSpec)
    {
        var clean = (diagramSpec ?? string.Empty).Trim();
        var stack = new StackPanel { Spacing = 8 };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topRow.Children.Add(UiBuilder.Icon(MaterialIconKind.SitemapOutline, 14, ThemeService.Brush(ThemeService.PriorityP2Fg)));
        topRow.Children.Add(new TextBlock
        {
            Text = "ARCHITECTURE / PROCESS FLOW DIAGRAM",
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.6,
            Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(topRow);

        // Parse node chains separated by ->, -->, or ==>
        var nodes = Regex.Split(clean, @"\s*(?:-->|==>|->)\s*")
            .Select(n => n.Trim().Trim('[', ']', '(', ')'))
            .Where(n => n.Length > 0)
            .ToList();

        Control diagramBody;
        if (nodes.Count >= 2 && !clean.Contains('\n'))
        {
            var flowWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            for (int i = 0; i < nodes.Count; i++)
            {
                var nodeBox = new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgElevated),
                    BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 7),
                    Margin = new Thickness(0, 3, 0, 3),
                    Child = new TextBlock
                    {
                        Text = nodes[i],
                        FontSize = 12.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                flowWrap.Children.Add(nodeBox);

                if (i + 1 < nodes.Count)
                {
                    flowWrap.Children.Add(new TextBlock
                    {
                        Text = "  →  ",
                        FontSize = 15,
                        FontWeight = FontWeight.Bold,
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0)
                    });
                }
            }

            diagramBody = flowWrap;
        }
        else
        {
            diagramBody = new TextBlock
            {
                Text = clean,
                FontFamily = MonospaceFont,
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap
            };
        }

        stack.Children.Add(new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10),
            Child = diagramBody
        });

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = stack
        };
    }
}
