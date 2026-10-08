-- Migration 072: link a report to the relationship that auto-created it.
-- The relationship wizard creates a hidden "Embedded for {Parent}" report on the child table;
-- RelationshipId lets deleting the relationship clean that report up. NULL for ordinary reports.
IF COL_LENGTH('meta.Report', 'RelationshipId') IS NULL
BEGIN
    ALTER TABLE meta.Report
        ADD RelationshipId BIGINT NULL
            CONSTRAINT FK_Report_Relationship REFERENCES meta.Relationship(Id);
END
GO
