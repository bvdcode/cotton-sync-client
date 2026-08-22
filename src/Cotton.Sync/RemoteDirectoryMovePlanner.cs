// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using static Cotton.Sync.SyncPathOperations;

namespace Cotton.Sync
{
    internal class RemoteDirectoryMovePlanner(ILogger logger)
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public List<RemoteDirectoryMoveCandidate> FindRemoteDirectoryMoveCandidates(
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            CancellationToken cancellationToken)
        {
            List<RemoteDirectoryMoveCandidate> accepted = [];
            foreach (KeyValuePair<string, SyncStateEntry> source in directoryStateByPath
                         .OrderBy(entry => GetPathDepth(entry.Value.RelativePath))
                         .ThenBy(entry => entry.Value.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryCreateRemoteDirectoryMoveCandidate(
                        source,
                        accepted,
                        localDirectoriesByPath,
                        localFilesByPath,
                        directoryStateByPath,
                        fileStateByPath,
                        remoteDirectoriesById,
                        remoteFilesById,
                        out RemoteDirectoryMoveCandidate candidate))
                {
                    accepted.Add(candidate);
                }
            }

            return accepted;
        }

        private bool TryCreateRemoteDirectoryMoveCandidate(
            KeyValuePair<string, SyncStateEntry> source,
            IReadOnlyCollection<RemoteDirectoryMoveCandidate> accepted,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            out RemoteDirectoryMoveCandidate candidate)
        {
            candidate = default;
            if (!source.Value.RemoteNodeId.HasValue || !localDirectoriesByPath.ContainsKey(source.Key))
            {
                return false;
            }

            if (!remoteDirectoriesById.TryGetValue(
                    source.Value.RemoteNodeId.Value,
                    out RemoteDirectorySnapshot? target)
                || string.Equals(source.Value.RelativePath, target.RelativePath, StringComparison.Ordinal))
            {
                return false;
            }

            string sourceKey = SyncPath.ToKey(source.Value.RelativePath);
            if (accepted.Any(existing => IsSameOrDescendantPathKey(sourceKey, existing.SourceKey)))
            {
                return false;
            }

            candidate = new RemoteDirectoryMoveCandidate(
                source.Value.RelativePath,
                target.RelativePath,
                sourceKey,
                SyncPath.ToKey(target.RelativePath));
            if (!CanCoalesceRemoteDirectoryMove(
                    candidate,
                    localDirectoriesByPath,
                    localFilesByPath,
                    directoryStateByPath,
                    fileStateByPath,
                    remoteDirectoriesById,
                    remoteFilesById,
                    out string? rejectionReason))
            {
                logger.LogInformation(
                    "Remote directory move from {SourcePath} to {TargetPath} was not coalesced: {Reason}",
                    candidate.SourcePath,
                    candidate.TargetPath,
                    rejectionReason);
                return false;
            }

            logger.LogInformation(
                "Remote directory move from {SourcePath} to {TargetPath} passed stable-id validation.",
                candidate.SourcePath,
                candidate.TargetPath);
            return true;
        }

        public static Dictionary<Guid, RemoteDirectorySnapshot> BuildUniqueRemoteDirectoriesById(
            IEnumerable<RemoteDirectorySnapshot> directories)
        {
            Dictionary<Guid, RemoteDirectorySnapshot> unique = new Dictionary<Guid, RemoteDirectorySnapshot>();
            HashSet<Guid> duplicates = [];
            foreach (RemoteDirectorySnapshot directory in directories)
            {
                if (!unique.TryAdd(directory.Node.Id, directory))
                {
                    duplicates.Add(directory.Node.Id);
                }
            }

            foreach (Guid duplicate in duplicates)
            {
                unique.Remove(duplicate);
            }

            return unique;
        }

