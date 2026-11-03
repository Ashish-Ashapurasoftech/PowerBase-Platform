-- Reference (and reference-proxy) form elements choose which parent fields their dropdown shows
-- (Quickbase: form element > "Select using record picker"). NULL RecordPickerMode = the parent table's
-- default record picker, which is what every existing element keeps using.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'RecordPickerMode')
BEGIN
    -- 'default' | 'report' | 'field'
    ALTER TABLE meta.FormElement ADD RecordPickerMode VARCHAR(10) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'RecordPickerReportPublicId')
BEGIN
    -- Mode 'report': the PublicId of a Table report on the PARENT table. No FK: reports are soft-deleted and
    -- a deleted or ineligible report just makes the element fall back to the default picker.
    ALTER TABLE meta.FormElement ADD RecordPickerReportPublicId UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'RecordPickerFieldId')
BEGIN
    -- Mode 'field': the Fid (like AppFieldId) of a Lookup field on the form's table, belonging to this reference's relationship.
    ALTER TABLE meta.FormElement ADD RecordPickerFieldId BIGINT NULL;
END
GO
