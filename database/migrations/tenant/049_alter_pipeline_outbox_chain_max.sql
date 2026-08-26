-- Alter Column PipelineChain in meta.PipelineOutbox to NVARCHAR(MAX) to prevent truncation on large recursion depths
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineOutbox') AND name = 'PipelineChain')
BEGIN
    DECLARE @ConstraintName NVARCHAR(200)
    SELECT @ConstraintName = name
    FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID('meta.PipelineOutbox')
      -- ColumnId is 1-based, we get it dynamically
      AND parent_column_id = COLUMNPROPERTY(OBJECT_ID('meta.PipelineOutbox'), 'PipelineChain', 'ColumnId')

    IF @ConstraintName IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE meta.PipelineOutbox DROP CONSTRAINT ' + @ConstraintName)
    END

    -- Alter column to NVARCHAR(MAX)
    ALTER TABLE meta.PipelineOutbox ALTER COLUMN PipelineChain NVARCHAR(MAX) NOT NULL;

    -- Re-add default constraint with a clean, named constraint
    ALTER TABLE meta.PipelineOutbox ADD CONSTRAINT DF_PipelineOutbox_PipelineChain DEFAULT '[]' FOR PipelineChain;
END
GO
