using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// config/config.json。手续费目前固定 0，但计算点已经留好
/// （见 <see cref="TradeService"/> 的 fee 计算），以后改配置即可开启。
/// </summary>
public sealed class ModConfigFile
{
    public decimal FeeRate { get; set; } = 0m;
    public long MailLifetimeSeconds { get; set; } = 604800;
    public bool EnableDevRoutes { get; set; } = false;
}

[Injectable(InjectionType.Singleton)]
public sealed class StockMarketConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public decimal FeeRate { get; private set; } = 0m;
    public long MailLifetimeSeconds { get; private set; } = 604800;
    public bool EnableDevRoutes { get; private set; }

    public StockMarketConfig(ISptLogger<StockMarketConfig> logger, ModHelper modHelper)
    {
        try
        {
            string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            string path = Path.Combine(modFolder, "config", "config.json");
            if (!File.Exists(path))
                return;

            ModConfigFile? config = JsonSerializer.Deserialize<ModConfigFile>(
                File.ReadAllText(path), JsonOptions);
            if (config is null)
                return;

            FeeRate = Math.Clamp(config.FeeRate, 0m, 1m);
            MailLifetimeSeconds = Math.Max(60, config.MailLifetimeSeconds);
            EnableDevRoutes = config.EnableDevRoutes;
        }
        catch (Exception ex)
        {
            logger.Error($"[StockMarket] config.json 读取失败，使用默认值：{ex.Message}");
        }
    }
}
