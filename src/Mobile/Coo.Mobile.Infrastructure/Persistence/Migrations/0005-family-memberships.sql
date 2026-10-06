-- 0005 (Spec 011b §7.1): the signed-in account's active family memberships, refreshed from the server, with what the
-- labels need (the Owner's display name, the member count). Sign-out clears it; app_state.families_user names the account
-- it was refreshed for, so "not refreshed yet" and "no family at all" read differently.

CREATE TABLE family_memberships (
    household_id  TEXT    NOT NULL PRIMARY KEY,
    role          TEXT    NOT NULL CHECK (role IN ('owner', 'parent', 'caregiver')),
    joined_utc_ms INTEGER NOT NULL,
    owner_name    TEXT    NOT NULL,
    member_count  INTEGER NOT NULL CHECK (member_count >= 1)
) STRICT;
