namespace Wtyczka;

internal sealed class BaseLinkerOptions
{
    public string ApiUrl { get; set; } = "https://api.baselinker.com/connector.php";
    public string ApiToken { get; set; } = "";
}

internal sealed class SubiektOptions
{
    public string SferaProgId { get; set; } = "InsERT.GT";
    public int ProductId { get; set; } = 1;
    public int Authentication { get; set; } = 0;
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Operator { get; set; } = "";
    public string OperatorPassword { get; set; } = "";
    public int RunMode { get; set; } = 0;
    public int RunFlags { get; set; } = 4;
    public bool CloseOnDispose { get; set; } = false;
}

internal sealed class MappingOptions
{
    public string ShippingItemSymbol { get; set; } = "DOSTAWA";
}

internal sealed class ExternalOrder
{
    public long OrderId { get; set; }
    public string? PaymentMethod { get; set; }
    public decimal ShippingCost { get; set; }
    public ExternalCustomer Customer { get; set; } = new();
    public List<ExternalOrderItem> Items { get; set; } = new();
}

internal sealed class ExternalCustomer
{
    public string? FullName { get; set; }
    public string? Company { get; set; }
    public string? Nip { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? PostCode { get; set; }
    public string? CountryCode { get; set; }
}

internal sealed class ExternalOrderItem
{
    public string Name { get; set; } = "Towar";
    public string? Sku { get; set; }
    public string? Ean { get; set; }
    public decimal Quantity { get; set; }
    public decimal PriceGross { get; set; }
    public decimal VatRate { get; set; }
}

internal sealed class ImportResult
{
    public string DocumentNumber { get; set; } = "";
}
