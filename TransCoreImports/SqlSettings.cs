namespace TransCoreImports;

public class SqlSettings
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool IntegratedSecurity { get; set; } = true;
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    public int CommandTimeoutSeconds { get; set; } = 120;
}

public class AppSettings
{
    public SqlSettings Sql { get; set; } = new();
    public SqlSettings SessionSql { get; set; } = new();
}
