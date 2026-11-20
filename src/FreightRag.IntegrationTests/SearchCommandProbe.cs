using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace FreightRag.IntegrationTests;

/// <summary>
/// EF Core interceptor that inspects the nearest-neighbour command EF actually sends — not a re-typed copy.
/// Just before EF executes it, on the SAME connection and transaction, the probe:
///   1. reads the hnsw settings (proves SET LOCAL landed where the query runs), and
///   2. runs EXPLAIN on the exact command text with the exact parameters (proves the plan).
/// </summary>
internal sealed class SearchCommandProbe : DbCommandInterceptor
{
    public List<Observation> Observations { get; } = [];

    public sealed record Observation(
        bool InTransaction, string IterativeScan, string MaxScanTuples, string EnableIndexScan,
        string CommandText, List<string> Plan);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("<=>", StringComparison.Ordinal)) // the search query, not the COUNT or SETs
        {
            var connection = (NpgsqlConnection)command.Connection!;
            var transaction = (NpgsqlTransaction?)command.Transaction;

            string iterative, maxTuples, indexScan;
            await using (var settings = new NpgsqlCommand(
                "SELECT current_setting('hnsw.iterative_scan'), current_setting('hnsw.max_scan_tuples'), current_setting('enable_indexscan')",
                connection, transaction))
            await using (var reader = await settings.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                (iterative, maxTuples, indexScan) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }

            var plan = new List<string>();
            await using (var explain = new NpgsqlCommand("EXPLAIN " + command.CommandText, connection, transaction))
            {
                foreach (NpgsqlParameter p in command.Parameters)
                    explain.Parameters.Add(p.Clone());
                await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    plan.Add(reader.GetString(0));
            }

            Observations.Add(new Observation(transaction is not null, iterative, maxTuples, indexScan, command.CommandText, plan));
        }
        return result;
    }
}
