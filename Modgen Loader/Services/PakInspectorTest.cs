using System;
using System.IO;

namespace Modgen_Loader.Services
{
    public class PakInspectorTest
    {
        public static void Run(
            string modDirectory)
        {
            PakInspector inspector =
                new PakInspector();

            PakManifest manifest =
                inspector.InspectMod(
                    modDirectory);

            Console.WriteLine(
                "PAK files: " +
                manifest.PakFiles.Count);

            Console.WriteLine(
                "Files inside archives: " +
                manifest.TotalFiles);

            Console.WriteLine(
                "Total archive size: " +
                manifest.TotalSize);

            foreach (PakFileInfo pak in
                     manifest.PakFiles)
            {
                Console.WriteLine(
                    pak.FileName);
            }

            foreach (PakFileEntry file in
                     manifest.Files)
            {
                Console.WriteLine(
                    file.Path);
            }
        }
    }
}