using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsMcp.Contracts;
namespace VsMcp.Services.Navigation
{
    internal sealed class NavigationService : INavigationService
    {
        private readonly INavigationService managed, native;
        public NavigationService(INavigationService managedService, INavigationService nativeService) { managed = managedService; native = nativeService; }
        public Task<NavigationResult> FindAsync(string path, int line, int column, bool references, int maxResults, string project, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(path ?? "");
            var isManaged = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vb", StringComparison.OrdinalIgnoreCase);
            return (isManaged ? managed : native).FindAsync(path, line, column, references, maxResults, project, cancellationToken);
        }
    }
}
