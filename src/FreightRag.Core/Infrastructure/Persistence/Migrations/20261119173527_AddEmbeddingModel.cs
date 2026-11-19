using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreightRag.Core.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>chunks.embedding_model</c>: vectors are only comparable when produced by the same model, so
    /// every search filters on it. Hand-edited from the generated version so existing rows are backfilled
    /// with the configured model name instead of an empty default.
    /// </summary>
    public partial class AddEmbeddingModel : Migration
    {
        /// <summary>Session setting the migrator fills with the configured embedding model name.</summary>
        public const string EmbeddingModelSetting = "freightrag.embedding_model";

        private const string BackfillSql = """
            DO $backfill$
            DECLARE
                configured_model text := nullif(current_setting('freightrag.embedding_model', true), '');
            BEGIN
                IF EXISTS (SELECT 1 FROM chunks WHERE embedding_model IS NULL) THEN
                    IF configured_model IS NULL THEN
                        RAISE EXCEPTION 'chunks.embedding_model backfill needs the configured model name: run migrations with FreightRag.Migrator (it sets freightrag.embedding_model).';
                    END IF;
                    UPDATE chunks SET embedding_model = configured_model WHERE embedding_model IS NULL;
                END IF;
            END
            $backfill$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the column as nullable so existing rows are valid for a moment.
            migrationBuilder.AddColumn<string>(
                name: "embedding_model",
                table: "chunks",
                type: "text",
                nullable: true);

            // 2. Backfill with the configured model name. The migrator puts it in the session setting
            //    freightrag.embedding_model (via the parameterized set_config). If rows need a value and the
            //    setting is missing, fail loudly rather than guess or leave NULLs.
            migrationBuilder.Sql(BackfillSql);

            // 3. Now every row has a value: make it required.
            migrationBuilder.AlterColumn<string>(
                name: "embedding_model",
                table: "chunks",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding_model",
                table: "chunks");
        }
    }
}
