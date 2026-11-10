namespace DebugFixture
{
    internal static class Program
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] private static void Run()
        {
            int counter = 41;
            counter++;
            System.Threading.Thread.Sleep(30000);
            System.Console.WriteLine(counter);
        }
        private static void Main()
        {
            using var ready = new System.Threading.ManualResetEventSlim();
            var worker = new System.Threading.Thread(() => { ready.Set(); System.Threading.Thread.Sleep(30000); }) { IsBackground = true, Name = "Worker" };
            worker.Start();
            ready.Wait();
            int callerValue = 17;
            Run();
            System.GC.KeepAlive(callerValue);
        }
    }
}
