// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using EasyExtensions.EntityFrameworkCore.Abstractions;

namespace Cotton.Sync.App.SyncPairs
{
    [Table("sync_pair_settings")]
    internal class SyncPairSettingsEntity : BaseEntity<Guid>
    {
        public SyncPairSettingsEntity()
        {
        }

        public SyncPairSettingsEntity(Guid id)
        {
            Id = id;
        }

        [Required]
        [MaxLength(256)]
        [Column("display_name")]
        public string DisplayName { get; set; } = null!;

        [Required]
        [MaxLength(4096)]
        [Column("local_root_path")]
        public string LocalRootPath { get; set; } = null!;

        [Column("remote_root_node_id")]
        public Guid RemoteRootNodeId { get; set; }

        [Required]
        [MaxLength(4096)]
        [Column("remote_display_path")]
        public string RemoteDisplayPath { get; set; } = null!;

        [Column("is_enabled")]
        public bool IsEnabled { get; set; }

        [Column("mode")]
        public SyncPairMode Mode { get; set; }

    }
}
