// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.ViewModels
{
    internal partial class ShellViewModel
    {
        public string DashboardStatusTitle => HasActionRequired
            ? "Action required"
            : HeaderStatusText;

        public string DashboardStatusDetails => HasActionRequired
            ? ActionRequiredMessage
            : HasCurrentWorkProgress ? CurrentWorkProgressTitle : CurrentProgressText;

        public string DashboardWorkDetails
        {
            get
            {
                if (!HasCurrentWorkProgress)
                {
                    return string.Empty;
                }

                return string.Join(" · ", new[]
                {
                    HasActionRequired ? CurrentWorkProgressTitle : string.Empty,
                    CurrentWorkProgressDetails,
                    CurrentWorkProgressSecondaryDetails,
                }.Where(static value => !string.IsNullOrWhiteSpace(value)));
            }
        }

        private void RaiseDashboardStatusProperties()
        {
            OnPropertyChanged(nameof(DashboardStatusTitle));
            OnPropertyChanged(nameof(DashboardStatusDetails));
            OnPropertyChanged(nameof(DashboardWorkDetails));
        }
    }
}
