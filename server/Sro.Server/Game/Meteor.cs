using Sro.Sim;

namespace Sro.Server.Game;

/// <summary>
/// Метеорит. Живёт в общем словаре кораблей, чтобы по нему стрелял обычный <see cref="Battle"/>, но это не корабль:
/// корпуса, щита, пушки и уклонения нет, в ростер и в ships[] снапшота он не попадает, после гибели не возвращается.
/// Всё, что читает корпус, перекрыто здесь; корпус по умолчанию нужен лишь затем, чтобы Hull() не падал.
/// </summary>
public sealed class Meteor : ShipEntity
{
    public Meteor(int id, string sizeId, MeteorSize size, long expiresAtTick)
        : base(id, size.Name, SimConfig.DefaultHull, "")
    {
        SizeId = sizeId;
        Size = size;
        ExpiresAtTick = expiresAtTick;
        Hp = size.Hp;
    }

    /// <summary>Ключ размера в meteors.json — уходит клиенту.</summary>
    public string SizeId { get; }

    /// <summary>Параметры на момент появления: правка баланса летящий камень не меняет.</summary>
    public MeteorSize Size { get; }

    public long ExpiresAtTick { get; }

    /// <summary>Разбился о корабль: минералов не даёт, иначе таран стал бы лучшим способом добычи.</summary>
    public bool Rammed { get; set; }

    public double Speed => Math.Sqrt(Ship.Vx * Ship.Vx + Ship.Vy * Ship.Vy);

    public override double MaxHp(HullParams hull) => Size.Hp;

    /// <summary>Щита нет — заодно Battle не пытается его восстанавливать.</summary>
    public override double MaxShield(HullParams hull) => 0;

    /// <summary>Камень не уклоняется: шанс попадания зависит только от пушки и дистанции.</summary>
    public override double Evasion(Balance balance, double speed) => 0;

    public override WeaponParams? Weapon(Balance balance) => null;
}
