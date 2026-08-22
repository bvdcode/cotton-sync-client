// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using EasyExtensions.EntityFrameworkCore.Abstractions;

namespace Cotton.Sync.App.Preferences
{
    [Table("app_preferences")]
    internal class AppPreferencesEntity : BaseEntity<int>
    {
        public AppPreferencesEntity()
        {
        }

        public AppPreferencesEntity(int id)
        {
            Id = id;
        }

        [MaxLength(2048)]
        [Column("remembered_server_url")]
        public string? RememberedServerUrl { get; set; }

        [MaxLength(320)]
        [Column("remembered_username")]
        public string? RememberedUsername { get; set; }

        [Column("start_with_operating_system")]
        public bool StartWithOperatingSystem { get; set; }

        [Column("start_minimized_to_tray")]
        public bool StartMinimizedToTray { get; set; }

        [Column("enable_notifications")]
        public bool EnableNotifications { get; set; }

        [Column("is_sync_paused")]
        public bool IsSyncPaused { get; set; }

        [Column("theme_mode")]
        public AppThemeMode ThemeMode { get; set; }

    }
}
