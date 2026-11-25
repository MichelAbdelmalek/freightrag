namespace FreightRag.Core.Domain;

/// <summary>A named piece of information that can be read from a document, such as carrierName or totalDue.</summary>
public sealed record DocumentField(string Name, string Description);

internal sealed record FieldRule(DocumentField Field, Func<FieldSource, string?> Read);

/// <summary>
/// The catalog: which fields exist for each document type and the rule that reads each one. The fields come from what the
/// real example documents contain. Keys are camelCase; the same key means the same thing in every type that has it,
/// except where noted (detention is a rate on a rate confirmation and a charge on an invoice).
/// </summary>
public static class DocumentFields
{
    public static IReadOnlyList<DocumentField> For(DocType type) => RulesFor(type).Select(r => r.Field).ToList();

    internal static IReadOnlyList<FieldRule> RulesFor(DocType type) =>
        type == DocType.RateConfirmation ? RateConfirmation
        : type == DocType.BillOfLading ? BillOfLading
        : type == DocType.Invoice ? Invoice
        : [];

    private static FieldRule R(string name, string description, Func<FieldSource, string?> read) => new(new DocumentField(name, description), read);

    private static string? Plain(FieldSource s, string section, int index) => s.PlainLines(section).Skip(index).FirstOrDefault();

    private static readonly IReadOnlyList<FieldRule> RateConfirmation =
    [
        R("brokerName", "The broker's company name", s => FieldSource.NameOf(s.Value("broker"))),
        R("brokerAddress", "The broker's address", s => FieldSource.AddressOf(s.Value("broker"))),
        R("brokerMc", "The broker's MC number", s => FieldSource.McOf(s.Value("broker"))),
        R("carrierName", "The carrier's company name", s => FieldSource.NameOf(s.Value("carrier"))),
        R("carrierMc", "The carrier's MC number", s => FieldSource.McOf(s.Value("carrier"))),
        R("carrierDot", "The carrier's DOT number", s => FieldSource.DotOf(s.Value("carrier"))),
        R("loadNumber", "The load number", s => s.Value("load number")),
        R("dateIssued", "The date the confirmation was issued", s => s.Value("date issued")),
        R("pickupLocation", "Where the load is picked up", s => s.Value("location", "PICKUP")),
        R("shipperName", "The shipper", s => s.Value("shipper", "PICKUP")),
        R("pickupWindow", "The pickup date and time window", s => s.Value("date and window", "PICKUP")),
        R("deliveryLocation", "Where the load is delivered", s => s.Value("location", "DELIVERY")),
        R("deliveryDate", "The delivery date and deadline", s => s.Value("date", "DELIVERY")),
        R("commodity", "What is being shipped", s => s.Value("commodity")),
        R("quantity", "How many pieces (for example 18 pallets)", s => FieldSource.SplitQuantity(s.Value("quantity")).Quantity),
        R("totalWeight", "The total weight", s => FieldSource.SplitQuantity(s.Value("quantity")).Weight ?? s.Value("total weight")),
        R("equipment", "The trailer type", s => s.Value("equipment")),
        R("temperature", "Temperature requirements", s => s.Value("temperature")),
        R("lineHaul", "The line haul amount", s => s.Value("line haul") ?? s.LastAmountOnRowLabelled(@"Line[\t ]+haul")),
        R("fuelSurcharge", "The fuel surcharge", s => s.Value("fuel surcharge") ?? s.LastAmountOnRowLabelled(@"Fuel[\t ]+surcharge")),
        R("totalAgreedRate", "The total agreed rate", s => s.Value("total agreed rate") ?? s.LastAmountOnRowLabelled(@"Total[\t ]+agreed[\t ]+rate")),
        R("detention", "The detention rate and its free time", s => s.Value("detention")),
        R("paymentTerms", "The payment terms", s => s.Value("payment terms") ?? Plain(s, "PAYMENT TERMS", 0)),
    ];

    private static (string? Quantity, string? Commodity, string? Weight) Shipment(FieldSource s) =>
        FieldSource.SplitShipment(Plain(s, "SHIPMENT DETAILS", 0));

