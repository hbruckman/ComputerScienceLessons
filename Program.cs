using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

/// <summary>
/// Minimal markup → HTML converter tailored for the provided document.
/// - Supports headings #..#### (h1–h4) with classes and slugged ids
/// - Supports unordered lists with indentation-based nesting (2 spaces per level)
/// - Preserves existing <code>...</code> inline tags
/// - Converts paragraphs
/// - Passes through <br> tags, normalizing to <br/>
/// - Wraps content in semantic container with a link to styles.css
/// Usage: dotnet run -- input.txt output.html
/// </summary>
class Program
{
	static int Main(string[] args)
	{
		var inputPath = "README.md";
		var outputPath = "docs/index.html";

		if (!File.Exists(inputPath))
		{
			Console.Error.WriteLine($"Input file not found: {inputPath}");
			return 2;
		}

		var lines = File.ReadAllLines(inputPath);
		var html = ConvertToHtml(lines);

		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
		File.WriteAllText(outputPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		Console.WriteLine($"Wrote {outputPath}");
		return 0;
	}

	static string ConvertToHtml(string[] lines)
	{
		var sb = new StringBuilder();

		// Document shell
		sb.AppendLine("<!DOCTYPE html>");
		sb.AppendLine("<html lang=\"en\">");
		sb.AppendLine("<head>");
		sb.AppendLine("\t<meta charset=\"utf-8\"/>");
		sb.AppendLine("\t<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
		sb.AppendLine("\t<title>Computer Science Lessons</title>");
		sb.AppendLine("\t<link rel=\"stylesheet\" href=\"styles.css\"/>");
		sb.AppendLine("</head>");
		sb.AppendLine("<body class=\"page\">");
		sb.AppendLine("\t<main class=\"container\">");
		sb.AppendLine("\t\t<article class=\"doc\" role=\"document\">");

		// State for lists and paragraphs
		var listStack = new Stack<int>(); // holds indent levels
		var paragraphOpen = false;

		// Utility local functions
		void CloseParagraphIfOpen()
		{
			if (paragraphOpen)
			{
				sb.AppendLine("\t\t\t</p>");
				paragraphOpen = false;
			}
		}

		void EnsureListLevel(int level)
		{
			// Open lists to reach desired level
			while (listStack.Count < level)
			{
				int nextLevel = listStack.Count + 1;
				string indent = new string('\t', nextLevel + 2);
				sb.AppendLine($"{indent}<ul class=\"list level-{nextLevel}\">");
				listStack.Push(nextLevel);
			}
			// Close lists to drop to desired level
			while (listStack.Count > level)
			{
				string indent = new string('\t', listStack.Count + 2);
				sb.AppendLine($"{indent}</ul>");
				listStack.Pop();
			}
		}

		void CloseAllLists()
		{
			EnsureListLevel(0);
		}

		foreach (var rawLine in lines)
		{
			var line = rawLine.Replace("\r", "");

			// Normalize explicit <br><br> to <br/><br/>
			line = Regex.Replace(line, @"<\s*br\s*>\s*<\s*br\s*>", "<br/><br/>", RegexOptions.IgnoreCase);

			// Trim end only; keep leading spaces for list indent detection
			var rtrim = line.TrimEnd();

			// Blank line → paragraph break (unless inside list)
			if (string.IsNullOrWhiteSpace(rtrim))
			{
				CloseParagraphIfOpen();
				// do not force-close lists here; blank lines can exist inside list blocks
				continue;
			}

			// Headings: ####, ###, ##, #
			var headingMatch = Regex.Match(rtrim, @"^(#{1,4})\s+(.*)$");
			if (headingMatch.Success)
			{
				CloseParagraphIfOpen();
				CloseAllLists();

				int level = headingMatch.Groups[1].Value.Length;
				string text = headingMatch.Groups[2].Value.Trim();

				string safe = EscapeHtmlExceptInlineCode(text);
				string id = Slugify(text);

				string tag = level switch { 1 => "h1", 2 => "h2", 3 => "h3", 4 => "h4", _ => "h2" };
				string cls = level switch { 1 => "heading h1", 2 => "heading h2", 3 => "heading h3", 4 => "heading h4", _ => "heading" };

				sb.AppendLine($"\t\t\t<{tag} class=\"{cls}\" id=\"{id}\">{safe}</{tag}>");
				continue;
			}

			// Unordered list item: leading spaces (indent), then "- "
			var liMatch = Regex.Match(rtrim, @"^(?<indent>\s*)-\s+(?<text>.+)$");
			if (liMatch.Success)
			{
				CloseParagraphIfOpen();

				int indentSpaces = liMatch.Groups["indent"].Value.Length;
				// define 2 spaces == one nesting level
				int level = Math.Max(1, (indentSpaces / 2) + 1);
				EnsureListLevel(level);

				string text = liMatch.Groups["text"].Value.Trim();
				string safe = EscapeHtmlExceptInlineCode(text);

				string indent = new string('\t', level + 1 + 2);
				sb.AppendLine($"{indent}<li class=\"item\">{safe}</li>");
				continue;
			}

			// A line that is just "<br/>" (or multiple) → passthrough but close paragraphs/lists appropriately
			if (Regex.IsMatch(rtrim, @"^(\s*<br\s*/>\s*)+$", RegexOptions.IgnoreCase))
			{
				CloseParagraphIfOpen();
				CloseAllLists();
				sb.AppendLine($"\t\t\t{rtrim}");
				continue;
			}

			// Any other line → paragraph text (auto-merge consecutive lines)
			CloseAllLists();
			string paraText = EscapeHtmlExceptInlineCode(rtrim);
			if (!paragraphOpen)
			{
				sb.Append("\t\t\t<p class=\"para\">");
				paragraphOpen = true;
			}
			else
			{
				sb.AppendLine("<br/>"); // soft wrap between logical lines inside same paragraph
				sb.Append("\t\t\t");
			}
			sb.Append(paraText);
		}

		// Close any open structures
		CloseParagraphIfOpen();
		CloseAllLists();

		sb.AppendLine("\t\t</article>");
		sb.AppendLine("\t</main>");
		sb.AppendLine("</body>");
		sb.AppendLine("</html>");

		return sb.ToString();
	}

	// Escapes HTML but preserves existing <code>...</code> inline tags in the source.
	// Strategy: temporarily replace <code> blocks with tokens, escape, then restore.
	static string EscapeHtmlExceptInlineCode(string input)
	{
		if (string.IsNullOrEmpty(input)) return "";

		var codeSpans = new List<string>();
		string tokenized = Regex.Replace(
				input,
				@"<\s*code\s*>(.*?)<\s*/\s*code\s*>",
				m =>
				{
					codeSpans.Add(m.Value); // keep as-is
					return $"@@CODE_TOKEN_{codeSpans.Count - 1}@@";
				},
				RegexOptions.Singleline | RegexOptions.IgnoreCase
		);

		string escaped = EscapeHtml(tokenized);

		// Allow explicit <br> tags coming from the source
		escaped = Regex.Replace(escaped, @"&lt;\s*br\s*/?&gt;", "<br/>", RegexOptions.IgnoreCase);
		escaped = ApplyBold(escaped);

		// Restore code spans
		for (int i = 0; i < codeSpans.Count; i++)
		{
			escaped = escaped.Replace($"@@CODE_TOKEN_{i}@@", codeSpans[i]);
		}
		return escaped;
	}

	static string EscapeHtml(string s)
	{
		// Minimal, sufficient escaping
		return s
				.Replace("&", "&amp;")
				.Replace("<", "&lt;")
				.Replace(">", "&gt;")
				.Replace("\"", "&quot;");
	}

	static string Slugify(string s)
	{
		// Lowercase, replace non-alnum with dashes, collapse repeats, trim dashes
		string lower = s.ToLowerInvariant();
		lower = Regex.Replace(lower, @"[^\p{L}\p{Nd}]+", "-");
		lower = Regex.Replace(lower, @"-+", "-");
		return lower.Trim('-');
	}

	// Converts *text* → <b>text</b> (outside of <code>…</code> spans).
	static string ApplyBold(string s)
	{
		// Match a single-asterisk pair where the inside doesn't start/end with whitespace.
		// Avoids **…** and stray asterisks.
		return Regex.Replace(
			s,
			@"(?<!\*)\*(?!\s)(.+?)(?<!\s)\*(?!\*)",
			"<b>$1</b>"
		);
	}
}
