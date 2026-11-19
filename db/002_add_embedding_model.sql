-- GENERATED from the EF Core migration "AddEmbeddingModel" (src/FreightRag.Core/Infrastructure/Persistence/Migrations).
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
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119173527_AddEmbeddingModel') THEN
    ALTER TABLE chunks ADD embedding_model text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119173527_AddEmbeddingModel') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119173527_AddEmbeddingModel') THEN
    ALTER TABLE chunks ALTER COLUMN embedding_model SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119173527_AddEmbeddingModel') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261119173527_AddEmbeddingModel', '10.0.14');
    END IF;
END $EF$;
COMMIT;

