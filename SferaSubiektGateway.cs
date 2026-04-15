using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Wtyczka;

internal sealed class SferaSubiektGateway : IDisposable
{
    private const BindingFlags GetFlags =
        BindingFlags.GetProperty | BindingFlags.GetField | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    private const BindingFlags SetFlags =
        BindingFlags.SetProperty | BindingFlags.SetField | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    private const BindingFlags InvokeFlags =
        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    private readonly SubiektOptions _options;
    private object? _gt;
    private object? _subiekt;

    public SferaSubiektGateway(SubiektOptions options) => _options = options;

    public void Connect()
    {
        var gtType = Type.GetTypeFromProgID(_options.SferaProgId, throwOnError: false);
        if (gtType is null)
            throw new SubiektIntegrationException($"Brak ProgID COM: {_options.SferaProgId}");

        _gt = Activator.CreateInstance(gtType) ?? throw new SubiektIntegrationException("Nie mozna utworzyc instancji GT.");

        ComSet(_gt, "Produkt", _options.ProductId);
        ComSet(_gt, "Autentykacja", _options.Authentication);
        ComSet(_gt, "Serwer", _options.Server);
        ComSet(_gt, "Baza", _options.Database);
        ComSet(_gt, "Uzytkownik", _options.Username);
        ComSet(_gt, "UzytkownikHaslo", _options.Password);
        ComSet(_gt, "Operator", _options.Operator);
        ComSet(_gt, "OperatorHaslo", _options.OperatorPassword);

        _subiekt = ComCall(_gt, "Uruchom", _options.RunMode, _options.RunFlags);
    }

    public ImportResult ImportOrder(ExternalOrder order, MappingOptions map)
    {
        if (_subiekt is null)
            throw new SubiektIntegrationException("Brak polaczenia z Sfera.");

        try
        {
            order.Items = MergeItems(order.Items);

            var contractors = ComGet(_subiekt, "KontrahenciManager");
            var products = ComGet(_subiekt, "TowaryManager");
            var documents = ComGet(_subiekt, "SuDokumentyManager");

            var contractor = EnsureContractor(contractors, order.Customer);
            var productRefs = order.Items.Select(i => EnsureProduct(products, i)).ToList();
            var shippingRef = order.ShippingCost > 0 ? EnsureShippingProduct(products, map.ShippingItemSymbol) : null;

            var number = CreateOrderDocument(documents, order, contractor, productRefs, shippingRef);
            return new ImportResult { DocumentNumber = number };
        }
        catch (Exception ex)
        {
            throw new SubiektIntegrationException($"Import order_id={order.OrderId} przerwany. {ex.Message}", ex);
        }
    }

    private static List<ExternalOrderItem> MergeItems(IEnumerable<ExternalOrderItem> items)
    {
        return items
            .GroupBy(i => new
            {
                Sku = (TrimOrNull(i.Sku) ?? "").ToUpperInvariant(),
                Price = i.PriceGross.ToString(CultureInfo.InvariantCulture),
                Vat = i.VatRate.ToString(CultureInfo.InvariantCulture)
            })
            .Select(group =>
            {
                var first = group.First();
                return new ExternalOrderItem
                {
                    Name = first.Name,
                    Sku = first.Sku,
                    Ean = first.Ean,
                    Quantity = group.Sum(x => x.Quantity <= 0 ? 1m : x.Quantity),
                    PriceGross = first.PriceGross,
                    VatRate = first.VatRate
                };
            })
            .ToList();
    }

    private static object EnsureContractor(object manager, ExternalCustomer customer)
    {
        var nip = TrimOrNull(customer.Nip);
        var email = TrimOrNull(customer.Email);

        var existing = FindContractor(manager, nip, email);
        if (existing is not null)
            return existing;

        var entry = ComCall(manager, "DodajKontrahenta");

        var symbol = NextContractorSymbol(manager, BuildContractorSymbol(customer, nip, email));
        ComSet(entry, "Symbol", symbol);
        ComSet(entry, "Typ", 0);
        ComSet(entry, "Osoba", string.IsNullOrWhiteSpace(customer.Company));

        var displayName = First(customer.Company, customer.FullName, "Kontrahent BaseLinker");
        ComSet(entry, "Nazwa", displayName);
        ComSet(entry, "NazwaPelna", displayName);

        if (string.IsNullOrWhiteSpace(customer.Company))
        {
            var fullName = TrimOrNull(customer.FullName);
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                var parts = fullName
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0)
                    .ToArray();

