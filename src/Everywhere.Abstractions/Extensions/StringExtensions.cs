using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Everywhere.Extensions;

public static class StringExtensions
{
    extension([NotNullWhen(false)] string? str)
    {
        public bool IsNullOrEmpty() => string.IsNullOrEmpty(str);
        public bool IsNullOrWhiteSpace() => string.IsNullOrWhiteSpace(str);
    }

    /// <param name="str"></param>
    extension(string? str)
    {
        [return: NotNullIfNotNull(nameof(str))]
        public string? SafeSubstring(int startIndex, int length)
        {
            if (str is null) return null;
            if (startIndex < 0) startIndex = 0;
            if (startIndex >= str.Length) return string.Empty;
            if (length < 0) length = 0;
            if (startIndex + length > str.Length) length = str.Length - startIndex;
            return str.Substring(startIndex, length);
        }

        /// <summary>
        /// Force enumerate the source str
        /// </summary>
        /// <param name="another"></param>
        /// <returns></returns>
        public bool TimingSafeEquals(string? another)
        {
            if (str is null) return another is null;
            var match = true;
            for (var i = 0; i < str.Length; i++)
            {
                if (!match) continue;
                match = another != null && i < another.Length && str[i] == another[i];
            }

            return match;
        }
    }

    public static StringBuilder TrimEnd(this StringBuilder sb)
    {
        var i = sb.Length - 1;
        for (; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(sb[i])) break;
        }
        if (i < sb.Length - 1)
        {
            sb.Remove(i + 1, sb.Length - i - 1);
        }
        return sb;
    }

    extension(string str)
    {
        public string TrimStart(params ReadOnlySpan<string> trimStrings)
        {
            if (string.IsNullOrEmpty(str)) return str;

            var startIndex = 0;
            foreach (var trimString in trimStrings)
            {
                while (startIndex < str.Length && str.AsSpan(startIndex).StartsWith(trimString, StringComparison.Ordinal))
                {
                    startIndex += trimString.Length;
                }
            }

            return str[startIndex..];
        }

        public string TrimEnd(params ReadOnlySpan<string> trimStrings)
        {
            if (string.IsNullOrEmpty(str)) return str;

            var endIndex = str.Length;
            foreach (var trimString in trimStrings)
            {
                while (endIndex > 0 && str.AsSpan(0, endIndex).EndsWith(trimString, StringComparison.Ordinal))
                {
                    endIndex -= trimString.Length;
                }
            }

            return str[..endIndex];
        }

        public string Trim(params ReadOnlySpan<string> trimStrings)
        {
            return str.TrimStart(trimStrings).TrimEnd(trimStrings);
        }

        public string Format(object? arg)
        {
            return string.Format(str, arg);
        }

        public string Format(params object?[] args)
        {
            return string.Format(str, args);
        }
    }
}