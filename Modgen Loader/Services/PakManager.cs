using InfinityModgenManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InfinityModgenManager.Services
{
    public class PakManager
    {
        private const string ManagedMoviesFolderName =
            ".infinity_modgen_movie_backups";

        private const string ManagedMoviesManifestFileName =
            ".infinity_modgen_movies.json";

        private const string RuntimeManifestFileName =
            ".infinity_modgen_mods.json";

        public void ApplyMods(
            Game game,
            IEnumerable<Mod> mods)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));

            List<Mod> enabledMods =
                mods
                    .Where(mod => mod.IsEnabled)
                    .OrderBy(mod => mod.LoadOrder)
                    .ToList();

            ApplyPakMods(
                game,
                enabledMods);

            ApplyMovieMods(
                game,
                enabledMods);
        }

        /// <summary>
        /// Returns the Manager's existing mod directory.
        /// PAK files remain in their installed mod directories and
        /// are not copied into a separate deployment directory.
        /// This directory is passed to shimloader as --pak-dir.
        /// </summary>
        public string GetPakDirectory(
            Game game)
        {
            return GetRuntimeManifestDirectory(
                game);
        }

        private void ApplyPakMods(
            Game game,
            IEnumerable<Mod> enabledMods)
        {
            if (!game.SupportsPakMods)
                return;

            if (string.IsNullOrWhiteSpace(
                game.GameDirectory))
            {
                throw new IOException(
                    "The game's directory has not been configured.");
            }

            string managerModsDirectory =
                game.ModsDirectory;

            if (string.IsNullOrWhiteSpace(
                managerModsDirectory))
            {
                managerModsDirectory =
                    Path.Combine(
                        game.GameDirectory,
                        "Mods");

                game.ModsDirectory =
                    managerModsDirectory;
            }

            Directory.CreateDirectory(
                managerModsDirectory);

            List<ExternalPakMod> pakMods =
                new List<ExternalPakMod>();

            int priority = 0;

            foreach (Mod mod in enabledMods)
            {
                if (!string.Equals(
                    mod.ModType,
                    "PAK",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(
                    mod.ModDirectory))
                {
                    continue;
                }

                if (!Directory.Exists(
                    mod.ModDirectory))
                {
                    continue;
                }

                List<string> pakFiles =
                    GetPakFiles(
                        mod);

                if (pakFiles.Count == 0)
                    continue;

                string modName =
                    GetSafeModName(
                        mod);

                /*
                 * PAK files are deliberately NOT copied.
                 *
                 * The existing PAK files remain in the Manager's
                 * mod library. The runtime manifest records the
                 * enabled mod and its priority, while shimloader's
                 * --pak-dir points at the Manager's mod directory.
                 */
                pakMods.Add(
                    new ExternalPakMod
                    {
                        Name =
                            mod.Name,

                        Author =
                            mod.Author,

                        Version =
                            mod.Version,

                        ModDirectory =
                            mod.ModDirectory,

                        PakPath =
                            mod.PakPath,

                        Priority =
                            priority,

                        PriorityFolderName =
                            modName,

                        PakFiles =
                            pakFiles
                    });

                priority++;
            }

            SaveRuntimeManifest(
                game,
                pakMods);
        }

        private void SaveRuntimeManifest(
            Game game,
            List<ExternalPakMod> pakMods)
        {
            string manifestDirectory =
                GetRuntimeManifestDirectory(
                    game);

            Directory.CreateDirectory(
                manifestDirectory);

            string manifestPath =
                Path.Combine(
                    manifestDirectory,
                    RuntimeManifestFileName);

            string json =
                JsonSerializer.Serialize(
                    pakMods,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                manifestPath,
                json);
        }

        /// <summary>
        /// Resolves the actual game-root "Mods" directory that
        /// shimloader looks for its runtime manifest in
        /// (<GameRoot>\Mods\.infinity_modgen_mods.json), where
        /// GameRoot is the folder containing Binaries\Win64\, not
        /// necessarily the folder the user's launcher exe sits in.
        /// Falls back to game.GameDirectory\Mods if the engine
        /// content directory hasn't been detected yet.
        /// </summary>
        private string GetRuntimeManifestDirectory(
            Game game)
        {
            string? gameRootDirectory =
                !string.IsNullOrWhiteSpace(
                    game.EngineContentDirectory)
                    ? Path.GetDirectoryName(
                        game.EngineContentDirectory)
                    : null;

            if (!string.IsNullOrWhiteSpace(
                gameRootDirectory))
            {
                string resolvedDirectory =
                    Path.Combine(
                        gameRootDirectory,
                        "Mods");

                game.ModsDirectory =
                    resolvedDirectory;

                return resolvedDirectory;
            }

            if (!string.IsNullOrWhiteSpace(
                game.ModsDirectory))
            {
                return game.ModsDirectory;
            }

            if (!string.IsNullOrWhiteSpace(
                game.GameDirectory))
            {
                string fallbackDirectory =
                    Path.Combine(
                        game.GameDirectory,
                        "Mods");

                game.ModsDirectory =
                    fallbackDirectory;

                return fallbackDirectory;
            }

            throw new IOException(
                "Unable to determine where the Infinity Modgen Manager runtime manifest should be stored.");
        }

        private List<string> GetPakFiles(
            Mod mod)
        {
            List<string> pakFiles =
                new List<string>();

            if (!string.IsNullOrWhiteSpace(
                mod.PakPath) &&
                File.Exists(
                mod.PakPath))
            {
                pakFiles.Add(
                    mod.PakPath);
            }

            if (Directory.Exists(
                mod.ModDirectory))
            {
                string[] files =
                    Directory.GetFiles(
                        mod.ModDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly);

                foreach (string file in files)
                {
                    string extension =
                        Path.GetExtension(
                            file);

                    if (!IsPakRelatedFile(
                        extension))
                    {
                        continue;
                    }

                    if (!pakFiles.Contains(
                        file,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        pakFiles.Add(
                            file);
                    }
                }
            }

            return pakFiles;
        }

        private void ApplyMovieMods(
            Game game,
            IEnumerable<Mod> enabledMods)
        {
            if (string.IsNullOrWhiteSpace(
                game.GameDirectory))
            {
                return;
            }

            string gameContentDirectory =
                !string.IsNullOrWhiteSpace(
                    game.EngineContentDirectory)
                    ? game.EngineContentDirectory
                    : Path.Combine(
                        game.GameDirectory,
                        "Content");

            string gameMoviesDirectory =
                Path.Combine(
                    gameContentDirectory,
                    "Movies");

            Directory.CreateDirectory(
                gameMoviesDirectory);

            string backupDirectory =
                Path.Combine(
                    gameMoviesDirectory,
                    ManagedMoviesFolderName);

            Directory.CreateDirectory(
                backupDirectory);

            Dictionary<string, ManagedMovieInfo>
                previousManagedMovies =
                LoadManagedMovieFiles(
                    gameMoviesDirectory);

            RestorePreviousMovies(
                gameMoviesDirectory,
                backupDirectory,
                previousManagedMovies);

            Dictionary<string, ManagedMovieInfo>
                currentManagedMovies =
                new Dictionary<string, ManagedMovieInfo>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (Mod mod in enabledMods)
            {
                string sourceMoviesDirectory =
                    Path.Combine(
                        mod.ModDirectory,
                        "Content",
                        "Movies");

                if (!Directory.Exists(
                    sourceMoviesDirectory))
                {
                    continue;
                }

                string[] movieFiles =
                    Directory.GetFiles(
                        sourceMoviesDirectory,
                        "*",
                        SearchOption.AllDirectories);

                foreach (string sourceMovie in movieFiles)
                {
                    string relativeMoviePath =
                        Path.GetRelativePath(
                            sourceMoviesDirectory,
                            sourceMovie);

                    if (string.IsNullOrWhiteSpace(
                        relativeMoviePath))
                    {
                        continue;
                    }

                    string destinationPath =
                        Path.Combine(
                            gameMoviesDirectory,
                            relativeMoviePath);

                    if (!currentManagedMovies.ContainsKey(
                        relativeMoviePath))
                    {
                        bool hadOriginal =
                            File.Exists(
                                destinationPath);

                        string backupPath =
                            Path.Combine(
                                backupDirectory,
                                relativeMoviePath);

                        if (hadOriginal)
                        {
                            string? backupParent =
                                Path.GetDirectoryName(
                                    backupPath);

                            if (!string.IsNullOrWhiteSpace(
                                backupParent))
                            {
                                Directory.CreateDirectory(
                                    backupParent);
                            }

                            File.Copy(
                                destinationPath,
                                backupPath,
                                true);
                        }

                        currentManagedMovies[
                            relativeMoviePath] =
                            new ManagedMovieInfo
                            {
                                RelativePath =
                                    relativeMoviePath,

                                HadOriginal =
                                    hadOriginal
                            };
                    }

                    string? destinationDirectory =
                        Path.GetDirectoryName(
                            destinationPath);

                    if (!string.IsNullOrWhiteSpace(
                        destinationDirectory))
                    {
                        Directory.CreateDirectory(
                            destinationDirectory);
                    }

                    File.Copy(
                        sourceMovie,
                        destinationPath,
                        true);
                }
            }

            CleanUnusedMovieBackups(
                backupDirectory,
                currentManagedMovies);

            SaveManagedMovieFiles(
                gameMoviesDirectory,
                currentManagedMovies);
        }

        private void RestorePreviousMovies(
            string gameMoviesDirectory,
            string backupDirectory,
            Dictionary<string, ManagedMovieInfo>
                previousManagedMovies)
        {
            foreach (
                KeyValuePair<
                    string,
                    ManagedMovieInfo> entry
                in previousManagedMovies)
            {
                string relativeMoviePath =
                    entry.Key;

                ManagedMovieInfo movieInfo =
                    entry.Value;

                string destinationPath =
                    Path.Combine(
                        gameMoviesDirectory,
                        relativeMoviePath);

                if (movieInfo.HadOriginal)
                {
                    string backupPath =
                        Path.Combine(
                            backupDirectory,
                            relativeMoviePath);

                    if (File.Exists(
                        backupPath))
                    {
                        string? destinationDirectory =
                            Path.GetDirectoryName(
                                destinationPath);

                        if (!string.IsNullOrWhiteSpace(
                            destinationDirectory))
                        {
                            Directory.CreateDirectory(
                                destinationDirectory);
                        }

                        File.Copy(
                            backupPath,
                            destinationPath,
                            true);
                    }
                }
                else
                {
                    if (File.Exists(
                        destinationPath))
                    {
                        File.Delete(
                            destinationPath);
                    }
                }
            }
        }

        private Dictionary<string, ManagedMovieInfo>
            LoadManagedMovieFiles(
                string gameMoviesDirectory)
        {
            string manifestPath =
                Path.Combine(
                    gameMoviesDirectory,
                    ManagedMoviesManifestFileName);

            if (!File.Exists(
                manifestPath))
            {
                return
                    new Dictionary<string, ManagedMovieInfo>(
                        StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                string json =
                    File.ReadAllText(
                        manifestPath);

                List<ManagedMovieInfo>? files =
                    JsonSerializer.Deserialize<
                        List<ManagedMovieInfo>>(
                            json);

                if (files == null)
                {
                    return
                        new Dictionary<
                            string,
                            ManagedMovieInfo>(
                            StringComparer.OrdinalIgnoreCase);
                }

                return
                    files
                        .Where(file =>
                            !string.IsNullOrWhiteSpace(
                                file.RelativePath))
                        .GroupBy(
                            file => file.RelativePath,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Last(),
                            StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return
                    new Dictionary<
                        string,
                        ManagedMovieInfo>(
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveManagedMovieFiles(
            string gameMoviesDirectory,
            Dictionary<string, ManagedMovieInfo>
                managedMovies)
        {
            string manifestPath =
                Path.Combine(
                    gameMoviesDirectory,
                    ManagedMoviesManifestFileName);

            List<ManagedMovieInfo> files =
                managedMovies
                    .Values
                    .OrderBy(
                        file => file.RelativePath)
                    .ToList();

            string json =
                JsonSerializer.Serialize(
                    files,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                manifestPath,
                json);
        }

        private void CleanUnusedMovieBackups(
            string backupDirectory,
            Dictionary<string, ManagedMovieInfo>
                currentManagedMovies)
        {
            if (!Directory.Exists(
                backupDirectory))
            {
                return;
            }

            string[] backupFiles =
                Directory.GetFiles(
                    backupDirectory,
                    "*",
                    SearchOption.AllDirectories);

            foreach (string backupFile in backupFiles)
            {
                string relativePath =
                    Path.GetRelativePath(
                        backupDirectory,
                        backupFile);

                if (!currentManagedMovies.ContainsKey(
                    relativePath))
                {
                    File.Delete(
                        backupFile);
                }
            }

            DeleteEmptyDirectories(
                backupDirectory);
        }

        private void DeleteEmptyDirectories(
            string directory)
        {
            if (!Directory.Exists(
                directory))
            {
                return;
            }

            foreach (string childDirectory in
                     Directory.GetDirectories(
                         directory))
            {
                DeleteEmptyDirectories(
                    childDirectory);

                if (!Directory.EnumerateFileSystemEntries(
                    childDirectory).Any())
                {
                    Directory.Delete(
                        childDirectory);
                }
            }
        }

        private bool IsPakRelatedFile(
            string extension)
        {
            return
                string.Equals(
                    extension,
                    ".pak",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    extension,
                    ".ucas",
                    StringComparison.OrdinalIgnoreCase) ||

                string.Equals(
                    extension,
                    ".utoc",
                    StringComparison.OrdinalIgnoreCase);
        }

        private string GetSafeModName(
            Mod mod)
        {
            string name =
                mod.Name;

            if (string.IsNullOrWhiteSpace(
                name))
            {
                name =
                    Path.GetFileName(
                        mod.ModDirectory);
            }

            if (string.IsNullOrWhiteSpace(
                name))
            {
                name = "Unnamed Mod";
            }

            foreach (char invalidCharacter in
                     Path.GetInvalidFileNameChars())
            {
                name =
                    name.Replace(
                        invalidCharacter,
                        '_');
            }

            return name.Trim();
        }

        private class ExternalPakMod
        {
            public string Name { get; set; } = "";

            public string Author { get; set; } = "";

            public string Version { get; set; } = "";

            public string ModDirectory { get; set; } = "";

            public string PakPath { get; set; } = "";

            public int Priority { get; set; }

            public string PriorityFolderName { get; set; } = "";

            public List<string> PakFiles { get; set; } =
                new List<string>();
        }

        private class ManagedMovieInfo
        {
            public string RelativePath { get; set; } = "";

            public bool HadOriginal { get; set; }
        }
    }
}