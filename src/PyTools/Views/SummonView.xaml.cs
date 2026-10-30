using System.Windows.Controls;

namespace PyTools.Views
{
    public partial class SummonView : UserControl
    {
        public SummonView()
        {
            InitializeComponent();
        }

        public TextBox SearchBox { get { return PART_SearchBox; } }

        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }
}
