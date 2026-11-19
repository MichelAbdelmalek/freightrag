namespace FreightRag.Tests.Support;

/// <summary>
/// Reads the real synthetic documents that the test project copies into its output folder
/// (see the &lt;None Include="..\..\data\**\*.txt"&gt; item in FreightRag.Tests.csproj).
/// </summary>
internal static class TestData
{
    public const string NileFreight = "tenant-nile-freight";
    public const string DeltaHaul = "tenant-delta-haul";

    public static string PathOf(string tenant, string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "data", tenant, fileName);

    public static string ReadText(string tenant, string fileName) => File.ReadAllText(PathOf(tenant, fileName));

    public static byte[] ReadBytes(string tenant, string fileName) => File.ReadAllBytes(PathOf(tenant, fileName));
}
