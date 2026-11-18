using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace CSTViewer;

internal sealed record OcrSearchPage(IReadOnlyList<IReadOnlyList<OcrWordRegion>> Lines, Size PixelSize);
internal readonly record struct OcrWordRegion(string Text, Rect Bounds);

internal static class OcrTextSearch
{
    private static readonly Regex WidthVariants = new("[\u3000\uff01-\uffee]+");

    private static readonly Dictionary<string, string> FullSizeKana = new(StringComparer.Ordinal)
    {
        ["ぁ"] = "あ", ["ぃ"] = "い", ["ぅ"] = "う", ["ぇ"] = "え", ["ぉ"] = "お",
        ["っ"] = "つ", ["ゃ"] = "や", ["ゅ"] = "ゆ", ["ょ"] = "よ", ["ゎ"] = "わ",
        ["ゕ"] = "か", ["ゖ"] = "け",
        ["ァ"] = "ア", ["ィ"] = "イ", ["ゥ"] = "ウ", ["ェ"] = "エ", ["ォ"] = "オ",
        ["ッ"] = "ツ", ["ャ"] = "ヤ", ["ュ"] = "ユ", ["ョ"] = "ヨ", ["ヮ"] = "ワ",
        ["ヵ"] = "カ", ["ヶ"] = "ケ",

        // アイヌ語などで使われる小書きのカタカナ。
        ["ㇰ"] = "ク", ["ㇱ"] = "シ", ["ㇲ"] = "ス", ["ㇳ"] = "ト", ["ㇴ"] = "ヌ",
        ["ㇵ"] = "ハ", ["ㇶ"] = "ヒ", ["ㇷ"] = "フ", ["ㇸ"] = "ヘ", ["ㇹ"] = "ホ",
        ["ㇺ"] = "ム", ["ㇻ"] = "ラ", ["ㇼ"] = "リ", ["ㇽ"] = "ル", ["ㇾ"] = "レ", ["ㇿ"] = "ロ",

        // 小書き仮名拡張の文字は、UTF-16 のサロゲートペアを1組として置き換える。
        ["\U0001B132"] = "こ", ["\U0001B150"] = "ゐ", ["\U0001B151"] = "ゑ", ["\U0001B152"] = "を",
        ["\U0001B155"] = "コ", ["\U0001B164"] = "ヰ", ["\U0001B165"] = "ヱ", ["\U0001B166"] = "ヲ",
        ["\U0001B167"] = "ン", ["\U0001B168"] = "\U0001B121"
    };
    private static readonly Regex SmallKana = new(string.Join('|', FullSizeKana.Keys));

    public static Rect[][] FindMatches(IReadOnlyList<OcrSearchPage> pages, string query, CancellationToken token)
    {
        query = NormalizeSearchText(query).Trim();
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
                string word = NormalizeSearchText(line[index].Text);
                // OCRが分けた日本語はつなぎ、英数字の単語間には空白を入れる。
                if (text.Length > 0 && NeedsWordSeparator(text[^1], word[0]))
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

    internal static bool NeedsWordSeparator(char previous, char next) => !IsJapanese(previous) && !IsJapanese(next);

    private static bool IsJapanese(char character) =>
        character is >= '\u3001' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uff65' and <= '\uff9f';

    private static string NormalizeSearchText(string text)
    {
        // 濁点付きの半角カナをまとめて全角化してから、小書き仮名を通常の大きさにそろえる。
        string widthNormalized = WidthVariants.Replace(text, static match => match.Value.Normalize(NormalizationForm.FormKC));
        return SmallKana.Replace(widthNormalized, static match => FullSizeKana[match.Value]);
    }
}
