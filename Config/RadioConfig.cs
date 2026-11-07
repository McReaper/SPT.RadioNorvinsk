namespace RadioNorvinsk.Config;

/// <summary>Contents of <c>config/config.json</c>. Defaults here are what a fresh install writes.</summary>
public class RadioConfig
{
    /// <summary>Discord webhook the chronicle is posted to. Empty: the radio stays off.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>File name in <c>locales/</c>, without <c>.json</c>. Missing lines fall back to English.</summary>
    public string Language { get; set; } = "en";

    public PersonaConfig Persona { get; set; } = new();

    public EventsConfig Events { get; set; } = new();

    /// <summary>
    /// A game that dropped without quitting (crash, lost connection) and reconnects within this delay
    /// stays in the same session: no message either way. Quitting from the game's menu ends it at once.
    /// </summary>
    public int ReconnectGraceMinutes { get; set; } = 10;
}

/// <summary>Name and avatar sent with every message, overriding the webhook's own.</summary>
public class PersonaConfig
{
    public string Name { get; set; } = "Radio Norvinsk";

    /// <summary>Public image URL. Empty: Discord shows the webhook's own avatar.</summary>
    public string AvatarUrl { get; set; } = "";
}

/// <summary>One switch per event, all on by default.</summary>
public class EventsConfig
{
    public bool Connect { get; set; } = true;
    public bool Disconnect { get; set; } = true;

    /// <summary>PMC raids only, one message per Fika match.</summary>
    public bool RaidStart { get; set; } = true;

    /// <summary>Deaths and missing in action, in PMC raids.</summary>
    public bool Death { get; set; } = true;

    /// <summary>Extractions and run-throughs, in PMC raids.</summary>
    public bool Extraction { get; set; } = true;

    public bool LevelUp { get; set; } = true;

    /// <summary>Bosses (and the Goons) killed by a player, in PMC raids.</summary>
    public bool BossKill { get; set; } = true;
}
