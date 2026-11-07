using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace VsMcp.Infrastructure
{
    internal static class BridgeDeployment
    {
        // The VSIX keeps the net10 bridge dependencies separate from the net472 extension.
        public static void Install(string source, string destination)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The VSIX has no bundled bridge: " + source);
            Directory.CreateDirectory(destination);
            var files = Directory.EnumerateFiles(source).ToArray();
            if (files.All(file => SameFile(file, Path.Combine(destination, Path.GetFileName(file))))) return;
            // Bridge processes share this lease. Updating requires exclusive access, so a live
            // client can never observe a partially updated set of executable/dependency files.
            using (var deploymentLock = File.Open(Path.Combine(destination, ".bridge.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var file in files)
                {
                    var target = Path.Combine(destination, Path.GetFileName(file));
                    if (SameFile(file, target)) continue;
                    File.Copy(file, target, overwrite: true);
                }
            }
        }

        private static bool SameFile(string source, string target)
        {
            if (!File.Exists(target)) return false;
            using (var hash = SHA256.Create())
                return hash.ComputeHash(File.ReadAllBytes(source)).SequenceEqual(hash.ComputeHash(File.ReadAllBytes(target)));
        }
    }
}
