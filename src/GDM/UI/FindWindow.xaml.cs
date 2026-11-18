using System.Windows;
using Golan.Gdm.Core;

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
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        };
    }

    void Find_Click(object sender, RoutedEventArgs e)
    {
        if (NameBox.IsChecked != true && DescriptionBox.IsChecked != true && AddressBox.IsChecked != true)
        {
            ErrorText.Text = Loc.T("win.find.err_no_scope");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Query = QueryBox.Text;
        InName = NameBox.IsChecked == true;
        InDescription = DescriptionBox.IsChecked == true;
        InAddress = AddressBox.IsChecked == true;
        MatchCase = CaseBox.IsChecked == true;
        WholeString = WholeBox.IsChecked == true;
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = true;
        else
            Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
