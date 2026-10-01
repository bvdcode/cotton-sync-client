// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Nodes;
using Cotton.Sync;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotton.Sync.Desktop.Startup
{
    internal partial class LiveSyncManifestBarrier
    {
        private const int MaximumObservedResponses = 16;
        private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        private readonly Dictionary<Guid, List<string>> _fileResponses = [];

        public void ObserveFile(Guid fileId)
        {
            lock (_gate)
            {
                _fileResponses.TryAdd(fileId, []);
            }
        }

        public string ReadFileResponses(Guid fileId)
        {
            lock (_gate)
            {
                return string.Join(";", _fileResponses[fileId]);
            }
        }

        private async Task ObserveFileResponseAsync(
            HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_fileResponses.Count == 0 || !response.IsSuccessStatusCode || request.Method != HttpMethod.Get)
                {
                    return;
                }
            }
            string? path = request.RequestUri?.AbsolutePath;
            if (path == "/api/v1/sync/changes")
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                SyncChangesResponseDto feed = JsonSerializer.Deserialize<SyncChangesResponseDto>(bytes, ResponseJson)!;
                foreach (SyncChangeDto change in feed.Changes)
                {
                    RecordFileResponse(change.ItemId, $"feed:{change.Id}:{change.Kind}:{change.FileManifestId}");
                }
            }
            else if (path is not null && path.EndsWith("/children", StringComparison.Ordinal))
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                NodeContentDto children = JsonSerializer.Deserialize<NodeContentDto>(bytes, ResponseJson)!;
                foreach (Cotton.Files.NodeFileManifestDto file in children.Files)
                {
                    RecordFileResponse(file.Id, $"tree:{file.ContentHash}:{file.FileManifestId}");
                }
            }
        }

        private void RecordFileResponse(Guid fileId, string value)
        {
            lock (_gate)
            {
                if (!_fileResponses.TryGetValue(fileId, out List<string>? values))
                {
                    return;
                }
                values.Add(value);
                if (values.Count > MaximumObservedResponses)
                {
                    values.RemoveAt(0);
                }
            }
        }
    }
}
