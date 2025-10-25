namespace CosmosBulkDemo.Models;

public class OrderDoc
{
    public string id { get; set; } = default!;
    public string userId { get; set; } = default!;
    public string orderId { get; set; } = default!;
    public decimal amount { get; set; }
    public string sku { get; set; } = default!;
    public DateTime ts { get; set; }
}
