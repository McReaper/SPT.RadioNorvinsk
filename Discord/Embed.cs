using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RadioNorvinsk.Discord;

/// <summary>The box under a message that holds its facts (killer, weapon, map...).</summary>
internal sealed class Embed
{
    [JsonPropertyName("color")]
    public int Color { get; init; }

    [JsonPropertyName("fields")]
    public List<EmbedField> Fields { get; init; } = new();
}

internal sealed class EmbedField
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; }

    [JsonPropertyName("inline")]
    public bool Inline { get; init; } = true;
}
