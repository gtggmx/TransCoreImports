namespace TransCoreLoader;

public class SqlSettings
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool IntegratedSecurity { get; set; }
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    public int CommandTimeoutSeconds { get; set; } = 3600;
}

public class LoaderSettings
{
    // CPC server (10.66.170.53, DB CPC): where the transaction procedure runs.
    public SqlSettings Source { get; set; } = new();

    // GmxRaw on 192.168.150.73: Data_1220, ProcessedFiles, ImportSession and the
    // GmxParameters lookup tables are all reachable from this one login.
    public SqlSettings Target { get; set; } = new();
}
