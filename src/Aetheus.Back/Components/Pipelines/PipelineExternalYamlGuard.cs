// SPDX-License-Identifier: EUPL-1.2
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-11: the <c>.pipeline/*.yaml</c> file of an external repository's mirror is untrusted input. Before any object
/// is built from it, this guard walks the YAML event stream (nothing is materialized) and refuses what could exhaust
/// memory, CPU or the stack: aliases (the "billion laughs" expansion, merge keys included: they are aliases too),
/// more than <see cref="MaxNodes"/> nodes, and a nesting deeper than <see cref="MaxDepth"/>. A pipeline definition
/// needs none of that; anchors without alias are harmless and left alone.
/// </summary>
internal static class PipelineExternalYamlGuard
{
    internal const int MaxNodes = 20_000;
    internal const int MaxDepth = 32;

    /// <summary>Null when the file may be parsed, otherwise why it is refused.</summary>
    public static string? Check(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var nodes = 0;
        var depth = 0;
        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case AnchorAlias alias:
                        return $"line {alias.Start.Line}: a YAML alias (*{alias.Value}) is not accepted in an external pipeline file.";
                    case Scalar:
                        nodes++;
                        break;
                    case SequenceStart or MappingStart:
                        nodes++;
                        depth++;
                        if (depth > MaxDepth)
                            return $"The file is nested deeper than {MaxDepth} levels.";
                        break;
                    case SequenceEnd or MappingEnd:
                        depth--;
                        break;
                }
                if (nodes > MaxNodes)
                    return $"The file holds more than {MaxNodes} YAML nodes.";
            }
        }
        catch (YamlException exception)
        {
            return YamlParsingHelper.DescribeParseError(exception);
        }
        return null;
    }
}
