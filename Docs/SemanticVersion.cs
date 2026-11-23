using System;

namespace FluentLocalizer.Documentation
{
    /// <summary>Compares the semantic versions used in the documentation catalog.</summary>
    public static class SemanticVersion
    {
        /// <summary>Returns the precedence of the left version relative to the right version.</summary>
        public static int Compare(string left, string right)
        {
            var leftParts = SplitVersion(left);
            var rightParts = SplitVersion(right);
            var result = Version.Parse(leftParts[0]).CompareTo(Version.Parse(rightParts[0]));
            if (result != 0) return result;
            if (leftParts.Length != rightParts.Length) return rightParts.Length.CompareTo(leftParts.Length);
            if (leftParts.Length == 1) return 0;

            var leftIdentifiers = leftParts[1].Split('.');
            var rightIdentifiers = rightParts[1].Split('.');
            for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
            {
                result = CompareIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
                if (result != 0) return result;
            }

            return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
        }

        private static string[] SplitVersion(string version)
        {
            var metadata = version.IndexOf('+');
            if (metadata >= 0) version = version.Substring(0, metadata);
            return version.Split(new[] { '-' }, 2);
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;

            if (leftNumeric)
            {
                // Valid numeric identifiers have no leading zeroes, so length preserves numeric order without overflow.
                var lengthOrder = left.Length.CompareTo(right.Length);
                if (lengthOrder != 0) return lengthOrder;
            }

            return string.CompareOrdinal(left, right);
        }

        private static bool IsNumeric(string identifier)
        {
            foreach (var character in identifier)
                if (character < '0' || character > '9') return false;
            return identifier.Length != 0;
        }
    }
}
