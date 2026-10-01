-- 0001: the initial schema (Spec 004 §4). A released script never changes: a fix is a new script.
-- STRICT tables make SQLite enforce column types; the CHECKs are the database guard G3 (ADR 0010 §7).

CREATE TABLE care_entries (
    id                    TEXT    NOT NULL PRIMARY KEY,
    household_id          TEXT    NOT NULL,
    kind                  TEXT    NOT NULL,
    subject_baby_id       TEXT    NULL,
    subject_person        TEXT    NULL,
    is_session            INTEGER NOT NULL CHECK (is_session IN (0, 1)),
    start_utc_ms          INTEGER NOT NULL,
    start_offset_min      INTEGER NOT NULL,
    end_utc_ms            INTEGER NULL,
    end_offset_min        INTEGER NULL,
    logged_by             TEXT    NOT NULL,
    source                TEXT    NOT NULL,
    source_prompt_version TEXT    NULL,
    note                  TEXT    NULL,
    details               TEXT    NOT NULL,
    version_wall_ms       INTEGER NOT NULL,
    version_counter       INTEGER NOT NULL,
    version_device        TEXT    NOT NULL,
    is_deleted            INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
    integrity             TEXT    NOT NULL DEFAULT 'ok' CHECK (integrity IN ('ok', 'read-only', 'quarantined')),
    integrity_reason      TEXT    NULL,
    -- IS, not =: a CHECK that evaluates to NULL passes, and json_type() is NULL when "v" is missing.
    CHECK (json_valid(details) AND json_type(details) IS 'object'
           AND json_type(details, '$.v') IS 'integer' AND json_extract(details, '$.v') >= 1),
    CHECK ((subject_baby_id IS NULL) <> (subject_person IS NULL)),
    CHECK (end_utc_ms IS NULL OR end_utc_ms > start_utc_ms),
    CHECK ((end_utc_ms IS NULL) = (end_offset_min IS NULL)),
    CHECK (is_session = 1 OR end_utc_ms IS NULL)
) STRICT;

CREATE INDEX ix_care_entries_timeline ON care_entries (household_id, start_utc_ms);
CREATE INDEX ix_care_entries_baby_kind ON care_entries (household_id, subject_baby_id, kind, start_utc_ms);

CREATE TABLE baby_profiles (
    id              TEXT    NOT NULL PRIMARY KEY,
    household_id    TEXT    NOT NULL,
    name            TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 40),
    nicknames       TEXT    NOT NULL CHECK (json_valid(nicknames) AND json_type(nicknames) IS 'array'),
    birth_date      TEXT    NOT NULL CHECK (date(birth_date) IS birth_date),
    due_date        TEXT    NULL CHECK (due_date IS NULL OR date(due_date) IS due_date),
    version_wall_ms INTEGER NOT NULL,
    version_counter INTEGER NOT NULL,
    version_device  TEXT    NOT NULL,
    is_deleted      INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1))
) STRICT;

CREATE INDEX ix_baby_profiles_household ON baby_profiles (household_id);

CREATE TABLE household_settings (
    household_id    TEXT    NOT NULL PRIMARY KEY,
    volume_unit     TEXT    NOT NULL CHECK (volume_unit IN ('ml', 'oz')),
    time_zone_id    TEXT    NOT NULL CHECK (length(time_zone_id) > 0),
    hidden_kinds    TEXT    NOT NULL CHECK (json_valid(hidden_kinds) AND json_type(hidden_kinds) IS 'array'),
    version_wall_ms INTEGER NOT NULL,
    version_counter INTEGER NOT NULL,
    version_device  TEXT    NOT NULL,
    is_deleted      INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1))
) STRICT;

CREATE TABLE outbox (
    seq             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    change_id       TEXT    NOT NULL UNIQUE,
    entity_type     TEXT    NOT NULL CHECK (entity_type IN ('CareEntry', 'BabyProfile', 'HouseholdSettings')),
    entity_id       TEXT    NOT NULL,
    operation       TEXT    NOT NULL CHECK (operation IN ('upsert', 'delete')),
    version_wall_ms INTEGER NOT NULL,
    version_counter INTEGER NOT NULL,
    version_device  TEXT    NOT NULL
) STRICT;

CREATE TABLE device_state (
    id              INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    device_id       TEXT    NOT NULL,
    hlc_wall_ms     INTEGER NULL,
    hlc_counter     INTEGER NULL,
    CHECK ((hlc_wall_ms IS NULL) = (hlc_counter IS NULL))
) STRICT;

CREATE TABLE data_migrations (
    id               TEXT    NOT NULL PRIMARY KEY,
    completed_utc_ms INTEGER NOT NULL
) STRICT;

CREATE TABLE app_state (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;
