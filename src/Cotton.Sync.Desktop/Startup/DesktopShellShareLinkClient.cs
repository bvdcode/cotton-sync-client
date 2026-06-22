// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cotton;
using Cotton.Auth;
using Cotton.Sdk.Auth;
using Cotton.Sync.App.ShellIntegration;

namespace Cotton.Sync.Desktop.Startup
{
    internal interface IDesktopShellShareLinkClient
    {
        Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
            ShellShareLinkTarget target,
            CancellationToken cancellationToken = default);
    }

    internal sealed record DesktopShellShareLinkResult(
        bool IsApiAvailable,
        bool IsCreated,
        string? ShareLink,
        string? FailureReason)
    {
        public static DesktopShellShareLinkResult Unavailable(string failureReason) =>
            new(false, false, null, failureReason);

        public static DesktopShellShareLinkResult Failed(string failureReason) =>
            new(true, false, null, failureReason);

        public static DesktopShellShareLinkResult Created(Uri shareUri) =>
            new(true, true, shareUri.AbsoluteUri, null);
    }

    internal sealed class DesktopShellShareLinkClient : IDesktopShellShareLinkClient
    {
        private const int DefaultExpireAfterMinutes = 1440;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly ICottonTokenStore _tokenStore;
        private readonly ICottonAuthClient _authClient;
        private readonly Uri _serverUrl;

        public DesktopShellShareLinkClient(
            HttpClient httpClient,
            ICottonTokenStore tokenStore,
            ICottonAuthClient authClient,
            Uri serverUrl)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
        }

        public async Task<DesktopShellShareLinkResult> CreateShareLinkAsync(
            ShellShareLinkTarget target,
            CancellationToken cancellationToken = default)
        {
            if (!target.CanCreateShareLink)
            {
                return DesktopShellShareLinkResult.Failed("target-not-shareable");
            }

            string path = target.RemoteFileId.HasValue
                ? CreateFileShareLinkPath(target.RemoteFileId.Value)
                : CreateFolderShareLinkPath(target.RemoteNodeId!.Value);
            return await GetShareLinkAsync(path, target.RemoteFileId.HasValue, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<DesktopShellShareLinkResult> GetShareLinkAsync(
            string path,
            bool convertDownloadLinkToShareLink,
            CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAuthorizedGetAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? reasonPhrase = response.ReasonPhrase;
                if (reasonPhrase is not null
                    && (string.Equals(reasonPhrase, "auth-token-missing", StringComparison.Ordinal)
                        || string.Equals(reasonPhrase, "auth-refresh-failed", StringComparison.Ordinal)))
                {
                    return DesktopShellShareLinkResult.Failed(reasonPhrase);
                }

                return DesktopShellShareLinkResult.Failed(
                    "api-status-" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            string? serverLink = await response.Content
                .ReadFromJsonAsync<string>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serverLink))
            {
                return DesktopShellShareLinkResult.Failed("empty-share-link");
            }

            Uri? shareUri = convertDownloadLinkToShareLink
                ? TryCreateFileShareUri(serverLink, out Uri? fileShareUri) ? fileShareUri : null
                : CreateRouteUri(_serverUrl, serverLink);
            return shareUri is null
                ? DesktopShellShareLinkResult.Failed("share-token-missing")
                : DesktopShellShareLinkResult.Created(shareUri);
        }

        private async Task<HttpResponseMessage> SendAuthorizedGetAsync(
            string path,
            CancellationToken cancellationToken)
        {
            TokenPairDto? tokens = await _tokenStore.GetAsync(cancellationToken).ConfigureAwait(false);
            if (tokens is null)
            {
                return CreateLocalFailureResponse(HttpStatusCode.Unauthorized, "auth-token-missing");
            }

            HttpResponseMessage response = await SendAuthorizedGetOnceAsync(path, tokens.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            TokenPairDto refreshed;
            try
            {
                refreshed = await _authClient.RefreshAsync(tokens.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return CreateLocalFailureResponse(HttpStatusCode.Unauthorized, "auth-refresh-failed");
            }

            return await SendAuthorizedGetOnceAsync(path, refreshed.AccessToken, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendAuthorizedGetOnceAsync(
            string path,
            string accessToken,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CreateRouteUri(_serverUrl, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static HttpResponseMessage CreateLocalFailureResponse(HttpStatusCode statusCode, string reason)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(reason, options: JsonOptions),
                ReasonPhrase = reason,
            };
        }

        private static string CreateFolderShareLinkPath(Guid nodeId)
        {
            return Routes.V1.Layouts
                + "/nodes/"
                + nodeId.ToString("D")
                + "/share-link?expireAfterMinutes="
                + DefaultExpireAfterMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string CreateFileShareLinkPath(Guid nodeFileId)
        {
            return Routes.V1.Files
                + "/"
                + nodeFileId.ToString("D")
                + "/download-link?expireAfterMinutes="
                + DefaultExpireAfterMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private bool TryCreateFileShareUri(string downloadLink, out Uri? shareUri)
        {
            shareUri = null;
            if (!TryExtractToken(downloadLink, out string? token))
            {
                return false;
            }

            shareUri = CreateRouteUri(_serverUrl, "/s/" + Uri.EscapeDataString(token!));
            return true;
        }

        private bool TryExtractToken(string link, out string? token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(link))
            {
                return false;
            }

            Uri uri = CreateRouteUri(_serverUrl, link);
            string queryToken = GetQueryParameter(uri.Query, "token");
            if (!string.IsNullOrWhiteSpace(queryToken))
            {
                token = queryToken;
                return true;
            }

            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int downloadIndex = Array.FindIndex(
                segments,
                static segment => string.Equals(segment, "download", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "d", StringComparison.OrdinalIgnoreCase));
            if (downloadIndex >= 0 && downloadIndex + 1 < segments.Length)
            {
                token = Uri.UnescapeDataString(segments[downloadIndex + 1]);
                return !string.IsNullOrWhiteSpace(token);
            }

            string? last = segments.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
            {
                token = Uri.UnescapeDataString(last);
                return true;
            }

            return false;
        }

        private static string GetQueryParameter(string query, string name)
        {
            string normalized = query.TrimStart('?');
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            foreach (string pair in normalized.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=', 2);
                string key = Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal));
                if (!string.Equals(key, name, StringComparison.Ordinal))
                {
                    continue;
                }

                return parts.Length == 2
                    ? Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal))
                    : string.Empty;
            }

            return string.Empty;
        }

        private static Uri CreateRouteUri(Uri baseAddress, string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absoluteUri)
                && IsHttpUri(absoluteUri))
            {
                return absoluteUri;
            }

            string relative = path.TrimStart('/');
            int queryIndex = relative.IndexOf('?', StringComparison.Ordinal);
            string relativePath = queryIndex >= 0 ? relative[..queryIndex] : relative;
            string query = queryIndex >= 0 ? relative[(queryIndex + 1)..] : string.Empty;
            string basePath = baseAddress.AbsolutePath.TrimEnd('/');
            string combinedPath = string.IsNullOrEmpty(basePath)
                ? "/" + relativePath
                : basePath + "/" + relativePath;

            var builder = new UriBuilder(baseAddress)
            {
                Path = combinedPath,
                Query = query,
            };
            return builder.Uri;
        }

        private static bool IsHttpUri(Uri uri)
        {
            return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }
    }
}
