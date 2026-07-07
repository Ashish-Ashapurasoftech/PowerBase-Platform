namespace PowerBase.API.Models.Tables;

public record UpdateTableRequest(
    string Name, 
    string? SingularLabel, 
    string? PluralLabel, 
    string? Description, 
    string? Icon,
    long? DefaultRecordPickerField1Id = null,
    long? DefaultRecordPickerField2Id = null,
    long? DefaultRecordPickerField3Id = null
);
