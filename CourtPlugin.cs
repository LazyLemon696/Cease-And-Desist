using System.Globalization;
using System.Numerics;
using OpenKogama.Api;

namespace CeaseAndDesist;

//   Commands
//   !court                 shows what the court is doing
//   !court max\min <number>    players needed before the court starts (default 4)
//   !court world           makes your world get cease and desist plguin settings
//   !judge custom <text>   the Judge writes the topic
//   !judge random          the Judge lets the court pick one
//   !spectate              a Witness goes up to the gallery

public sealed class CourtPlugin : IPlugin
{
    const string WorldName = "Cease And Desist";
    const string WorldKey = "world";
    const string NeedKey = "need";
    const int DefaultNeed = 4;
    const int MaxPlayers = 15;
    const int LobbySeconds = 60;
    const int TopicSeconds = 60;
    const int FinalCount = 10;
    const long ReturnDelay = 5_000;
    const long FailedStartRest = 10_000;
    const int TopicLimit = 200;
    const Team LobbyTeam = Team.Blue;
    static readonly bool OnlyAdminsConfigure = false;

    const long TripRetry = 500;
    const long TripCheck = 2_500;
    const long TripGiveUp = 12_000;
    const float NearDistance = 6f;

    const string Orange = "#FFA500";
    const string LightOrange = "#FFC266";
    const string Cyan = "#00FFFF";
    const string Green = "#00C853";
    const string LightGreen = "#90EE90";
    const string Red = "#FF3B3B";
    const string Maroon = "#B03060";
    const string Yellow = "#FFE600";
    const string Purple = "#B266FF";
    const string Grey = "#BBBBBB";

    static readonly Vector3 LobbySpot = new(4f, 36f, -37f);
    static readonly Vector3 SpectatorSpot = new(4f, 5f, -70f);
    static readonly Vector3 SuspectSpot = new(-9.5f, 4f, -26.2f);
    static readonly Vector3 AttorneySpot = new(17.6f, 3.5f, -25.3f);
    static readonly Vector3 JudgeSpot = new(4.1f, 7.5f, -12.4f);
    static readonly Vector3[] WitnessSpots =
    [
        new(13f, 4f, -56f), new(4f, 4f, -56f), new(-7f, 4f, -56f),
        new(-7f, 4f, -49f), new(4f, 4f, -49f), new(13f, 4f, -49f),
        new(-7f, 4f, -41f), new(4f, 4f, -41f), new(13f, 4f, -41f),
        new(-7f, 4f, -31f), new(4f, 4f, -31f), new(13f, 4f, -31f),
    ];

    static readonly string[] Topics =
    [
        "Suspect is a KaGaMian Spy.",
        "Suspect was spamming discord links in the chat",
        "Suspect is Ronman",
        "Suspect was deforming blocks without a permit",
        "Suspect was standing on thin air where a block clearly used to be",
        "Suspect was wiring a sound effect or a light to trigger 5,000 times a second, causing temporary server confusion",
        "Suspect was attempting to squeeze a whole novel into a single logic textbox",
        "Suspect was stealing a player's design idea",
        "Suspect was leaving a hovercraft blocking the courthouse main entrance",
        "Suspect was blocking the entrance in cubegun map",
        "Suspect was caught cheating",
        "Suspect is [REDACTED]",
        "Suspect was spamming racial slurs in maps",
        "Suspect has built \"The Tower\"",
        "Suspect was making unplayable parkour and dragging about it",
        "Suspect has said that Nightus is a bad developer",
        "Suspect was spawnkilling innocent players for \"his own fun\"",
        "Suspect was destroying the whole map publically",
        "Suspect has started an argument in the comment section",
        "Suspect was using mousegun against innocent players in WAR 4",
    ];

    enum Phase { Lobby, Topic, Court, Ending }

    enum Role { Witness, Suspect, Attorney, Judge }

    sealed class Trip(Vector3 target, long deadline)
    {
        public Vector3 Target => target;
        public long Deadline => deadline;
        public long NextAt { get; set; }
        public long CheckAt { get; set; }
    }

    readonly object _sync = new();
    readonly Dictionary<IPlayer, Role> _roles = [];
    readonly Dictionary<IPlayer, Trip> _trips = [];
    IServer _server = null!;
    IPluginStorage _store = null!;
    IWorld _world = null!;
    bool _bound;
    int? _claimed;
    int _need = DefaultNeed;
    Phase _phase;
    IPlayer? _judge;
    IPlayer? _suspect;
    bool _suspectTrue;
    long _startAt;
    int _shown;
    int _announced = -1;
    long _restUntil;
    long _topicEnd;
    int _topicShown;
    long _endAt;

