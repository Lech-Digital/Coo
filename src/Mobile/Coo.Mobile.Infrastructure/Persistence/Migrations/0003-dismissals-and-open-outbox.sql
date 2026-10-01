-- 0003 (Spec 007 §7): the "keep both" decisions, and an outbox that no longer lists entity types in a CHECK.
-- The allowed types live in code (SyncedEntityType), so a new synced type never needs an outbox rebuild again.

CREATE TABLE duplicate_dismissals (
    id              TEXT    NOT NULL PRIMARY KEY,
    household_id    TEXT    NOT NULL,
    entry_a         TEXT    NOT NULL,
    entry_b         TEXT    NOT NULL,
    version_wall_ms INTEGER NOT NULL,
    version_counter INTEGER NOT NULL,
    version_device  TEXT    NOT NULL,
    is_deleted      INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
    CHECK (entry_a < entry_b)
) STRICT;

CREATE INDEX ix_duplicate_dismissals_household ON duplicate_dismissals (household_id);

CREATE TABLE outbox_0003 (
    seq             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    change_id       TEXT    NOT NULL UNIQUE,
    entity_type     TEXT    NOT NULL CHECK (length(entity_type) > 0),
    entity_id       TEXT    NOT NULL,
    operation       TEXT    NOT NULL CHECK (operation IN ('upsert', 'delete')),
    version_wall_ms INTEGER NOT NULL,
    version_counter INTEGER NOT NULL,
    version_device  TEXT    NOT NULL
) STRICT;

-- Every row and its order (seq) are kept.
INSERT INTO outbox_0003 (seq, change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
SELECT seq, change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device FROM outbox;

DROP TABLE outbox;
ALTER TABLE outbox_0003 RENAME TO outbox;
