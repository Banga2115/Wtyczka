using System.Globalization;
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Wtyczka;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!Cli.TryParse(args, out var orderId))
        {
            Console.Error.WriteLine("Uzycie: wtyczka.exe --order-id <ID>");
            return 1;
        }

        try
        {
            Console.WriteLine($"[{DateTimeOffset.Now:O}] Start importu order_id={orderId}");
            var cfg = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables("WTYCZKA_")
                .Build();

            var bl = cfg.GetSection("BaseLinker").Get<BaseLinkerOptions>() ?? new();
            var sgt = cfg.GetSection("Subiekt").Get<SubiektOptions>() ?? new();
            var map = cfg.GetSection("Mappings").Get<MappingOptions>() ?? new();
            Validate(bl, sgt, map);

            using var http = new HttpClient();
            var blClient = new BaseLinkerClient(http, bl);
            Console.WriteLine($"[{DateTimeOffset.Now:O}] Pobieranie zamowienia z BaseLinkera...");
            var order = blClient.GetOrderByIdAsync(orderId, CancellationToken.None).GetAwaiter().GetResult();

            using var sfera = new SferaSubiektGateway(sgt);
            Console.WriteLine($"[{DateTimeOffset.Now:O}] Laczenie z Sfera...");
            sfera.Connect();
            Console.WriteLine($"[{DateTimeOffset.Now:O}] Import zamowienia do Subiekta...");
            var result = sfera.ImportOrder(order, map);
            Console.WriteLine($"Sukces. Utworzono ZK: {result.DocumentNumber}");
            return 0;
        }
        catch (BaseLinkerException ex)
        {
            Console.Error.WriteLine($"[BL] {ex.Message}");
            return 3;
        }
        catch (SubiektIntegrationException ex)
        {
            Console.Error.WriteLine($"[SGT] {ex.Message}");
            return 4;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[CONFIG] {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 5;
        }
    }

    private static void Validate(BaseLinkerOptions bl, SubiektOptions sgt, MappingOptions map)
    {
        static void Require(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Brak konfiguracji: {key}");
        }

        Require(bl.ApiToken, "BaseLinker.ApiToken");
        Require(sgt.SferaProgId, "Subiekt.SferaProgId");
        Require(sgt.Server, "Subiekt.Server");
        Require(sgt.Database, "Subiekt.Database");
        Require(sgt.Username, "Subiekt.Username");
        Require(sgt.Operator, "Subiekt.Operator");
        Require(map.ShippingItemSymbol, "Mappings.ShippingItemSymbol");
    }
}

internal static class Cli
{
    public static bool TryParse(string[] args, out long orderId)
    {
        orderId = 0;
        return args.Length == 2 &&
               string.Equals(args[0], "--order-id", StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out orderId) &&
               orderId > 0;
    }
}
