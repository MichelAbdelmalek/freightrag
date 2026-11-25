using FreightRag.Core.Domain;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Domain;

public class DocumentFieldExtractorTests
{
    private static ExtractedFields Extract(string text, DocType type, string? fields = null)
    {
        Assert.True(FieldSelection.TryParse(fields, type, out var selection, out _));
        return DocumentFieldExtractor.Extract(text, selection!);
    }

    private static string? Field(string text, DocType type, string name) => Extract(text, type, name)[name];

    // ------------------------------------------------------------------ every real document

    public static TheoryData<string, string> RealFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var tenant in Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "data")).Order())
            foreach (var file in Directory.GetFiles(tenant, "*.txt").Order())
                data.Add(Path.GetFileName(tenant), Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(RealFiles))]
    public void Every_real_document_gives_fields_that_appear_verbatim_in_it_and_the_ids_are_never_null(string tenant, string file)
    {
        var text = TestData.ReadText(tenant, file);
        Assert.True(DocType.TryFromFileName(file, out var type));

        var fields = Extract(text, type!);

        if (type == DocType.DriverHandbook)
        {
            Assert.Equal(0, fields.Count);
            return;
        }
        Assert.NotNull(fields["loadNumber"]);
        Assert.NotNull(fields["carrierName"]);
        foreach (var (name, value) in fields.Items.Where(i => i.Value is not null))
            Assert.True(text.Contains(value!, StringComparison.Ordinal), $"{file}: {name} = '{value}' is not in the document");
    }

    public static TheoryData<string, string, string, string?> Expected()
    {
        var d = new TheoryData<string, string, string, string?>();
        void Add(string tenant, string file, params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values) d.Add(tenant, file, name, value);
        }
        const string N = TestData.NileFreight, D = TestData.DeltaHaul;

        Add(N, "rate-confirmation-48213.txt",
            ("brokerName", "Crescent Logistics LLC"), ("brokerAddress", "1200 Peachtree St NE, Atlanta, GA 30309"), ("brokerMc", "784512"),
            ("carrierName", "Nile Freight Lines Inc."), ("carrierMc", "902114"), ("carrierDot", "3318820"), ("loadNumber", "48213"),
            ("dateIssued", "2026-10-12"), ("pickupLocation", "Savannah Port Terminal, Gate 3, 400 Port Rd, Savannah, GA 31401"),
            ("shipperName", "Coastal Electronics Import Co."), ("pickupWindow", "2026-10-14, 08:00 to 12:00"),
            ("deliveryLocation", "Peachtree Distribution Center, Dock 4, 2250 Fulton Industrial Blvd SW, Atlanta, GA 30336"),
            ("deliveryDate", "2026-10-15, deliver by 14:00"), ("commodity", "Packaged consumer electronics"), ("quantity", "18 pallets"),
            ("totalWeight", "38,400 lbs"), ("equipment", "53 ft dry van"), ("temperature", null), ("lineHaul", "$2,450.00"),
            ("fuelSurcharge", "$180.00"), ("totalAgreedRate", "$2,630.00"),
            ("detention", "$50.00 per hour after 2 free hours at pickup or delivery"),
            ("paymentTerms", "Net 30 days from receipt of signed proof of delivery (POD) and carrier invoice."));
        Add(D, "rate-confirmation-77104.txt",
            ("shipperName", null), ("brokerName", "Gulfline Brokerage Inc."), ("brokerMc", "661207"), ("carrierDot", "2970415"),
            ("quantity", "22 pallets"), ("totalWeight", "41,200 lbs"),
            ("temperature", "Maintain -10 degrees F continuously. Reefer must be pre-cooled before loading."),
            ("totalAgreedRate", "$4,210.00"), ("detention", "$75.00 per hour after 2 free hours"),
            ("paymentTerms", "Net 21 days from receipt of signed POD and temperature download report."));
        Add(D, "rate-confirmation-77125.txt",
            ("brokerName", "Lone Star Freight Partners"), ("brokerAddress", "2100 Ross Ave, Dallas, TX 75201"), ("brokerMc", "590144"),
            ("shipperName", "Prairie Creamery"), ("deliveryLocation", "Delta Cold Storage, Door 3, 2950 Democrat Rd, Memphis, TN 38118"),
            ("deliveryDate", "2026-11-06, deliver by 06:00"), ("detention", "$70.00 per hour after 2 free hours"),
            ("totalAgreedRate", "$1,840.00"));
        Add(N, "rate-confirmation-48240.txt",
            ("brokerName", "Atlantic Bridge Freight Inc."), ("brokerMc", "702388"), ("commodity", "Paper towels and tissue products"),
            ("quantity", "26 pallets"), ("totalWeight", "21,800 lbs"), ("totalAgreedRate", "$2,110.00"), ("temperature", null));
        Add(N, "bill-of-lading-SAV-778431.txt",
            ("bolNumber", "SAV-778431"), ("loadNumber", "48213"), ("shipDate", "2026-10-14"), ("shipperName", "Coastal Electronics Import Co."),
            ("shipperAddress", "400 Port Rd, Savannah, GA 31401"), ("consigneeName", "Peachtree Distribution Center"),
            ("consigneeAddress", "2250 Fulton Industrial Blvd SW, Atlanta, GA 30336"), ("carrierName", "Nile Freight Lines Inc."),
            ("driverName", "Omar Haddad"), ("truckNumber", "NFL-217"), ("trailerNumber", "NFL-T5530"), ("sealNumber", "CE-559201"),
            ("quantity", "18 pallets"), ("commodity", "packaged consumer electronics"), ("totalWeight", "38,400 lbs"),
            ("freightClass", "92.5"), ("temperatureSetting", null), ("hazardousMaterials", "None"),
            ("deliveredAt", "2026-10-15 at 11:40"), ("receivedBy", "Lena Morris, Receiving Supervisor"), ("sealIntact", "Yes"));
        Add(D, "bill-of-lading-DAL-090233.txt",
            ("shipperName", "Prairie Creamery"), ("shipperAddress", "3300 Irving Blvd, Dallas, TX 75247"), ("consigneeName", "Delta Cold Storage"),
            ("driverName", "Tomas Reyes"), ("truckNumber", "DH-051"), ("trailerNumber", "DH-R1203"), ("sealNumber", "PC-774015"),
            ("commodity", "ice cream"), ("temperatureSetting", "-20 degrees F"), ("receivedBy", "Teresa Glenn, Night Receiver"),
            ("deliveryCondition", "17 pallets accepted. 1 pallet rejected by the receiver because of crushed cartons."),
            ("productTemperatureAtDelivery", "-19 degrees F."), ("freightClass", null));
        Add(D, "bill-of-lading-HOU-220957.txt",
            ("deliveryCondition", "22 pallets received in good condition. Product temperature at delivery: -8 degrees F."),
            ("productTemperatureAtDelivery", null),   // the label sits inside a sentence, so it is part of the condition, by design
            ("receivedBy", "James Okafor, Dock Lead"), ("sealNumber", "BF-300178"));
        Add(N, "bill-of-lading-SAV-780377.txt",
            ("commodity", "fresh produce (bananas and pineapples)"), ("totalWeight", "39,000 lbs"), ("trailerNumber", "NFL-R2208"),
            ("temperatureSetting", "34 degrees F"), ("productTemperatureAtDelivery", "36 degrees F."));
        Add(N, "invoice-NFL-2026-1187.txt",
            ("carrierName", "Nile Freight Lines Inc."), ("carrierAddress", "85 Harbor View Rd, Savannah, GA 31405"),
            ("billToName", "Crescent Logistics LLC"), ("billToAddress", "1200 Peachtree St NE, Atlanta, GA 30309"),
            ("invoiceNumber", "NFL-2026-1187"), ("invoiceDate", "2026-10-16"), ("loadNumber", "48213"), ("bolNumber", "SAV-778431"),
            ("lineHaul", "$2,450.00"), ("fuelSurcharge", "$180.00"), ("detention", "$75.00"), ("totalDue", "$2,705.00"),
            ("paymentDueDate", "2026-11-15"), ("paymentTerms", "Net 30"), ("attachments", null), ("billingContact", "billing@nilefreight.example"));
        Add(D, "invoice-DH-26-0412.txt",
            ("carrierName", "Delta Haul Co."), ("billToName", "Gulfline Brokerage Inc."), ("detention", "none"), ("totalDue", "$4,210.00"),
            ("paymentDueDate", "2026-11-15"), ("paymentTerms", "Net 21"),
            ("attachments", "signed POD, reefer temperature download report"), ("billingContact", "accounts@deltahaul.example"));
        Add(D, "invoice-DH-26-0431.txt", ("detention", "$150.00"), ("totalDue", "$4,300.00"), ("paymentDueDate", "2026-11-22"));
        Add(N, "invoice-NFL-2026-1219.txt",
            ("billToName", "Atlantic Bridge Freight Inc."), ("billToAddress", "300 Bay St, Jacksonville, FL 32202"), ("detention", "$135.00"),
            ("totalDue", "$2,245.00"), ("paymentDueDate", "2026-11-29"));
        return d;
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public void Fields_of_the_real_documents_have_the_expected_values(string tenant, string file, string name, string? expected)
    {
        Assert.True(DocType.TryFromFileName(file, out var type));

        var actual = Extract(TestData.ReadText(tenant, file), type!)[name];

        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------ edge cases (small inline texts)

    private static string? Rc(string text, string name) => Field(text, DocType.RateConfirmation, name);

    [Fact]
    public void A_missing_label_is_null() => Assert.Null(Rc("RATE CONFIRMATION\nLoad number: 1", "dateIssued"));

    [Fact]
    public void OCR_noise_in_a_value_is_kept_as_written() => Assert.Equal("482l3", Rc("Load number: 482l3", "loadNumber"));

    [Fact]
    public void Extra_spaces_around_the_label_and_the_colon_are_tolerated() =>
        Assert.Equal("48213", Rc("Load  number :   48213", "loadNumber"));

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void Every_kind_of_line_ending_gives_the_same_result(string eol) =>
        Assert.Equal("$50.00", Rc($"RATE{eol}Line haul: $50.00{eol}Fuel surcharge: $1.00", "lineHaul"));

    [Fact]
    public void The_first_of_two_equal_labels_wins() => Assert.Equal("1", Rc("Load number: 1\nLoad number: 2", "loadNumber"));

    [Theory]
    [InlineData("Received by: ______")]
    [InlineData("Received by:    ")]
    public void A_blank_or_underscore_value_means_not_found(string line) =>
        Assert.Null(Field("DELIVERY RECEIPT\n" + line, DocType.BillOfLading, "receivedBy"));

    [Fact]
    public void A_value_over_300_characters_is_null_not_truncated() =>
        Assert.Null(Rc("Commodity: " + new string('x', 301), "commodity"));

    [Fact]
    public void A_line_over_1000_characters_is_skipped() =>
        Assert.Null(Rc("Commodity: " + new string('x', 1000) + "\nEquipment: van", "commodity"));

    [Fact]
    public void A_label_in_the_middle_of_a_sentence_is_not_a_label() =>
        Assert.Null(Rc("The note says Load number: 1", "loadNumber"));

    [Fact]
    public void Two_labels_on_one_line_split_after_a_comma_or_after_spaces()
    {
        const string bol = "Truck number: A-1, Trailer number: B-2";
        Assert.Equal(("A-1", "B-2"), (Field(bol, DocType.BillOfLading, "truckNumber"), Field(bol, DocType.BillOfLading, "trailerNumber")));
        const string ocr = "BOL number: X-1          Load number: 52010          Ship date: 2026-11-19";
        Assert.Equal(("X-1", "52010", "2026-11-19"), (Field(ocr, DocType.BillOfLading, "bolNumber"),
            Field(ocr, DocType.BillOfLading, "loadNumber"), Field(ocr, DocType.BillOfLading, "shipDate")));
    }

    [Fact]
    public void Inline_party_labels_work_as_in_fax_style_text()
    {
        const string text = "SHIPPER: Southern Steel, 1900 Fulton Blvd, Atlanta, GA 30336\nCONSIGNEE: Music City, 3300 Pike, Nashville, TN 37217\nCARRIER: Blue Ridge Freight Co.   Driver: Terrence Hall   Truck: BRF-421";
        Assert.Equal("Southern Steel", Field(text, DocType.BillOfLading, "shipperName"));
        Assert.Equal("1900 Fulton Blvd, Atlanta, GA 30336", Field(text, DocType.BillOfLading, "shipperAddress"));
        Assert.Equal("Blue Ridge Freight Co.", Field(text, DocType.BillOfLading, "carrierName"));
        Assert.Equal(("Terrence Hall", "BRF-421"), (Field(text, DocType.BillOfLading, "driverName"), Field(text, DocType.BillOfLading, "truckNumber")));
    }

    [Fact]
    public void A_table_row_gives_its_last_amount()
    {
        const string text = "RATE\nLine haul    1    $2,780.00    $2,780.00\nFuel surcharge (per mile)    412    $0.62    $255.44";
        Assert.Equal("$2,780.00", Rc(text, "lineHaul"));
        Assert.Equal("$255.44", Rc(text, "fuelSurcharge"));
    }

    [Fact]
    public void A_handwritten_date_outside_the_delivery_section_is_ignored() =>
        Assert.Null(Rc("PICKUP\nLocation: A, 1 St\nDate: 11/18/26\nDELIVERY\nLocation: B", "deliveryDate"));

    [Fact]
    public void Hostile_input_finishes_quickly_without_an_exception()
    {
        var started = DateTime.UtcNow;
        foreach (var text in new[]
                 {
                     "Detention " + new string('(', 50_000), string.Concat(Enumerable.Repeat("a: ", 100_000)),
                     string.Concat(Enumerable.Repeat("Load number: x\n", 200_000)), new string('\t', 300_000) + "Load number: 1",
                 })
        {
            Extract(text, DocType.Invoice);
            Extract(text, DocType.BillOfLading);
        }
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void ToString_never_shows_a_value()
    {
        var fields = Extract("Load number: SECRET-4321\nCarrier: Hidden Carrier Inc.", DocType.RateConfirmation);

        Assert.DoesNotContain("SECRET", fields.ToString());
        Assert.DoesNotContain("Hidden", fields.ToString());
    }
}
