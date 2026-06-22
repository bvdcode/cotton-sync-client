// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.ViewModels
{
    internal class SelfTestItemRowViewModel : ViewModelBase
    {
        private bool _areDetailsExpanded;
        private string _details = string.Empty;
        private string _name = string.Empty;
        private bool _passed;
        private bool _skipped;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Details
        {
            get => _details;
            set
            {
                if (SetProperty(ref _details, value))
                {
                    OnPropertyChanged(nameof(HasDetails));
                }
            }
        }

        public bool HasDetails => !string.IsNullOrWhiteSpace(Details);

        public bool AreDetailsExpanded
        {
            get => _areDetailsExpanded;
            set => SetProperty(ref _areDetailsExpanded, value);
        }

        public bool Passed
        {
            get => _passed;
            set
            {
                if (SetProperty(ref _passed, value))
                {
                    OnPropertyChanged(nameof(ResultText));
                    OnPropertyChanged(nameof(IsFailed));
                }
            }
        }

        public bool Skipped
        {
            get => _skipped;
            set
            {
                if (SetProperty(ref _skipped, value))
                {
                    OnPropertyChanged(nameof(ResultText));
                    OnPropertyChanged(nameof(IsFailed));
                }
            }
        }

        public string ResultText => Skipped ? "Skipped" : Passed ? "OK" : "Issue";

        public bool IsFailed => !Passed && !Skipped;
    }
}