    public void Load(IServer server)
    {
        _server = server;
        _store = server.Storage("court");
        _claimed = Stored(WorldKey);
        _need = Stored(NeedKey) is int saved && saved >= 1 ? saved : DefaultNeed;
        server.AddCommand("court", CourtCommand);
        server.AddCommand("judge", JudgeCommand);
        server.AddCommand("spectate", SpectateCommand);
        server.Update += Update;
        server.PlayerJoined += Joined;
        server.PlayerLeft += Left;
        server.PlayerKilled += (victim, _) => Killed(victim);
    }

    public void Unload()
    {
        lock (_sync)
        {
            if (!_bound) return;
            long now = Environment.TickCount64;
            if (_roles.Count > 0) SendHome(now, null);
            Travel(now, _world.Players);
        }
    }

    bool Managed(IWorld world) => world.Play && (_claimed is int id
        ? world.Id == id
        : world.Name.Contains(WorldName, StringComparison.OrdinalIgnoreCase));

    bool Attach()
    {
        IWorld? found = _server.Worlds.Where(Managed).MaxBy(open => open.Players.Count);
        if (found is null)
        {
            if (_bound) Forget();
            return false;
        }
        if (!_bound || found != _world)
        {
            Forget();
            _world = found;
            _bound = true;
        }
        return true;
    }

    void Reset()
    {
        _roles.Clear();
        _judge = _suspect = null;
        _phase = Phase.Lobby;
        _startAt = 0;
        _announced = -1;
    }

    void Forget()
    {
        Reset();
        _trips.Clear();
        _restUntil = 0;
    }

    void Update(float elapsed)
    {
        lock (_sync)
        {
            long now = Environment.TickCount64;
            if (!Attach()) return;
            IReadOnlyList<IPlayer> present = _world.Players;
            Travel(now, present);
            switch (_phase)
            {
                case Phase.Lobby:
                    Lobby(now, present.Count);
                    break;
                case Phase.Ending:
                    if (now >= _endAt) SendHome(now, null);
                    break;
                default:
                    Trial(now, present);
                    break;
            }
        }
    }

    void Lobby(long now, int count)
    {
        if (now < _restUntil) return;
        if (count != _announced)
        {
            _announced = count;
            if (count < _need)
            {
                _startAt = 0;
                _world.Broadcast($"Waiting for players {count}/{_need}", Orange);
            }
            else if (_startAt == 0)
            {
                _startAt = now + LobbySeconds * 1000L;
                _shown = LobbySeconds;
                _world.Broadcast($"The court starts in {LobbySeconds} seconds", Orange);
            }
        }
        if (_startAt == 0) return;

        long left = _startAt - now;
        if (left <= 0)
        {
            Begin(now);
            return;
        }
        int seconds = (int)((left + 999) / 1000);
        if (seconds > FinalCount || seconds >= _shown) return;
        _shown = seconds;
        _world.Broadcast(Bold(seconds.ToString(CultureInfo.InvariantCulture)), Orange);
    }

    void Begin(long now)
    {
        _startAt = 0;
        _announced = -1;

        Role[] lead = [Role.Judge, Role.Suspect, Role.Attorney];
        IPlayer[] players = [.. _world.Players.OrderBy(_ => Random.Shared.Next()).Take(MaxPlayers)];
        _roles.Clear();
        for (int i = 0; i < players.Length; i++)
            _roles[players[i]] = i < lead.Length ? lead[i] : Role.Witness;

        Team? lacking = null;
        foreach ((IPlayer player, Role role) in _roles)
            if (!player.SetTeam(TeamOf(role)))
            {
                lacking = TeamOf(role);
                break;
            }
        if (lacking is Team missing)
        {
            _world.Broadcast($"This world has no {missing} team spawn point, the court can't start", Red);
            _restUntil = now + FailedStartRest;
            SendHome(now, null);
            return;
        }

        _judge = Holder(Role.Judge);
        _suspect = Holder(Role.Suspect);
        _suspectTrue = Random.Shared.Next(2) == 0;
        _phase = Phase.Topic;
        _topicEnd = now + TopicSeconds * 1000L;
        _topicShown = TopicSeconds;

        Vector3[] seats = [.. WitnessSpots.OrderBy(_ => Random.Shared.Next())];
        int seat = 0;
        foreach ((IPlayer player, Role role) in _roles)
        {
            Vector3 spot = role switch
            {
                Role.Witness => seats[seat++ % seats.Length],
                Role.Suspect => SuspectSpot,
                Role.Attorney => AttorneySpot,
                _ => JudgeSpot,
            };
            Send(player, spot, now);
            Welcome(player, role);
        }
        foreach (IPlayer other in _world.Players)
            if (other != _judge) other.Message($"Judge has {TopicSeconds} seconds to choose today's court topic", LightOrange);
    }

