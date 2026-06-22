// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Updates
{
    internal readonly record struct DesktopSemanticVersion(
        int Major,
        int Minor,
        int Patch,
        string? Prerelease) : IComparable<DesktopSemanticVersion>
    {
        public static DesktopSemanticVersion Parse(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            string text = value.Trim();
            int metadataIndex = text.IndexOf('+', StringComparison.Ordinal);
            if (metadataIndex >= 0)
            {
                text = text[..metadataIndex];
            }

            string? prerelease = null;
            int prereleaseIndex = text.IndexOf('-', StringComparison.Ordinal);
            if (prereleaseIndex >= 0)
            {
                prerelease = text[(prereleaseIndex + 1)..];
                text = text[..prereleaseIndex];
                if (string.IsNullOrWhiteSpace(prerelease))
                {
                    throw new FormatException("Semantic version prerelease identifier is empty.");
                }
            }

            string[] parts = text.Split('.');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor)
                || !int.TryParse(parts[2], out int patch)
                || major < 0
                || minor < 0
                || patch < 0)
            {
                throw new FormatException("Semantic version must use major.minor.patch format.");
            }

            return new DesktopSemanticVersion(major, minor, patch, prerelease);
        }

        public int CompareTo(DesktopSemanticVersion other)
        {
            int majorComparison = Major.CompareTo(other.Major);
            if (majorComparison != 0)
            {
                return majorComparison;
            }

            int minorComparison = Minor.CompareTo(other.Minor);
            if (minorComparison != 0)
            {
                return minorComparison;
            }

            int patchComparison = Patch.CompareTo(other.Patch);
            if (patchComparison != 0)
            {
                return patchComparison;
            }

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        public override string ToString()
        {
            string core = Major.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "."
                + Minor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "."
                + Patch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(Prerelease) ? core : core + "-" + Prerelease;
        }

        private static int ComparePrerelease(string? left, string? right)
        {
            bool hasLeft = !string.IsNullOrWhiteSpace(left);
            bool hasRight = !string.IsNullOrWhiteSpace(right);
            if (!hasLeft && !hasRight)
            {
                return 0;
            }

            if (!hasLeft)
            {
                return 1;
            }

            if (!hasRight)
            {
                return -1;
            }

            string[] leftParts = left!.Split('.');
            string[] rightParts = right!.Split('.');
            int count = Math.Min(leftParts.Length, rightParts.Length);
            for (int index = 0; index < count; index++)
            {
                int comparison = ComparePrereleaseIdentifier(leftParts[index], rightParts[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return leftParts.Length.CompareTo(rightParts.Length);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            bool leftIsNumber = int.TryParse(left, out int leftNumber);
            bool rightIsNumber = int.TryParse(right, out int rightNumber);
            if (leftIsNumber && rightIsNumber)
            {
                return leftNumber.CompareTo(rightNumber);
            }

            if (leftIsNumber)
            {
                return -1;
            }

            if (rightIsNumber)
            {
                return 1;
            }

            return string.CompareOrdinal(left, right);
        }
    }
}
