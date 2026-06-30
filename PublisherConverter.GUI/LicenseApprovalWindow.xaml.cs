using System.Windows;
using PublisherConverter.Core.FontSources;

namespace PublisherConverter.GUI
{
    /// <summary>
    /// Modal license-review dialog shown when a community/non-Microsoft font
    /// candidate needs manual approval before install. <c>DialogResult == true</c>
    /// means Install; the default (and cancel) action is Skip.
    /// </summary>
    public partial class LicenseApprovalWindow : Window
    {
        private const string NoLicenseNotice =
            "No license text was found in the download. DaFont fonts are typically " +
            "free for personal use only — confirm you have the right to install this.";

        public LicenseApprovalWindow(LicenseApprovalRequest request)
        {
            InitializeComponent();

            FontNameText.Text = request.FontName;
            SourceText.Text = string.IsNullOrWhiteSpace(request.SourceUrl)
                ? request.SourceId
                : $"{request.SourceId}  —  {request.SourceUrl}";
            ReasonText.Text = request.LicenseReason;

            if (!string.IsNullOrWhiteSpace(request.LicenseText))
            {
                LicenseTextBox.Text = request.LicenseText;
            }
            else
            {
                LicenseTextBox.Text = NoLicenseNotice;
                LicenseTextBox.FontStyle = FontStyles.Italic;
            }

            // Safe default: focus Skip.
            Loaded += (_, _) => SkipButton.Focus();
        }

        private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
