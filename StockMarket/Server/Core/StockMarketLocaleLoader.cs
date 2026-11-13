using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace TarkFX.StockMarket.Server.Core;

/// <summary>
/// 把 locales/*.json 合并进服务端本地化表。
/// 客户端用原生的 <c>.Localized()</c> 扩展按键取文案，不需要额外协议。
/// （与 OperatorTraits 的做法一致：LazyLoad transformer 每次序列化都会合并，多个 mod 互不影响。）
/// </summary>
[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad - 1)]
public sealed class StockMarketLocaleLoader(
    LocaleTable localeTable,
    ModHelper modHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        string modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        string localesFolder = Path.Combine(modFolder, "locales");

        if (!Directory.Exists(localesFolder))
            return Task.CompletedTask;

        foreach (string file in Directory.GetFiles(localesFolder, "*.json"))
        {
            string langCode = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

            if (!localeTable.Global.TryGetValue(langCode, out var lazyLocale))
                continue;

            Dictionary<string, string>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;
            }

            if (entries is null || entries.Count == 0)
                continue;

            lazyLocale.AddTransformer(locale =>
            {
                if (locale is null)
                    return null;

                foreach (KeyValuePair<string, string> entry in entries)
                    locale[entry.Key] = entry.Value;
                return locale;
            });
        }

        return Task.CompletedTask;
    }
}
