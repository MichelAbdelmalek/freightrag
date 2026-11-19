using FreightRag.Core.Application.Ports;
using FreightRag.Core.Domain;
using Npgsql;
using Pgvector;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// Postgres + pgvector implementation of <see cref="IDocumentRepository"/>.
///
/// Security: every value goes through a query parameter ($1, $2, ...). SQL text is constant and
/// never built from data, so file names or document text cannot inject SQL.
/// Tables are created by db/001_schema.sql, never by this code.
/// </summary>
public sealed class PostgresDocumentRepository(NpgsqlDataSource dataSource) : IDocumentRepository
{
    /// <summary>
    /// Builds the connection pool. UseVector() teaches Npgsql how to send/receive the pgvector
    /// "vector" type as <see cref="Vector"/>.
    /// </summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    public async Task<bool> ExistsAsync(TenantId tenantId, SourceHash sourceHash, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM documents WHERE tenant_id = $1 AND source_hash = $2)";

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenantId.Value });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sourceHash.Value });

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> AddAsync(FreightDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsReadyToStore)
            throw new InvalidOperationException($"Document '{document.Title}' cannot be stored before all chunks are embedded.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // ON CONFLICT ... DO NOTHING: if another run inserted the same (tenant, hash) after our
        // ExistsAsync check, nothing is inserted and RETURNING yields no row.
        const string insertDocument = """
            INSERT INTO documents (id, tenant_id, title, doc_type, source_hash)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (tenant_id, source_hash) DO NOTHING
            RETURNING id
            """;
        await using (var command = new NpgsqlCommand(insertDocument, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = document.Id });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = document.TenantId.Value });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = document.Title });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = document.DocType.Name });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = document.SourceHash.Value });

            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // All chunk INSERTs travel in one round trip (NpgsqlBatch), still one parameterised statement each.
        const string insertChunk = """
            INSERT INTO chunks (document_id, tenant_id, chunk_index, content, embedding)
            VALUES ($1, $2, $3, $4, $5)
            """;
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            foreach (var chunk in document.Chunks)
            {
                var command = new NpgsqlBatchCommand(insertChunk);
                command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = document.Id });
                command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = document.TenantId.Value });
                command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = chunk.Index });
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = chunk.Content });
                command.Parameters.Add(new NpgsqlParameter
                {
                    Value = new Vector(chunk.Embedding!.Values),
                    DataTypeName = "vector", // explicit, so Npgsql never has to guess the type
                });
                batch.BatchCommands.Add(command);
            }
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        // If anything above threw, the transaction is disposed without commit = rolled back.
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
