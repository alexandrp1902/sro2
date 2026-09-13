using System.Text.Json;

namespace Sro.Sim;

/// <summary>Разбор shared/hulls.json: словарь «id корпуса → параметры».</summary>
public static class HullCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Файл правят руками во время плейтеста — прощаем комментарии и лишние запятые.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static bool TryParse(string json, out IReadOnlyDictionary<string, HullParams> hulls, out string? error)
    {
        hulls = new Dictionary<string, HullParams>();
        Dictionary<string, HullParams>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, HullParams>>(json, Json);
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }

        if (parsed is null || parsed.Count == 0)
        {
            error = "no hulls";
            return false;
        }
        if (!parsed.ContainsKey(SimConfig.DefaultHull))
        {
            error = $"default hull '{SimConfig.DefaultHull}' is missing";
            return false;
        }
        foreach (var (id, hull) in parsed)
        {
            var problem = hull is null ? "is null" : hull.Validate();
            if (problem is not null)
            {
                error = $"{id}: {problem}";
                return false;
            }
        }

        hulls = parsed;
        error = null;
        return true;
    }
}
