using System.Diagnostics;

namespace Everywhere.Extensions;

public static class GuidExtensions
{
    extension(Guid guid)
    {
        public unsafe Guid SetVersion(int version)
        {
            Debug.Assert(version is >= 0 and <= 15, "GUID version must be between 0 and 15.");

            var span = new Span<short>(&guid, 8);
            span[3] = (short)((span[3] & 0x0FFF) | (version << 12));
            return guid;
        }
    }
}