                if (parts.Length > 0) ComSet(entry, "OsobaImie", parts[0]);
                if (parts.Length > 1) ComSet(entry, "OsobaNazwisko", string.Join(" ", parts.Skip(1)));
            }
        }

        SetTextIfValue(entry, "NIP", nip);
        SetTextIfValue(entry, "Email", email);
        SetTextIfValue(entry, "Ulica", TrimOrNull(customer.Address));
        SetTextIfValue(entry, "Miejscowosc", TrimOrNull(customer.City));
        SetTextIfValue(entry, "KodPocztowy", TrimOrNull(customer.PostCode));
        if (string.Equals(TrimOrNull(customer.CountryCode), "PL", StringComparison.OrdinalIgnoreCase))
            ComSet(entry, "Panstwo", 1);

        SaveEntity(entry, "kontrahent");
        return entry;
    }

    private static bool IsPermanentContractor(object contractor)
    {
        var symbol = TryReadText(contractor, "Symbol");
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        return !symbol.Contains('*');
    }

    private static string BuildContractorSymbol(ExternalCustomer customer, string? nip, string? email)
    {
        var seed = First(nip, email?.Split('@')[0], customer.Company, customer.FullName, "BL");
        var alnum = new string(seed
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(alnum))
            alnum = "BL";

        var raw = $"BL{alnum}";
        return raw.Length <= 20 ? raw : raw.Substring(0, 20);
    }

    private static string NextContractorSymbol(object manager, string baseSymbol)
    {
        var symbol = baseSymbol;
        var counter = 1;

        while (FindProductLike(manager, "Symbol", symbol) is not null && counter < 1000)
        {
            var suffix = counter.ToString(CultureInfo.InvariantCulture);
            var headLen = Math.Max(1, 20 - suffix.Length);
            var head = baseSymbol.Length > headLen ? baseSymbol.Substring(0, headLen) : baseSymbol;
            symbol = head + suffix;
            counter++;
        }

        return symbol;
    }

    private static object EnsureProduct(object manager, ExternalOrderItem item)
    {
        var symbol = TrimOrNull(item.Sku);
        if (string.IsNullOrWhiteSpace(symbol))
            throw new SubiektIntegrationException($"Produkt '{item.Name}' nie ma SKU (Symbol=SKU).");

        var existing = FindProductLike(manager, "Symbol", symbol);
        if (existing is not null)
            return existing;

        var entry = ComCall(manager, "DodajTowar");
        ComSet(entry, "Symbol", symbol);
        ComSet(entry, "Nazwa", item.Name);
        SaveEntity(entry, $"towar {symbol}");
        return entry;
    }

    private static object EnsureShippingProduct(object manager, string shippingSymbol)
    {
        var symbol = TrimOrNull(shippingSymbol) ?? throw new SubiektIntegrationException("Pusty ShippingItemSymbol.");
        var existing = FindProductLike(manager, "Symbol", symbol);
        if (existing is not null)
            return existing;

        var entry = ComCall(manager, "DodajUsluge");
        ComSet(entry, "Symbol", symbol);
        ComSet(entry, "Nazwa", "Dostawa");
        SaveEntity(entry, $"dostawa {symbol}");
        return entry;
    }

    private static string CreateOrderDocument(
        object documentsManager,
        ExternalOrder order,
        object contractor,
        IReadOnlyList<object> products,
        object? shippingProduct)
    {
        var document = ComCall(documentsManager, "DodajZK");

        var contractorId = ComGet(contractor, "Identyfikator");
        ComSet(document, "KontrahentId", contractorId);
        ComSet(document, "OdbiorcaId", contractorId);
        ComSet(document, "Uwagi", $"BaseLinker order_id={order.OrderId}");
        ComSet(document, "DataWystawienia", DateTime.Now);

        for (var i = 0; i < order.Items.Count; i++)
            AddRow(document, products[i], order.Items[i].Quantity);

        if (shippingProduct is not null && order.ShippingCost > 0)
            AddRow(document, shippingProduct, 1m);

        var amountDue = ComGet(document, "KwotaDoZaplaty");
        ComSet(document, "PlatnoscPrzelewKwota", amountDue);

        SaveEntity(document, "dokument ZK");
        return First(TryReadText(document, "NumerPelny"), $"ZK(order_id={order.OrderId})");
    }

    private static void AddRow(object doc, object product, decimal qty)
    {
        var quantity = qty <= 0 ? 1m : qty;
        var rows = ComGet(doc, "Pozycje");
        var row = ComCall(rows, "Dodaj", product);
        ComSet(row, "IloscJm", quantity);
    }

    private static object? FindContractor(object manager, string? nip, string? email)
    {
        foreach (var item in EnumerateCollection(manager))
        {
            if (!IsPermanentContractor(item))
                continue;

            var nipValue = TryReadText(item, "NIP");
            if (!string.IsNullOrWhiteSpace(nip) &&
                string.Equals(nipValue, nip, StringComparison.OrdinalIgnoreCase))
                return item;

            var emailValue = TryReadText(item, "Email");
            if (!string.IsNullOrWhiteSpace(email) &&
                string.Equals(emailValue, email, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    private static object? FindProductLike(object manager, string propertyName, string wanted)
    {
        foreach (var item in EnumerateCollection(manager))
        {
            var value = TryReadText(item, propertyName);
            if (!string.IsNullOrWhiteSpace(value) &&
                string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    private static IEnumerable<object> EnumerateCollection(object manager)
    {
        var collection = ComCall(manager, "OtworzKolekcje", null, null);
        if (collection is not IEnumerable enumerable)
            throw new SubiektIntegrationException("Nie udalo sie otworzyc kolekcji managera.");

        foreach (var item in enumerable)
            if (item is not null)
                yield return item;
    }

    private static object ComGet(object target, string member)
    {
        try
        {
            var value = target.GetType().InvokeMember(member, GetFlags, null, target, null);
            return value ?? throw new SubiektIntegrationException($"COM zwrocil null dla {target.GetType().Name}.{member}");
        }
        catch (Exception ex)
        {
            throw new SubiektIntegrationException($"Nie moge odczytac COM: {target.GetType().Name}.{member}", ex);
        }
    }

    private static string? TryReadText(object target, string member)
    {
        try
        {
            var value = target.GetType().InvokeMember(member, GetFlags, null, target, null);
            return value is null ? null : TrimOrNull(value.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static void ComSet(object target, string member, object value)
    {
        try
        {
            target.GetType().InvokeMember(member, SetFlags, null, target, new[] { value });
        }
        catch (Exception ex)
        {
            throw new SubiektIntegrationException($"Nie moge ustawic COM: {target.GetType().Name}.{member}", ex);
        }
    }

    private static object ComCall(object target, string method, params object?[] args)
    {
        try
        {
            var value = target.GetType().InvokeMember(method, InvokeFlags, null, target, args);
            return value ?? throw new SubiektIntegrationException($"Metoda COM zwrocila null: {target.GetType().Name}.{method}");
        }
        catch (Exception ex)
        {
            throw new SubiektIntegrationException($"Nie moge wywolac COM: {target.GetType().Name}.{method}", ex);
        }
    }

    private static void ComCallVoid(object target, string method, params object?[] args)
    {
        try
        {
            target.GetType().InvokeMember(method, InvokeFlags, null, target, args);
        }
        catch (Exception ex)
        {
            throw new SubiektIntegrationException($"Nie moge wywolac COM: {target.GetType().Name}.{method}", ex);
        }
    }

    private static void SaveEntity(object entity, string context)
    {
        try
        {
            ComCallVoid(entity, "Zapisz");
        }
        catch (Exception ex)
        {
            var details = TryReadText(entity, "SzczegolyOstatniegoBledu");
            var detailsPart = string.IsNullOrWhiteSpace(details) ? "" : $" Szczegoly: {details}";
            var reason = ex.InnerException?.Message ?? ex.Message;
            throw new SubiektIntegrationException(
                $"Nie udalo sie zapisac encji ({context}) {entity.GetType().Name}.{detailsPart} Przyczyna: {reason}", ex);
        }
    }

    private static void SetTextIfValue(object target, string member, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ComSet(target, member, value);
    }

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    public void Dispose()
    {
        if (_subiekt is not null)
        {
            if (_options.CloseOnDispose)
            {
                try
                {
                    ComCallVoid(_subiekt, "Zakoncz");
                }
                catch
                {
                }
            }

            ReleaseCom(_subiekt);
            _subiekt = null;
        }

        if (_gt is not null)
        {
            ReleaseCom(_gt);
            _gt = null;
        }
    }

    private static void ReleaseCom(object value)
    {
        if (!value.GetType().IsCOMObject)
            return;

        try
        {
            while (Marshal.ReleaseComObject(value) > 0)
            {
            }
        }
        catch
        {
        }
    }
}
