// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cotton.Files;
using Cotton.Nodes;

namespace Cotton.Sync.Desktop.Tests.Live
{
    internal class LiveTestDataCleanupHandler : HttpMessageHandler
    {
        public Guid RootId { get; } = Guid.NewGuid();
        public Guid TrashId { get; } = Guid.NewGuid();
        public Guid WrapperId { get; } = Guid.NewGuid();
        public Guid FirstNamespaceId { get; } = Guid.NewGuid();
        public Guid SecondNamespaceId { get; } = Guid.NewGuid();
        public Guid FirstFileId { get; } = Guid.NewGuid();
        public Guid SecondFileId { get; } = Guid.NewGuid();
        public bool IsPublic { get; set; } = true;
        public List<string> Deletes { get; } = [];
        public List<string> Reads { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                Deletes.Add(request.RequestUri.PathAndQuery);
                return Task.FromResult(JsonResponse(Guid.NewGuid()));
            }
            Reads.Add(request.RequestUri.PathAndQuery);
            if (path.EndsWith("/server/info", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(new { isPublicInstance = IsPublic }));
            }
            if (path.EndsWith("/users/me/storage-quota", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(new { usedBytes = 900L, quotaBytes = 1000L, availableBytes = 100L }));
            }
            if (path.EndsWith("/resolver", StringComparison.Ordinal))
            {
                Guid id = request.RequestUri.Query.Contains("trash", StringComparison.Ordinal) ? TrashId : RootId;
                return Task.FromResult(JsonResponse(new NodeDto { Id = id, Name = "Root" }));
            }
            NodeContentDto content = new();
            int total;
            if (path.Contains(RootId.ToString(), StringComparison.Ordinal))
            {
                bool firstPage = request.RequestUri.Query.Contains("page=1&", StringComparison.Ordinal);
                content.Nodes = firstPage
                    ? [Namespace(FirstNamespaceId)]
                    : [Namespace(SecondNamespaceId), new NodeDto { Id = Guid.NewGuid(), Name = "Documents" },
                        new NodeDto { Id = Guid.NewGuid(), Name = LiveTestDataCleanup.NamespacePrefix + "notes" }];
                total = 4;
            }
            else if (path.Contains(TrashId.ToString(), StringComparison.Ordinal))
            {
                content.Nodes = [new NodeDto { Id = WrapperId, Name = "Trash item" }];
                total = 1;
            }
            else if (path.Contains(WrapperId.ToString(), StringComparison.Ordinal))
            {
                content.Files =
                [
                    new NodeFileManifestDto { Id = FirstFileId, Metadata = Parents(FirstNamespaceId, Namespace(FirstNamespaceId).Name) },
                    new NodeFileManifestDto { Id = SecondFileId, Metadata = Parents(SecondNamespaceId, Namespace(SecondNamespaceId).Name) },
                    new NodeFileManifestDto { Id = Guid.NewGuid(), Metadata = Parents(Guid.NewGuid(), "Documents") },
                    new NodeFileManifestDto { Id = Guid.NewGuid(), Name = LiveTestDataCleanup.NamespacePrefix + FirstNamespaceId.ToString("N") },
                ];
                total = 4;
            }
            else
            {
                throw new InvalidOperationException("Unexpected test request: " + path);
            }
            HttpResponseMessage response = JsonResponse(content);
            response.Headers.Add("X-Total-Count", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }

        private static NodeDto Namespace(Guid id)
        {
            return new NodeDto { Id = id, Name = LiveTestDataCleanup.NamespacePrefix + id.ToString("N") };
        }

        private static Dictionary<string, string> Parents(Guid id, string name)
        {
            return new() { ["originalParentsV1"] = JsonSerializer.Serialize(new[] { new { id, name } }) };
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        }
    }
}
