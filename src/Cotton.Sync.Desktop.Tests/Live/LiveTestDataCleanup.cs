// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sdk.Auth;

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal class LiveTestDataCleanup(
        ICottonCloudClient client, HttpClient http, ICottonTokenStore tokens, Uri server)
    {
        public const string NamespacePrefix = "SyncLoadAcceptance-";
        private const int PageSize = 100;
        private const string OriginalParentsKey = "originalParentsV1";

        public async Task ClearAbandonedAsync(CancellationToken cancellationToken)
        {
            using JsonDocument info = await GetAsync<JsonDocument>("api/v1/server/info", cancellationToken);
            if (!info.RootElement.GetProperty("isPublicInstance").GetBoolean())
            {
                throw new InvalidOperationException("Abandoned-test cleanup requires a public QA instance.");
            }
            await ClearAsync(null, cancellationToken);
        }

        public async Task ClearAsync(Guid? namespaceId, CancellationToken cancellationToken)
        {
            NodeDto activeRoot = await client.Nodes.ResolveAsync(cancellationToken: cancellationToken);
            List<NodeDto> folders = [];
            List<NodeFileManifestDto> files = [];
            await foreach (NodeContentDto page in ReadPagesAsync(activeRoot.Id, trash: false, cancellationToken))
            {
                folders.AddRange(page.Nodes.Where(node => IsNamespace(node.Name)
                    && (!namespaceId.HasValue || node.Id == namespaceId.Value)));
            }
            await CollectTrashAsync(namespaceId, folders, files, cancellationToken);

            foreach (NodeFileManifestDto file in files.DistinctBy(file => file.Id))
            {
                await client.Files.DeleteAsync(file.Id, skipTrash: true, cancellationToken: cancellationToken);
            }
            foreach (NodeDto folder in folders.DistinctBy(folder => folder.Id))
            {
                await client.Nodes.DeleteAsync(folder.Id, skipTrash: true, cancellationToken);
            }
            TestContext.Progress.WriteLine($"Test data cleanup: {folders.Count} namespaces/folders and {files.Count} trashed files.");
        }

        private async Task CollectTrashAsync(Guid? namespaceId, List<NodeDto> folders,
            List<NodeFileManifestDto> files, CancellationToken cancellationToken)
        {
            NodeDto trashRoot = await GetAsync<NodeDto>("api/v1/layouts/resolver?nodeType=trash", cancellationToken);
            Queue<Guid> pending = new([trashRoot.Id]);
            HashSet<Guid> visited = [];
            while (pending.TryDequeue(out Guid nodeId))
            {
                if (!visited.Add(nodeId))
                {
                    throw new InvalidDataException("Repeated node in test-account trash listing.");
                }
                await foreach (NodeContentDto page in ReadPagesAsync(nodeId, trash: true, cancellationToken))
                {
                    files.AddRange(page.Files.Where(file => BelongsToNamespace(file.Metadata, namespaceId)));
                    foreach (NodeDto child in page.Nodes)
                    {
                        if ((IsNamespace(child.Name) && (!namespaceId.HasValue || child.Id == namespaceId.Value))
                            || BelongsToNamespace(child.Metadata, namespaceId))
                        {
                            folders.Add(child);
                        }
                        else
                        {
                            pending.Enqueue(child.Id);
                        }
                    }
                }
            }
        }

        public async Task<long> RequireAvailableBytesAsync(long requiredBytes, CancellationToken cancellationToken)
        {
            using JsonDocument quota = await GetAsync<JsonDocument>("api/v1/users/me/storage-quota", cancellationToken);
            long used = quota.RootElement.GetProperty("usedBytes").GetInt64();
            JsonElement available = quota.RootElement.GetProperty("availableBytes");
            TestContext.Progress.WriteLine($"Test-account storage: used={used}, available={available}, required={requiredBytes} bytes.");
            if (available.ValueKind != JsonValueKind.Null && available.GetInt64() < requiredBytes)
            {
                throw new InvalidOperationException("The test account does not have enough quota for this scenario.");
            }
            return used;
        }

        public async Task<long> ReadUsedBytesAsync(CancellationToken cancellationToken)
        {
            using JsonDocument quota = await GetAsync<JsonDocument>("api/v1/users/me/storage-quota", cancellationToken);
            return quota.RootElement.GetProperty("usedBytes").GetInt64();
        }

        internal static bool IsNamespace(string name)
        {
            return name.Length == NamespacePrefix.Length + 32
                && name.StartsWith(NamespacePrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(name[NamespacePrefix.Length..], "N", out _);
        }

        internal static bool BelongsToNamespace(IReadOnlyDictionary<string, string> metadata, Guid? namespaceId)
        {
            if (!metadata.TryGetValue(OriginalParentsKey, out string? savedParents))
            {
                return false;
            }
            using JsonDocument parents = JsonDocument.Parse(savedParents);
            foreach (JsonElement parent in parents.RootElement.EnumerateArray())
            {
                Guid id = parent.GetProperty("id").GetGuid();
                string name = parent.GetProperty("name").GetString() ?? string.Empty;
                if (IsNamespace(name) && (!namespaceId.HasValue || id == namespaceId.Value))
                {
                    return true;
                }
            }
            return false;
        }

        private async IAsyncEnumerable<NodeContentDto> ReadPagesAsync(
            Guid nodeId, bool trash,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            int seen = 0;
            for (int page = 1; ; page++)
            {
                string kind = trash ? "trash" : "default";
                string path = $"api/v1/layouts/nodes/{nodeId}/children?nodeType={kind}&page={page}&pageSize={PageSize}&depth=0";
                using HttpRequestMessage request = await CreateRequestAsync(path, cancellationToken);
                using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                NodeContentDto content = await response.Content.ReadFromJsonAsync<NodeContentDto>(cancellationToken)
                    ?? throw new InvalidDataException("Empty test-account directory listing.");
                int total = int.Parse(response.Headers.GetValues("X-Total-Count").Single(), CultureInfo.InvariantCulture);
                int count = content.Nodes.Count + content.Files.Count;
                seen += count;
                yield return content;
                if (seen == total)
                {
                    yield break;
                }
                if (count == 0 || seen > total)
                {
                    throw new InvalidDataException("Test-account directory listing changed during cleanup.");
                }
            }
        }

        private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = await CreateRequestAsync(path, cancellationToken);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new InvalidDataException("Empty test-account response.");
        }

        private async Task<HttpRequestMessage> CreateRequestAsync(string path, CancellationToken cancellationToken)
        {
            TokenPairDto session = await tokens.GetAsync(cancellationToken)
                ?? throw new InvalidOperationException("Test-account session is unavailable.");
            HttpRequestMessage request = new(HttpMethod.Get, new Uri(server, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            return request;
        }
    }
}
