using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RadioNorvinsk.Chronicle;
using RadioNorvinsk.Config;
using RadioNorvinsk.Discord;
using RadioNorvinsk.Levels;
using RadioNorvinsk.Patches;
using RadioNorvinsk.Raids;
using RadioNorvinsk.Sessions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Locales;

namespace RadioNorvinsk;

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class RadioNorvinskMod(
    ISptLogger<RadioNorvinskMod> logger,
    ModHelper modHelper,
    SaveServer saveServer,
    ProfileHelper profileHelper,
    WeatherHelper weatherHelper,
    LocaleService localeService) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        var config = ConfigLoader.Load(modFolder, logger);
        if (config == null)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            logger.Warning("[Radio Norvinsk] No webhookUrl in config/config.json: the radio stays off.");
            return Task.CompletedTask;
        }

        if (!Uri.TryCreate(config.WebhookUrl, UriKind.Absolute, out var webhookUrl) || webhookUrl.Scheme != Uri.UriSchemeHttps)
        {
            logger.Error("[Radio Norvinsk] webhookUrl in config/config.json is not an https URL: the radio stays off.");
            return Task.CompletedTask;
        }

        var locale = Locale.Load(modFolder, config.Language, logger);
        if (locale == null)
        {
            return Task.CompletedTask;
        }

        var webhook = new DiscordWebhook(webhookUrl, config.Persona ?? new PersonaConfig(), logger);
        var names = new Names(locale, localeService, config.Language);
        var chronicler = new Chronicler(config.Events ?? new EventsConfig(), locale, names, webhook);
        var levels = new LevelTracker(chronicler);
        var grace = TimeSpan.FromMinutes(Math.Max(0, config.ReconnectGraceMinutes));

        PatchContext.Logger = logger;
        PatchContext.Sessions = new SessionTracker(grace, chronicler, saveServer, logger);
        PatchContext.Levels = levels;
        PatchContext.Raids = new RaidReporter(chronicler, levels, saveServer, profileHelper, weatherHelper);

        AbstractPatch[] patches =
        [
            new PlayerConnectedPatch(),
            new PlayerDisconnectedPatch(),
            new PlayerLoggedOutPatch(),
            new LocalRaidStartPatch(),
            new LocalRaidEndPatch(),
            new MatchStatusPatch(),
            new LevelRecalculatedPatch(),
        ];

        var active = patches.Count(Enable);
        if (active == patches.Length)
        {
            logger.Success($"[Radio Norvinsk] On the air, in '{config.Language}'.");
        }
        else
        {
            logger.Warning($"[Radio Norvinsk] On the air, in '{config.Language}', with {active} of {patches.Length} hooks: some events stay silent.");
        }

        return Task.CompletedTask;
    }

    // AbstractPatch.Enable() can return without patching (e.g. caller-assembly check), so confirm it took.
    private bool Enable(AbstractPatch patch)
    {
        try
        {
            patch.Enable();
        }
        catch (Exception ex)
        {
            logger.Error($"[Radio Norvinsk] {patch.GetType().Name} failed: SPT or Fika may have changed.", ex);
            return false;
        }

        if (!patch.IsActive)
        {
            logger.Error($"[Radio Norvinsk] {patch.GetType().Name} NOT applied: SPT or Fika may have changed.");
        }

        return patch.IsActive;
    }
}
