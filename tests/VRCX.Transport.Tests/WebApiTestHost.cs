using SixLabors.ImageSharp;

// Compile the original HTTP transport against an isolated host so lifecycle
// regressions can be exercised without Electron, an account, or a real DB.
namespace VRCX;

internal static class Program
{
    public const string Version = "VRCX-Transport-Tests";
    public static readonly TestAppApi AppApiInstance = new();
}

internal sealed class TestAppApi
{
    public byte[] ResizeImageToFitLimits(byte[] value, bool matchingDimensions) => throw new NotSupportedException();
    public byte[] ResizePrintImage(byte[] value) => throw new NotSupportedException();
    public bool CropPrint(ref Image image) => throw new NotSupportedException();
}

internal static class StartupArgs
{
    public static readonly TestLaunchArguments LaunchArguments = new();
}

internal sealed class TestLaunchArguments
{
    public bool IsServerMode = true;
    public string ProxyUrl = "";
}

internal sealed class VRCXStorage
{
    public static readonly VRCXStorage Instance = new();
    public string Get(string key) => "";
    public void Set(string key, string value) => throw new NotSupportedException();
    public void Save() => throw new NotSupportedException();
}

internal sealed class SQLite
{
    public static readonly SQLite Instance = new();
    public Action<string, Dictionary<string, object>>? Write;

    public List<List<object>> Execute(string sql, Dictionary<string, object> parameters) => [];
    public void ExecuteNonQuery(string sql, Dictionary<string, object>? parameters = null) =>
        (Write ?? throw new InvalidOperationException("No test cookie store was configured."))(sql, parameters!);
}
