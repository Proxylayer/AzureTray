using System.Threading.Tasks;

namespace AzureTray;

public interface IUpdateService
{
    string CurrentVersionDisplay { get; }
    Task CheckOnStartupAsync();
    Task<string> CheckAndApplyAsync();
}
