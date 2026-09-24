-- Tenant DB: meta.ReportGridEditRule — a report-scoped, ordered allow-list of which Form Rules
-- get enforced when a user grid-edits a record through THIS report specifically. Server-side
-- write-path enforcement (FormRuleServerValidator) is unchanged by this table and keeps checking
-- the full table-wide union of every active Require/PreventSave rule on every save, regardless of
-- which report (if any) initiated the write — this table only drives a CLIENT-SIDE pre-check in
-- the report grid itself (Require/PreventSave/Enable/Disable/ChangeValue/DisplayMessage), giving
-- immediate feedback without waiting for a round trip, same "client checks for UX, server is the
-- real gate" split the Add/Edit Record form already has with its own live rule evaluation.
--
-- Modeled directly on meta.FormRuleAction (FormRuleId FK + DisplayOrder) rather than the
-- unordered meta.AppRoleReport join table, since a report's "top to bottom" rule order is
-- meaningful here (it's the priority order shown/edited in the picker UI).

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.ReportGridEditRule'))
BEGIN
    CREATE TABLE meta.ReportGridEditRule (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        ReportId     BIGINT NOT NULL,
        FormRuleId   BIGINT NOT NULL,
        DisplayOrder INT    NOT NULL DEFAULT 0,
        CONSTRAINT PK_ReportGridEditRule PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_ReportGridEditRule UNIQUE (ReportId, FormRuleId),
        CONSTRAINT FK_ReportGridEditRule_Report   FOREIGN KEY (ReportId)   REFERENCES meta.Report(Id)   ON DELETE CASCADE,
        CONSTRAINT FK_ReportGridEditRule_FormRule FOREIGN KEY (FormRuleId) REFERENCES meta.FormRule(Id)
    );

    CREATE NONCLUSTERED INDEX IX_ReportGridEditRule_ReportId ON meta.ReportGridEditRule (ReportId);
END
GO
