namespace GameSaves.Core.Save
{
    public interface IMappingImportService
    {
        MappingImportDocument ParseDocument(string jsonContent);

        MappingImportReport Import(string databasePath, MappingImportDocument document, MappingImportOptions? options = null);

        MappingImportReport ImportJson(string databasePath, string jsonContent, MappingImportOptions? options = null);

        MappingImportReport ImportFile(string databasePath, string filePath, MappingImportOptions? options = null);
    }
}
