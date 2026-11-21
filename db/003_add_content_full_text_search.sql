-- GENERATED from the EF Core migration "AddContentFullTextSearch" (src/FreightRag.Core/Infrastructure/Persistence/Migrations).
-- Do not edit: the C# migration is the source of truth. This idempotent script exists so the change can be
-- reviewed (or applied by hand by a DBA). Normal path: `docker compose run --rm migrate`.
-- Works on an EF-managed database and on one created by db/001_schema.sql (adopted below).
-- Backfill note: set the session setting freightrag.embedding_model to the configured model first, e.g.
--   SELECT set_config('freightrag.embedding_model', 'nomic-embed-text', false);
-- Adopt a database created before EF Core (db/001_schema.sql): record InitialSchema as already applied.
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT '20261119173506_InitialSchema', '10.0.14'
WHERE to_regclass('public.documents') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119173506_InitialSchema');

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120160657_AddContentFullTextSearch') THEN
    ALTER TABLE chunks ADD content_tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120160657_AddContentFullTextSearch') THEN
    CREATE INDEX chunks_content_tsv ON chunks USING GIN (content_tsv);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120160657_AddContentFullTextSearch') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261120160657_AddContentFullTextSearch', '10.0.14');
    END IF;
END $EF$;
COMMIT;

