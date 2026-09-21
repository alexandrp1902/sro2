using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Комната и репутация (M13): что двигает отношение системы и места и как об этом узнаёт пилот.
/// Все начисления идут через <see cref="AddRep"/> — там же и сохранение, и строка в журнал.
/// С M15 мест в системе несколько: у станции и у каждого поселения своя память о пилоте.
/// </summary>
public sealed partial class Room
{
    /// <summary>
    /// Ключ места, чья репутация сейчас в ходу: где пилот стоит, а в космосе — главное место системы.
    /// Летящий мимо пилот всё ещё имеет дело с властями системы, и витрину ему считать не по чему.
    /// </summary>
    private string? PlaceKeyOf(Player player) => PlaceOf(player)?.Key ?? Balance.DefaultPlace?.Key;

    /// <summary>Очки того места, где стоит пилот.</summary>
    private double PlaceRep(Player player) =>
        PlaceKeyOf(player) is { } key ? player.Rep.Value(key, NowSeconds, Balance.Reputation) : 0;

    /// <summary>Очки властей системы: по ним закрывается док и звереют рейнджеры.</summary>
    private double SystemRep(Player player) => player.Rep.Value(Reputation.System(SystemId), NowSeconds, Balance.Reputation);

    /// <summary>
    /// Очки, по которым решают, что вообще выложить на витрину: лучшее из станции и среднего по региону.
    /// Регион нужен ровно для этого (спека M13: «доступ к дальним магазинам») — имя, заработанное в соседних
    /// системах, открывает двери в дальних.
    ///
    /// На цену, скидку и доску он **не** влияет: там считает только станция. Иначе нулевой регион
    /// вокруг вытягивал бы вверх любой штраф, и враг платил бы как нейтрал — наказание испарялось бы.
    /// </summary>
    private double GateRep(Player player) =>
        Math.Max(PlaceRep(player), player.Rep.Region(Balance.Galaxy, Balance.SystemDef.Region, NowSeconds, Balance.Reputation));

    /// <summary>Пилот — враг властей этой системы: худшая ступень шкалы.</summary>
    public bool IsEnemy(Player player) =>
        Balance.Reputation.Any && Balance.Reputation.Index(SystemRep(player)) == 0;

    /// <summary>
    /// Рейнджеры прямо сейчас идут на этот корабль: он за кого-то ответит. Это текущая злость, а не
    /// отношение (M16a) — её ставит нападение и снимает время, гибель или уход из системы.
    /// </summary>
    public bool Hunted(int id) => _offenders.GetValueOrDefault(id) > Tick;

    /// <summary>
    /// Начислить очки и рассказать об этом. Гостю тоже начисляем — в его сессии репутация работает,
    /// просто не переживает выход: <see cref="Save"/> сам отсечёт пилота без аккаунта.
    /// </summary>
    /// <param name="hourlyCap">Сколько этот ключ принимает за час; null — без потолка.</param>
    private void AddRep(Player player, string key, double delta, string code, double? hourlyCap = null)
    {
        var rules = Balance.Reputation;
        if (!rules.Any || delta == 0) return;
        var laid = player.Rep.Add(key, delta, NowSeconds, rules, hourlyCap);
        // Потолок за час выбран или шкала упёрлась в предел — поступок засчитан, но говорить не о чем.
        if (laid == 0) return;
        Save(player);
        SendRep(player, new RepChangeDto(code, Round(laid), key, Round(player.Rep.Value(key, NowSeconds, rules))));
        _log.LogInformation("Player {Id} reputation {Key} {Delta:+0.##;-0.##} for {Code}", player.Id, key, laid, code);
    }

    /// <summary>Очки системы этой комнаты — самый частый случай начисления.</summary>
    private void AddSystemRep(Player player, double delta, string code, double? hourlyCap = null) =>
        AddRep(player, Reputation.System(SystemId), delta, code, hourlyCap);

    /// <summary>
    /// Игроку показывают целые очки: внутри они дробные (полбалла за пирата), но «+42.3» в доке — это шум.
    /// </summary>
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Отбили вторжение (GDD §38): очки по доле в общем фонде — кто больше сделал, тому больше и спасибо.
    /// Зовётся из <see cref="InvasionDirector"/> и только при победе: за проигранную оборону не благодарят.
    /// </summary>
    /// <param name="system">Система, которую обороняли; пилот к подсчёту мог быть уже в другой.</param>
    public void AwardInvasionRep(Player player, string system, int share, int fund)
    {
        if (share <= 0 || fund <= 0) return;
        var delta = Balance.Reputation.Event.InvasionMax * share / fund;
        AddRep(player, Reputation.System(system), delta, Protocol.RepInvasion);
    }

    /// <summary>Репутация — личное дело пилота, как трюм и ангар.</summary>
    private void SendRep(Player player, RepChangeDto? change = null)
    {
        if (player.Connection is null) return;
        var rules = Balance.Reputation;
        player.Rep.Touch(NowSeconds, rules);

        var systems = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var places = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in player.Rep.Values)
        {
            var rounded = Round(value);
            if (rounded == 0) continue; // меньше половины очка — для клиента это ноль
            var (kind, id) = Reputation.Split(key);
            if (kind == "sys") systems[id] = rounded;
            else places[key] = rounded;
        }

        RepHereDto? here = null;
        if (PlaceKeyOf(player) is { } hereKey && rules.Any)
        {
            var system = SystemRep(player);
            var place = PlaceRep(player);
            here = new RepHereDto(
                hereKey,
                Round(place),
                rules.Level(place).Id,
                rules.Level(GateRep(player)).Id,
                Round(system),
                rules.Level(system).Id,
                Round(player.Rep.Region(Balance.Galaxy, Balance.SystemDef.Region, NowSeconds, rules)));
        }
        player.Connection.Send(new RepMsg(systems, places, here, change));
    }
}
