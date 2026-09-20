using System.Text;

namespace DocxHeaderExtractor.Application.Review;

/// <summary>
/// Encodes the source-owned review key for use as one HTTP route segment.
/// The store continues to use the raw document id; this token is transport-only.
/// </summary>
public static class ReviewDocumentIdCodec
{
    private const string Prefix = "r1_";

    public static string Encode(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var bytes = Encoding.UTF8.GetBytes(documentId);
        return Prefix + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static bool TryDecode(string routeId, out string documentId)
    {
        documentId = string.Empty;
        if (string.IsNullOrWhiteSpace(routeId))
            return false;

        if (!routeId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (!IsSafeLegacyRouteId(routeId))
                return false;
            documentId = routeId;
            return true;
        }

        var encoded = routeId[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Any(c => !IsBase64Url(c)))
            return false;

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            documentId = strictUtf8.GetString(Convert.FromBase64String(padded));
            return !string.IsNullOrWhiteSpace(documentId);
        }
        catch (FormatException)
        {
            documentId = string.Empty;
            return false;
        }
        catch (DecoderFallbackException)
        {
            documentId = string.Empty;
            return false;
        }
    }

    private static bool IsBase64Url(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static bool IsSafeLegacyRouteId(string value) =>
        value.All(value => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '-' or '_' or '.' or '~');
}
