// SPDX-License-Identifier: EUPL-1.2
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aetheus.Front.Components.Shared;

public sealed class YamlSerializationService
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        // Computed properties, same exclusions as the backend serializer: written out, they come back
        // as unknown keys (`is_empty`, `is_container`) that the backend reports as warnings on save.
        .WithAttributeOverride<PipelineRequiresDefinition>(requires => requires.IsEmpty, new YamlIgnoreAttribute())
        .WithAttributeOverride<PipelineIsolationDefinition>(isolation => isolation.IsContainer, new YamlIgnoreAttribute())
        .Build();

    public PipelineYamlDefinition? Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        try
        {
            return Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
        }
        catch
        {
            return null;
        }
    }

    public string Serialize(PipelineYamlDefinition definition)
    {
        return Serializer.Serialize(definition).TrimEnd();
    }

    /// <summary>One stage as the mapping a <c>stages:</c> entry holds (R-105: the wizard rewrites a template's stage).</summary>
    public string SerializeStage(PipelineStageDefinition stage)
    {
        return Serializer.Serialize(stage).TrimEnd();
    }
}
