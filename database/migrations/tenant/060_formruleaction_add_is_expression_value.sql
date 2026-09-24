-- Tenant DB: add meta.FormRuleAction.IsExpressionValue — lets ChangeLabel/ChangeValue/
-- DisplayMessage/PreventSave carry a FORMULA expression in ActionValue instead of a literal
-- string, mirroring how meta.FormRule already has IsExpressionMode/ExpressionText for a rule's
-- own conditions. When true, ActionValue holds an expression (e.g. "You cannot save after " &
-- [Calculated Deadline]) compiled/evaluated the same way an Expression Mode rule condition is
-- (FormulaEngine, [Field Name] bracket references), and the action uses the LIVE evaluated
-- result instead of the raw string. No new column for the expression text itself — ActionValue
-- is reused for both, exactly as FormRule.ExpressionText already parallels its own ActionValue-
-- equivalent split.
--
-- Defaults to 0 (a literal value, today's only behavior) so every already-saved action keeps
-- behaving exactly as it did before this column existed.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'IsExpressionValue')
BEGIN
    ALTER TABLE meta.FormRuleAction ADD IsExpressionValue BIT NOT NULL DEFAULT 0;
END
GO
