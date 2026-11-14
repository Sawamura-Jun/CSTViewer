using System.IO;
using System.Text;
using System.Windows;

namespace CSTViewer;

public partial class OcrResultWindow : Window
{
    internal OcrResultWindow(MainWindow owner, string path, IReadOnlyList<OcrSearchPage> pages)
    {
        InitializeComponent();
        Owner = owner;
        Title = $"OCR results — {Path.GetFileName(path)}";
        OcrResultTextBox.Text = FormatPages(pages);
    }

    internal static string FormatPages(IReadOnlyList<OcrSearchPage> pages)
    {
        var text = new StringBuilder();
        for (int index = 0; index < pages.Count; index++)
        {
            if (index > 0) text.AppendLine();
            text.AppendLine($"Page {index + 1} / {pages.Count}");
            int pageStart = text.Length;
            foreach (var line in pages[index].Lines)
            {
                if (line.Count == 0) continue;
                text.Append(line[0].Text);
                for (int word = 1; word < line.Count; word++)
                {
                    if (OcrTextSearch.NeedsWordSeparator(text[^1], line[word].Text[0])) text.Append(' ');
                    text.Append(line[word].Text);
                }
                text.AppendLine();
            }
            if (text.Length == pageStart) text.AppendLine("No text recognized");
        }
        return text.ToString();
    }
}
