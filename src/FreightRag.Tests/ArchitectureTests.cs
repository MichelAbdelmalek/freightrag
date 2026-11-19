using System.Reflection;
using FreightRag.Core.Domain;

namespace FreightRag.Tests;

/// <summary>
/// Enforces the DDD dependency rule inside FreightRag.Core:
///   Domain         → may not use Application, Infrastructure or any third-party library
///   Application    → may use Domain only (no Infrastructure, no third-party library)
///   Infrastructure → may use anything
/// It inspects type signatures (base types, interfaces, fields, properties, method and
/// constructor parameters/return types, generic arguments). Method bodies are not inspected,
/// which is acceptable for a learning project; a library like NetArchTest would cover those too.
/// </summary>
public class ArchitectureTests
{
    private const string DomainNs = "FreightRag.Core.Domain";
    private const string ApplicationNs = "FreightRag.Core.Application";
    private const string InfrastructureNs = "FreightRag.Core.Infrastructure";

    private static readonly string[] ThirdPartyAssemblies =
        ["Npgsql", "Pgvector", "OllamaSharp", "Microsoft.Extensions.AI", "Microsoft.Extensions.AI.Abstractions",
         "Npgsql.EntityFrameworkCore.PostgreSQL", "Pgvector.EntityFrameworkCore",
         "Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"];

    private static readonly Assembly Core = typeof(FreightDocument).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_outside_the_domain()
    {
        var violations = Violations(DomainNs, forbiddenNamespaces: [ApplicationNs, InfrastructureNs]);
        Assert.Empty(violations);
    }

    [Fact]
    public void Application_depends_only_on_the_domain()
    {
        var violations = Violations(ApplicationNs, forbiddenNamespaces: [InfrastructureNs]);
        Assert.Empty(violations);
    }

    [Fact]
    public void The_scanner_does_detect_third_party_dependencies()
    {
        // Guards against a scanner bug that would make the two tests above pass vacuously.
        Assert.NotEmpty(Violations(InfrastructureNs, forbiddenNamespaces: []));
    }

    private static List<string> Violations(string layer, string[] forbiddenNamespaces)
    {
        var layerTypes = Core.GetTypes().Where(t => t.Namespace?.StartsWith(layer, StringComparison.Ordinal) == true);

        return layerTypes
            .SelectMany(t => ReferencedTypes(t).Select(r => (Owner: t, Referenced: r)))
            .Where(x => IsThirdParty(x.Referenced)
                        || forbiddenNamespaces.Any(ns => x.Referenced.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
            .Select(x => $"{x.Owner.FullName} → {x.Referenced.FullName}")
            .Distinct()
            .ToList();
    }

    private static bool IsThirdParty(Type type) => ThirdPartyAssemblies.Contains(type.Assembly.GetName().Name);

    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var direct = new List<Type?> { type.BaseType };
        direct.AddRange(type.GetInterfaces());
        direct.AddRange(type.GetFields(All).Select(f => f.FieldType));
        direct.AddRange(type.GetProperties(All).Select(p => p.PropertyType));
        foreach (var method in type.GetMethods(All))
        {
            direct.Add(method.ReturnType);
            direct.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }
        foreach (var ctor in type.GetConstructors(All))
            direct.AddRange(ctor.GetParameters().Select(p => p.ParameterType));

        return direct.OfType<Type>().SelectMany(Expand);
    }

    /// <summary>Unwraps arrays, by-ref, pointers and generic arguments (Task&lt;List&lt;X&gt;&gt; → X).</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
            return Expand(type.GetElementType()!);
        if (type.IsGenericType)
            return new[] { type.GetGenericTypeDefinition() }.Concat(type.GetGenericArguments().SelectMany(Expand));
        return [type];
    }
}
