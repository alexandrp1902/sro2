namespace Sro.Sim;

/// <summary>
/// Ключ места (M15). До M15 «место» и «система» были одним и тем же: у системы либо есть станция, либо нет,
/// и станция звалась id системы. С посадкой на планеты в одной системе мест стало несколько, поэтому у каждого
/// появилось своё удостоверение: «st:sol» — станция системы sol, «pl:terra» — поселение на планете terra.
///
/// Этим же ключом ключуются магазин (shop.json), рынок (market.json), доска заданий, репутация места
/// и дом пилота: формат один на всё, чтобы «место» нигде не приходилось собирать заново.
/// </summary>
public static class PlaceKey
{
    /// <summary>Станция на орбите системы.</summary>
    public const string StationKind = "st";

    /// <summary>Поселение на планете.</summary>
    public const string PlanetKind = "pl";

    /// <summary>Отношение властей системы — не место, но ключ того же вида (M13).</summary>
    public const string SystemKind = "sys";

    public static string Station(string id) => $"{StationKind}:{id}";

    public static string Planet(string id) => $"{PlanetKind}:{id}";

    public static string System(string id) => $"{SystemKind}:{id}";

    /// <summary>Разбор ключа: («st», «sol»). Чужой ключ — («», ключ целиком).</summary>
    public static (string Kind, string Id) Split(string key)
    {
        var colon = key.IndexOf(':');
        return colon < 0 ? ("", key) : (key[..colon], key[(colon + 1)..]);
    }

    /// <summary>Это ключ места (станции или поселения), а не системы и не мусор.</summary>
    public static bool IsPlace(string? key) => key is not null && Split(key).Kind is StationKind or PlanetKind;

    /// <summary>
    /// Ключ из профиля старше M15, где местом звали голый id системы: «sol» — это станция «st:sol».
    /// Уже размеченный ключ возвращается как есть.
    /// </summary>
    public static string Upgrade(string key) => Split(key).Kind == "" ? Station(key) : key;
}

/// <summary>
/// Место, куда можно встать: станция на орбите или поселение на планете (M15). Всё, что комнате нужно знать,
/// чтобы принять корабль и открыть ему док, — магазин и рынок лежат отдельно, по этому же ключу.
///
/// Станция и поселение различаются только содержимым полей: обе ходят по орбите вокруг звезды, у обеих
/// свой радиус подлёта и свой набор фонов. Комната про разницу почти не знает.
/// </summary>
/// <param name="Key">Ключ места: см. <see cref="PlaceKey"/>.</param>
/// <param name="Kind"><see cref="PlaceKey.StationKind"/> или <see cref="PlaceKey.PlanetKind"/>.</param>
/// <param name="Id">Id системы у станции, id планеты у поселения.</param>
/// <param name="SystemId">Система, в которой место стоит.</param>
/// <param name="Name">Как называть место в интерфейсе.</param>
/// <param name="Orbit">Орбита, по которой место ходит вокруг звезды.</param>
/// <param name="Range">
/// Ближе этого можно пристыковаться или сесть. У станции это общий <see cref="LootRules.StationRange"/>,
/// у поселения — он же плюс радиус планеты: планета большая, и подлетать пришлось бы внутрь картинки.
/// Радиус едет на клиент вместе с местом, чтобы обе стороны считали одно и то же число.
/// </param>
/// <param name="Scene">Свой набор фонов дока; null — общий по виду места.</param>
/// <param name="Shipyard">Здесь продают и меняют корпуса. Верфь есть не в каждом поселении.</param>
public sealed record PlaceDef(
    string Key,
    string Kind,
    string Id,
    string SystemId,
    string Name,
    OrbitDef Orbit,
    double Range,
    string? Scene = null,
    bool Shipyard = true)
{
    /// <summary>Это поселение на планете, а не станция.</summary>
    public bool IsPlanet => Kind == PlaceKey.PlanetKind;

    /// <summary>Где место находится через seconds секунд орбитального времени системы.</summary>
    public (double X, double Y) At(double seconds) => Orbit.At(seconds);
}
