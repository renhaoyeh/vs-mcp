using System;
using System.IO;
using System.Reflection;

namespace VsMcp.Infrastructure
{
    internal static class HostAssemblyResolver
    {
        internal static void Register() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        internal static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            // JIT binds can omit RequestingAssembly; reject identifiable requests from other extensions.
            if (args.RequestingAssembly != null && (args.RequestingAssembly.IsDynamic || !string.Equals(
                Path.GetDirectoryName(args.RequestingAssembly.Location),
                Path.GetDirectoryName(typeof(HostAssemblyResolver).Assembly.Location), StringComparison.OrdinalIgnoreCase))) return null;

            var host = GetHostIdentity(new AssemblyName(args.Name), AppDomain.CurrentDomain.ApplyPolicy);
            return host == null ? null : Assembly.Load(host);
        }

        internal static AssemblyName GetHostIdentity(AssemblyName requested, Func<string, string> applyPolicy)
        {
            string token;
            switch (requested.Name)
            {
                case "System.Text.Json":
                case "Microsoft.Bcl.AsyncInterfaces":
                case "System.Diagnostics.DiagnosticSource":
                case "System.IO.Pipelines":
                case "System.Text.Encodings.Web":
                case "System.Threading.Channels": token = "cc7b13ffcd2ddd51"; break;
                case "System.Collections.Immutable": token = "b03f5f7f11d50a3a"; break;
                case "Microsoft.Extensions.Logging.Abstractions":
                case "Microsoft.Extensions.DependencyInjection.Abstractions": token = "adb9793829ddae60"; break;
                default: return null;
            }
            if (requested.Version?.Major != 10 || !string.IsNullOrEmpty(requested.CultureName)
                || BitConverter.ToString(requested.GetPublicKeyToken() ?? new byte[0]).Replace("-", "").ToLowerInvariant() != token) return null;

            var baseline = new AssemblyName(requested.FullName) { Version = new Version(10, 0, 0, 0) };
            var host = new AssemblyName(applyPolicy(baseline.FullName));
            // Never recursively retry a failed host bind, or cross a major-version boundary.
            return host.Version.Major == 10 && host.Version < requested.Version ? host : null;
        }
    }
}
