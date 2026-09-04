using Dapper;
using Npgsql;

namespace ModOrganizer.Core.Storage;

internal static class Migrations
{
    public static readonly (int Version, string Sql)[] All =
    {
        // v1: consolidated initial schema (covers SQLite v1-v3 equivalents)
        (1, """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version     INTEGER PRIMARY KEY,
            applied_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        CREATE TABLE IF NOT EXISTS roots (
            id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            path          TEXT NOT NULL UNIQUE,
            display_name  TEXT NOT NULL,
            enabled       BOOLEAN NOT NULL DEFAULT TRUE,
            added_at      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS categories (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            root_id      BIGINT NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            sort_order   INTEGER NOT NULL DEFAULT 0,
            icon_name    TEXT,
            color_hex    TEXT,
            description  TEXT,
            is_missing   BOOLEAN NOT NULL DEFAULT FALSE,
            UNIQUE (root_id, name)
        );
        CREATE INDEX IF NOT EXISTS idx_categories_root ON categories(root_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_categories_root_name_lower
            ON categories(root_id, LOWER(name));

        CREATE TABLE IF NOT EXISTS mods (
            id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            category_id     BIGINT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
            folder_name     TEXT NOT NULL,
            display_name    TEXT,
            comment_md      TEXT,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            deleted_at      TEXT,
            is_missing      BOOLEAN NOT NULL DEFAULT FALSE,
            rating          INTEGER NOT NULL DEFAULT 0,
            last_viewed_at  TEXT,
            folder_ctime    TEXT,
            folder_mtime    TEXT,
            created_by      UUID,
            updated_by      UUID,
            UNIQUE (category_id, folder_name)
        );
        CREATE INDEX IF NOT EXISTS idx_mods_category ON mods(category_id);
        CREATE INDEX IF NOT EXISTS idx_mods_deleted  ON mods(deleted_at);
        CREATE INDEX IF NOT EXISTS idx_mods_comment_trgm
            ON mods USING gin (comment_md gin_trgm_ops);

        CREATE TABLE IF NOT EXISTS mod_files (
            id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            mod_id        BIGINT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            kind          INTEGER NOT NULL,
            size_bytes    BIGINT NOT NULL,
            xxhash64      BIGINT,
            mtime         TEXT NOT NULL,
            UNIQUE (mod_id, relative_path)
        );
        CREATE INDEX IF NOT EXISTS idx_mod_files_mod  ON mod_files(mod_id);
        CREATE INDEX IF NOT EXISTS idx_mod_files_kind ON mod_files(kind);
        CREATE INDEX IF NOT EXISTS idx_mod_files_hash ON mod_files(xxhash64);

        CREATE TABLE IF NOT EXISTS thumbnails (
            mod_file_id   BIGINT PRIMARY KEY REFERENCES mod_files(id) ON DELETE CASCADE,
            cache_path    TEXT NOT NULL,
            width         INTEGER NOT NULL,
            height        INTEGER NOT NULL,
            generated_at  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tags (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name         TEXT NOT NULL,
            color_hex    TEXT,
            description  TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_tags_name_lower ON tags(LOWER(name));

        CREATE TABLE IF NOT EXISTS mod_tags (
            mod_id     BIGINT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
            tag_id     BIGINT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            added_at   TEXT NOT NULL,
            added_by   UUID,
            PRIMARY KEY (mod_id, tag_id)
        );
        CREATE INDEX IF NOT EXISTS idx_mod_tags_tag ON mod_tags(tag_id);

        CREATE TABLE IF NOT EXISTS mod_links (
            id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            mod_id    BIGINT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
            url       TEXT NOT NULL,
            title     TEXT,
            domain    TEXT,
            kind      INTEGER NOT NULL DEFAULT 0,
            added_at  TEXT NOT NULL,
            added_by  UUID
        );
        CREATE INDEX IF NOT EXISTS idx_mod_links_mod ON mod_links(mod_id);

        CREATE TABLE IF NOT EXISTS health_issues (
            id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            mod_id      BIGINT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
            kind        INTEGER NOT NULL,
            severity    INTEGER NOT NULL,
            detail      TEXT,
            resolved_at TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_health_mod ON health_issues(mod_id);

        CREATE TABLE IF NOT EXISTS action_log (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            ts           TEXT NOT NULL,
            action       INTEGER NOT NULL,
            mod_id       BIGINT,
            user_id      UUID,
            from_path    TEXT,
            to_path      TEXT,
            tx_id        TEXT NOT NULL,
            payload_json TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_action_log_tx ON action_log(tx_id);
        CREATE INDEX IF NOT EXISTS idx_action_log_ts ON action_log(ts);

        CREATE TABLE IF NOT EXISTS pmp_meta (
            mod_file_id  BIGINT PRIMARY KEY REFERENCES mod_files(id) ON DELETE CASCADE,
            name         TEXT,
            author       TEXT,
            version      TEXT,
            description  TEXT,
            website      TEXT,
            raw_json     TEXT,
            parsed_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS pmp_groups (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            mod_file_id  BIGINT NOT NULL REFERENCES mod_files(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            type         TEXT,
            option_json  TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_pmp_groups_file ON pmp_groups(mod_file_id);

        CREATE TABLE IF NOT EXISTS pmp_game_paths (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            mod_file_id  BIGINT NOT NULL REFERENCES mod_files(id) ON DELETE CASCADE,
            game_path    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_pmp_paths_file ON pmp_game_paths(mod_file_id);
        CREATE INDEX IF NOT EXISTS idx_pmp_paths_path ON pmp_game_paths(game_path);

        CREATE TABLE IF NOT EXISTS pmp_previews (
            mod_file_id  BIGINT PRIMARY KEY REFERENCES mod_files(id) ON DELETE CASCADE,
            cache_path   TEXT NOT NULL,
            extracted_at TEXT NOT NULL
        );
        """),

        // v2: multi-user tables (users mirror, mod_comments thread, smart_collections)
        (2, """
        CREATE TABLE IF NOT EXISTS users (
            id            UUID PRIMARY KEY,
            email         TEXT,
            display_name  TEXT,
            color_hex     TEXT,
            created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS mod_comments (
            id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            mod_id     BIGINT NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
            user_id    UUID REFERENCES users(id) ON DELETE SET NULL,
            body_md    TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS idx_mod_comments_mod ON mod_comments(mod_id);

        CREATE TABLE IF NOT EXISTS smart_collections (
            id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            owner_id     UUID REFERENCES users(id) ON DELETE SET NULL,
            name         TEXT NOT NULL,
            filter_json  TEXT NOT NULL,
            created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """),

        // v3: Drop CITEXT from existing DBs — Npgsql 8 doesn't auto-map this extension type.
        //     Idempotent: only alters if the column is still CITEXT.
        (3, """
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'categories' AND column_name = 'name'
                  AND udt_name = 'citext'
            ) THEN
                ALTER TABLE categories ALTER COLUMN name TYPE TEXT;
            END IF;

            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'tags' AND column_name = 'name'
                  AND udt_name = 'citext'
            ) THEN
                ALTER TABLE tags DROP CONSTRAINT IF EXISTS tags_name_key;
                ALTER TABLE tags ALTER COLUMN name TYPE TEXT;
            END IF;
        END $$;

        CREATE UNIQUE INDEX IF NOT EXISTS idx_tags_name_lower
            ON tags(LOWER(name));
        CREATE UNIQUE INDEX IF NOT EXISTS idx_categories_root_name_lower
            ON categories(root_id, LOWER(name));
        """),

        // v4: enable Supabase realtime for tables we want to push-sync
        (4, """
        DO $$
        DECLARE
            t TEXT;
            tables TEXT[] := ARRAY['mods','categories','tags','mod_tags','mod_links','mod_comments','action_log'];
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'supabase_realtime') THEN
                FOREACH t IN ARRAY tables LOOP
                    BEGIN
                        EXECUTE format('ALTER PUBLICATION supabase_realtime ADD TABLE public.%I', t);
                    EXCEPTION
                        WHEN duplicate_object THEN NULL;
                        WHEN insufficient_privilege THEN NULL;
                    END;
                END LOOP;
            END IF;
        END $$;
        """),

        // v5: auto-mirror auth.users → public.users on signup so activity feed
        //     shows display names. Idempotent.
        (5, """
        CREATE OR REPLACE FUNCTION public.handle_new_user()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = public
        AS $$
        DECLARE
            color_palette text[] := ARRAY['#7A5CFA','#FF8A65','#26A69A','#FFB300','#5C6BC0',
                                          '#EC407A','#26C6DA','#9CCC65','#AB47BC','#42A5F5'];
            picked text;
        BEGIN
            picked := color_palette[1 + (abs(hashtext(NEW.email::text)) % array_length(color_palette, 1))];
            INSERT INTO public.users (id, email, display_name, color_hex)
            VALUES (
                NEW.id,
                NEW.email,
                COALESCE(NEW.raw_user_meta_data->>'display_name', NEW.email),
                picked
            )
            ON CONFLICT (id) DO NOTHING;
            RETURN NEW;
        END;
        $$;

        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'auth') THEN
                BEGIN
                    DROP TRIGGER IF EXISTS on_auth_user_created ON auth.users;
                    CREATE TRIGGER on_auth_user_created
                        AFTER INSERT ON auth.users
                        FOR EACH ROW EXECUTE PROCEDURE public.handle_new_user();
                EXCEPTION
                    WHEN insufficient_privilege THEN NULL;
                END;

                -- Backfill any existing auth users
                INSERT INTO public.users (id, email, display_name, color_hex)
                SELECT u.id, u.email,
                       COALESCE(u.raw_user_meta_data->>'display_name', u.email),
                       (ARRAY['#7A5CFA','#FF8A65','#26A69A','#FFB300','#5C6BC0',
                              '#EC407A','#26C6DA','#9CCC65','#AB47BC','#42A5F5'])[
                           1 + (abs(hashtext(u.email::text)) % 10)]
                FROM auth.users u
                ON CONFLICT (id) DO NOTHING;
            END IF;
        END $$;
        """),

        // v6: per-user Penumbra snapshot — collections + per-mod active/imported state
        (6, """
        CREATE TABLE IF NOT EXISTS penumbra_user_state (
            user_id      UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            payload_json TEXT NOT NULL,
            updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'supabase_realtime') THEN
                BEGIN
                    ALTER PUBLICATION supabase_realtime ADD TABLE public.penumbra_user_state;
                EXCEPTION
                    WHEN duplicate_object THEN NULL;
                    WHEN insufficient_privilege THEN NULL;
                END;
            END IF;
        END $$;
        """),

        // v7: indexes for the gallery query and the mod search.
        //
        // The gallery aggregates mod_files by (mod_id, kind) and picks a primary image by
        // (mod_id, kind, relative_path). Separate single-column indexes on mod_id and kind
        // meant Postgres had to filter after the index scan on every one of them.
        //
        // Search moved from LIKE (case-sensitive — "akari" never matched "Akari Catsuit")
        // to ILIKE '%…%', which only uses an index if it is a trigram index.
        (7, """
        CREATE INDEX IF NOT EXISTS idx_mod_files_mod_kind
            ON mod_files(mod_id, kind);
        CREATE INDEX IF NOT EXISTS idx_mod_files_mod_kind_path
            ON mod_files(mod_id, kind, relative_path);

        CREATE INDEX IF NOT EXISTS idx_mods_folder_name_trgm
            ON mods USING gin (folder_name gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS idx_mods_display_name_trgm
            ON mods USING gin (display_name gin_trgm_ops);

        -- The gallery always filters live mods; a partial index keeps it small.
        CREATE INDEX IF NOT EXISTS idx_mods_live_category
            ON mods(category_id) WHERE deleted_at IS NULL;
        """),
    };

    public static int CurrentVersion(NpgsqlConnection conn)
    {
        try
        {
            return conn.ExecuteScalar<int?>(
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations") ?? 0;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // undefined_table
        {
            return 0;
        }
    }

    public static void Apply(NpgsqlConnection conn)
    {
        var current = CurrentVersion(conn);
        foreach (var (version, sql) in All)
        {
            if (version <= current) continue;

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            using (var bump = conn.CreateCommand())
            {
                bump.Transaction = tx;
                bump.CommandText = "INSERT INTO schema_migrations(version) VALUES (@v) ON CONFLICT DO NOTHING";
                bump.Parameters.AddWithValue("v", version);
                bump.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
