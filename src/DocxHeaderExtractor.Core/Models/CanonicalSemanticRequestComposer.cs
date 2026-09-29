using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The one place a packet and a contract become the exact bytes a classifier receives.
/// <para>
/// A measurement helper and the production engine each grew their own version of this step, and
/// they disagreed: one embedded the schema as a field inside the packet, the other appended it as
/// text after a literal <c>\nSCHEMA=</c>. Same evidence, same six calls, different wire bytes - and
/// the measurement helper's hash had been standing in as "what the provider will receive" without
/// ever being checked against what the provider would actually receive.
/// </para>
/// <para>
/// There is now exactly one composition, and both the transporting caller and anything that wants
/// to hash a request without transporting it call this same method. A second implementation of this
/// step, anywhere, is the defect this type exists to make impossible.
/// </para>
/// </summary>
public static class CanonicalSemanticRequestComposer
{
    /// <summary>
    /// The exact string a classifier receives as its user message: the packet, exactly as the
    /// caller serialized it, followed by a literal newline, the token <c>SCHEMA=</c>, and the
    /// contract's schema serialized the same way. Neither side is reformatted here - composition
    /// only concatenates what it is given.
    /// <para>
    /// Deliberately the framework's own default <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions?)"/>
    /// options, with no custom encoder: that is what every request on the wire today was built
    /// with, and a canonical-hashing encoder here would be a byte-identical-looking but actually
    /// different composer - the same defect this type exists to remove, reintroduced one call
    /// later.
    /// </para>
    /// </summary>
    public static string Compose(string packetJson, SemanticCoordinateContract contract)
    {
        ArgumentNullException.ThrowIfNull(packetJson);
        ArgumentNullException.ThrowIfNull(contract);
        return packetJson + "\nSCHEMA=" + JsonSerializer.Serialize(contract.Schema());
    }

    /// <summary>Convenience overload: serializes <paramref name="packet"/> the same default way first.</summary>
    public static string Compose(object packet, SemanticCoordinateContract contract) =>
        Compose(JsonSerializer.Serialize(packet), contract);

    public static string Hash(string requestBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(requestBytes)));
}
