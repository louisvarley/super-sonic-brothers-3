using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace S2Port.Extraction;

/// <summary>
/// Indented JSON where arrays of primitives stay on one line, so tile rows and layout rows remain
/// readable instead of being split one value per line.
/// </summary>
public static class CompactJson
{
    public static string Serialize(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, Manifest.JsonOptions);
        var sb = new StringBuilder();
        Write(sb, node, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var (key, child) in obj)
                {
                    Indent(sb, depth + 1);
                    sb.Append(JsonSerializer.Serialize(key, Manifest.JsonOptions)).Append(": ");
                    Write(sb, child, depth + 1);
                    if (++i < obj.Count) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append('}');
                break;

            case JsonArray arr when arr.All(e => e is JsonValue or null):
                sb.Append('[');
                for (int j = 0; j < arr.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(arr[j]?.ToJsonString(Manifest.JsonOptions) ?? "null");
                }
                sb.Append(']');
                break;

            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int j = 0; j < arr.Count; j++)
                {
                    Indent(sb, depth + 1);
                    Write(sb, arr[j], depth + 1);
                    if (j < arr.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append(']');
                break;

            default:
                sb.Append(node?.ToJsonString(Manifest.JsonOptions) ?? "null");
                break;
        }
    }

    private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);
}
