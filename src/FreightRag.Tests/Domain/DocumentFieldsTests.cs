using FreightRag.Core.Domain;

namespace FreightRag.Tests.Domain;

public class DocumentFieldsTests
{
    private static IReadOnlyList<string> Names(DocType type) => DocumentFields.For(type).Select(f => f.Name).ToList();

    [Fact]
    public void Rate_confirmations_have_these_23_fields_in_this_order() => Assert.Equal(
        ["brokerName", "brokerAddress", "brokerMc", "carrierName", "carrierMc", "carrierDot", "loadNumber", "dateIssued",
         "pickupLocation", "shipperName", "pickupWindow", "deliveryLocation", "deliveryDate", "commodity", "quantity",
         "totalWeight", "equipment", "temperature", "lineHaul", "fuelSurcharge", "totalAgreedRate", "detention", "paymentTerms"],
        Names(DocType.RateConfirmation));

    [Fact]
    public void Bills_of_lading_have_these_23_fields_in_this_order() => Assert.Equal(
        ["bolNumber", "loadNumber", "shipDate", "shipperName", "shipperAddress", "consigneeName", "consigneeAddress",
         "carrierName", "driverName", "truckNumber", "trailerNumber", "sealNumber", "quantity", "commodity", "totalWeight",
         "freightClass", "temperatureSetting", "hazardousMaterials", "deliveredAt", "receivedBy", "deliveryCondition",
         "productTemperatureAtDelivery", "sealIntact"],
        Names(DocType.BillOfLading));

    [Fact]
    public void Invoices_have_these_16_fields_in_this_order() => Assert.Equal(
        ["carrierName", "carrierAddress", "billToName", "billToAddress", "invoiceNumber", "invoiceDate", "loadNumber",
         "bolNumber", "lineHaul", "fuelSurcharge", "detention", "totalDue", "paymentDueDate", "paymentTerms", "attachments",
         "billingContact"],
        Names(DocType.Invoice));

    [Fact]
    public void The_driver_handbook_has_no_fields() => Assert.Empty(DocumentFields.For(DocType.DriverHandbook));

    [Fact]
    public void Names_are_camel_case_words_and_unique_per_type()
    {
        foreach (var type in DocType.All)
        {
            var names = Names(type);
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(names, n => Assert.Matches("^[a-z][A-Za-z]{1,39}$", n));
        }
    }

    [Fact]
    public void Common_fields_exist_in_every_type_that_has_them()
    {
        foreach (var name in new[] { "loadNumber", "carrierName" })
            Assert.All([DocType.RateConfirmation, DocType.BillOfLading, DocType.Invoice], t => Assert.Contains(name, Names(t)));
        foreach (var name in new[] { "lineHaul", "fuelSurcharge", "detention", "paymentTerms" })
            Assert.All([DocType.RateConfirmation, DocType.Invoice], t => Assert.Contains(name, Names(t)));
    }
}

public class FieldSelectionTests
{
    private static FieldSelection Parse(string? raw, DocType? type = null)
    {
        Assert.True(FieldSelection.TryParse(raw, type ?? DocType.RateConfirmation, out var selection, out var error), error?.Code);
        return selection!;
    }

    private static FieldSelectionError Fail(string? raw, DocType? type = null)
    {
        Assert.False(FieldSelection.TryParse(raw, type ?? DocType.RateConfirmation, out _, out var error));
        return error!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(",,")]
    public void Nothing_requested_means_every_field(string? raw) =>
        Assert.Equal(DocumentFields.For(DocType.RateConfirmation).Select(f => f.Name), Parse(raw).Names);

    [Fact]
    public void Names_match_case_insensitively_come_back_in_catalog_order_and_duplicates_are_ignored() =>
        Assert.Equal(["carrierName", "loadNumber"], Parse("LOADNUMBER, carrierName,loadNumber").Names);

    [Fact]
    public void An_unknown_name_is_reported_with_the_valid_names_and_does_not_echo_the_input()
    {
        var error = Fail("loadNumber,nope");
        Assert.Equal("unknown_field", error.Code);
        Assert.Equal(DocumentFields.For(DocType.RateConfirmation).Select(f => f.Name), error.ValidNames);
        Assert.DoesNotContain("nope", error.Message);
    }

    [Fact]
    public void Any_name_for_the_handbook_is_unknown_with_an_empty_list()
    {
        var error = Fail("loadNumber", DocType.DriverHandbook);
        Assert.Equal(("unknown_field", 0), (error.Code, error.ValidNames.Count));
    }

    [Theory]
    [InlineData("load-number")]
    [InlineData("load number")]
    [InlineData("1abc")]
    public void Malformed_names_are_invalid(string raw) => Assert.Equal("invalid_fields", Fail(raw).Code);

    [Fact]
    public void Too_many_names_or_too_long_a_value_is_invalid()
    {
        Assert.Equal("invalid_fields", Fail(string.Join(",", Enumerable.Range(0, 51).Select(i => "a" + new string('b', i % 5 + 1)))).Code);
        Assert.Equal("invalid_fields", Fail(new string('a', 1001)).Code);
    }
}
