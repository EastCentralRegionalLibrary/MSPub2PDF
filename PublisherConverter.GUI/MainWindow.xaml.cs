using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.GUI
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        private readonly ConverterEngine _engine;

        // Tracks absolute source paths attempted during the most recent run
        // (success or failure). Populated from progress reports so a follow-up
        // "Continue" pass can skip them and process the remaining files.
        private readonly HashSet<string> _attemptedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<string> LogMessages { get; set; } = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();

            LstConsoleLog.ItemsSource = LogMessages;

            var inspector = new PublisherInspector();
            var hashProvider = new HashProvider();
            var manifestWriter = new ManifestWriter();
            var renderer = new PublisherLifecycleManager();

            // Shared cache so the auditor and the resolver agree on
            // installed-font state. The resolver mutates the cache after
            // successful provisioning; the next per-file audit picks it up.
            var fontCache = new FontAvailabilityCache(new WindowsRegistryFontProvider());
            var fontAuditor = new FontAuditor(new PublisherFontExtractor(), fontCache);

            // Mapping ships next to the GUI binary; missing/malformed file
            // collapses to an empty table and the resolver simply finds
            // nothing to provision.
            string mappingPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "FontMapping.json");
            var fontMappings = FontMappingLoader.LoadFromFile(mappingPath);

            // Shared HTTP plumbing. The download/install strategies are built
            // per cycle by the coordinator so it can pick the right install
            // sink: system-wide via the elevated worker when elevated installs
            // are permitted, or user-level (HKCU) otherwise. Strategy order is
            // intentional — Google Fonts (cleanest config), then GitHub
            // (config-driven), then the generic direct-URL fallback.
            var downloader = new HttpFontDownloader();
            var fontLogger = CreateMainStructuredLogger();

            IReadOnlyList<IFontProvisioningStrategy> BuildDownloadStrategies(IElevatedFontWorkerClient? worker)
            {
                IUserFontInstaller installSink = worker != null
                    ? new WorkerBackedFontInstaller(worker, downloader)   // system-wide via elevated worker
                    : new WindowsUserFontInstaller(downloader);           // user-level HKCU
                return new IFontProvisioningStrategy[]
                {
                    new GoogleFontsProvisioningStrategy(fontMappings, downloader, installSink),
                    new GitHubFontsProvisioningStrategy(fontMappings, downloader, installSink),
                    new DownloadableFontProvisioningStrategy(fontMappings, installSink),
                };
            }

            // The worker client launches the current executable in
            // --mode=font-worker, elevated via the "runas" verb. Capability
            // installs are routed here as a single batch; the main process never
            // performs privileged installation itself.
            IElevatedFontWorkerClient BuildWorkerClient() => new ElevatedFontWorkerClient(
                new FontWorkerProcessLauncher(),
                pipeName => new NamedPipeFontWorkerTransport(pipeName),
                new DefaultWorkerHealthMonitor(),
                fontLogger);

            var fontService = new FontManagementService(
                fontMappings,
                fontCache,
                BuildWorkerClient,
                BuildDownloadStrategies,
                fontLogger);

            _engine = new ConverterEngine(
                inspector,
                hashProvider,
                manifestWriter,
                renderer,
                fontAuditor,
                fontService,
                (path, compress) => new ArchiveService(path, compress)
            );
        }

        /// <summary>
        /// Structured log sink for the main (non-elevated) process. JSON Lines
        /// to a per-session file under local app data; falls back to a no-op if
        /// the file can't be opened.
        /// </summary>
        private static IStructuredLogger CreateMainStructuredLogger()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MSPub2PDF", "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"main-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl");
                var writer = new StreamWriter(path, append: true) { AutoFlush = true };
                return new JsonLinesStructuredLogger(writer, source: "main", ownsWriter: true);
            }
            catch
            {
                return NullStructuredLogger.Instance;
            }
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

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            _attemptedSourcePaths.Clear();
            BtnContinue.Visibility = Visibility.Collapsed;
            await RunMigrationAsync(resuming: false);
        }

        private async void BtnContinue_Click(object sender, RoutedEventArgs e)
        {
            BtnContinue.Visibility = Visibility.Collapsed;
            AppendConsoleLog($"Resuming run, skipping {_attemptedSourcePaths.Count} previously attempted file(s).");
            await RunMigrationAsync(resuming: true);
        }

        private async System.Threading.Tasks.Task RunMigrationAsync(bool resuming)
        {
            string sourceDir = TxtSourcePath.Text.Trim();
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                MessageBox.Show("Please select a valid root source folder layout before continuing.", "Path Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool enableRecycle = ChkEnableRecycle.IsChecked ?? false;
            int recycleInterval = 0;
            if (enableRecycle && (!int.TryParse(TxtRecycleInterval.Text, out recycleInterval) || recycleInterval <= 0))
            {
                MessageBox.Show("Please enter a valid positive integer value for the engine recycle limit.", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool enableCircuitBreaker = ChkEnableCircuitBreaker.IsChecked ?? false;
            int maxFailures = 0;
            if (enableCircuitBreaker && (!int.TryParse(TxtMaxFailures.Text, out maxFailures) || maxFailures <= 0))
            {
                MessageBox.Show("Please enter a valid positive integer value for the circuit breaker failure threshold.", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtTimeout.Text, out int timeoutSeconds) || timeoutSeconds <= 0)
            {
                MessageBox.Show("Please enter a valid positive integer value for the file timeout.", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ToggleUiControls(isRunning: true);
            ProgressIndicator.Value = 0;
            _cts = new CancellationTokenSource();

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

                        // Track every file the engine actually attempted so a
                        // follow-up "Continue" pass can skip it.
                        if (report.CurrentFile.Status != MigrationStatus.Pending
                            && !string.IsNullOrEmpty(report.CurrentFile.OriginalFullPath))
                        {
                            _attemptedSourcePaths.Add(report.CurrentFile.OriginalFullPath);
                        }
                    }
                }
            });

            var runOptions = new ConversionOptions
            {
                SourcePath = sourceDir,
                ArchivePath = TxtArchivePath.Text.Trim(),
                RunLinkCheck = ChkRunLinkCheck.IsChecked ?? true,
                DeleteSourceOnSuccess = ChkDeleteSource.IsChecked ?? false,
                EnableProcessRecycle = enableRecycle,
                ProcessRecycleInterval = recycleInterval,
                EnableCircuitBreaker = enableCircuitBreaker,
                MaxConsecutiveFailures = maxFailures,
                CompressArchive = ChkCompressArchive.IsChecked ?? true,
                FileTimeoutSeconds = timeoutSeconds,
                EnableAutoFontInstallation = ChkEnableAutoFontInstall.IsChecked ?? false,
                AllowElevatedFontInstallation = ChkAllowElevatedFontInstall.IsChecked ?? false,
                MissingFontHandling = (CmbMissingFontMode.SelectedIndex == 1)
                    ? MissingFontHandlingMode.NotifyOnly
                    : MissingFontHandlingMode.Strict,
                OverrideFontSkip = ChkOverrideFontSkip.IsChecked ?? false,
                SkipSourcePaths = resuming ? new HashSet<string>(_attemptedSourcePaths, StringComparer.OrdinalIgnoreCase) : null
            };

            try
            {
                AppendConsoleLog(resuming
                    ? "Resuming batch transformation sequence pipeline..."
                    : "Initializing batch transformation sequence pipeline...");

                await _engine.ExecuteMigrationAsync(runOptions, progressHandler, _cts.Token);

                MessageBox.Show("Migration pipeline processing run completed. Review manifest CSV file details inside source path target root.", "Run Finished", MessageBoxButton.OK, MessageBoxImage.Information);
                LblStatusMessage.Text = "Migration process successfully finalized.";

                // Successful completion clears any prior attempted-path tracking.
                _attemptedSourcePaths.Clear();
            }
            catch (OperationCanceledException)
            {
                AppendConsoleLog("CRITICAL: Conversion routine sequence aborted by administrative supervisor request.");
                LblStatusMessage.Text = "Operation cancelled.";
                MessageBox.Show("Transformation pass cancelled safely. Scratch items cleared.", "Aborted", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (CircuitBreakerTrippedException ex)
            {
                // Make sure every file the engine reached is in the skip list
                // for the next attempt — progress reports normally cover this,
                // but the exception carries the authoritative list.
                foreach (var path in ex.AttemptedSourcePaths)
                {
                    _attemptedSourcePaths.Add(path);
                }

                AppendConsoleLog($"Halted: {ex.Message}");
                LblStatusMessage.Text = "Halted due to consecutive failures — click Continue to skip them and resume.";
                BtnContinue.Visibility = Visibility.Visible;

                MessageBox.Show(
                    $"{ex.Message}\n\nClick 'Continue (Skip Attempted)' to resume processing the remaining files.",
                    "Run Halted",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendConsoleLog($"CRITICAL RUN PIPELINE EXCEPTION: {ex.Message}");
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
            BtnContinue.IsEnabled = !isRunning;

            TxtSourcePath.IsEnabled = !isRunning;
            TxtArchivePath.IsEnabled = !isRunning;
            BtnBrowseSource.IsEnabled = !isRunning;
            BtnBrowseArchive.IsEnabled = !isRunning;
            ChkRunLinkCheck.IsEnabled = !isRunning;
            ChkDeleteSource.IsEnabled = !isRunning;
            ChkEnableRecycle.IsEnabled = !isRunning;
            TxtRecycleInterval.IsEnabled = !isRunning;
            ChkEnableCircuitBreaker.IsEnabled = !isRunning;
            TxtMaxFailures.IsEnabled = !isRunning;
            ChkCompressArchive.IsEnabled = !isRunning;
            TxtTimeout.IsEnabled = !isRunning;
            ChkEnableAutoFontInstall.IsEnabled = !isRunning;
            ChkAllowElevatedFontInstall.IsEnabled = !isRunning;
            CmbMissingFontMode.IsEnabled = !isRunning;
            ChkOverrideFontSkip.IsEnabled = !isRunning;
        }

        private void AppendConsoleLog(string message)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

                if (LogMessages.Count > 500)
                {
                    LogMessages.RemoveAt(0);
                }

                if (LogMessages.Count > 0)
                {
                    LstConsoleLog.ScrollIntoView(LogMessages[LogMessages.Count - 1]);
                }
            });
        }
    }
}
