-- "Display message" rule actions can be presented three ways:
--   MessageType = 'notification' (a message at the top of the screen - the original behaviour) |
--                 'modal' (a centred pop-up) | 'field' (shown with a specific field, named by TargetElement*).
-- NULL on older actions means 'notification'. Only meaningful for ActionType = 'DisplayMessage'.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.FormRuleAction') AND name = 'MessageType')
    ALTER TABLE meta.FormRuleAction ADD MessageType VARCHAR(20) NULL;
GO
