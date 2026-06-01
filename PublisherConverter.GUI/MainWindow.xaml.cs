using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using PublisherConverter.Core;

namespace PublisherConverter.GUI
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        private readonly ConverterEngine _engine;

        private ObservableCollection<string> LogMessages { get; set; } = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();

            LstConsoleLog.ItemsSource = LogMessages;

            var inspector = new PublisherInspector();
            var hashProvider = new HashProvider();
            var manifestWriter = new ManifestWriter();
            var renderer = new PublisherLifecycleManager();

            _engine = new ConverterEngine(
                inspector,
                hashProvider,
                manifestWriter,
                renderer,
                (path, compress) => new ArchiveService(path, compress)
            );
        }

        // Folder Picker helper logic using standard Windows Dialog hooks
        private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Root Source Directory containing .pub Files" };
            if (dialog.ShowDialog() == true) TxtSourcePath.Text = dialog.FolderName;
        }

        private void BtnBrowseArchive_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Archival Workspace Target Folder" };
            if (dialog.ShowDialog() == true) TxtArchivePath.Text = dialog.FolderName;
        }

        // Core execution processing initialization loop
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            string sourceDir = TxtSourcePath.Text.Trim();
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                // FIX: Changed MessageBoxIcon to MessageBoxImage.Warning
                MessageBox.Show("Please select a valid root source folder layout before continuing.", "Path Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtRecycleInterval.Text, out int recycleInterval) || recycleInterval <= 0)
            {
                // FIX: Changed MessageBoxIcon to MessageBoxImage.Warning
                MessageBox.Show("Please enter a valid positive integer value for the engine recycle limit.", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtTimeout.Text, out int timeoutSeconds) || timeoutSeconds <= 0)
            {
                MessageBox.Show("Please enter a valid positive integer value for the file timeout.", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Configure layout interaction lockout bounds
            ToggleUiControls(isRunning: true);

            ProgressIndicator.Value = 0;

            _cts = new CancellationTokenSource();

            // Direct thread configuration translation loop update channel
            var progressHandler = new Progress<ProgressReport>(report =>
            {
                if (!string.IsNullOrEmpty(report.CurrentActionMessage))
                {
                    LblStatusMessage.Text = report.CurrentActionMessage;
                    AppendConsoleLog(report.CurrentActionMessage);
                }

                if (report.TotalFiles > 0)
                {
                    double percent = ((double)report.ProcessedFiles / report.TotalFiles) * 100;
                    ProgressIndicator.Value = percent;
                    LblProgressCounts.Text = $"{report.ProcessedFiles} / {report.TotalFiles} files ({Math.Round(percent, 0)}%)";

                    if (report.CurrentFile != null)
                    {
                        string outcome = $"[{report.CurrentFile.Status}] {report.CurrentFile.FileName} -> {report.CurrentFile.Details}";
                        AppendConsoleLog(outcome);
                    }
                }
            });

            var runOptions = new ConversionOptions
            {
                SourcePath = sourceDir,
                ArchivePath = TxtArchivePath.Text.Trim(),
                RunLinkCheck = ChkRunLinkCheck.IsChecked ?? true,
                DeleteSourceOnSuccess = ChkDeleteSource.IsChecked ?? false,
                ProcessRecycleInterval = recycleInterval,
                CompressArchive = ChkCompressArchive.IsChecked ?? true,
                FileTimeoutSeconds = timeoutSeconds
            };

            try
            {
                AppendConsoleLog("Initializing batch transformation sequence pipeline...");

                // Spawn migration processing task onto a separate worker thread pass
                await _engine.ExecuteMigrationAsync(runOptions, progressHandler, _cts.Token);

                // FIX: Changed MessageBoxInformation to MessageBoxImage.Information
                MessageBox.Show("Migration pipeline processing run completed. Review manifest CSV file details inside source path target root.", "Run Finished", MessageBoxButton.OK, MessageBoxImage.Information);
                LblStatusMessage.Text = "Migration process successfully finalized.";
            }
            catch (OperationCanceledException)
            {
                AppendConsoleLog("CRITICAL: Conversion routine sequence aborted by administrative supervisor request.");
                LblStatusMessage.Text = "Operation cancelled.";
                // FIX: Changed MessageBoxIcon to MessageBoxImage.Information
                MessageBox.Show("Transformation pass cancelled safely. Scratch items cleared.", "Aborted", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendConsoleLog($"CRITICAL RUN PIPELINE EXCEPTION: {ex.Message}");
                // FIX: Changed MessageBoxIcon to MessageBoxImage.Error
                MessageBox.Show($"Pipeline process execution failure: {ex.Message}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                ToggleUiControls(isRunning: false);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            BtnCancel.IsEnabled = false;
            LblStatusMessage.Text = "Sending abort interrupt signal to active threads...";
            _cts?.Cancel();
        }

        private void ToggleUiControls(bool isRunning)
        {
            BtnStart.IsEnabled = !isRunning;
            BtnCancel.IsEnabled = isRunning;

            TxtSourcePath.IsEnabled = !isRunning;
            TxtArchivePath.IsEnabled = !isRunning;
            BtnBrowseSource.IsEnabled = !isRunning;
            BtnBrowseArchive.IsEnabled = !isRunning;
            ChkRunLinkCheck.IsEnabled = !isRunning;
            ChkDeleteSource.IsEnabled = !isRunning;
            TxtRecycleInterval.IsEnabled = !isRunning;
            ChkCompressArchive.IsEnabled = !isRunning;
            TxtTimeout.IsEnabled = !isRunning;
        }

        private void AppendConsoleLog(string message)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

                // Keep a maximum buffer of 500 items
                if (LogMessages.Count > 500)
                {
                    LogMessages.RemoveAt(0);
                }

                // Scroll to the latest entry
                if (LogMessages.Count > 0)
                {
                    LstConsoleLog.ScrollIntoView(LogMessages[LogMessages.Count - 1]);
                }
            });
        }
    }
}