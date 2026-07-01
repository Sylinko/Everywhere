using System.ComponentModel;
using Everywhere.Configuration;
using Everywhere.Utilities;

namespace Everywhere.AI;

/// <summary>
/// Represents the modalities supported by an AI model for input and output.
/// </summary>
[Flags]
[TypeConverter(typeof(FallbackEnumConverter))]
[DefaultValue(0x1)]
public enum Modalities : uint
{
    None = 0x0,

    Text = 0x1,
    Image = 0x2,
    Audio = 0x4,
    Video = 0x8,
    Pdf = 0x10
}

public static class ModalitiesExtensions
{
    extension (Modalities modalities)
    {
        public bool SupportsText => (modalities & Modalities.Text) != 0;

        public bool SupportsImage => (modalities & Modalities.Image) != 0;

        public bool SupportsAudio => (modalities & Modalities.Audio) != 0;

        public bool SupportsVideo => (modalities & Modalities.Video) != 0;

        public bool SupportsPdf => (modalities & Modalities.Pdf) != 0;

        /// <summary>
        /// Checks if the given MIME type is supported by the modalities.
        /// </summary>
        /// <param name="mimeType"></param>
        /// <returns></returns>
        public bool SupportsMimeType(string mimeType)
        {
            var category = FileUtilities.GetCategory(mimeType);
            return category switch
            {
                FileTypeCategory.Image when modalities.SupportsImage => true,
                FileTypeCategory.Audio when modalities.SupportsAudio => true,
                FileTypeCategory.Video when modalities.SupportsVideo => true,
                _ when mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) && modalities.SupportsPdf => true,
                _ => false
            };
        }
    }
}