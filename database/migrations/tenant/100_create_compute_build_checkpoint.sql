-- Progress of a first build of a stored column (c_{fid}), so a build that is interrupted (worker restart, new revision, crash, the
-- database being busy) continues where it stopped instead of recomputing every row from the start. A multi-hour build of a field over a
-- table of a million rows must not be lost by a restart.
--
-- One row per Id range ("slice") of one field build: the build splits the table into ranges (After < Id <= UpTo, the last one open-ended)
-- and every range is worked through in Id order. DoneThroughId is the last Id whose value is written; the range is finished when IsDone = 1.
-- (FieldId, Version) is the field's MaterializeVersion the build writes for: a new version is a new build and ignores older rows.
-- Watermark is the highest meta.ComputeOutbox.Id seen when the build first started: changes saved after that are replayed from the outbox
-- before the field becomes Ready, however many times the build was resumed.
-- Rows hold ids and counters only, nothing sensitive. Deleted when the field is Ready / Failed / switched off. Additive and safe to re-run.

IF OBJECT_ID('meta.ComputeBuildCheckpoint') IS NULL
BEGIN
    CREATE TABLE meta.ComputeBuildCheckpoint (
        FieldId        BIGINT       NOT NULL,
        Version        INT          NOT NULL,
        SliceAfter     BIGINT       NOT NULL,
        SliceUpTo      BIGINT       NOT NULL,
        DoneThroughId  BIGINT       NOT NULL,
        RowsWritten    BIGINT       NOT NULL CONSTRAINT DF_ComputeBuildCheckpoint_Rows DEFAULT 0,
        Watermark      BIGINT       NOT NULL,
        IsDone         BIT          NOT NULL CONSTRAINT DF_ComputeBuildCheckpoint_Done DEFAULT 0,
        UpdatedOn      DATETIME2(3) NOT NULL CONSTRAINT DF_ComputeBuildCheckpoint_UpdatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ComputeBuildCheckpoint PRIMARY KEY (FieldId, Version, SliceAfter)
    );
END
GO
