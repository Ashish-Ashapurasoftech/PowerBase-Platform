-- Tenant DB: create PipelineSchedule table for Pipeline-Level Scheduling.
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineSchedule')
BEGIN
    CREATE TABLE meta.PipelineSchedule (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        PublicId        UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        PipelineId      BIGINT NOT NULL,
        ScheduleType    VARCHAR(50) NOT NULL, -- 'hourly', 'daily', 'weekly', 'monthly', 'yearly', 'custom'
        Interval        INT NULL,             -- X hours, X days, etc.
        TimeOfDay       TIME NULL,            -- e.g. 07:30:00
        Weekdays        VARCHAR(50) NULL,     -- Comma-separated: '1,3,5'
        MonthDay        VARCHAR(50) NULL,     -- Specific day number or 'last'
        MonthOfYear     INT NULL,             -- 1-12
        RelativeWeek    INT NULL,             -- 1-5 (first, second, third, fourth, last)
        RelativeDay     INT NULL,             -- Day of week for relative schedules
        TimeZone        VARCHAR(100) NOT NULL DEFAULT 'UTC',
        CronExpression  VARCHAR(100) NOT NULL,
        NextRunOn       DATETIME2(3) NULL,
        LastRunOn       DATETIME2(3) NULL,
        IsDeleted       BIT NOT NULL DEFAULT 0,
        CreatedOn       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy       BIGINT NOT NULL DEFAULT 0,
        ModifiedOn      DATETIME2(3) NULL,
        ModifiedBy      BIGINT NULL,
        RowVersion      ROWVERSION NOT NULL,
        
        CONSTRAINT PK_PipelineSchedule PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineSchedule_Pipeline FOREIGN KEY (PipelineId) REFERENCES meta.Pipeline(Id)
    );

    CREATE NONCLUSTERED INDEX IX_PipelineSchedule_NextRun 
    ON meta.PipelineSchedule(NextRunOn) 
    WHERE IsDeleted = 0;
END
GO
