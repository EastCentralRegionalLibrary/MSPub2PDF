using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    public class WorkerBackedFontInstallerTests : IDisposable
    {
        private readonly string _stagingDir;

        public WorkerBackedFontInstallerTests()
        {
            _stagingDir = Path.Combine(Path.GetTempPath(), $"wbfi_{Guid.NewGuid():N}");
        }

        private WorkerBackedFontInstaller Create(FakeElevatedFontWorkerClient worker, FakeFontDownloader downloader)
            => new WorkerBackedFontInstaller(worker, downloader, () => _stagingDir);

        [Fact]
        public async Task InstallFromUrl_DownloadsThenRoutesSystemInstallToWorker()
        {
            var worker = new FakeElevatedFontWorkerClient();
            var downloader = new FakeFontDownloader();
            byte[] payload = Encoding.UTF8.GetBytes("OTTO-fake-font-bytes");
            downloader.RespondWithBytes("https://x/font.ttf", payload);
            var installer = Create(worker, downloader);
            var log = new List<string>();

            bool ok = await installer.InstallFromUrlAsync("Noto Serif KR", "https://x/font.ttf", "NotoSerifKR.ttf", log, CancellationToken.None);

            Assert.True(ok);
            var req = Assert.Single(worker.FontRequests);
            Assert.Equal("Noto Serif KR", req.FamilyName);
            Assert.Equal("NotoSerifKR.ttf", req.FileName);
            Assert.Equal(payload, Assert.Single(worker.FontPayloads));
            // Staged temp file is cleaned up.
            Assert.True(!Directory.Exists(_stagingDir) || Directory.GetFiles(_stagingDir).Length == 0);
        }

        [Fact]
        public async Task InstallFromStream_StagesBytesThenRoutesToWorker()
        {
            var worker = new FakeElevatedFontWorkerClient();
            var downloader = new FakeFontDownloader();
            var installer = Create(worker, downloader);
            byte[] payload = Encoding.UTF8.GetBytes("stream-font");
            using var src = new MemoryStream(payload);
            var log = new List<string>();

            bool ok = await installer.InstallFromStreamAsync("Fam", "fam.otf", src, log, CancellationToken.None);

            Assert.True(ok);
            Assert.Equal("Fam", worker.FontRequests[0].FamilyName);
            Assert.Equal(payload, worker.FontPayloads[0]);
        }

        [Fact]
        public async Task WorkerReportsFailure_ReturnsFalse()
        {
            var worker = new FakeElevatedFontWorkerClient();
            worker.FontOutcomes["Fam"] = InstallOutcome.Failed;
            var downloader = new FakeFontDownloader();
            downloader.RespondWithBytes("https://x/f.ttf", new byte[] { 1, 2, 3 });
            var installer = Create(worker, downloader);

            bool ok = await installer.InstallFromUrlAsync("Fam", "https://x/f.ttf", "f.ttf", new List<string>(), CancellationToken.None);

            Assert.False(ok);
        }

        [Fact]
        public async Task AlreadyPresent_CountsAsSuccess()
        {
            var worker = new FakeElevatedFontWorkerClient();
            worker.FontOutcomes["Fam"] = InstallOutcome.AlreadyPresent;
            var downloader = new FakeFontDownloader();
            downloader.RespondWithBytes("https://x/f.ttf", new byte[] { 9 });
            var installer = Create(worker, downloader);

            bool ok = await installer.InstallFromUrlAsync("Fam", "https://x/f.ttf", "f.ttf", new List<string>(), CancellationToken.None);

            Assert.True(ok);
        }

        [Fact]
        public async Task DownloadFailure_ReturnsFalse_AndDoesNotCallWorker()
        {
            var worker = new FakeElevatedFontWorkerClient();
            var downloader = new FakeFontDownloader(); // no canned response → throws
            var installer = Create(worker, downloader);

            bool ok = await installer.InstallFromUrlAsync("Fam", "https://x/missing.ttf", "f.ttf", new List<string>(), CancellationToken.None);

            Assert.False(ok);
            Assert.Empty(worker.FontRequests);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_stagingDir)) Directory.Delete(_stagingDir, true); } catch { }
        }
    }
}
