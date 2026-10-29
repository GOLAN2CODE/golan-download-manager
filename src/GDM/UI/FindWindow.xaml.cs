using System.Windows;

namespace Golan.Gdm.UI;

public partial class FindWindow : Window
{
    public string Query { get; private set; } = "";
    public bool InName { get; private set; } = true;
    public bool InDescription { get; private set; } = true;
    public bool InAddress { get; private set; } = true;
    public bool MatchCase { get; private set; }
    public bool WholeString { get; private set; }

    public FindWindow(string initial)
    {
        InitializeComponent();
        QueryBox.Text = initial;
    }

    void Find_Click(object sender, RoutedEventArgs e)
    {
        Query = QueryBox.Text;
        InName = NameBox.IsChecked == true;
        InDescription = DescriptionBox.IsChecked == true;
        InAddress = AddressBox.IsChecked == true;
        MatchCase = CaseBox.IsChecked == true;
        WholeString = WholeBox.IsChecked == true;
        DialogResult = true;
    }
}
