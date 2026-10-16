-- Display message actions shown as a notification or next to a field carry a severity that sets their colour:
--   MessageSeverity = 'success' | 'info' | 'warning' | 'error'. NULL means 'info' (the original look).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'MessageSeverity')
    ALTER TABLE meta.FormRuleAction ADD MessageSeverity VARCHAR(20) NULL;
GO
