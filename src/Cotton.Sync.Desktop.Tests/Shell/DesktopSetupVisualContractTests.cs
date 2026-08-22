// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Shell
{
    public partial class DesktopSetupVisualContractTests
    {
        [Test]
        public void Application_DefaultsToDarkThemeForFirstRun()
        {
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));

            Assert.That(appXaml, Does.Contain("RequestedThemeVariant=\"Dark\""));
        }

        [Test]
        public void Application_UsesCottonAccentPaletteForFluentControls()
        {
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(appXaml, Does.Contain("<FluentTheme>"));
                Assert.That(appXaml, Does.Contain("<FluentTheme.Palettes>"));
                Assert.That(appXaml, Does.Contain("ColorPaletteResources x:Key=\"Light\""));
                Assert.That(appXaml, Does.Contain("ColorPaletteResources x:Key=\"Dark\""));
                Assert.That(CountOccurrences(appXaml, "Accent=\"#96BE02\""), Is.EqualTo(2));
                Assert.That(appXaml, Does.Not.Contain("SystemAccentColor"));
                Assert.That(appXaml, Does.Not.Contain("#8B5CF6"));
            });
        }

        [Test]
        public void Application_RegistersDesktopIconLibrary()
        {
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(appXaml, Does.Contain("Material.Icons.Avalonia"));
                Assert.That(appXaml, Does.Contain("materialIcons:MaterialIconStyles"));
            });
        }

        [Test]
        public void RefreshActions_UseUncircledIcon()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Not.Contain("Kind=\"RefreshCircle\""));
                Assert.That(CountOccurrences(mainWindowXaml, "Kind=\"Refresh\""), Is.EqualTo(4));
                Assert.That(CountOccurrences(mainWindowXaml, "Classes=\"icon flatIcon\""), Is.EqualTo(2));
                Assert.That(CountOccurrences(mainWindowXaml, "Foreground=\"{DynamicResource CottonPrimaryBrush}\""), Is.EqualTo(3));
                Assert.That(appXaml, Does.Contain("Style Selector=\"Button.flatIcon\""));
                Assert.That(appXaml, Does.Contain("Property=\"Background\" Value=\"Transparent\""));
            });
        }

        [Test]
        public void SetupView_DoesNotRenderNumberedStepper()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string appXaml = File.ReadAllText(GetDesktopFilePath("App.axaml"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowXaml, Does.Not.Contain("setupStepBadge"));
                Assert.That(mainWindowXaml, Does.Not.Contain("setupStepLabel"));
                Assert.That(appXaml, Does.Not.Contain("setupStepBadge"));
                Assert.That(appXaml, Does.Not.Contain("setupStepLabel"));
            });
        }

        [Test]
        public void SetupView_StretchesAndScrollsWithoutFixedContentWidth()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string setupView = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsSetupVisible}\"",
                "IsVisible=\"{Binding IsDashboardVisible}\"");

            Assert.Multiple(() =>
            {
                Assert.That(setupView, Does.Not.Contain("Width=\"296\""));
                Assert.That(setupView, Does.Contain("HorizontalAlignment=\"Stretch\""));
                Assert.That(setupView, Does.Contain("Margin=\"20,12\""));
                Assert.That(setupView, Does.Contain("HorizontalScrollBarVisibility=\"Disabled\""));
                Assert.That(setupView, Does.Contain("VerticalScrollBarVisibility=\"Auto\""));
                Assert.That(setupView, Does.Contain("VerticalContentAlignment=\"Center\""));
            });
        }


        [Test]
        public void SetupErrorArea_ReservesSpaceWithoutReflowingTheForm()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string setupErrorArea = GetSlice(
                mainWindowXaml,
                "ToolTip.Tip=\"{Binding ActionRequiredMessage}\"",
                "<StackPanel Spacing=\"8\"");

            Assert.Multiple(() =>
            {
                Assert.That(setupErrorArea, Does.Contain("Opacity=\"{Binding ActionRequiredOpacity}\""));
                Assert.That(setupErrorArea, Does.Not.Contain("IsVisible=\"{Binding HasActionRequired}\""));
            });
        }

        [Test]
        public void SignInInputs_SubmitOnEnterAndReturnKeys()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string mainWindowCode = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml.cs"));
            string signInStep = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsSignInStepVisible}\"",
                "<Button Content=\"Sign in\"");

            Assert.Multiple(() =>
            {
                Assert.That(CountOccurrences(signInStep, "KeyDown=\"SignInInput_KeyDown\""), Is.EqualTo(3));
                Assert.That(signInStep, Does.Contain("Kind=\"Check\""));
                Assert.That(signInStep, Does.Not.Contain("Text=\"✓\""));
                Assert.That(mainWindowCode, Does.Contain("e.Key != Key.Enter && e.Key != Key.Return"));
                Assert.That(mainWindowCode, Does.Contain("viewModel.SignInCommand.Execute(null);"));
            });
        }

        [Test]
        public void PasswordSignIn_UsesActivePrimarySubmitButton()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string signInStep = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsSignInStepVisible}\"",
                "<Grid Grid.Row=\"2\"");
            string signInButton = GetSlice(
                signInStep,
                "<Button Content=\"Sign in\"",
                "</StackPanel>");

            Assert.Multiple(() =>
            {
                Assert.That(signInButton, Does.Contain("Content=\"Sign in\""));
                Assert.That(signInButton, Does.Contain("Classes=\"primary\""));
                Assert.That(signInButton, Does.Contain("Command=\"{Binding SignInCommand}\""));
            });
        }

        [Test]
        public void MainWindow_InitializesShellOnlyOnceAcrossTrayShow()
        {
            string mainWindowCode = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml.cs"));
            string openedHandler = GetSlice(
                mainWindowCode,
                "Opened += async",
                "Closing += OnClosing;");
            string oneShotInitializer = GetSlice(
                mainWindowCode,
                "private async Task InitializeShellOnceAsync",
                "private void ScrollSelectedSyncPairIntoView");

            Assert.Multiple(() =>
            {
                Assert.That(openedHandler, Does.Contain("await InitializeShellOnceAsync(_viewModel).ConfigureAwait(true);"));
                Assert.That(openedHandler, Does.Not.Contain("viewModel.InitializeAsync()"));
                Assert.That(oneShotInitializer, Does.Contain("if (_hasInitializedShell)"));
                Assert.That(oneShotInitializer, Does.Contain("_hasInitializedShell = true;"));
                Assert.That(oneShotInitializer, Does.Contain("await viewModel.InitializeAsync().ConfigureAwait(true);"));
                Assert.That(oneShotInitializer, Does.Contain("ApplyVisualSmokeScenarioAsync"));
            });
        }

        [Test]
        public void MainWindow_UsesExplicitClosedShutdownPath()
        {
            string mainWindowCode = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(mainWindowCode, Does.Contain("Closed += OnClosed;"));
                Assert.That(mainWindowCode, Does.Not.Contain("Closed += async"));
                Assert.That(mainWindowCode, Does.Contain("private void OnClosed"));
                Assert.That(mainWindowCode, Does.Contain("_ = ShutdownShellAsync();"));
                Assert.That(mainWindowCode, Does.Contain("private async Task ShutdownShellAsync()"));
                Assert.That(mainWindowCode, Does.Contain("_logger.LogError(exception, \"Failed to shut down the desktop shell.\");"));
            });
        }

        [Test]
        public void CloudFolderPicker_UsesCompactIconNavigationButtons()
        {
            string mainWindowXaml = File.ReadAllText(GetDesktopFilePath("MainWindow.axaml"));
            string cloudFolderPicker = GetSlice(
                mainWindowXaml,
                "IsVisible=\"{Binding IsAddSyncPairCloudStepVisible}\"",
                "<TextBlock Grid.Row=\"4\"");

            Assert.Multiple(() =>
            {
                Assert.That(cloudFolderPicker, Does.Contain("Kind=\"ArrowLeftCircleOutline\""));
                Assert.That(cloudFolderPicker, Does.Contain("ToolTip.Tip=\"Create cloud folder\""));
                Assert.That(cloudFolderPicker, Does.Contain("ShowCreateRemoteFolderCommand"));
                Assert.That(cloudFolderPicker, Does.Contain("Kind=\"FolderPlusOutline\""));
                Assert.That(cloudFolderPicker, Does.Contain("CreateRemoteFolderCommand"));
                Assert.That(cloudFolderPicker, Does.Contain("Kind=\"ArrowRightCircleOutline\""));
                Assert.That(cloudFolderPicker, Does.Contain("Kind=\"CheckCircleOutline\""));
                Assert.That(cloudFolderPicker, Does.Contain("Kind=\"CloseCircleOutline\""));
                Assert.That(cloudFolderPicker, Does.Contain("Kind=\"ChevronRight\""));
                Assert.That(CountOccurrences(cloudFolderPicker, "Classes=\"icon\""), Is.EqualTo(4));
                Assert.That(cloudFolderPicker, Does.Not.Contain("RowDefinitions=\"Auto,Auto,160,Auto\""));
                Assert.That(cloudFolderPicker, Does.Contain("Text=\"{Binding RemoteFolderFilter, UpdateSourceTrigger=PropertyChanged}\""));
                Assert.That(cloudFolderPicker, Does.Contain("PlaceholderText=\"Search cloud folders\""));
                Assert.That(cloudFolderPicker, Does.Contain("Text=\"{Binding RemoteFolderCountLabel}\""));
                Assert.That(cloudFolderPicker, Does.Contain("IsVisible=\"{Binding HasRemoteFolderCount}\""));
                Assert.That(cloudFolderPicker, Does.Contain("Text=\"{Binding RemoteFolderEmptyTitle}\""));
                Assert.That(cloudFolderPicker, Does.Contain("Text=\"{Binding RemoteFolderEmptySubtitle}\""));
                Assert.That(cloudFolderPicker, Does.Contain("MinHeight=\"132\""));
                Assert.That(cloudFolderPicker, Does.Contain("MaxHeight=\"240\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("MaxHeight=\"160\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Height=\"260\""));
                Assert.That(cloudFolderPicker, Does.Contain("ScrollViewer.VerticalScrollBarVisibility=\"Auto\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Text=\"›\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Content=\"←\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Content=\"→\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Content=\"^\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Content=\">\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Content=\"Up\""));
                Assert.That(cloudFolderPicker, Does.Not.Contain("Content=\"Open\""));
            });
        }


        private static string GetDesktopFilePath(string fileName)
        {
            string directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string candidate = Path.Combine(directory, "src", "Cotton.Sync.Desktop", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                string? parent = Directory.GetParent(directory)?.FullName;
                if (parent == directory)
                {
                    break;
                }

                directory = parent ?? string.Empty;
            }

            throw new FileNotFoundException(fileName + " was not found from the test directory.");
        }

        private static string GetSlice(string text, string startMarker, string endMarker)
        {
            text = NormalizeLineEndings(text);
            startMarker = NormalizeLineEndings(startMarker);
            endMarker = NormalizeLineEndings(endMarker);

            int start = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidOperationException(startMarker + " was not found.");
            }

            int end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException(endMarker + " was not found.");
            }

            return text[start..end];
        }

        private static string NormalizeLineEndings(string value)
        {
            return value.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int currentIndex = 0;
            while (currentIndex < text.Length)
            {
                int nextIndex = text.IndexOf(value, currentIndex, StringComparison.Ordinal);
                if (nextIndex < 0)
                {
                    return count;
                }

                count++;
                currentIndex = nextIndex + value.Length;
            }

            return count;
        }

        private static IReadOnlyList<string> FindIconButtonTooltipsWithoutAutomationName(string xaml)
        {
            List<string> missingAutomationNames = new();
            int currentIndex = 0;

            while (currentIndex < xaml.Length)
            {
                int buttonStart = xaml.IndexOf("<Button", currentIndex, StringComparison.Ordinal);
                if (buttonStart < 0)
                {
                    break;
                }

                int buttonEnd = xaml.IndexOf("</Button>", buttonStart, StringComparison.Ordinal);
                if (buttonEnd < 0)
                {
                    break;
                }

                string buttonBlock = xaml[buttonStart..(buttonEnd + "</Button>".Length)];
                if (buttonBlock.Contains("Classes=\"icon", StringComparison.Ordinal)
                    && buttonBlock.Contains("ToolTip.Tip=", StringComparison.Ordinal)
                    && !buttonBlock.Contains("AutomationProperties.Name=", StringComparison.Ordinal))
                {
                    int line = xaml[..buttonStart].Split('\n').Length;
                    missingAutomationNames.Add("Icon button near line " + line + " has a tooltip but no AutomationProperties.Name.");
                }

                currentIndex = buttonEnd + "</Button>".Length;
            }

            return missingAutomationNames;
        }
    }
}
