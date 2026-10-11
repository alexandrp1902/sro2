namespace Sro.Sim.Race;

public sealed record RaceInput(long Seq = 0, string SteerMode = "turn", double Heading = 0,
    double Turn = 0, double Throttle = 0, bool Brake = false, long BoostPressId = 0, bool ControlActive = true)
{
    public bool Valid => Seq > 0 && (SteerMode == "turn" || SteerMode == "heading") &&
        double.IsFinite(Heading) && double.IsFinite(Turn) && double.IsFinite(Throttle) &&
        Turn is >= -1 and <= 1 && Throttle is >= 0 and <= 1 && BoostPressId >= 0;
}
public sealed class RaceBody
{
    public RaceVec Position { get; set; }
    public RaceVec Velocity { get; set; }
    public double Heading { get; set; }
    public double Omega { get; set; }
    public double Hp { get; set; } = 100;
    public double BoostRemaining { get; set; }
    public double CooldownRemaining { get; set; }
    public long BoostPressId { get; set; }
    public HashSet<string> Contacts { get; } = [];
    public string BoostState => BoostRemaining > 0 ? "ACTIVE" : CooldownRemaining > 0 ? "COOLDOWN" : "READY";
}
public sealed record RaceSegment(RaceVec From, RaceVec To, double StartFraction, double EndFraction);
public sealed record RaceMotion(List<RaceSegment> Segments, string? FatalReason, double FatalFraction,
    double NormalSpeed, bool ContactLimit, double? HpBeforeFatal = null);

public static class RacePhysics
{
    public const double Dt = 1.0 / 60;
    public static double Angle(double a) => Math.Atan2(Math.Sin(a), Math.Cos(a));
    public static void CancelBoost(RaceBody body, RaceRules rules)
    {
        if (body.BoostRemaining <= 0) return;
        body.BoostRemaining = 0;
        body.CooldownRemaining = rules.CooldownSeconds;
    }
    public static double Damage(double vn) => vn < 2 ? 0 : vn < 7 ? 2 + 2 * (vn - 2) : 12 + 6 * (vn - 7);

    public static RaceMotion Step(RaceBody b, RaceInput input, RaceTrack track, RaceRules r)
    {
        if (input.Brake || !input.ControlActive) CancelBoost(b, r);
        if (input.BoostPressId > b.BoostPressId)
        {
            b.BoostPressId = input.BoostPressId;
            if (input.ControlActive && !input.Brake && b.BoostState == "READY") b.BoostRemaining = r.BoostSeconds;
        }
        var speed = b.Velocity.Length;
        var limit = r.TurnSlow + (r.TurnFast - r.TurnSlow) * Math.Min(speed / r.EngineLimit, 1);
        var target = input.SteerMode == "heading" ? Math.Clamp(Angle(input.Heading - b.Heading) * r.HeadingGain, -limit, limit) : input.Turn * limit;
        b.Omega += Math.Clamp(target - b.Omega, -r.AngularAccel * Dt, r.AngularAccel * Dt);
        b.Heading = Angle(b.Heading + b.Omega * Dt);
        var f = new RaceVec(Math.Cos(b.Heading), Math.Sin(b.Heading));
        var forward = b.Velocity.Dot(f);
        var side = b.Velocity - f * forward;
        var engine = input.Brake ? 0 : r.EngineAccel * input.Throttle * Math.Max(0, 1 - Math.Pow(Math.Max(0, forward) / r.EngineLimit, 2));
        var boost = b.BoostRemaining > 0 && !input.Brake ? r.BoostAccel * Math.Max(0, 1 - Math.Pow(Math.Max(0, forward) / r.BoostLimit, 2)) : 0;
        b.Velocity += (f * (engine + boost) - b.Velocity * r.DragK - side * r.LateralK) * Dt;
        if (input.Brake && b.Velocity.Length > 0) b.Velocity *= Math.Max(0, 1 - r.BrakeAccel * Dt / b.Velocity.Length);
        if (b.BoostRemaining > 0)
        {
            b.BoostRemaining = Math.Max(0, b.BoostRemaining - Dt);
            if (b.BoostRemaining <= 1e-9) { b.BoostRemaining = 0; b.CooldownRemaining = r.CooldownSeconds; }
        }
        else b.CooldownRemaining = Math.Max(0, b.CooldownRemaining - Dt);

        var segments = new List<RaceSegment>();
        var touching = new HashSet<string>();
        var fraction = 0.0;
        for (var iteration = 0; iteration < r.MaxContacts; iteration++)
        {
            var from = b.Position;
            var delta = b.Velocity * (Dt * (1 - fraction));
            var hit = FirstHit(from, delta, track, r.Radius);
            if (hit is null)
            {
                b.Position += delta;
                segments.Add(new(from, b.Position, fraction, 1));
                UpdateContacts();
                return new(segments, null, 1, 0, false);
            }
            var h = hit.Value;
            var end = fraction + (1 - fraction) * h.T;
            b.Position += delta * h.T;
            segments.Add(new(from, b.Position, fraction, end));
            var vn = Math.Max(0, -b.Velocity.Dot(h.Normal));
            var newImpact = !b.Contacts.Contains(h.Id) && !touching.Contains(h.Id);
            if (newImpact)
            {
                var hpBefore = b.Hp;
                b.Hp -= Damage(vn);
                if (vn >= r.FatalSpeed || b.Hp <= 0)
                {
                    b.Hp = 0;
                    return new(segments, h.Reason, end, vn, false, hpBefore);
                }
            }
            touching.Add(h.Id);
            var normalVelocity = b.Velocity.Dot(h.Normal);
            b.Velocity = (b.Velocity - h.Normal * normalVelocity) * (newImpact ? 1 - r.TangentLoss : 1) + h.Normal * (vn * r.Restitution);
            b.Position += h.Normal * r.ContactSlop;
            fraction = end;
            if (fraction >= 1 - 1e-10) { UpdateContacts(); return new(segments, null, 1, vn, false); }
        }
        UpdateContacts();
        return new(segments, null, fraction, 0, true);

        void UpdateContacts()
        {
            b.Contacts.Clear();
            foreach (var w in track.Walls)
                if (Distance(b.Position, w.A, w.B) <= r.Radius + r.ContactSlop * 3) b.Contacts.Add(w.Id);
            foreach (var o in track.Obstacles)
                if ((b.Position - o.Center).Length <= r.Radius + o.Radius + r.ContactSlop * 3) b.Contacts.Add(o.Id);
        }
    }

