// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests
{
    public class MainWindowSizingTests
    {
        [TestCase(863, 1.0, 540.0, 520.0)]
        [TestCase(863, 1.25, 540.0, 520.0)]
        [TestCase(863, 1.5, 527.333333, 520.0)]
        [TestCase(863, 2.0, 383.5, 383.5)]
        public void CalculateFittedWindowHeight_KeepsWindowInsideScaledWorkingArea(
            int workingAreaPixelHeight,
            double renderScaling,
            double expectedHeight,
            double expectedMinHeight)
        {
            (double height, double minHeight) = MainWindow.CalculateFittedWindowHeight(
                desiredHeight: 540,
                minimumHeight: 520,
                workingAreaPixelHeight,
                renderScaling);

            Assert.Multiple(() =>
            {
                Assert.That(height, Is.EqualTo(expectedHeight).Within(0.000001));
                Assert.That(minHeight, Is.EqualTo(expectedMinHeight).Within(0.000001));
            });
        }

        [Test]
        public void CalculateFittedWindowHeight_FitsSignInProfileAtTwoHundredPercent()
        {
            (double height, double minHeight) = MainWindow.CalculateFittedWindowHeight(
                desiredHeight: 452,
                minimumHeight: 440,
                workingAreaPixelHeight: 863,
                renderScaling: 2.0);

            Assert.Multiple(() =>
            {
                Assert.That(height, Is.EqualTo(383.5));
                Assert.That(minHeight, Is.EqualTo(383.5));
            });
        }

        [Test]
        public void CalculateFittedWindowHeight_UsesProfileDimensionsWhenScreenMetricsAreUnavailable()
        {
            (double height, double minHeight) = MainWindow.CalculateFittedWindowHeight(
                desiredHeight: 540,
                minimumHeight: 520,
                workingAreaPixelHeight: 0,
                renderScaling: 0);

            Assert.Multiple(() =>
            {
                Assert.That(height, Is.EqualTo(540));
                Assert.That(minHeight, Is.EqualTo(520));
            });
        }
    }
}
