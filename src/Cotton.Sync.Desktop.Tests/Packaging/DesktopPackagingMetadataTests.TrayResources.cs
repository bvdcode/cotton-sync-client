// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Xml.Linq;
using Cotton.Sync.Desktop.Shell;

namespace Cotton.Sync.Desktop.Tests.Packaging
{
    public partial class DesktopPackagingMetadataTests
    {
        [Test]
        public void DesktopProject_EmbedsEveryTrayStatusIconWithoutLooseTaskbarOverlays()
        {
            string projectPath = GetDesktopProjectPath();
            XDocument project = XDocument.Load(projectPath);
            string[] embeddedAssets = project.Root!.Elements("ItemGroup")
                .Elements("AvaloniaResource")
                .Select(static item => item.Attribute("Include")?.Value ?? string.Empty)
                .ToArray();
            string[] looseAssets = project.Root.Elements("ItemGroup")
                .Elements("Content")
                .Select(static item => item.Attribute("Include")?.Value ?? string.Empty)
                .ToArray();
            string projectDirectory = Path.GetDirectoryName(projectPath)!;

            Assert.Multiple(() =>
            {
                Assert.That(embeddedAssets, Does.Contain("Assets/tray-*.ico"));
                Assert.That(looseAssets.Any(static path => path.Contains("taskbar-", StringComparison.Ordinal)), Is.False);
                foreach (DesktopTrayStatusKind kind in Enum.GetValues<DesktopTrayStatusKind>())
                {
                    if (kind == DesktopTrayStatusKind.Unknown)
                    {
                        continue;
                    }

                    Uri icon = DesktopTrayIconAssetResolver.Resolve(kind);
                    string iconPath = Path.Combine(projectDirectory, icon.AbsolutePath.TrimStart('/'));
                    Assert.That(File.Exists(iconPath), Is.True, $"Tray icon for {kind} must exist: {iconPath}");
                }
            });
        }
    }
}
