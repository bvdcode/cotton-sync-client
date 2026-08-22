// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal static class WindowsCloudFilesPlaceholderFlags
    {
        private const uint PlaceholderCreateDisableOnDemandPopulation = 0x00000001;
        private const uint PlaceholderCreateMarkInSync = 0x00000002;
        private const uint UpdateVerifyInSync = 0x00000001;
        private const uint UpdateMarkInSync = 0x00000002;
        private const uint UpdateDehydrate = 0x00000004;
        private const uint UpdateDisableOnDemandPopulation = 0x00000010;
        private const uint UpdateAllowPartial = 0x00000400;

        public static uint CreatePlaceholderCreateFlags(bool isDirectory)
        {
            uint flags = PlaceholderCreateMarkInSync;
            return isDirectory
                ? flags | PlaceholderCreateDisableOnDemandPopulation
                : flags;
        }

        public static uint CreateUpdateFlags(bool isDirectory)
        {
            uint flags = UpdateVerifyInSync | UpdateMarkInSync;
            return isDirectory
                ? flags | UpdateDisableOnDemandPopulation
                : flags | UpdateDehydrate | UpdateAllowPartial;
        }
    }
}
