using System.Text.Json;
using Sro.Server.Accounts;
using Sro.Server.Net;
using Sro.Sim.Mech;

namespace Sro.Server.Game;

public sealed record TerritoryClaim(string Id, string Account, int Season, string Kind, TerritoryReward Reward, bool Claimed = false);
public sealed record TerritoryArchive(int Season, TerritoryObject[] Objects, Dictionary<string,int> Contributions, DateTimeOffset Ended);
public sealed record TerritoryParticipant(string Account, string Name, string[] Squad, bool Participated = false,
    DateTimeOffset? LostAt = null, bool Left = false, bool Retreat = false);
public sealed record TerritoryFight(string Id, string Object, string Operation, int Season, MechBattleSave Battle,
    TerritoryParticipant[] Participants, DateTimeOffset Deadline, bool Discount, int Revision = 0,
    Dictionary<string,MechStateMsg>? Receipts = null, string? StartRequest = null, TerritoryConfig? FrozenConfig = null);
public sealed record TerritoryWorld(int Schema, int Season, DateTimeOffset SeasonEnd, int Revision,
    TerritoryObject[] Objects, Dictionary<string,int> Contributions, Dictionary<string,Dictionary<string,int>> Days,
    TerritoryClaim[] Claims, TerritoryArchive[] Archives, TerritoryFight[] Fights, string[] ProcessedDays,
    DateTimeOffset NextStrike, Dictionary<string,string> Requests, string? LastTransaction = null);
public sealed record TerritoryProfilePatch(string Account, int CreditsDelta, MechWorkshopState Workshop,
    TerritoryProfile Territory, IReadOnlyDictionary<string,int>? Cargo = null, IReadOnlyDictionary<string,StoryProgress>? Story = null);
public sealed record TerritoryTransaction(string Id, TerritoryWorld World, TerritoryProfilePatch[] Profiles, string[]? Ended = null);

