-- Tenant DB: add meta.FormRuleAction.RunOnceOnActivation — a ChangeValue-only option ("Run this
-- action when the condition changes from false to true"). When set, the frontend's rule engine
-- (FormRendererComponent.evaluateRules) fires that action once on the rule's condition false→true
-- transition instead of on every evaluation pass while the condition stays true, so it won't keep
-- re-stamping a value (e.g. a "became Approved on" date) or fight a user's later edit to the same
-- field. Meaningless for every other action type, which already has its own continuous/live
-- semantics (Show/Hide, Enable/Disable, Require/NotRequired all revert to baseline on their own —
-- see the client-side evaluateRules baseline-reset logic).
--
-- Defaults to 0 (the pre-existing "runs every time" behavior) so every already-saved action keeps
-- behaving exactly as it did before this column existed — only a NEWLY authored ChangeValue action
-- gets this checked by default, which the frontend sets explicitly when the action is created, not
-- via this column default.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'RunOnceOnActivation')
BEGIN
    ALTER TABLE meta.FormRuleAction ADD RunOnceOnActivation BIT NOT NULL DEFAULT 0;
END
GO
