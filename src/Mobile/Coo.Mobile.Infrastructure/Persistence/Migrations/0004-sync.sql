-- 0004 (Spec 011 §6.1, §6.2): the pull cursor per household, committed with the page it applied; the changes the server
-- did not accept, kept until the record is fixed (ADR 0002 §5); and an index for the per-record outbox lookups.

CREATE TABLE sync_cursors (
    household_id   TEXT    NOT NULL PRIMARY KEY,
    last_seq       INTEGER NOT NULL CHECK (last_seq >= 0),
    bootstrap_head INTEGER NULL CHECK (bootstrap_head IS NULL OR bootstrap_head >= 0)
) STRICT;

CREATE TABLE sync_rejections (
    entity_type     TEXT    NOT NULL,
    entity_id       TEXT    NOT NULL,
    household_id    TEXT    NOT NULL,
    reason          TEXT    NOT NULL CHECK (length(reason) > 0),
    rejected_utc_ms INTEGER NOT NULL,
    PRIMARY KEY (entity_type, entity_id)
) STRICT;

CREATE INDEX ix_sync_rejections_household ON sync_rejections (household_id);
CREATE INDEX ix_outbox_entity ON outbox (entity_type, entity_id);
