namespace DebugFixture
{
    internal static class Program
    {
        private static void Main()
        {
            int counter = 41;
            counter++;
            System.Threading.Thread.Sleep(30000);
            System.Console.WriteLine(counter);
        }
    }
}
