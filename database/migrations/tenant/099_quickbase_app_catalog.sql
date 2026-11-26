-- Migration 099: Quickbase app catalog + user tokens, managed from the hidden admin page (/admin/quickbase).
--   meta.QuickbaseUserToken  — Quickbase user tokens used to read an app's table ids during import ("QB-USER-TOKEN <token>").
--   meta.QuickbaseAppCatalog — the Quickbase apps that are imported: Quickbase app id, logical id in the QBL file ($App_…),
--                              solution group and which user token has access to it.
-- The import reads both: the app id by logical id (stored on meta.App.QBAppID, migration 098) and the token to call
-- GET /v1/tables with (its own token first, then every other active token).
-- Seeded with the current list (81 apps, 5 tokens). Idempotent: a seed row is only inserted when no token with the same name /
-- no app with the same Quickbase app id or logical id exists.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'QuickbaseUserToken' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.QuickbaseUserToken (
        Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
        Name        NVARCHAR(100) NOT NULL,
        Token       NVARCHAR(200) NOT NULL,
        SortOrder   INT NOT NULL CONSTRAINT DF_QuickbaseUserToken_SortOrder DEFAULT (0),
        IsActive    BIT NOT NULL CONSTRAINT DF_QuickbaseUserToken_IsActive DEFAULT (1),
        CreatedOn   DATETIME2 NOT NULL CONSTRAINT DF_QuickbaseUserToken_CreatedOn DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT NULL,
        ModifiedOn  DATETIME2 NULL,
        ModifiedBy  BIGINT NULL
    );
    CREATE UNIQUE INDEX UX_QuickbaseUserToken_Name ON meta.QuickbaseUserToken (Name);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'QuickbaseAppCatalog' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.QuickbaseAppCatalog (
        Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
        QbAppId     NVARCHAR(20)  NOT NULL,
        LogicalId   NVARCHAR(200) NOT NULL,
        Solution    NVARCHAR(200) NULL,
        UserTokenId BIGINT NULL
            CONSTRAINT FK_QuickbaseAppCatalog_UserToken REFERENCES meta.QuickbaseUserToken (Id) ON DELETE SET NULL,
        CreatedOn   DATETIME2 NOT NULL CONSTRAINT DF_QuickbaseAppCatalog_CreatedOn DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT NULL,
        ModifiedOn  DATETIME2 NULL,
        ModifiedBy  BIGINT NULL
    );
    CREATE UNIQUE INDEX UX_QuickbaseAppCatalog_QbAppId ON meta.QuickbaseAppCatalog (QbAppId);
    CREATE UNIQUE INDEX UX_QuickbaseAppCatalog_LogicalId ON meta.QuickbaseAppCatalog (LogicalId);
END
GO

-- ---- Seed: user tokens ----
INSERT INTO meta.QuickbaseUserToken (Name, Token, SortOrder)
SELECT s.Name, s.Token, s.SortOrder
FROM (VALUES
    (N'First 20', N'b8vzuw_m9kn_0_tg6fr9tt7rd4dmjzdzscbv3h8h', 1),
    (N'Second 20', N'b8vzuw_m9kn_0_c39x48wdz47fgnckeewykcqcwpwk', 2),
    (N'Third 20', N'b8vzuw_m9kn_0_dbqpjfibdiz55yx5hcdeb7km8e4', 3),
    (N'Fourth 20', N'b8vzuw_m9kn_0_45fc9ucrqfm5rbgvzcj4b39m3bc', 4),
    (N'Fifth 19', N'b8vzuw_m9kn_0_b3j9cv2d4fu5tbb7gvm6bykhgc9', 5)
) AS s (Name, Token, SortOrder)
WHERE NOT EXISTS (SELECT 1 FROM meta.QuickbaseUserToken t WHERE t.Name = s.Name);
GO

