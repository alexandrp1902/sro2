namespace Sro.Sim;

/// <summary>
/// Множители старшего тира (M11) к записи Mk1 из weapons.json и modules.json. Хранятся в shop.json, блок tiers:
/// первый — Mk2, второй — Mk3.
/// </summary>
/// <param name="Stat">Урон пушки; щит, его восстановление, топливо, выход генератора, ремонт, охлаждение, трюм у модулей.</param>
/// <param name="Power">Сколько энергии забирает предмет.</param>
/// <param name="Price">Цена к цене Mk1.</param>
/// <param name="Engine">Прибавка двигателя сверх ×1 (speed − 1, accel − 1) растёт в столько раз; ухудшения не растут.</param>
/// <param name="Radar">Дальность радара.</param>
public sealed record TierDef(double Stat = 1, double Power = 1, double Price = 1, double Engine = 1, double Radar = 1)
{
    public string? Validate()
    {
        if (!(Stat > 0) || !(Power > 0) || !(Price > 0) || !(Engine > 0) || !(Radar > 0)) return "all multipliers must be positive";
        return null;
    }
}

/// <summary>
/// Тиры снаряжения Mk1–Mk3 (M11). Mk1 — запись из файла под своим id, старшие — та же запись с множителями,
/// id с суффиксом «_mk2», «_mk3». Так старые профили не ломаются, а три ручные копии записи не нужны.
/// </summary>
public static class Tiers
{
    public const int Max = 3;
    private const string Suffix = "_mk";

    /// <summary>Id предмета тира tier: Mk1 — сам baseId.</summary>
    public static string Id(string baseId, int tier) => tier <= 1 ? baseId : $"{baseId}{Suffix}{tier}";

    /// <summary>Базовый id и тир: «ion_mk2» → («ion», 2), «ion» → («ion», 1).</summary>
    public static (string Base, int Tier) Split(string id)
    {
        var at = id.LastIndexOf(Suffix, StringComparison.Ordinal);
        if (at > 0 && at + Suffix.Length == id.Length - 1 && id[^1] is >= '2' and <= '9' && id[^1] - '0' <= Max)
            return (id[..at], id[^1] - '0');
        return (id, 1);
    }

    /// <summary>«Лазер Mk1» → «Лазер Mk2»; у имени без «Mk1» тир дописывается.</summary>
    public static string Name(string name, int tier) =>
        tier <= 1 ? name : name.Contains("Mk1", StringComparison.Ordinal) ? name.Replace("Mk1", $"Mk{tier}") : $"{name} Mk{tier}";

    public static string? Validate(IReadOnlyList<TierDef>? tiers)
    {
        if (tiers is null) return null;
        if (tiers.Count > Max - 1) return $"at most {Max - 1} tiers (Mk2, Mk3)";
        for (var i = 0; i < tiers.Count; i++)
        {
            var problem = tiers[i] is null ? "is null" : tiers[i].Validate();
            if (problem is not null) return $"tiers[{i}]: {problem}";
        }
        return null;
    }

    /// <summary>
    /// Тир двигает только урон и потребление. Радиус взрыва (M15.5) намеренно остаётся прежним:
    /// осколки и так растут вместе с уроном, а больший радиус — это уже другая роль пушки, а не более мощная та же.
    /// </summary>
    public static IReadOnlyDictionary<string, WeaponParams> Expand(
        IReadOnlyDictionary<string, WeaponParams> weapons, IReadOnlyList<TierDef>? tiers)
    {
        var result = new Dictionary<string, WeaponParams>(weapons);
        if (tiers is null) return result;
        foreach (var (id, w) in weapons)
        {
            for (var i = 0; i < tiers.Count; i++)
            {
                var tier = i + 2;
                var t = tiers[i];
                result[Id(id, tier)] = w with
                {
                    Name = Name(w.Name, tier),
                    Damage = Fine(w.Damage * t.Stat),
                    Power = Whole(w.Power * t.Power),
                    Tier = tier,
                };
            }
        }
        return result;
    }

    public static IReadOnlyDictionary<string, ModuleParams> Expand(
        IReadOnlyDictionary<string, ModuleParams> modules, IReadOnlyList<TierDef>? tiers)
    {
        var result = new Dictionary<string, ModuleParams>(modules);
        if (tiers is null) return result;
        foreach (var (id, m) in modules)
        {
            for (var i = 0; i < tiers.Count; i++)
            {
                var tier = i + 2;
                var t = tiers[i];
                result[Id(id, tier)] = m with
                {
                    Name = Name(m.Name, tier),
                    Power = Whole(m.Power * t.Power),
                    Speed = Fine(Boost(m.Speed, t.Engine)),
                    Accel = Fine(Boost(m.Accel, t.Engine)),
                    Shield = Whole(m.Shield * t.Stat),
                    ShieldRegen = Whole(m.ShieldRegen * t.Stat),
                    Radar = Whole(m.Radar * t.Radar),
                    Output = Whole(m.Output * t.Stat),
                    Repair = Whole(m.Repair * t.Stat),
                    Cooling = Math.Min(Fitting.MaxCooling, Fine(m.Cooling * t.Stat)),
                    Cargo = Whole(m.Cargo * t.Stat),
                    Tier = tier,
                };
            }
        }
        return result;
    }

    /// <summary>Множитель двигателя: прибавка сверх ×1 растёт, ухудшение (форсаж хуже разгоняется) остаётся как есть.</summary>
    private static double Boost(double value, double k) => value > 1 ? 1 + (value - 1) * k : value;

    /// <summary>
    /// Целое: энергия, щит, радар, бак, выход генератора, ремонт и трюм — счётные величины, и игрок видит их
    /// как есть. Множители вроде 1.15 в двоичной дроби не ложатся ровно, и без округления щит Mk2 просил бы
    /// «22.999999999999996 энергии».
    /// </summary>
    private static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Дробное, но без мусора: множители двигателя, охлаждение и урон бывают нецелыми по смыслу,
    /// а «×1.1500000000000001» на карточке — нет.
    /// </summary>
    private static double Fine(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    /// <summary>Цена тира: цена Mk1 × множитель, округлённая до десятков.</summary>
    public static int Price(int basePrice, int tier, IReadOnlyList<TierDef>? tiers)
    {
        if (tier <= 1 || tiers is null || tier - 2 >= tiers.Count) return basePrice;
        return (int)(Math.Round(basePrice * tiers[tier - 2].Price / 10) * 10);
    }
}
