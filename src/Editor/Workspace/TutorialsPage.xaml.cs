using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class TutorialsPage : UserControl
{
    private static readonly string[] codeTopics = ["Структура проекта", "Первая сцена на KRL", "Выборы и переходы в коде"];
    private static readonly string[] graphTopics = ["Структура проекта", "Первая сцена в блок-схеме", "Выборы и переходы между блоками"];

    public TutorialsPage()
    {
        InitializeComponent();
        TraditionalMode.IsChecked = true;
    }

    public event RoutedEventHandler? BackRequested;

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, e);

    private void Mode_Checked(object sender, RoutedEventArgs e) =>
        TutorialList.ItemsSource = ReferenceEquals(sender, VisualNodesMode) ? graphTopics : codeTopics;
}
