using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace CSTViewer;

public partial class HelpWindow : Window
{
    private readonly Brush _tableHeaderBrush = new SolidColorBrush(Color.FromRgb(43, 43, 43));
    private readonly Brush _tableDividerBrush = new SolidColorBrush(Color.FromRgb(61, 61, 61));
    private readonly Brush _buttonMarkBrush = new SolidColorBrush(Color.FromRgb(48, 48, 48));

    internal HelpWindow(MainWindow owner)
    {
        InitializeComponent();
        Owner = owner;
        HelpLanguageComboBox.ItemsSource = HelpContent.Languages;
        HelpLanguageComboBox.SelectedItem = HelpContent.WindowsDefaultLanguage();
    }

    private void HelpLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HelpLanguageComboBox.SelectedItem is not HelpContent help) return;

        Title = help.Title;
        HelpLanguageLabel.Content = help.LanguageLabel;
        Language = XmlLanguage.GetLanguage(help.LanguageCode);
        var document = new FlowDocument
        {
            FontFamily = this.FontFamily,
            FontSize = ViewerSettings.HelpBodyFontSize,
            TextAlignment = TextAlignment.Left,
            PagePadding = new Thickness(0, 0, 12, 12)
        };

        document.Blocks.Add(CreateHeading(help.Toolbar.Title));
        Table toolbarTable = CreateTable(1.5, 1.15, 2.95);
        AddTableHeader(toolbarTable, help.Toolbar.ButtonHeader, help.Toolbar.ShortcutHeader, help.FunctionHeader);
        foreach (var (buttonName, label, shortcut, description) in help.Toolbar.Rows)
        {
            AddTableRow(toolbarTable, CreateButtonLabel(buttonName, label), TableText(shortcut), TableText(description));
        }
        document.Blocks.Add(toolbarTable);

        AddCropSection(document, help.Crop);

        document.Blocks.Add(CreateHeading(help.Mouse.Title));
        Table mouseTable = CreateTable(1.5, 4.1);
        AddTableHeader(mouseTable, help.Mouse.ActionHeader, help.FunctionHeader);
        foreach (var (action, description) in help.Mouse.Rows)
        {
            AddTableRow(mouseTable, TableText(action), TableText(description));
        }
        document.Blocks.Add(mouseTable);

        HelpDocumentViewer.Document = document;
    }

    private static Paragraph CreateHeading(string title) => new(new Run(title))
    {
        FontSize = ViewerSettings.HelpHeadingFontSize,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 8)
    };

    private static void AddCropSection(FlowDocument document, HelpCrop crop)
    {
        document.Blocks.Add(CreateHeading(crop.Title));
        var steps = new List
        {
            MarkerStyle = TextMarkerStyle.Decimal,
            Padding = new Thickness(28, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 4)
        };
        foreach (var (action, description) in crop.Steps)
        {
            var paragraph = new Paragraph(new Bold(new Run(action))) { Margin = new Thickness(0, 0, 0, 6) };
            paragraph.Inlines.Add(new Run("  " + description));
            steps.ListItems.Add(new ListItem(paragraph));
        }
        document.Blocks.Add(steps);
        document.Blocks.Add(new Paragraph(new Run(crop.Shortcuts)) { Margin = new Thickness(0, 6, 0, 6) });

        var destination = new Paragraph(new Bold(new Run("Save to"))) { Margin = new Thickness(0, 6, 0, 6) };
        destination.Inlines.Add(new Run(crop.SaveToDescription));
        document.Blocks.Add(destination);
    }

    private static Table CreateTable(params double[] columnWidths)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 4) };
        foreach (double width in columnWidths)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(width, GridUnitType.Star) });
        }
        table.RowGroups.Add(new TableRowGroup());
        return table;
    }

    private void AddTableHeader(Table table, params string[] headers)
    {
        TableRow row = AddTableRow(table, headers.Select(TableText).ToArray());
        row.FontWeight = FontWeights.SemiBold;
        row.Background = _tableHeaderBrush;
    }

    private TableRow AddTableRow(Table table, params Paragraph[] columns)
    {
        var row = new TableRow();
        foreach (Paragraph column in columns)
        {
            row.Cells.Add(new TableCell(column)
            {
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush = _tableDividerBrush,
                BorderThickness = new Thickness(0, 0, 0, 1)
            });
        }
        table.RowGroups[0].Rows.Add(row);
        return row;
    }

    private Paragraph CreateButtonLabel(string buttonName, string label)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), FontWeight = FontWeights.SemiBold };
        // ツールバーの字形と反転設定を複製し、ヘルプでも同じアイコンを表示する。
        object? content = (Owner.FindName(buttonName) as ButtonBase)?.Content;
        FrameworkElement? mark = content switch
        {
            ToolbarIcon icon => new ToolbarIcon { Glyph = icon.Glyph, IsMirrored = icon.IsMirrored, Foreground = Foreground },
            string text => new TextBlock
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            _ => null
        };
        if (mark is not null)
        {
            var badge = new Border
            {
                Width = 30,
                Height = ViewerSettings.ToolbarButtonHeight,
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(4),
                Background = _buttonMarkBrush,
                Child = mark
            };
            paragraph.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
        }
        paragraph.Inlines.Add(new Run(label));
        return paragraph;
    }

    private static Paragraph TableText(string text) => new(new Run(text)) { Margin = new Thickness(0) };
}
