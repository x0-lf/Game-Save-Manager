using System.IO;

namespace GameSave
{
    public class Program
    {
        public static void Main(string[] args)
        {
            SteamDiscoveryService.ValidateLocation();
            Console.WriteLine();
            Console.WriteLine("Press any key to exit");
            Console.ReadKey();

        }
    }
}
