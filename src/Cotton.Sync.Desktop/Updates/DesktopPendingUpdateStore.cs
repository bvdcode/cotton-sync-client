// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Text.Json;

namespace Cotton.Sync.Desktop.Updates
{
    internal sealed class DesktopPendingUpdateStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        public DesktopPendingUpdateStore(string updateCacheDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(updateCacheDirectory);
            PendingUpdatePath = Path.Combine(updateCacheDirectory, "pending-update.json");
        }

        public string PendingUpdatePath { get; }

        public void Save(DesktopPendingUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);
            Directory.CreateDirectory(Path.GetDirectoryName(PendingUpdatePath) ?? AppContext.BaseDirectory);
            string json = JsonSerializer.Serialize(update, JsonOptions);
            File.WriteAllText(PendingUpdatePath, json + Environment.NewLine);
        }

        public DesktopPendingUpdate? TryLoad()
        {
            if (!File.Exists(PendingUpdatePath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(PendingUpdatePath);
                return JsonSerializer.Deserialize<DesktopPendingUpdate>(json, JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Delete();
                return null;
            }
        }

        public void Delete()
        {
            if (File.Exists(PendingUpdatePath))
            {
                File.Delete(PendingUpdatePath);
            }
        }
    }
}
