using System.Collections.Generic;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace RadioNorvinsk;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.mcreaper.radionorvinsk";
    public string Name { get; init; } = "Radio Norvinsk";
    public string Author { get; init; } = "McReaper";
    public List<string> Contributors { get; init; }
    // The version is set once, in RadioNorvinsk.csproj.
    public Version Version { get; init; } = AssemblyVersion();
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string> Incompatibilities { get; init; }

    // Patches Fika's PresenceService internals: pinned to the 2.4 line it was written against.
    public Dictionary<string, Range> ModDependencies { get; init; } = new() { ["Fika"] = new Range("~2.4.2") };

    public string Url { get; init; } = "https://github.com/McReaper/SPT.RadioNorvinsk";
    public string License { get; init; } = "MIT";

    private static Version AssemblyVersion()
    {
        var version = typeof(ModMetadata).Assembly.GetName().Version;
        return new Version(version.Major, version.Minor, version.Build);
    }
}