        public static Dictionary<Guid, RemoteFileSnapshot> BuildUniqueRemoteFilesById(
            IEnumerable<RemoteFileSnapshot> files)
        {
            Dictionary<Guid, RemoteFileSnapshot> unique = new Dictionary<Guid, RemoteFileSnapshot>();
            HashSet<Guid> duplicates = [];
            foreach (RemoteFileSnapshot file in files)
            {
                if (!unique.TryAdd(file.File.Id, file))
                {
                    duplicates.Add(file.File.Id);
                }
            }

            foreach (Guid duplicate in duplicates)
            {
                unique.Remove(duplicate);
            }

            return unique;
        }

        private static bool CanCoalesceRemoteDirectoryMove(
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById,
            out string? rejectionReason)
        {
            if (!PathComparer.Equals(candidate.SourceKey, candidate.TargetKey)
                && IsSameOrDescendantPathKey(candidate.TargetKey, candidate.SourceKey))
            {
                rejectionReason = "the target path is inside the source subtree";
                return false;
            }

            HashSet<string> sourceDirectoryKeys = localDirectoriesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, candidate.SourceKey))
                .ToHashSet(PathComparer);
            HashSet<string> sourceFileKeys = localFilesByPath.Keys
                .Where(key => IsSameOrDescendantPathKey(key, candidate.SourceKey))
                .ToHashSet(PathComparer);
            rejectionReason = FindRemoteDirectoryMoveLocalCollision(
                candidate,
                sourceDirectoryKeys,
                sourceFileKeys,
                localDirectoriesByPath,
                localFilesByPath);
            if (rejectionReason is not null)
            {
                return false;
            }

            rejectionReason = ValidateTrackedRemoteDirectoryMoveDirectories(
                candidate,
                directoryStateByPath,
                localDirectoriesByPath,
                remoteDirectoriesById);
            if (rejectionReason is not null)
            {
                return false;
            }

            rejectionReason = ValidateTrackedRemoteDirectoryMoveFiles(
                candidate,
                fileStateByPath,
                localFilesByPath,
                remoteFilesById);
            return rejectionReason is null;
        }

        private static string? FindRemoteDirectoryMoveLocalCollision(
            RemoteDirectoryMoveCandidate candidate,
            IReadOnlySet<string> sourceDirectoryKeys,
            IReadOnlySet<string> sourceFileKeys,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath)
        {
            foreach (string sourceKey in sourceDirectoryKeys)
            {
                LocalDirectorySnapshot local = localDirectoriesByPath[sourceKey];
                string targetPath = ReplacePathPrefix(local.RelativePath, candidate.SourcePath, candidate.TargetPath);
                string targetKey = SyncPath.ToKey(targetPath);
                if ((localDirectoriesByPath.ContainsKey(targetKey) && !sourceDirectoryKeys.Contains(targetKey))
                    || (localFilesByPath.ContainsKey(targetKey) && !sourceFileKeys.Contains(targetKey)))
                {
                    return $"the target path '{targetPath}' collides with an existing local item";
                }
            }

            foreach (string sourceKey in sourceFileKeys)
            {
                LocalFileSnapshot local = localFilesByPath[sourceKey];
                string targetPath = ReplacePathPrefix(local.RelativePath, candidate.SourcePath, candidate.TargetPath);
                string targetKey = SyncPath.ToKey(targetPath);
                if ((localFilesByPath.ContainsKey(targetKey) && !sourceFileKeys.Contains(targetKey))
                    || (localDirectoriesByPath.ContainsKey(targetKey) && !sourceDirectoryKeys.Contains(targetKey)))
                {
                    return $"the target path '{targetPath}' collides with an existing local item";
                }
            }

            return null;
        }

        private static string? ValidateTrackedRemoteDirectoryMoveDirectories(
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, SyncStateEntry> directoryStateByPath,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath,
            IReadOnlyDictionary<Guid, RemoteDirectorySnapshot> remoteDirectoriesById)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in directoryStateByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey)))
            {
                if (!localDirectoriesByPath.ContainsKey(entry.Key))
                {
                    return $"tracked directory '{entry.Value.RelativePath}' is absent from the local snapshot";
                }

                if (!entry.Value.RemoteNodeId.HasValue)
                {
                    return $"tracked directory '{entry.Value.RelativePath}' has no remote node id";
                }

                if (!remoteDirectoriesById.TryGetValue(
                        entry.Value.RemoteNodeId.Value,
                        out RemoteDirectorySnapshot? remote))
                {
                    return $"tracked directory '{entry.Value.RelativePath}' is absent from the remote snapshot by id";
                }

                string expectedRemotePath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                if (!string.Equals(remote.RelativePath, expectedRemotePath, StringComparison.Ordinal))
                {
                    return $"tracked directory '{entry.Value.RelativePath}' maps to remote path '{remote.RelativePath}' instead of '{expectedRemotePath}'";
                }
            }

            return null;
        }

        private static string? ValidateTrackedRemoteDirectoryMoveFiles(
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, SyncStateEntry> fileStateByPath,
            IDictionary<string, LocalFileSnapshot> localFilesByPath,
            IReadOnlyDictionary<Guid, RemoteFileSnapshot> remoteFilesById)
        {
            foreach (KeyValuePair<string, SyncStateEntry> entry in fileStateByPath
                         .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey)))
            {
                if (!localFilesByPath.ContainsKey(entry.Key))
                {
                    return $"tracked file '{entry.Value.RelativePath}' is absent from the local snapshot";
                }

                if (!entry.Value.RemoteFileId.HasValue)
                {
                    return $"tracked file '{entry.Value.RelativePath}' has no remote file id";
                }

                if (!remoteFilesById.TryGetValue(entry.Value.RemoteFileId.Value, out RemoteFileSnapshot? remote))
                {
                    return $"tracked file '{entry.Value.RelativePath}' is absent from the remote snapshot by id";
                }

                string expectedRemotePath = ReplacePathPrefix(
                    entry.Value.RelativePath,
                    candidate.SourcePath,
                    candidate.TargetPath);
                if (!string.Equals(remote.RelativePath, expectedRemotePath, StringComparison.Ordinal))
                {
                    return $"tracked file '{entry.Value.RelativePath}' maps to remote path '{remote.RelativePath}' instead of '{expectedRemotePath}'";
                }
            }

            return null;
        }

        public static void MoveLocalDirectoryLookups(
            string localRootPath,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalDirectorySnapshot> localDirectoriesByPath)
        {
            List<KeyValuePair<string, LocalDirectorySnapshot>> moved = localDirectoriesByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .ToList();
            foreach (KeyValuePair<string, LocalDirectorySnapshot> entry in moved)
            {
                localDirectoriesByPath.Remove(entry.Key);
            }

            foreach (KeyValuePair<string, LocalDirectorySnapshot> entry in moved)
            {
                string targetPath = ReplacePathPrefix(entry.Value.RelativePath, candidate.SourcePath, candidate.TargetPath);
                entry.Value.RelativePath = targetPath;
                entry.Value.FullPath = ResolveLocalPath(localRootPath, targetPath);
                localDirectoriesByPath.Add(SyncPath.ToKey(targetPath), entry.Value);
            }
        }

        public static void MoveLocalFileLookups(
            string localRootPath,
            RemoteDirectoryMoveCandidate candidate,
            IDictionary<string, LocalFileSnapshot> localFilesByPath)
        {
            List<KeyValuePair<string, LocalFileSnapshot>> moved = localFilesByPath
                .Where(entry => IsSameOrDescendantPathKey(entry.Key, candidate.SourceKey))
                .ToList();
            foreach (KeyValuePair<string, LocalFileSnapshot> entry in moved)
            {
                localFilesByPath.Remove(entry.Key);
            }

            foreach (KeyValuePair<string, LocalFileSnapshot> entry in moved)
            {
                string targetPath = ReplacePathPrefix(entry.Value.RelativePath, candidate.SourcePath, candidate.TargetPath);
                entry.Value.RelativePath = targetPath;
                entry.Value.FullPath = ResolveLocalPath(localRootPath, targetPath);
                localFilesByPath.Add(SyncPath.ToKey(targetPath), entry.Value);
            }
        }
    }
}
