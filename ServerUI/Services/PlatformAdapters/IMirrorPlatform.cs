using System.Threading.Tasks;

namespace ServerUI.Services.PlatformAdapters;

public interface IMirrorPlatform
{
    string Name { get; }
    Task<bool> UploadPackageAsync(string pkgName, byte[] zip, string sha);
    Task<bool> UploadFileAsync(string remotePath, byte[] data, string message);
    Task<bool> UploadChangelogAsync(byte[] data, string sha, string message);
    Task CleanupOldPackagesAsync(int keepCount = 5);
}