    void Welcome(IPlayer player, Role role)
    {
        switch (role)
        {
            case Role.Witness:
                player.Message("You are a <b>WITNESS</b>", Cyan);
                break;
            case Role.Suspect:
                player.Message("You are a <b>SUSPECT</b>", Green);
                if (_suspectTrue) player.Message("and also you are <b>TRUE!</b>", LightGreen);
                else player.Message("and also you are a <b>CRIMINAL!</b>", Red);
                break;
            case Role.Attorney:
                player.Message("You are an <b>ATTORNEY</b>", Red);
                break;
            default:
                player.Message("You are a <b>JUDGE</b>", Yellow);
                player.Message($"You have {TopicSeconds} seconds to think about the court topic", LightOrange);
                player.Message("Use !judge custom {your text here} to write your own topic for the court. or use !judge random if you have no ideas", LightOrange);
                break;
        }
    }

    void Trial(long now, IReadOnlyList<IPlayer> present)
    {
        if (_roles.Any(pair => !present.Contains(pair.Key) || pair.Key.Team != TeamOf(pair.Value)))
        {
            Pause(now, null);
            return;
        }
        if (_phase != Phase.Topic) return;

        long left = _topicEnd - now;
        if (left <= 0)
        {
            _world.Broadcast("Timeout: The Judge has forgot the topic", Orange);
            SendHome(now, null);
            return;
        }
        int seconds = (int)((left + 999) / 1000);
        if (seconds > FinalCount || seconds >= _topicShown) return;
        _topicShown = seconds;
        _world.Broadcast(Bold(seconds.ToString(CultureInfo.InvariantCulture)), Orange);
    }

    void Killed(IPlayer victim)
    {
        lock (_sync)
        {
            if (!_bound || _phase is not (Phase.Topic or Phase.Court)) return;
            if (victim == _judge) Verdict(true, Environment.TickCount64);
            else if (victim == _suspect) Verdict(false, Environment.TickCount64);
        }
    }

    void Verdict(bool judgeDied, long now)
    {
        _phase = Phase.Ending;
        _endAt = now + ReturnDelay;
        foreach ((IPlayer player, Role role) in _roles)
        {
            if (judgeDied)
            {
                if (role is Role.Judge or Role.Witness) player.Message("GAME OVER: Judge has lost the court", Red);
                else player.Message("You WON! The <b>Judge</b> has lost this court", LightGreen);
            }
            else if (role == Role.Suspect)
                player.Message($"GAME OVER, Judge has sentenced you as a <b>{(_suspectTrue ? "TRUE" : "CRIMINAL")}</b>", Maroon);
            else if (role == Role.Attorney)
                player.Message("GAME OVER, Judge has sentenced suspect.", Maroon);
            else if (_suspectTrue)
                player.Message("GAME OVER, The suspect was <b>TRUE</b>", Red);
            else
                player.Message("YOU WON, The suspect was a <b>CRIMINAL</b>", LightGreen);
        }
    }

    void Pause(long now, IPlayer? leaver)
    {
        _world.Broadcast("Court is paused. Someone is failing to appear", Orange);
        SendHome(now, leaver);
    }

    void SendHome(long now, IPlayer? except)
    {
        IReadOnlyList<IPlayer> present = _world.Players;
        foreach (IPlayer player in _roles.Keys)
        {
            if (player == except || !present.Contains(player)) continue;
            player.SetTeam(LobbyTeam);
            Send(player, LobbySpot, now);
        }
        Reset();
    }

    IPlayer? Holder(Role role)
    {
        foreach ((IPlayer player, Role held) in _roles)
            if (held == role) return player;
        return null;
    }

    static Team TeamOf(Role role) => role switch
    {
        Role.Witness => Team.Blue,
        Role.Suspect => Team.Green,
        Role.Attorney => Team.Red,
        _ => Team.Yellow,
    };

    void Send(IPlayer player, Vector3 target, long now) => _trips[player] = new Trip(target, now + TripGiveUp);

