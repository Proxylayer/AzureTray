using System.Threading;
using System.Threading.Tasks;
using AzureTray.Dto;

namespace AzureTray.Graph;

public interface IGraphMeClient
{
    Task<MeResponse> GetMeAsync(string tenantId, CancellationToken cancellationToken);
}
