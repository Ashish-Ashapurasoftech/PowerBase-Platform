ALTER TABLE meta.AppTable
ADD 
    DefaultRecordPickerField1Id bigint NULL,
    DefaultRecordPickerField2Id bigint NULL,
    DefaultRecordPickerField3Id bigint NULL;
GO

ALTER TABLE meta.AppTable
ADD CONSTRAINT FK_AppTable_AppField_DefaultRecordPicker1 FOREIGN KEY (DefaultRecordPickerField1Id) REFERENCES meta.AppField (Id);

ALTER TABLE meta.AppTable
ADD CONSTRAINT FK_AppTable_AppField_DefaultRecordPicker2 FOREIGN KEY (DefaultRecordPickerField2Id) REFERENCES meta.AppField (Id);

ALTER TABLE meta.AppTable
ADD CONSTRAINT FK_AppTable_AppField_DefaultRecordPicker3 FOREIGN KEY (DefaultRecordPickerField3Id) REFERENCES meta.AppField (Id);
GO
