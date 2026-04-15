using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Wtyczka;

internal sealed class BaseLinkerClient
{
    private readonly HttpClient _http;
    private readonly BaseLinkerOptions _options;

    public BaseLinkerClient(HttpClient http, BaseLinkerOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<ExternalOrder> GetOrderByIdAsync(long orderId, CancellationToken ct)
    {
        var json = await CallGetOrdersAsync(new Dictionary<string, object?> { ["order_id"] = orderId }, ct);
        return ParseOrderFromResponse(json, orderId);
    }

    private async Task<string> CallGetOrdersAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["token"] = _options.ApiToken,
            ["method"] = "getOrders",
            ["parameters"] = JsonSerializer.Serialize(payload)
        };

        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _options.ApiUrl)
                {
                    Content = new FormUrlEncodedContent(form)
                };

                using var res = await _http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync();

                if ((int)res.StatusCode is >= 500 or 429)
                    throw new HttpRequestException($"HTTP {(int)res.StatusCode}");

                if (!res.IsSuccessStatusCode)
                    throw new BaseLinkerException($"BaseLinker HTTP {(int)res.StatusCode}: {body}");

                return body;
            }
            catch (Exception ex)
            {
                lastError = ex;

                var transient = ex is HttpRequestException || ex is TaskCanceledException;
                if (!transient || attempt == 3)
                    break;

                await Task.Delay(attempt * 700, ct);
            }
        }

        throw new BaseLinkerException("Nie udalo sie pobrac zamowienia z BaseLinkera.", lastError);
    }

    private static ExternalOrder ParseOrderFromResponse(string json, long wantedOrderId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = ReadText(root, "status");
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            var message = ReadText(root, "error_message") ?? "Brak szczegolow";
            throw new BaseLinkerException($"BaseLinker status={status}, blad={message}");
        }

        if (!root.TryGetProperty("orders", out var ordersNode) || ordersNode.ValueKind != JsonValueKind.Array)
            throw new BaseLinkerException("Brak tablicy 'orders' w odpowiedzi BaseLinkera.");

        foreach (var orderNode in ordersNode.EnumerateArray())
        {
            var order = ParseOrder(orderNode);
            if (order.OrderId == wantedOrderId)
                return order;
        }

        throw new BaseLinkerException($"Nie znaleziono zamowienia order_id={wantedOrderId}.");
    }

    private static ExternalOrder ParseOrder(JsonElement node)
    {
        if (!node.TryGetProperty("products", out var productsNode) || productsNode.ValueKind != JsonValueKind.Array)
            throw new BaseLinkerException("Brak tablicy 'products' w zamowieniu.");

        var items = new List<ExternalOrderItem>();
        foreach (var p in productsNode.EnumerateArray())
        {
            items.Add(new ExternalOrderItem
            {
                Name = ReadText(p, "name", required: true)!,
                Sku = ReadText(p, "sku", required: true),
                Ean = ReadText(p, "ean"),
                Quantity = ReadDecimal(p, "quantity"),
                PriceGross = ReadDecimal(p, "price_brutto"),
                VatRate = ReadDecimal(p, "tax_rate")
            });
        }

        if (items.Count == 0)
            throw new BaseLinkerException("Zamowienie nie ma pozycji.");

        return new ExternalOrder
        {
            OrderId = ReadLong(node, "order_id"),
            PaymentMethod = ReadText(node, "payment_method"),
            ShippingCost = ReadDecimal(node, "delivery_price"),
            Customer = new ExternalCustomer
            {
                FullName = ReadText(node, "delivery_fullname"),
                Company = ReadText(node, "invoice_company"),
                Nip = ReadText(node, "invoice_nip"),
                Email = ReadText(node, "email"),
                Phone = ReadText(node, "phone"),
                Address = ReadText(node, "delivery_address"),
                City = ReadText(node, "delivery_city"),
                PostCode = ReadText(node, "delivery_postcode"),
                CountryCode = ReadText(node, "delivery_country_code")
            },
            Items = items
        };
    }

    private static string? ReadText(JsonElement node, string name, bool required = false)
    {
        if (!node.TryGetProperty(name, out var value))
        {
            if (required)
                throw new BaseLinkerException($"Brak wymaganego pola tekstowego '{name}' w odpowiedzi BaseLinkera.");
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = Normalize(value.GetString());
            if (!required || !string.IsNullOrWhiteSpace(text))
                return text;
        }
        else if (!required && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            return Normalize(value.ToString());
        }

        if (required)
            throw new BaseLinkerException($"Brak wymaganego pola tekstowego '{name}' w odpowiedzi BaseLinkera.");

        return null;
    }

    private static long ReadLong(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value))
            throw new BaseLinkerException($"Brak wymaganego pola liczbowego '{name}' w odpowiedzi BaseLinkera.");

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            return numeric;

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new BaseLinkerException($"Pole '{name}' nie ma poprawnej wartosci liczbowej.");
    }

    private static decimal ReadDecimal(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value))
            throw new BaseLinkerException($"Brak wymaganego pola liczbowego '{name}' w odpowiedzi BaseLinkera.");

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var direct))
            return direct;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant))
                return invariant;
            if (decimal.TryParse(text, NumberStyles.Any, new CultureInfo("pl-PL"), out var polish))
                return polish;
        }

        throw new BaseLinkerException($"Pole '{name}' nie ma poprawnej wartosci liczbowej.");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
