-- 0002: an expected baby has a due date and no birth date yet (Spec 006 §3a).
-- SQLite cannot drop a NOT NULL in place, so the table is rebuilt and its rows are copied unchanged.

CREATE TABLE baby_profiles_0002 (
    id              TEXT    NOT NULL PRIMARY KEY,
    household_id    TEXT    NOT NULL,
    name            TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 40),
    nicknames       TEXT    NOT NULL CHECK (json_valid(nicknames) AND json_type(nicknames) IS 'array'),
    birth_date      TEXT    NULL CHECK (birth_date IS NULL OR date(birth_date) IS birth_date),
    due_date        TEXT    NULL CHECK (due_date IS NULL OR date(due_date) IS due_date),
    version_wall_ms INTEGER NOT NULL,
    version_counter INTEGER NOT NULL,
    version_device  TEXT    NOT NULL,
    is_deleted      INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
    CHECK (birth_date IS NOT NULL OR due_date IS NOT NULL)
) STRICT;

INSERT INTO baby_profiles_0002
    (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted)
SELECT id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted
FROM baby_profiles;

DROP TABLE baby_profiles;
ALTER TABLE baby_profiles_0002 RENAME TO baby_profiles;
CREATE INDEX ix_baby_profiles_household ON baby_profiles (household_id);