    void Travel(long now, IReadOnlyList<IPlayer> present)
    {
        if (_trips.Count == 0) return;
        foreach ((IPlayer player, Trip trip) in _trips.ToList())
        {
            if (now >= trip.Deadline || !present.Contains(player))
            {
                _trips.Remove(player);
                continue;
            }
            if (now < trip.CheckAt) continue;
            if (Near(player, trip.Target))
            {
                _trips.Remove(player);
                continue;
            }
            if (now < trip.NextAt) continue;
            if (Warp(player, trip.Target)) trip.CheckAt = now + TripCheck;
            else trip.NextAt = now + TripRetry;
        }
    }

    static bool Near(IPlayer player, Vector3 target) =>
        player.Position is Vector3 spot && Vector3.Distance(spot, target) <= NearDistance;

    static bool Warp(IPlayer player, Vector3 target) =>
        player.Teleport(target, TeleportMethod.Teleporter) || player.Teleport(target, TeleportMethod.HiddenVehicle);

    void Joined(IPlayer player)
    {
        lock (_sync)
            if (_bound && player.World == _world && _phase != Phase.Lobby)
                player.Message("A court is in session, you will play in the next one", Orange);
    }

    void Left(IPlayer player)
    {
        lock (_sync)
        {
            _trips.Remove(player);
            if (!_bound || _phase is not (Phase.Topic or Phase.Court) || !_roles.ContainsKey(player)) return;
            Pause(Environment.TickCount64, player);
        }
    }

    void CourtCommand(IPlayer player, string[] arguments)
    {
        lock (_sync)
        {
            string action = arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "";
            if (action == "")
            {
                player.Message(Describe(player), Orange);
                return;
            }
            if (OnlyAdminsConfigure && !player.IsAdmin)
            {
                player.Message("Only the server's admins can change the court settings", Grey);
                return;
            }
            if (action is "max" or "min")
            {
                string value = arguments.Length > 1 ? arguments[1] : "";
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int need) || need < 1)
                {
                    player.Message("Use !court max <players>, for example !court max 2", Grey);
                    return;
                }
                _need = need;
                _announced = -1;
                _store.Set(NeedKey, need.ToString(CultureInfo.InvariantCulture));
                player.Message($"The court needs {need} {(need == 1 ? "player" : "players")} to start", Orange);
            }
            else if (action == "world")
            {
                int id = player.World.Id;
                if (id == 0)
                {
                    player.Message("This world isn't saved, so the court can't remember it", Grey);
                    return;
                }
                _claimed = id;
                _store.Set(WorldKey, id.ToString(CultureInfo.InvariantCulture));
                player.Message("This world is the court world now", Orange);
            }
            else player.Message("Use !court, !court max <players> or !court world", Grey);
        }
    }

    string Describe(IPlayer player)
    {
        if (!_bound || player.World != _world)
            return $"This isn't the court world. Type !court world here to make it one. The court needs {_need} players";
        return _phase == Phase.Lobby ? $"Waiting for players {_world.Players.Count}/{_need}" : "A court is in session";
    }

    void JudgeCommand(IPlayer player, string[] arguments)
    {
        lock (_sync)
        {
            if (!_bound || player != _judge || _phase is Phase.Lobby or Phase.Ending)
            {
                player.Message("Only the Judge can use this command", Grey);
                return;
            }
            if (_phase == Phase.Court)
            {
                player.Message("The topic is already chosen", Grey);
                return;
            }
            string action = arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "";
            if (action == "random") Choose(Topics[Random.Shared.Next(Topics.Length)]);
            else if (action == "custom" && Clean(string.Join(' ', arguments[1..])) is { Length: > 0 } custom) Choose(custom);
            else player.Message("Use !judge custom {your text here} or !judge random", LightOrange);
        }
    }

    void Choose(string topic)
    {
        _phase = Phase.Court;
        _world.Broadcast($"THE TOPIC IS: {topic}", Purple);
    }

    void SpectateCommand(IPlayer player, string[] arguments)
    {
        lock (_sync)
        {
            bool inCourt = _bound && player.World == _world && _phase is Phase.Topic or Phase.Court;
            if (!inCourt || !_roles.TryGetValue(player, out Role role) || role != Role.Witness)
            {
                player.Message("Only a Witness can use !spectate while the court is on", Grey);
                return;
            }
            Send(player, SpectatorSpot, Environment.TickCount64);
        }
    }

    static string Clean(string text)
    {
        string plain = text.Replace("<", "").Replace(">", "").Trim();
        return plain.Length > TopicLimit ? plain[..TopicLimit].TrimEnd() : plain;
    }

    static string Bold(string text) => $"<b>{text}</b>";

    int? Stored(string key) => int.TryParse(_store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
