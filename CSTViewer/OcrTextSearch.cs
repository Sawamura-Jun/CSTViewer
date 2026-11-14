using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace CSTViewer;

internal sealed record OcrSearchPage(IReadOnlyList<IReadOnlyList<OcrWordRegion>> Lines, Size PixelSize);
internal readonly record struct OcrWordRegion(string Text, Rect Bounds);

internal static class OcrTextSearch
{
    private static readonly Regex WidthVariants = new("[\u3000\uff01-\uffee]+");

    public static Rect[][] FindMatches(IReadOnlyList<OcrSearchPage> pages, string query, CancellationToken token)
    {
        query = NormalizeWidth(query).Trim();
        var matches = new Rect[pages.Count][];
        for (int index = 0; index < pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            matches[index] = query.Length == 0 ? [] : FindPageMatches(pages[index], query, token);
        }
        return matches;
    }

    private static Rect[] FindPageMatches(OcrSearchPage page, string query, CancellationToken token)
    {
        var matches = new List<Rect>();
        foreach (IReadOnlyList<OcrWordRegion> line in page.Lines)
        {
            token.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var wordRanges = new (int Start, int End)[line.Count];
            for (int index = 0; index < line.Count; index++)
            {
                string word = NormalizeWidth(line[index].Text);
                // OCRが分けた日本語はつなぎ、英数字の単語間には空白を入れる。
                if (text.Length > 0 && !IsJapanese(text[^1]) && !IsJapanese(word[0]))
                    text.Append(' ');
                int start = text.Length;
                text.Append(word);
                wordRanges[index] = (start, text.Length);
            }

            string lineText = text.ToString();
            int offset = 0;
            int wordIndex = 0;
            while (offset < lineText.Length)
            {
                int start = lineText.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                int end = start + query.Length;
                while (wordRanges[wordIndex].End <= start) wordIndex++;
                var bounds = line[wordIndex].Bounds;
                for (int index = wordIndex + 1; index < line.Count && wordRanges[index].Start < end; index++)
                    bounds.Union(line[index].Bounds);
                // プレビューの解像度に依存しないよう、元ページに対する比率で保存する。
                matches.Add(new Rect(bounds.X / page.PixelSize.Width, bounds.Y / page.PixelSize.Height,
                    bounds.Width / page.PixelSize.Width, bounds.Height / page.PixelSize.Height));
                offset = end;
            }
        }
        return matches.ToArray();
    }

    private static bool IsJapanese(char character) =>
        character is >= '\u3001' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uff65' and <= '\uff9f';

    // 濁点付きの半角カナを含む幅違いを、単語ごとにまとめて正規化する。
    private static string NormalizeWidth(string text) =>
        WidthVariants.Replace(text, match => match.Value.Normalize(NormalizationForm.FormKC));
}