    private readonly record struct Hit(double T, RaceVec Normal, string Id, string Reason);
    private static Hit? FirstHit(RaceVec p, RaceVec d, RaceTrack track, double radius)
    {
        Hit? best = null;
        void Candidate(double t, RaceVec n, string id, string reason)
        {
            if (t >= -1e-9 && t <= 1 && d.Dot(n) < -1e-10 && (best is null || t < best.Value.T)) best = new(Math.Max(0, t), n, id, reason);
        }
        void Circle(RaceVec c, double rad, string id, string reason)
        {
            var v = p - c; var a = d.Dot(d); if (a < 1e-20) return;
            var k = v.Dot(d); var disc = k * k - a * (v.Dot(v) - rad * rad);
            if (disc < 0) return;
            var t = (-k - Math.Sqrt(disc)) / a;
            var at = v + d * Math.Max(0, t);
            if (at.Length > 1e-12) Candidate(t, at * (1 / at.Length), id, reason);
        }
        foreach (var w in track.Walls)
        {
            var axis = w.B - w.A; var length = axis.Length; var u = axis * (1 / length);
            var n = new RaceVec(-u.Y, u.X); var dist = (p - w.A).Dot(n); var rate = d.Dot(n);
            if (Math.Abs(rate) > 1e-12)
            {
                foreach (var sign in new[] { -1, 1 })
                {
                    var t = (sign * radius - dist) / rate;
                    var along = (p + d * t - w.A).Dot(u);
                    if (along >= 0 && along <= length) Candidate(t, n * sign, w.Id, w.Reason);
                }
            }
            Circle(w.A, radius, w.Id, w.Reason); Circle(w.B, radius, w.Id, w.Reason);
        }
        foreach (var o in track.Obstacles) Circle(o.Center, radius + o.Radius, o.Id, o.Reason);
        return best;
    }
    public static double Distance(RaceVec p, RaceVec a, RaceVec b)
    {
        var ab = b - a;
        return (p - (a + ab * Math.Clamp((p - a).Dot(ab) / ab.Dot(ab), 0, 1))).Length;
    }
    public static double? Crossing(RaceVec from, RaceVec to, RaceGate g)
    {
        var da = (from - g.A).Dot(g.Normal); var db = (to - g.A).Dot(g.Normal);
        if (da >= 0 || db < 0 || db - da < 1e-12) return null;
        var t = -da / (db - da); var p = from + (to - from) * t; var line = g.B - g.A;
        var along = (p - g.A).Dot(line) / line.Dot(line);
        return along is >= 0 and <= 1 ? t : null;
    }
}