-- ---- Seed: apps (TokenName = the token with access; NULL = none known, every token is tried) ----
INSERT INTO meta.QuickbaseAppCatalog (QbAppId, LogicalId, Solution, UserTokenId)
SELECT s.QbAppId, s.LogicalId, s.Solution, t.Id
FROM (VALUES
    (N'bv7eq2pur', N'$App_ABA_Claims_Hub', N'Solution 6 - CCR & ABA', N'Fifth 19'),
    (N'bv5m46bqr', N'$App_ABA_Billing_Process', N'Solution 6 - CCR & ABA', N'Fifth 19'),
    (N'bv77y482c', N'$App_CCR_ABA_Workflow', N'Solution 6 - CCR & ABA', N'Fifth 19'),
    (N'bv5jfunvd', N'$App_CCR_ABA_Database', N'Solution 6 - CCR & ABA', N'Fifth 19'),
    (N'btdebznhc', N'$App_Training_Signoff', N'Solution 6 - CCR & ABA', N'First 20'),
    (N'btex32xd7', N'$App_Compliance_Manual_Signoff', N'Solution 6 - CCR & ABA', N'Second 20'),
    (N'buzkbek8b', N'$App_Clinic_QA_UR', N'Solution 6 - CCR & ABA', N'Fourth 20'),
    (N'bthp9pdst', N'$App_TS_Audit_Tool', N'Solution 6 - CCR & ABA', N'Second 20'),
    (N'bv4wsinyq', N'$App_CCR_Notifications', N'Solution 6 - CCR & ABA', N'Fifth 19'),
    (N'bveizki58', N'$App_DSS_Supervision', N'Solution 3 - CFTSS & Supervision', N'Fifth 19'),
    (N'bt644ndgc', N'$App_CFTSS_Supervision_Notes', N'Solution 3 - CFTSS & Supervision', N'Third 20'),
    (N'btkxt3dhj', N'$App_CFTSS_Session_Notes', N'Solution 3 - CFTSS & Supervision', N'Second 20'),
    (N'bu8watiqn', N'$App_CARES_API_Database', N'Solution 4 - CCR & Compliance', N'Fourth 20'),
    (N'bvk52p5jt', N'$App_CCR_Timesheet_Storage', N'Solution 4 - CCR & Compliance', N'Fifth 19'),
    (N'bvb5y9e35', N'$App_CCR_QA', N'Solution 4 - CCR & Compliance', N'Fifth 19'),
    (N'bu2mdsvgk', N'$App_CFTSS_Compliance_Process', N'Solution 4 - CCR & Compliance', N'Fourth 20'),
    (N'bsxm7aa8r', N'$App_Core_Compliance_Management', N'Solution 4 - CCR & Compliance', N'First 20'),
    (N'bts2q8uk2', N'$App_Beacon_Financial', N'Solution 7 - VDB Other & Serenity', N'Second 20'),
    (N'btuqnazwg', N'$App_Destiny_Financial', N'Solution 7 - VDB Other & Serenity', N'Third 20'),
    (N'btuvxygs9', N'$App_HR_Clinicians_Process', N'Solution 7 - VDB Other & Serenity', N'Third 20'),
    (N'bt8msrcjx', N'$App_OLP_Workflow', N'Solution 7 - VDB Other & Serenity', N'Third 20'),
    (N'btqat37ah', N'$App_Voyage_HCBS_Workflow', N'Solution 7 - VDB Other & Serenity', N'Second 20'),
    (N'bt3j7gp34', N'$App_VDB_Notifications', N'Solution 7 - VDB Other & Serenity', N'Third 20'),
    (N'btwnfm7e9', N'$App_VDB_Financial_Docs', N'Solution 7 - VDB Other & Serenity', N'Third 20'),
    (N'btsqaeh7i', N'$App_VDB_HR', N'Solution 7 - VDB Other & Serenity', N'Second 20'),
    (N'bunx343v9', N'$App_HIH_Archive', N'Solution 9 - HIH', N'Third 20'),
    (N'bvnyxbypb', N'$App_HIH_Care_Management', N'Solution 9 - HIH', N'Fifth 19'),
    (N'btcxr57sp', N'$App_HIH_Care', N'Solution 9 - HIH', N'First 20'),
    (N'btcxr3var', N'$App_HIH_Database', N'Solution 9 - HIH', N'First 20'),
    (N'bu5hwxa6i', N'$App_Thryve_Financial', N'Solution 8.1 - Thryve', N'Fourth 20'),
    (N'bu5h68svc', N'$App_Thryve_Database', N'Solution 8.1 - Thryve', N'Fourth 20'),
    (N'btad9q84k', N'$App_DSP_Supervision', N'Solution 2.1 - CCRS & DSP', N'First 20'),
    (N'btabryr3h', N'$App_CCRS_Database', N'Solution 2.1 - CCRS & DSP', N'First 20'),
    (N'bsz9xvg3b', N'$App_Special_Advantage_Time_Sheets', N'Solution 2 - VDB', N'First 20'),
    (N'bsvdwctde', N'$App_Voyage_Time_Sheets', N'Solution 2 - VDB', N'First 20'),
    (N'btjtdyt4j', N'$App_Beacon_CM_Workflow', N'Solution 2 - VDB', N'Second 20'),
    (N'bthsfbsnd', N'$App_VDB_Database', N'Solution 2 - VDB', N'Second 20'),
    (N'brkm6fq2j', N'$App_Voyage_Financial', N'Solution 2 - VDB', N'First 20'),
    (N'bua5w26ri', N'$App_Temp', N'Solution 11 - Other', N'Third 20'),
    (N'bij5jnger', N'$App_Tes', N'Solution 11 - Other', N'First 20'),
    (N'brn5mfrs3', N'$App_M_Kohn_and_Co', N'Solution 11 - Other', N'First 20'),
    (N'bng6y32vi', N'$App_Superior_BSD', N'Solution 11 - Other', N'First 20'),
    (N'buz8jtfpw', N'$App_Strive_HR_Onboarding_Process', N'Solution 11 - Other', N'Fourth 20'),
    (N'bunsvz4sn', N'$App_Staffing_Test_Management', N'Solution 11 - Other', N'Third 20'),
    (N'buixjcgtc', N'$App_Copy_of_TrinityCalendar_App', N'Solution 11 - Other', N'Third 20'),
    (N'buxeczfs2', N'$App_Calendar_Demo', N'Solution 11 - Other', N'Fourth 20'),
    (N'btnjnvb5j', N'$App_Clinical_Appointments', N'Solution 11 - Other', N'Second 20'),
    (N'btjs54sfz', N'$App_Location_Asset_Delivery', N'Solution 11 - Other', N'Second 20'),
    (N'bv9wjhcgq', N'$App_Construction_Process', N'Solution 11 - Other', N'Fifth 19'),
    (N'btgbwkix2', N'$App_Application_Analyzer', N'Solution 11 - Other', N'Second 20'),
    (N'buv5nbjhb', N'$App_Solution_Lifecycle_Manager', N'Solution 11 - Other', N'Fourth 20'),
    (N'bs23gqymu', N'$App_YSL_Support', N'Solution 11 - Other', N'First 20'),
    (N'btx9rn2pa', N'$App_CARES_CFTSS_Workflow', N'Solution 11 - Other', N'Third 20'),
    (N'bta4jja5p', N'$App_SA_Financial_Management', N'Solution 11 - Other', N'First 20'),
    (N'btyb7pycu', N'$App_CARES_CFTSS_Management', N'Solution 3.1 - CFTSS Mgmt & Destiny', N'Third 20'),
    (N'btnuf85uk', N'$App_Destiny_CFTSS_Workflow', N'Solution 3.1 - CFTSS Mgmt & Destiny', N'Second 20'),
    (N'btnr4ugbb', N'$App_HR_Onboarding_Process', N'Solution 3.1 - CFTSS Mgmt & Destiny', N'Second 20'),
    (N'bth2hfyqg', N'$App_Destiny_Pre_Intake', N'Solution 3.1 - CFTSS Mgmt & Destiny', N'Second 20'),
    (N'bvbrebtdn', N'$App_YSL_Data_Entry', N'Solution 5 - CARES Other', NULL),
    (N'bve2ii5pq', N'$App_Elevate_Data_Center', N'Solution 5 - CARES Other', N'Fifth 19'),
    (N'bu88mhmzb', N'$App_Consent_Log_Database', N'Solution 5 - CARES Other', N'Fourth 20'),
    (N'btw3fa7bq', N'$App_HCBS_QA', N'Solution 5 - CARES Other', N'Third 20'),
    (N'bvbbgrtnz', N'$App_CFTSS_Auxiliary_App', N'Solution 5 - CARES Other', N'Fourth 20'),
    (N'butqrqpz5', N'$App_CFTSS_QA', N'Solution 5 - CARES Other', N'Third 20'),
    (N'bvefumhsu', N'$App_CFTSS_Goals_Objectives', N'Solution 5 - CARES Other', N'Fifth 19'),
    (N'bvdvygmkk', N'$App_CFTSS_Billing_Process', N'Solution 5 - CARES Other', N'Fifth 19'),
    (N'btprcpmia', N'$App_CARES_Comm_Portal', N'Solution 5 - CARES Other', N'Second 20'),
    (N'bt7yxpsf2', N'$App_CARES_Imports', N'Solution 5 - CARES Other', N'Third 20'),
    (N'buwcxjqvt', N'$App_SPH_Sessions', N'Solution 8 - SPH', N'Fourth 20'),
    (N'buwcw7zuu', N'$App_SPH_HR_Onboarding_Process', N'Solution 8 - SPH', N'Fourth 20'),
    (N'buwcsyq2y', N'$App_SPH_Workflow', N'Solution 8 - SPH', N'Fourth 20'),
    (N'buwcwtipa', N'$App_SPH_Database', N'Solution 8 - SPH', N'Fourth 20'),
    (N'buiu4jea7', N'$App_CARES_Training_Tracking', N'Solution 1 - CARES', N'Third 20'),
    (N'bthp3vu5e', N'$App_CM_File_Management', N'Solution 1 - CARES', N'Second 20'),
    (N'bteqxqwx3', N'$App_CARES_HCBS_Management', N'Solution 1 - CARES', N'Second 20'),
    (N'btueuk3ai', N'$App_CARES_Time_Sheets', N'Solution 1 - CARES', N'Second 20'),
    (N'bteqnetwv', N'$App_CARES_Database', N'Solution 1 - CARES', N'Second 20'),
    (N'bu5hxjjxw', N'$App_Staffcore_Financial', N'Solution 7.1 - Serenity', N'Fourth 20'),
    (N'bu8cch54n', N'$App_Serenity_HR_Onboarding_Process', N'Solution 7.1 - Serenity', N'Fourth 20'),
    (N'bu3wxsy83', N'$App_Serenity_Workflow', N'Solution 7.1 - Serenity', N'Fourth 20'),
    (N'bu3wxqd6m', N'$App_Serenity_Database', N'Solution 7.1 - Serenity', N'Fourth 20')
) AS s (QbAppId, LogicalId, Solution, TokenName)
LEFT JOIN meta.QuickbaseUserToken t ON t.Name = s.TokenName
WHERE NOT EXISTS (SELECT 1 FROM meta.QuickbaseAppCatalog c WHERE c.QbAppId = s.QbAppId OR c.LogicalId = s.LogicalId);
GO
