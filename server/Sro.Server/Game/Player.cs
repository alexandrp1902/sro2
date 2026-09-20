using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <param name="token">
/// Ключ возврата к кораблю после обрыва связи: у пилота с аккаунтом — id аккаунта, у гостя — сессия вкладки;
/// null — вернуться нельзя, корабль удаляется сразу.
/// </param>
/// <param name="accountId">Аккаунт (GDD §61–62); null — гость, у него ничего не сохраняется и весь ангар открыт.</param>
/// <param name="weaponId">Пушка в первом слоте; остальное оснащение — стартовое (<see cref="Fitting.Starter"/>).</param>
public sealed class Player(int id, string? token, string name, string hullId, string weaponId, string? accountId = null)
    : ShipEntity(id, name, hullId, [weaponId])
{
    private HullParams? _stats;
    private Balance? _statsBalance;
    private HullParams? _statsHull;
    private ShipFit? _statsFit;

    public string? Token { get; } = token;

    public string? AccountId { get; } = accountId;

    public bool IsGuest => AccountId is null;

    /// <summary>О чём судачил торговец на этой станции (M12); пусто — вне дока и там, где рынка нет.</summary>
    public IReadOnlyList<Rumour> Rumours = [];

    /// <summary>
    /// Отношение систем и станций к этому пилоту (M13). Переезжает вместе с ним между комнатами;
    /// у гостя копится в памяти сессии и пропадает вместе с ней, как и всё остальное.
    /// </summary>
    public Reputation Rep { get; } = new();

    /// <summary>null — связи нет: корабль висит в космосе и тормозит, пока игрок не вернётся.</summary>
    public IClientConnection? Connection { get; private set; }
    public long LostAtTick { get; private set; }

    /// <summary>
    /// Выбранный предмет (боевой документ §45); 0 — нет. На подбор не влияет: тракторный луч берёт всё,
    /// что попало в радиус. Сервер хранит выбор, чтобы гасить его, когда предмет исчез.
    /// </summary>
    public int SelectedLootId { get; set; }

    /// <summary>
    /// Переключатель PvP (<see cref="Net.PvpMsg"/>). Клиент шлёт его после каждого входа, а пока не прислал —
    /// ограничений нет: так играют тесты и смоук-скрипты, которые переключателя не знают.
    /// </summary>
    public bool PvpOn { get; set; } = true;

    /// <summary>Трюм (GDD §21). Переживает уничтожение корабля (§24) и обрыв связи — он у игрока, а не у корабля.</summary>
    public Cargo Cargo { get; } = new();

    /// <summary>До этого тика про полный трюм молчим: иначе сообщение повторялось бы каждый тик у обломков.</summary>
    public long CargoFullUntilTick;

    /// <summary>Кредиты (GDD §27).</summary>
    public int Credits;

    /// <summary>Купленные корпуса — ангар (GDD §51). Стартовый есть всегда.</summary>
    public HashSet<string> Hulls { get; } = new(StringComparer.Ordinal) { SimConfig.DefaultHull };

    /// <summary>Что стоит на корабле (GDD §62): пушки по слотам и модули. Переходит с корпуса на корпус.</summary>
    public ShipFit Fit { get; set; } = Fitting.Starter.With("w0", weaponId);

    /// <summary>Склад на станции: купленные или снятые пушки и модули, которые сейчас не стоят. id — сколько штук.</summary>
    public Dictionary<string, int> Storage { get; } = new(StringComparer.Ordinal);

    public override IReadOnlyList<string?> WeaponIds
    {
        get => Fit.Weapons;
        set => Fit = Fit with { Weapons = value };
    }

    /// <summary>Пушка в первом слоте — для тестов и гостя с пушкой из hello.</summary>
    public string? WeaponId
    {
        get => Fit.Get("w0");
        set => Fit = Fit.With("w0", value);
    }

    /// <summary>Корпус с модулями; пересчитывается, только когда сменились баланс, корпус или оснащение.</summary>
    public override HullParams Effective(Balance balance)
    {
        var hull = Hull(balance.Hulls);
        if (_stats is null || !ReferenceEquals(_statsBalance, balance) || !ReferenceEquals(_statsHull, hull) || !ReferenceEquals(_statsFit, Fit))
        {
            _stats = Fitting.Effective(hull, Fit, balance.Modules);
            _statsBalance = balance;
            _statsHull = hull;
            _statsFit = Fit;
        }
        return _stats;
    }

    public override double RepairRate(Balance balance) => Fitting.Repair(Fit, balance.Modules);

    public override double CooldownScale(Balance balance) => Fitting.CooldownScale(Fit, balance.Modules);

    /// <summary>Положить на склад.</summary>
    public void Store(string id, int count = 1) => Storage[id] = Storage.GetValueOrDefault(id) + count;

    /// <summary>Взять со склада одну штуку.</summary>
    /// <returns>false — такого на складе нет.</returns>
    public bool Unstore(string id)
    {
        if (!Storage.TryGetValue(id, out var count) || count <= 0) return false;
        if (count == 1) Storage.Remove(id);
        else Storage[id] = count - 1;
        return true;
    }

    /// <summary>
    /// Корабль в доке станции: его нет в космосе — ни в снапшоте, ни среди целей, ни на пути метеоритов.
    /// Пилот в это время торгует и меняет корабль.
    /// </summary>
    public bool Docked;

    /// <summary>
    /// Где корабль стыковался — в осях станции (<see cref="OrbitDef.ToLocal"/>): станция за это время ушла по орбите,
    /// а вылет — с той же её стороны.
    /// </summary>
    public (double X, double Y) DockOffset;

    /// <summary>Топливо (GDD §6): тратится только на гиперпрыжки, заправляется в доке.</summary>
    public int Fuel;

    /// <summary>Система последней стыковки: здесь корабль появляется после гибели и после входа. null — стартовая.</summary>
    public string? Home;

    /// <summary>Готовится гиперпрыжок в эту систему (GDD §5); null — нет.</summary>
    public string? JumpTo;

    /// <summary>В этот тик прыжок состоится.</summary>
    public long JumpAtTick;

    /// <summary>Обучение и задания (GDD §36, §54).</summary>
    public MissionLog Missions { get; } = new();

    /// <summary>Что этот клиент уже знает о системе — от этого считаются дельты снапшота.</summary>
    public SnapshotCodec.Encoder View { get; } = new();

    public InputBuffer Inputs { get; private set; } = new(new MoveInput(0, -1, 0));

    /// <summary>Вход корабля без связи: курс прежний, тяга 0 — Movement сам гасит скорость.</summary>
    public MoveInput StopInput => Inputs.Last with { Throttle = 0 };

    public bool OwnsHull(string id) => IsGuest || Hulls.Contains(id);


    public void Attach(IClientConnection connection)
    {
        Connection = connection;
        ResetInputs();
    }

    /// <summary>Новая сессия или вылет из дока: клиент нумерует входы с 1 — старый буфер отбросил бы их как устаревшие.</summary>
    public void ResetInputs() => Inputs = new InputBuffer(StopInput);

    public void Detach(long tick)
    {
        Connection = null;
        LostAtTick = tick;
        FireHeld = false; // иначе корабль без связи палил бы сам до возвращения игрока
    }
}
