using System.Windows;

namespace CSTViewer;

public partial class CropSaveDestinationWindow : Window
{
    internal CropSaveDestinationWindow(MainWindow owner, string destination)
    {
        InitializeComponent();
        Owner = owner;
        CropSavePathTextBox.Text = destination;
        CropSavePathTextBox.SelectAll();
    }

    internal string Destination => CropSavePathTextBox.Text;

    // ShowDialog の戻り値を true にし、入力を確定して閉じる。
    private void ConfirmDestinationButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
