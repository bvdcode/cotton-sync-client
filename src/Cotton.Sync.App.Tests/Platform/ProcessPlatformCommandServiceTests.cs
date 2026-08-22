// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Platform;

namespace Cotton.Sync.App.Tests.Platform
{
    public class ProcessPlatformCommandServiceTests
    {
        [Test]
        public void OpenFolderAsync_RejectsEmptyPath()
        {
            ProcessPlatformCommandService service = new ProcessPlatformCommandService();

            ArgumentException? exception = Assert.ThrowsAsync<ArgumentException>(
                async () => await service.OpenFolderAsync(" "));

            Assert.That(exception, Is.Not.Null);
        }

        [Test]
        public void OpenWebAsync_RejectsRelativeUrl()
        {
            ProcessPlatformCommandService service = new ProcessPlatformCommandService();

            ArgumentException? exception = Assert.ThrowsAsync<ArgumentException>(
                async () => await service.OpenWebAsync(new Uri("/relative", UriKind.Relative)));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.ParamName, Is.EqualTo("url"));
            });
        }

        [TestCase("file:///C:/Temp/page.html")]
        [TestCase("custom://approval/session")]
        public void OpenWebAsync_RejectsNonHttpUrl(string value)
        {
            ProcessPlatformCommandService service = new();

            ArgumentException? exception = Assert.ThrowsAsync<ArgumentException>(
                async () => await service.OpenWebAsync(new Uri(value)));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.ParamName, Is.EqualTo("url"));
                Assert.That(exception.Message, Does.Contain("HTTP or HTTPS"));
            });
        }
    }
}