    private static string? Party(FieldSource s, string label, string section, bool address)
    {
        var inline = s.Value(label);
        if (inline is not null)
            return address ? FieldSource.AddressOf(inline) : FieldSource.NameOf(inline);

        var first = Plain(s, section, 0);
        if (first is null) return null;
        var nameAndAddress = FieldSource.AddressOf(first);
        if (nameAndAddress is not null)   // "Name, 12 Main St" on one line
            return address ? nameAndAddress : FieldSource.NameOf(first);
        return address ? Plain(s, section, 1) : first;
    }

    private static readonly IReadOnlyList<FieldRule> BillOfLading =
    [
        R("bolNumber", "The bill of lading number", s => s.Value("bol number")),
        R("loadNumber", "The load number", s => s.Value("load number")),
        R("shipDate", "The ship date", s => s.Value("ship date")),
        R("shipperName", "The shipper", s => Party(s, "shipper", "SHIPPER", address: false)),
        R("shipperAddress", "The shipper's address", s => Party(s, "shipper", "SHIPPER", address: true)),
        R("consigneeName", "The consignee (the receiver)", s => Party(s, "consignee", "CONSIGNEE", address: false)),
        R("consigneeAddress", "The consignee's address", s => Party(s, "consignee", "CONSIGNEE", address: true)),
        R("carrierName", "The carrier", s => FieldSource.NameOf(s.Value("carrier")) ?? Plain(s, "CARRIER", 0)),
        R("driverName", "The driver", s => s.Value("driver")),
        R("truckNumber", "The truck number", s => s.Value("truck number")),
        R("trailerNumber", "The trailer number", s => s.Value("trailer number")),
        R("sealNumber", "The seal number", s => s.Value("seal number")),
        R("quantity", "How many pieces (for example 18 pallets)", s => Shipment(s).Quantity),
        R("commodity", "What is being shipped", s => Shipment(s).Commodity),
        R("totalWeight", "The total weight", s => Shipment(s).Weight ?? s.Value("total weight")),
        R("freightClass", "The freight class", s => s.Value("freight class")),
        R("temperatureSetting", "The temperature setting", s => s.Value("temperature setting")),
        R("hazardousMaterials", "Whether hazardous materials are carried", s => s.Value("hazardous materials")),
        R("deliveredAt", "When it was delivered", s => s.Value("delivered")),
        R("receivedBy", "Who received it", s => s.Value("received by")),
        R("deliveryCondition", "The condition on delivery", s => s.Value("condition")),
        R("productTemperatureAtDelivery", "The product temperature on delivery", s => s.Value("product temperature at delivery")),
        R("sealIntact", "Whether the seal was intact on arrival", s => s.Value("seal intact on arrival")),
    ];

    private static readonly IReadOnlyList<FieldRule> Invoice =
    [
        R("carrierName", "The carrier that issued the invoice", s => FieldSource.NameOf(s.Value("from"))),
        R("carrierAddress", "The carrier's address", s => FieldSource.AddressOf(s.Value("from"))),
        R("billToName", "Who is billed", s => FieldSource.NameOf(s.Value("bill to"))),
        R("billToAddress", "The billed party's address", s => FieldSource.AddressOf(s.Value("bill to"))),
        R("invoiceNumber", "The invoice number", s => s.Value("invoice number")),
        R("invoiceDate", "The invoice date", s => s.Value("invoice date")),
        R("loadNumber", "The load number", s => s.Value("load number")),
        R("bolNumber", "The bill of lading number", s => s.Value("bol number")),
        R("lineHaul", "The line haul charge", s => s.Value("line haul") ?? s.LastAmountOnRowLabelled(@"Line[\t ]+haul")),
        R("fuelSurcharge", "The fuel surcharge", s => s.Value("fuel surcharge") ?? s.LastAmountOnRowLabelled(@"Fuel[\t ]+surcharge")),
        R("detention", "The detention charge (none when there was none)", s => s.Value("detention") ?? s.LastAmountOnRowLabelled("Detention")),
        R("totalDue", "The total due", s => s.Value("total due") ?? s.LastAmountOnRowLabelled(@"Total[\t ]+due")),
        R("paymentDueDate", "The payment due date", s => FieldSource.SplitDateAndTerms(s.Value("payment due date")).Date),
        R("paymentTerms", "The payment terms, such as Net 30", s => FieldSource.SplitDateAndTerms(s.Value("payment due date")).Terms),
        R("attachments", "The documents attached", s => s.Value("attachments")),
        R("billingContact", "Who to contact about billing", s => s.Value("questions")),
    ];
}
