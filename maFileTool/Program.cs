using System;
using System.IO;
using System.Threading;

namespace maFileTool
{
    internal abstract class Program
    {
        public static void Main(string[] args)
        {
            var accounts = File.ReadAllLines("accounts.txt");

            Console.Write($"Loaded {accounts.Length} accounts, press enter to start");
            Console.ReadLine();

            foreach (var accountData in accounts)
            {
                var array = new string[] {};
                
                if (accountData.Contains(":"))
                    array = accountData.Split(':');
                else if (accountData.Contains(" "))
                    array = accountData.Split(' ');
                else if (accountData.Contains("\t"))
                    array = accountData.Split('\t');

                if (array.Length > 0)
                    new Worker(array[0], array[1], array[2], array[3]).DoWork();
                 
                Thread.Sleep(15000);
            }
          
            Console.Write("\nAll done.");
            Console.ReadLine();
        }
    }
}