-- Data fix: remove phantom meta.PageVersion rows left by the original (pre-fix)
-- CreatePageCommandHandler/DuplicatePageCommandHandler, which pre-inserted a "version 1"
-- row at creation time. That row collides with UpdatePage/RestorePageVersion's first
-- pre-edit snapshot, which is also written at VersionNo = CurrentVersionNo — causing a
-- PK_PageVersion violation on the first edit/restore of any page created before the fix.
--
-- Invariant going forward: a PageVersion row must only exist for VersionNo strictly less
-- than its page's live CurrentVersionNo (CurrentVersionNo is always the un-snapshotted,
-- still-live state). Any row that violates this invariant is exactly the leftover phantom
-- row from the old bug, so it is safe to delete unconditionally.

-- meta.PageVersion has an INSTEAD OF DELETE trigger (TR_PageVersion_AppendOnly) that blocks
-- exactly this kind of statement in normal operation. This is the one sanctioned exception:
-- a one-time repair of rows that should never have been written, so the trigger is disabled
-- for the duration of this statement only, then restored immediately after.
DISABLE TRIGGER meta.TR_PageVersion_AppendOnly ON meta.PageVersion;

DELETE pv
FROM meta.PageVersion pv
JOIN meta.Page p ON p.Id = pv.PageId
WHERE pv.VersionNo >= p.CurrentVersionNo;

ENABLE TRIGGER meta.TR_PageVersion_AppendOnly ON meta.PageVersion;
GO
