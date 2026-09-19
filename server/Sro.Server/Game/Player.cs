using Sro.Server.Net;
using Sro.Sim;

namespace Sro.Server.Game;

/// <param name="token">
/// Ключ возврата к кораблю после обрыва связи: у пилота с аккаунтом — id аккаунта, у гостя — сессия вкладки;
/// null — вернуться нельзя, корабль удаляется сразу.
/// </param>
/// <param name="accountId">Аккаунт (GDD §61–62); null — гость, у него ничего не сохраняется и весь ангар открыт.</param>
public sealed class Player(int id, string? token, string name, string hullId, string weaponId, string? accountId = null)
    : ShipEntity(id, name, hullId, weaponId)
{
    public string? Token { get; } = token;

    public string? AccountId { get; } = accountId;

    public bool IsGuest => AccountId is null;

    /// <summary>null — связи нет: корабль висит в космосе и тормозит, пока игрок не вернётся.</summary>
    public IClientConnection? Connection { get; private set; }
    public long LostAtTick { get; private set; }

    /// <summary>
    /// Выбранный предмет (боевой документ §45); 0 — нет. На подбор не влияет: тракторный луч берёт всё,
    /// что попало в радиус. Сервер хранит выбор, чтобы гасить его, когда предмет исчез.
    /// </summary>
    public int SelectedLootId { get; set; }

    /// <summary>Трюм (GDD §21). Переживает уничтожение корабля (§24) и обрыв связи — он у игрока, а не у корабля.</summary>
    public Cargo Cargo { get; } = new();

    /// <summary>До этого тика про полный трюм молчим: иначе сообщение повторялось бы каждый тик у обломков.</summary>
    public long CargoFullUntilTick;

    /// <summary>Кредиты (GDD §27).</summary>
    public int Credits;

    /// <summary>Купленные корпуса — ангар (GDD §51). Стартовый есть всегда.</summary>
    public HashSet<string> Hulls { get; } = new(StringComparer.Ordinal) { SimConfig.DefaultHull };

    /// <summary>Купленные пушки. Стартовая есть всегда.</summary>
    public HashSet<string> Weapons { get; } = new(StringComparer.Ordinal) { SimConfig.DefaultWeapon };

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

    /// <summary>Что этот клиент уже знает о системе — от этого считаются дельты снапшота.</summary>
    public SnapshotCodec.Encoder View { get; } = new();

    public InputBuffer Inputs { get; private set; } = new(new MoveInput(0, -1, 0));

    /// <summary>Вход корабля без связи: курс прежний, тяга 0 — Movement сам гасит скорость.</summary>
    public MoveInput StopInput => Inputs.Last with { Throttle = 0 };

    public bool OwnsHull(string id) => IsGuest || Hulls.Contains(id);

    public bool OwnsWeapon(string id) => IsGuest || Weapons.Contains(id);

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
