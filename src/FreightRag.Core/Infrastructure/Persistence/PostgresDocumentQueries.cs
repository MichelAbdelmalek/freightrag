using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Queries;
using FreightRag.Core.Domain;
using Npgsql;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// Read-side queries in plain parameterized SQL. Every statement filters on <c>tenant_id</c>, so a
/// document of another tenant is simply "not found".
/// </summary>
public sealed class PostgresDocumentQueries(NpgsqlDataSource dataSource) : IDocumentQueries
{
    public async Task<PagedResult<DocumentSummary>> ListAsync(TenantId tenantId, PageRequest page, CancellationToken cancellationToken)
    {
        // count(*) OVER () returns the total next to each row, saving a second round trip.
        const string sql = """
            SELECT d.id, d.title, d.doc_type, d.created_at,
                   (SELECT count(*) FROM chunks c WHERE c.document_id = d.id AND c.tenant_id = d.tenant_id) AS chunk_count,
                   count(*) OVER () AS total_count
            FROM documents d
            WHERE d.tenant_id = $1
            ORDER BY d.created_at DESC, d.id DESC
            LIMIT $2 OFFSET $3
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenantId.Value });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = page.PageSize });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = page.Offset });

        var items = new List<DocumentSummary>();
        long total = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DocumentSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), (int)reader.GetInt64(4)));
            total = reader.GetInt64(5);
        }

        // A page past the end returns no rows (and therefore no total): count separately in that case.
        if (items.Count == 0 && page.Offset > 0)
            total = await CountAsync(tenantId, cancellationToken);

        return new PagedResult<DocumentSummary>(items, page, (int)total);
    }

    public async Task<DocumentDetails?> GetAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        DocumentDetails? document;
        await using (var command = new NpgsqlCommand(
            "SELECT id, title, doc_type, created_at FROM documents WHERE id = $1 AND tenant_id = $2", connection))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = documentId });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenantId.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            document = new DocumentDetails(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), []);
        }

        var chunks = new List<ChunkText>();
        await using (var command = new NpgsqlCommand(
            "SELECT chunk_index, content FROM chunks WHERE document_id = $1 AND tenant_id = $2 ORDER BY chunk_index", connection))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = documentId });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenantId.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                chunks.Add(new ChunkText(reader.GetInt32(0), reader.GetString(1)));
        }

        return document with { Chunks = chunks };
    }

    private async Task<long> CountAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM documents WHERE tenant_id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenantId.Value });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
