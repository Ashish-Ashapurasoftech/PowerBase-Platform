-- Create meta.PipelineTriggerSubscription table for centralized pipeline trigger subscriptions
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription')
BEGIN
    CREATE TABLE meta.PipelineTriggerSubscription (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        
        OwnerTenantId BIGINT NOT NULL,           -- Owner tenant ID (where pipeline resides)
        OwnerPipelineId BIGINT NOT NULL,         -- Owner pipeline integer ID
        PipelinePublicId UNIQUEIDENTIFIER NOT NULL, -- Pipeline public ID
        TriggerStepPublicId UNIQUEIDENTIFIER NOT NULL, -- Trigger step public ID
        TriggerStepRefId NVARCHAR(100) NOT NULL, -- Trigger step reference ID
        
        TargetTenantId BIGINT NOT NULL,          -- Target tenant ID (where events happen)
        TargetAppPublicId UNIQUEIDENTIFIER NOT NULL, -- Target App public ID
        TargetTablePublicId UNIQUEIDENTIFIER NOT NULL, -- Target Table public ID
        TargetConnectionPublicId UNIQUEIDENTIFIER NOT NULL, -- Original connection/account public ID
        
        TriggerOnAdded BIT NOT NULL DEFAULT 0,
        TriggerOnModified BIT NOT NULL DEFAULT 0,
        TriggerOnDeleted BIT NOT NULL DEFAULT 0,
        TriggerOnAnyField BIT NOT NULL DEFAULT 1,
        TriggerFieldsJson NVARCHAR(MAX) NULL, -- JSON array of monitored fields
        FiltersJson NVARCHAR(MAX) NULL, -- JSON array of filter rules
        FilterGroupsJson NVARCHAR(MAX) NULL, -- JSON array of filter groups
        
        IsActive BIT NOT NULL DEFAULT 1,
        
        CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        LastModifiedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        
        CONSTRAINT PK_PipelineTriggerSubscription PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_PipelineTriggerSubscription_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_PipelineTriggerSubscription_OwnerTenant FOREIGN KEY (OwnerTenantId) REFERENCES meta.Tenant(Id),
        CONSTRAINT FK_PipelineTriggerSubscription_TargetTenant FOREIGN KEY (TargetTenantId) REFERENCES meta.Tenant(Id),
        -- Ensure unique active subscription per trigger step
        CONSTRAINT UX_PipelineTriggerSubscription_Step UNIQUE (OwnerTenantId, PipelinePublicId, TriggerStepRefId)
    );
    
    CREATE NONCLUSTERED INDEX IX_PipelineTriggerSubscription_Matching 
    ON meta.PipelineTriggerSubscription (TargetTenantId, TargetTablePublicId, IsActive);
END
GO
