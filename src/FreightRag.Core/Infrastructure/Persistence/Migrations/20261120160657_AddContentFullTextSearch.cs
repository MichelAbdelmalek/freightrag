using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace FreightRag.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "content_tsv",
                table: "chunks",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "content" });

            migrationBuilder.CreateIndex(
                name: "chunks_content_tsv",
                table: "chunks",
                column: "content_tsv")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "chunks_content_tsv",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "content_tsv",
                table: "chunks");
        }
    }
}
