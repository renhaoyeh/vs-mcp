using System.Threading;
using System.Threading.Tasks;
using VsMcp.Contracts;

namespace VsMcp.Services.Navigation
{
    internal interface INavigationService
    {
        Task<NavigationResult> FindAsync(string path, int line, int column, bool references, int maxResults, string project, CancellationToken cancellationToken);
    }
}
