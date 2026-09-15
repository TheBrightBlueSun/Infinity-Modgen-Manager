using System;
using System.Collections.Generic;
using System.IO;
using Modgen_Loader.Models;

namespace Modgen_Loader.Services
{
    public class GameScanner
    {
        public event Action<string> ScanProgress;

        public List<Game> ScanForGames()
        {
            List<Game> games = new List<Game>();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                ScanProgress?.Invoke("Scanning " + drive.Name);

                ScanDirectory(drive.RootDirectory.FullName, games);
            }

            return games;
        }

        private void ScanDirectory(string directory, List<Game> games)
        {
            try
            {
                ScanProgress?.Invoke("Scanning: " + directory);

                string exePath = Path.Combine(directory, "Sonic Omens.exe");

                if (File.Exists(exePath))
                {
                    games.Add(new Game
                    {
                        Name = "Sonic Omens",
                        ExePath = exePath,
                        GameDirectory = directory,
                        EngineVersion = "Unreal Engine 4.24"
                    });

                    return;
                }

                foreach (string subDirectory in Directory.GetDirectories(directory))
                {
                    ScanDirectory(subDirectory, games);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}