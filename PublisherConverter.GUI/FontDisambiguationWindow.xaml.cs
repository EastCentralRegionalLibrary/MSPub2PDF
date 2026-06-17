using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PublisherConverter.Core.FontSources;

namespace PublisherConverter.GUI
{
    /// <summary>
    /// Modal disambiguation dialog shown when a community search returns several
    /// candidates above the confidence threshold. <see cref="SelectedIndex"/> is
    /// the chosen candidate's index; Cancel (or no selection) leaves it -1, which
    /// the caller treats as "cancel this source".
    /// </summary>
    public partial class FontDisambiguationWindow : Window
    {
        public int SelectedIndex { get; private set; } = -1;

        public FontDisambiguationWindow(string fontName, IReadOnlyList<DisambiguationCandidate> candidates)
        {
            InitializeComponent();

            PromptText.Text = $"Several fonts match \"{fontName}\". Choose which to download, or cancel.";

            var items = new List<string>(candidates.Count);
            foreach (var c in candidates)
            {
                items.Add($"{c.Slug}   ({c.SourceId}, {c.Confidence:P0} match)");
            }
            CandidateList.ItemsSource = items;
            if (items.Count > 0) CandidateList.SelectedIndex = 0;

            Loaded += (_, _) => CandidateList.Focus();
        }

        private void Select_Click(object sender, RoutedEventArgs e) => Confirm();

        private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CandidateList.SelectedIndex >= 0) Confirm();
        }

        private void Confirm()
        {
            SelectedIndex = CandidateList.SelectedIndex;
            DialogResult = SelectedIndex >= 0;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedIndex = -1;
            DialogResult = false;
        }
    }
}