/// <summary>Write-ahead intent, durable profile application with per-account receipts, then world replacement.
/// A failed step retains the same intent and freezes territory until recovery succeeds.</summary>
public sealed class TerritoryStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _worldPath, _journalPath;
    private readonly AccountStore _accounts;
    private readonly ILogger _log;
    private TerritoryTransaction? _pending;
    public TerritoryWorld World { get; private set; }
    public bool Disabled { get; private set; }
    public bool Waiting => _pending is not null;
    public string? Error { get; private set; }
    public string[] AppliedAccounts { get; private set; } = [];
    public string[] AppliedCargoAccounts { get; private set; } = [];
    public string[] EndedSessions { get; private set; } = [];
    public bool ProfileFrozen(string? account) => _pending?.Profiles.Any(p=>p.Account==account)==true;
    // Tests can fail at each durable step, including after a successful profile write.
    public Action<string>? Checkpoint { get; set; }

    public TerritoryStore(string directory, TerritoryConfig config, AccountStore accounts, ILogger log, DateTimeOffset now)
    {
        _accounts=accounts;_log=log;_worldPath=Path.Combine(directory,"territory-world.json");_journalPath=Path.Combine(directory,"territory-journal.json");
        var season=Math.Max(1,(int)Math.Floor((now-config.SeasonEpoch).TotalSeconds/config.SeasonSeconds)+1);
        World=new(1,season,config.SeasonEpoch.AddSeconds((long)season*config.SeasonSeconds),0,
            config.Objects.Select(o=>new TerritoryObject(o.Id,o.StartControl,o.StartControl==100?TerritoryRules.Resistance:TerritoryRules.Corporation)).ToArray(),
            [],[],[],[],[],[],now.AddSeconds(config.StrikeSeconds),[]);
        try
        {
            Directory.CreateDirectory(directory);
            if(File.Exists(_worldPath)) World=Read<TerritoryWorld>(_worldPath);
            if(World.Schema!=1 || World.Objects is null || World.Fights is null || World.Contributions is null || World.Days is null
                || World.Claims is null || World.Archives is null || World.ProcessedDays is null || World.Requests is null
                || World.Objects.Length!=12 || World.Objects.Select(o=>o.Id).Distinct().Count()!=12
                || World.Objects.Any(o=>!config.Objects.Any(d=>d.Id==o.Id) || o.Control is <0 or >100 || o.Owner is not ("resistance" or "corporation"))
                || World.Fights.Select(f=>f.Id).Distinct().Count()!=World.Fights.Length
                || World.Fights.SelectMany(f=>f.Participants).Select(p=>p.Account).Distinct().Count()!=World.Fights.Sum(f=>f.Participants.Length)
                || World.Fights.Any(f=>f.Participants.Length is <1 or >2 || f.Battle.Units.Any(u=>u.View.Assembly is null || u.View.Hp.Length!=4 || u.View.Max.Length!=4)
                    || !World.Objects.Any(o=>o.Id==f.Object&&o.Active==f.Id))
                || World.Objects.Any(o=>o.Active is not null&&!World.Fights.Any(f=>f.Id==o.Active))) throw new InvalidDataException("Invalid territory world schema");
            if(File.Exists(_journalPath))
            {
                _pending=Read<TerritoryTransaction>(_journalPath);
                if(_pending.World is null || _pending.Profiles is null || string.IsNullOrEmpty(_pending.Id)) throw new InvalidDataException("Invalid territory journal");
            }
            if(_pending is not null) _accounts.FreezeTerritory(_pending.Profiles.Select(p=>p.Account));
            if(_pending is not null) Recover();
            else if(!File.Exists(_worldPath)) Write(_worldPath,World);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or NullReferenceException or ArgumentException)
        { Disabled=true;Error=e.Message;_log.LogError(e,"Territory is disabled; existing files retained"); }
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,Json),Json)!;
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),Json) ?? throw new InvalidDataException($"Empty {path}");
    private static void Write<T>(string path,T value)
    {
        using(var file=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None))
        {file.Write(JsonSerializer.SerializeToUtf8Bytes(value,Json));file.Flush(true);}
        File.Move(path+".tmp",path,true);
    }
    public bool Commit(TerritoryWorld next, params TerritoryProfilePatch[] profiles)
    {
        if(Disabled || Waiting) return false;
        var id=Guid.NewGuid().ToString("N");
        _pending=new(id,Clone(next with {LastTransaction=id}),Clone(profiles),World.Fights.Where(f=>!next.Fights.Any(n=>n.Id==f.Id)).Select(f=>f.Id).ToArray());
        _accounts.FreezeTerritory(profiles.Select(p=>p.Account));
        return Recover(writeIntent:true);
    }
    public bool Recover(bool writeIntent=false)
    {
        if(Disabled) return false;
        if(_pending is not { } tx) return true;
        try
        {
            // Rewriting the same intent also retries a failure before its first durable write.
            Write(_journalPath,tx);Checkpoint?.Invoke("intent");
            foreach(var patch in tx.Profiles)
            {
                var profile=_accounts.Profile(patch.Account) ?? throw new InvalidDataException("Missing territory participant account");
                if((profile.Territory?.Applied ?? []).Contains(tx.Id)) continue;
                var territory=patch.Territory with {Applied=[..patch.Territory.Applied ?? [],tx.Id]};
                if(!_accounts.SaveDurable(patch.Account,profile with {Credits=checked(profile.Credits+patch.CreditsDelta),Workshop=patch.Workshop,
                    Territory=territory,Cargo=patch.Cargo ?? profile.Cargo,Story=patch.Story??profile.Story},territory:true)) return false;
                Checkpoint?.Invoke("profile:"+patch.Account);
            }
            Write(_worldPath,tx.World);Checkpoint?.Invoke("world");World=tx.World;
            File.Delete(_journalPath);AppliedAccounts=tx.Profiles.Select(p=>p.Account).ToArray();AppliedCargoAccounts=tx.Profiles.Where(p=>p.Cargo is not null).Select(p=>p.Account).ToArray();
            EndedSessions=tx.Ended??[];
            _accounts.UnfreezeTerritory(AppliedAccounts);_pending=null;Error=null;return true;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException)
        {Error=e.Message;_log.LogError(e,"Territory transaction {Id} awaiting durable storage",tx.Id);return false;}
        catch(Exception e) when(e is InvalidDataException or OverflowException)
        {Disabled=true;Error=e.Message;_log.LogError(e,"Territory journal cannot be applied; files retained");return false;}
    }
}
