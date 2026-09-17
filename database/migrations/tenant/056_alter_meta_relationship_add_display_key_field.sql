-- Migration 056: add DisplayKeyFieldId to meta.Relationship
-- Task 6 — Standard / Override key field on relationship creation.
-- DisplayKeyFieldId: when set, the reference picker shows records identified
-- by this parent field instead of the default (Record ID# or the table's global KeyFieldId).
-- NULL = Standard key (Record ID# / table default). Storage is always the internal row Id.

ALTER TABLE meta.Relationship
    ADD DisplayKeyFieldId BIGINT NULL;
