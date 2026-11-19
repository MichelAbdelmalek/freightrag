namespace FreightRag.Core.Domain;

/// <summary>Thrown when a domain rule (invariant) is violated.</summary>
public sealed class DomainException(string message) : Exception(message);
