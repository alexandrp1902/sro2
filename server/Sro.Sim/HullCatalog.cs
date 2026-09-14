using System.Text.Json;

namespace Sro.Sim;

/// <summary>Разбор shared/hulls.json: словарь «id корпуса → параметры».</summary>
public static class HullCatalog
{
    public static bool TryParse(string json, out IReadOnlyDictionary<string, HullParams> hulls, out string? error) =>
        JsonCatalog.TryParse<HullParams>(json, "hull", SimConfig.DefaultHull, h => h.Validate(), out hulls, out error);
}

/// <summary>Разбор shared/weapons.json: словарь «id пушки → параметры».</summary>
public static class WeaponCatalog
{
    public static bool TryParse(string json, out IReadOnlyDictionary<string, WeaponParams> weapons, out string? error) =>
        JsonCatalog.TryParse<WeaponParams>(json, "weapon", SimConfig.DefaultWeapon, w => w.Validate(), out weapons, out error);
}

/// <summary>Файлы баланса вида «id → параметры». Невалидный файл отклоняется целиком.</summary>
internal static class JsonCatalog
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Файлы правят руками во время плейтеста — прощаем комментарии и лишние запятые.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="noun">Что лежит в файле — для текста ошибки.</param>
    /// <param name="requiredId">Запись по умолчанию, без которой файл не годится.</param>
    public static bool TryParse<T>(
        string json,
        string noun,
        string requiredId,
        Func<T, string?> validate,
        out IReadOnlyDictionary<string, T> items,
        out string? error) where T : class
    {
        items = new Dictionary<string, T>();
        Dictionary<string, T?>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, T?>>(json, Options);
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }

        if (parsed is null || parsed.Count == 0)
        {
            error = $"no {noun}s";
            return false;
        }
        if (!parsed.ContainsKey(requiredId))
        {
            error = $"default {noun} '{requiredId}' is missing";
            return false;
        }
        var result = new Dictionary<string, T>();
        foreach (var (id, item) in parsed)
        {
            var problem = item is null ? "is null" : validate(item);
            if (problem is not null)
            {
                error = $"{id}: {problem}";
                return false;
            }
            result[id] = item!;
        }

        items = result;
        error = null;
        return true;
    }
}
