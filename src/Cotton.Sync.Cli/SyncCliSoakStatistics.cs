// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal class SyncCliSoakStatistics : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();

        public SyncCliSoakStatistics(int? durationSeconds)
        {
            StartedAt = DateTime.UtcNow;
            StartedCpu = _process.TotalProcessorTime;
            StartedWorkingSetBytes = GetWorkingSetBytes();
            StartedManagedMemoryBytes = GetManagedMemoryBytes();
            PeakWorkingSetBytes = StartedWorkingSetBytes;
            PeakManagedMemoryBytes = StartedManagedMemoryBytes;
            StopAt = durationSeconds.HasValue
                ? StartedAt.AddSeconds(durationSeconds.Value)
                : null;
        }

        public int CompletedIterations { get; private set; }

        public int? FinalConvergenceActivities { get; private set; }

        public int? FinalStateEntries { get; private set; }

        public bool IsConverged => SyncErrors == 0 && FinalConvergenceActivities == 0;

        public TimeSpan LongestIterationElapsed { get; private set; }

        public long PeakManagedMemoryBytes { get; private set; }

        public long PeakWorkingSetBytes { get; private set; }

        public DateTime StartedAt { get; }

        public TimeSpan StartedCpu { get; }

        public long StartedManagedMemoryBytes { get; }

        public long StartedWorkingSetBytes { get; }

        public DateTime? StopAt { get; }

        public int SyncErrors { get; private set; }

        public int TotalActivities { get; private set; }

        public TimeSpan TotalIterationElapsed { get; private set; }

        public int FailureCount => SyncErrors == 0 && FinalConvergenceActivities.GetValueOrDefault() > 0
            ? 1
            : SyncErrors;

        public void CaptureResourcePeaks()
        {
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, GetWorkingSetBytes());
            PeakManagedMemoryBytes = Math.Max(PeakManagedMemoryBytes, GetManagedMemoryBytes());
        }

        public TimeSpan GetCpuTime()
        {
            _process.Refresh();
            return _process.TotalProcessorTime - StartedCpu;
        }

        public long GetManagedMemoryBytes()
        {
            return GC.GetTotalMemory(forceFullCollection: false);
        }

        public long GetWorkingSetBytes()
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }

        public void RecordConvergence(SyncCliPassResult pass, SyncCliPassResult? secondPass)
        {
            FinalConvergenceActivities = pass.Result.TotalActivityCount
                + (secondPass?.Result.TotalActivityCount ?? 0);
            FinalStateEntries = pass.StateEntries.Count + (secondPass?.StateEntries.Count ?? 0);
            CaptureResourcePeaks();
        }

        public void RecordError()
        {
            SyncErrors++;
            CaptureResourcePeaks();
        }

        public void RecordIteration(
            SyncCliPassResult pass,
            SyncCliPassResult? secondPass,
            TimeSpan elapsed)
        {
            CompletedIterations++;
            TotalActivities += pass.Result.TotalActivityCount + (secondPass?.Result.TotalActivityCount ?? 0);
            TotalIterationElapsed += elapsed;
            LongestIterationElapsed = TimeSpan.FromTicks(Math.Max(LongestIterationElapsed.Ticks, elapsed.Ticks));
            CaptureResourcePeaks();
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }
}
