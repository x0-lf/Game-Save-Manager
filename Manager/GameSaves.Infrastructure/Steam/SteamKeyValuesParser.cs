using ValveKeyValue;

namespace GameSaves.Infrastructure.Steam;

/// <summary>
/// Keeps the selected Valve KeyValues implementation and Steam-specific parser
/// options inside Infrastructure.
/// </summary>
internal static class SteamKeyValuesParser
{
    private static readonly KVSerializer Serializer =
        KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

    public static KVDocument Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Steam serializes Windows path separators and embedded quotes as KV1
        // escape sequences. Strict translation preserves those paths while
        // rejecting unknown escapes instead of enabling Valve's truncation bug.
        var options = new KVSerializerOptions
        {
            HasEscapeSequences = true,
            EnableValveNullByteBugBehavior = false
        };

        return Serializer.Deserialize(stream, options);
    }

    public static string? GetString(KVObject source, string key)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        KVObject? child = source.Children.FirstOrDefault(candidate =>
            candidate.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

        return child?.Value.ValueType == KVValueType.String
            ? (string)child.Value
            : null;
    }
}
