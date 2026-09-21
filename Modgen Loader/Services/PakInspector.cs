using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InfinityModgenManager.Services
{
    public class PakInspector
    {
        public PakManifest InspectMod(
            string modDirectory)
        {
            if (string.IsNullOrWhiteSpace(modDirectory))
            {
                throw new ArgumentException(
                    "Mod directory was not provided.",
                    nameof(modDirectory));
            }

            if (!Directory.Exists(modDirectory))
            {
                throw new DirectoryNotFoundException(
                    "Mod directory does not exist: " +
                    modDirectory);
            }

            string[] pakFiles =
                Directory.GetFiles(
                    modDirectory,
                    "*.pak",
                    SearchOption.AllDirectories);

            string[] utocFiles =
                Directory.GetFiles(
                    modDirectory,
                    "*.utoc",
                    SearchOption.AllDirectories);

            string[] ucasFiles =
                Directory.GetFiles(
                    modDirectory,
                    "*.ucas",
                    SearchOption.AllDirectories);

            if (pakFiles.Length == 0 &&
                utocFiles.Length == 0)
            {
                return new PakManifest();
            }

            VersionContainer versions =
                new VersionContainer(
                    EGame.GAME_UE4_24);

            DefaultFileProvider provider =
                new DefaultFileProvider(
                    modDirectory,
                    SearchOption.AllDirectories,
                    true,
                    versions);

            provider.Initialize();

            PakManifest manifest =
                new PakManifest();

            foreach (string pakFile in pakFiles)
            {
                string relativePath =
                    Path.GetRelativePath(
                        modDirectory,
                        pakFile);

                manifest.PakFiles.Add(
                    new PakFileInfo
                    {
                        FileName =
                            Path.GetFileName(
                                pakFile),

                        RelativePath =
                            relativePath,

                        FileSize =
                            new FileInfo(
                                pakFile).Length
                    });
            }

            HashSet<string> processedIoStores =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (string utocFile in utocFiles)
            {
                string baseName =
                    Path.GetFileNameWithoutExtension(
                        utocFile);

                if (processedIoStores.Contains(
                    baseName))
                {
                    continue;
                }

                string? matchingUcas =
                    ucasFiles.FirstOrDefault(
                        file =>
                            string.Equals(
                                Path.GetFileNameWithoutExtension(
                                    file),
                                baseName,
                                StringComparison.OrdinalIgnoreCase));

                if (matchingUcas == null)
                {
                    continue;
                }

                manifest.PakFiles.Add(
                    new PakFileInfo
                    {
                        FileName =
                            baseName +
                            " (IoStore)",

                        RelativePath =
                            Path.GetRelativePath(
                                modDirectory,
                                utocFile),

                        FileSize =
                            new FileInfo(
                                utocFile).Length,

                        UcasPath =
                            Path.GetRelativePath(
                                modDirectory,
                                matchingUcas)
                    });

                processedIoStores.Add(
                    baseName);
            }

            foreach (var file in provider.Files.Values)
            {
                manifest.Files.Add(
                    new PakFileEntry
                    {
                        Path =
                            file.Path
                    });
            }

            manifest.TotalFiles =
                manifest.Files.Count;

            manifest.TotalSize =
                manifest.PakFiles.Sum(
                    file => file.FileSize);

            return manifest;
        }
    }

    public class PakManifest
    {
        public List<PakFileInfo> PakFiles { get; set; } =
            new List<PakFileInfo>();

        public List<PakFileEntry> Files { get; set; } =
            new List<PakFileEntry>();

        public long TotalFiles { get; set; }

        public long TotalSize { get; set; }
    }

    public class PakFileInfo
    {
        public string FileName { get; set; } = "";

        public string RelativePath { get; set; } = "";

        public long FileSize { get; set; }

        public string UcasPath { get; set; } = "";
    }

    public class PakFileEntry
    {
        public string Path { get; set; } = "";
    }
}