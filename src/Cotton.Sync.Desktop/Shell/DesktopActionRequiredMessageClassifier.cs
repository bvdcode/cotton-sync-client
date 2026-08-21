// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Text.Json;

namespace Cotton.Sync.Desktop.Shell
{
    internal static class DesktopActionRequiredMessageClassifier
    {
        public static string? ExtractResponseMessage(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody) || LooksLikeHtml(responseBody))
            {
                return null;
            }

            string trimmed = responseBody.Trim();
            if (!trimmed.StartsWith('{'))
            {
                return trimmed;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                JsonElement root = document.RootElement;
                if (TryGetStringProperty(root, "message", out string? message)
                    || TryGetStringProperty(root, "detail", out message)
                    || TryGetStringProperty(root, "title", out message))
                {
                    return message;
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        public static string? ExtractEmbeddedResponseBody(string message)
        {
            const string marker = "Response:";
            int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            string responseBody = message[(markerIndex + marker.Length)..].Trim();
            if (!responseBody.StartsWith('{'))
            {
                return responseBody;
            }

            int endIndex = responseBody.LastIndexOf('}');
            return endIndex >= 0 ? responseBody[..(endIndex + 1)] : responseBody;
        }

        public static bool LooksLikeCreateFileFromChunksBadRequest(string message)
        {
            return message.Contains("POST /api/v1/files/from-chunks", StringComparison.OrdinalIgnoreCase)
                && message.Contains("status 400", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGenericBadRequestMessage(string? message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && string.Equals(message.Trim(), "Bad request", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
        {
            value = null;
            if (!element.TryGetProperty(propertyName, out JsonElement property)
                || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        public static bool LooksLikeMissingDesktopSyncChangesApi(string message, string? responseBody)
        {
            return message.Contains("GET /api/v1/sync/changes", StringComparison.Ordinal)
                && (LooksLikeHtmlInsteadOfJson(message, responseBody)
                    || message.Contains("status 404", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(responseBody?.Trim(), "404 page not found", StringComparison.OrdinalIgnoreCase));
        }

        public static bool LooksLikeHtmlInsteadOfJson(string message, string? responseBody)
        {
            return (message.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase)
                    && (message.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                        || LooksLikeHtml(responseBody)
                        || LooksLikeHtml(message)))
                || LooksLikeJsonParserHtmlStartMessage(message);
        }

        public static bool LooksLikeJsonParserHtmlStartMessage(string message)
        {
            return message.Contains("'<' is an invalid start of a value", StringComparison.OrdinalIgnoreCase)
                || message.Contains("\"<\" is an invalid start of a value", StringComparison.OrdinalIgnoreCase);
        }

        public static bool LooksLikeDiskFull(string message)
        {
            return message.Contains("no space left on device", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase)
                || message.Contains("disk full", StringComparison.OrdinalIgnoreCase);
        }

        public static bool LooksLikeLocalPermissionDenied(string message)
        {
            return (message.Contains("local file", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase))
                || (message.Contains("access to the path", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("is denied", StringComparison.OrdinalIgnoreCase));
        }

        public static bool LooksLikeLocalFileUnavailable(string message)
        {
            return message.Contains("local file", StringComparison.OrdinalIgnoreCase)
                && message.Contains("could not be scanned safely", StringComparison.OrdinalIgnoreCase);
        }

        public static bool LooksLikeLocalSyncFolderMissing(string message)
        {
            return message.Contains("local root does not exist", StringComparison.OrdinalIgnoreCase)
                || (message.Contains("local sync root", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        }

        public static bool LooksLikeLocalSyncStateDatabaseUnavailable(string message)
        {
            return message.Contains("SQLite Error", StringComparison.OrdinalIgnoreCase)
                && message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("sync_change_cursors", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("sync_entries", StringComparison.OrdinalIgnoreCase));
        }

        public static bool LooksLikeLocalStateDatabaseCorrupt(string message)
        {
            return message.Contains("SQLite Error", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("file is encrypted or is not a database", StringComparison.OrdinalIgnoreCase));
        }

        public static string? ExtractSingleQuotedPath(string message)
        {
            int start = message.IndexOf('\'');
            if (start < 0)
            {
                return null;
            }

            int end = message.IndexOf('\'', start + 1);
            return end > start + 1 ? message[(start + 1)..end] : null;
        }

        public static bool LooksLikeHtml(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.TrimStart();
            return trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }
    }
}
