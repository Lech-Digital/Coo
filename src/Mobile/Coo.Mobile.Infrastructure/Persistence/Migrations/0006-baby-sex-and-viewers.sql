-- 0006 (1.9.0): the baby's sex, chosen once with the baby for the growth chart's standard and synced with the family;
-- and family_memberships accepts 'viewer'. 1.6.0 added the Viewer role, but 0005's check still refused it, so a viewer's
-- phone could not store its family list.

ALTER TABLE baby_profiles ADD COLUMN sex TEXT NULL CHECK (sex IS NULL OR sex IN ('girl', 'boy'));

CREATE TABLE family_memberships_0006 (
    household_id  TEXT    NOT NULL PRIMARY KEY,
    role          TEXT    NOT NULL CHECK (role IN ('owner', 'parent', 'caregiver', 'viewer')),
    joined_utc_ms INTEGER NOT NULL,
    owner_name    TEXT    NOT NULL,
    member_count  INTEGER NOT NULL CHECK (member_count >= 1)
) STRICT;

INSERT INTO family_memberships_0006 (household_id, role, joined_utc_ms, owner_name, member_count)
SELECT household_id, role, joined_utc_ms, owner_name, member_count FROM family_memberships;

DROP TABLE family_memberships;
ALTER TABLE family_memberships_0006 RENAME TO family_memberships;
