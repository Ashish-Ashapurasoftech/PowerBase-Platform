-- Adds the "Use as Quick Peek form" flag to meta.Form. At most one form per table has this set —
-- SetQuickPeekFormAsync always clears every other form on the table before setting a new one,
-- mirroring IsDefault's exclusivity (see FormRepository.SetDefaultAsync). Unlike IsDefault
-- though, a table can also have NO Quick Peek form (all rows 0) — the frontend simply hides the
-- Quick Peek action icon in that case. See PowerBase.Application.Forms.Commands.SetQuickPeekForm
-- and PowerBase.Application.Forms.Queries.GetQuickPeekForm.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Form') AND name = 'IsQuickPeekForm')
BEGIN
    ALTER TABLE meta.Form ADD IsQuickPeekForm BIT NOT NULL CONSTRAINT DF_Form_IsQuickPeekForm DEFAULT(0);
END
GO
